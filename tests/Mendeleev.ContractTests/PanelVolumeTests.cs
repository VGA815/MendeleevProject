using System.Diagnostics;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Panel.Reconciliation;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mendeleev.ContractTests
{
    /// <summary>
    /// ТЗ 50, «Нагрузочный тест», объём: 5000 пользователей в БД и в панели (NFR-17). Access for everybody goes
    /// through the outbox exactly as in production (one sequential loop), then the reconciliation must fit its
    /// 10-minute interval (FR-PNL-05) and roll back manual edits. Timings go to the test output.
    /// </summary>
    [Collection(nameof(VolumeStandCollection))]
    [Trait("Category", "Volume")]
    public sealed class PanelVolumeTests(VolumeStand stand, ITestOutputHelper output)
    {
        private const int Users = 5000;
        private const int Tampered = 100;
        private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(10);

        [VolumeFact]
        public async Task FiveThousandUsers_GetAccess_AndReconciliationFitsItsInterval()
        {
            Stopwatch clock = Stopwatch.StartNew();
            await SeedTrialsAsync();
            Report($"seed: {Users} users with a trial in the DB — {clock.Elapsed:mm\\:ss}");

            // 1. Access for everybody: the outbox pushes the desired state of each subscription to the panel.
            long requestsBefore = stand.Requests.Count;
            clock.Restart();
            int messages = await DrainOutboxAsync();
            TimeSpan sync = clock.Elapsed;
            long syncRequests = stand.Requests.Count - requestsBefore;

            (await CountAsync(s => s.SyncState != SyncState.Synced)).ShouldBe(0, "every subscription reached the panel");
            Report($"sync: {messages} outbox messages, {syncRequests} panel requests ({(double)syncRequests / Users:0.0} per user) — "
                + $"{sync:mm\\:ss}, {Users / sync.TotalSeconds:0} users/s");
            Report($"  in production each request also pays the RTT to the panel abroad: at 80 ms that adds "
                + $"{TimeSpan.FromMilliseconds(syncRequests * 80.0):mm\\:ss} for {Users} users (a wave of 300/h needs {300.0 * syncRequests / Users * 0.08 / 60:0.0} min)");

            // 2. A clean reconciliation over the whole panel.
            requestsBefore = stand.Requests.Count;
            clock.Restart();
            PanelReconciliationSummary clean = await ReconcileAsync();
            TimeSpan reconcile = clock.Elapsed;

            clean.PanelUsers.ShouldBeGreaterThanOrEqualTo(Users);
            clean.Drifted.ShouldBe(0);
            clean.Missing.ShouldBe(0);
            reconcile.ShouldBeLessThan(ReconciliationInterval / 5);
            Report($"reconcile, clean: {clean.PanelUsers} panel users, {stand.Requests.Count - requestsBefore} requests — {reconcile:mm\\:ss\\.f}");

            // 3. Somebody extended users by hand in the panel UI: the reconciliation rolls it back (FR-PNL-06).
            await TamperInPanelAsync(Tampered);
            clock.Restart();
            PanelReconciliationSummary drifted = await ReconcileAsync();
            await DrainOutboxAsync();
            TimeSpan repair = clock.Elapsed;

            drifted.Drifted.ShouldBe(Tampered);
            (await ReconcileAsync()).Drifted.ShouldBe(0);
            Report($"reconcile with {Tampered} manual edits + resync: {repair:mm\\:ss\\.f}");

            stand.Alerts.ShouldNotContain(a => a.Key.StartsWith("panel-reconcile-unavailable", StringComparison.Ordinal));
        }

        private async Task SeedTrialsAsync()
        {
            Tariff trial;
            await using (AsyncServiceScope scope = stand.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                trial = Tariff.Create(Tariff.TrialCode, "Пробный", TariffTier.Trial, 0, 2, 3, 10L * 1024 * 1024 * 1024, [stand.Panel.BasicSquad], true, 0);
                db.Tariffs.Add(trial);
                await db.SaveChangesAsync();
            }

            const int batch = 500;
            for (int offset = 0; offset < Users; offset += batch)
            {
                await using AsyncServiceScope scope = stand.Services.CreateAsyncScope();
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Tariff tariff = await db.Tariffs.SingleAsync(t => t.Id == trial.Id);
                DateTime now = DateTime.UtcNow;

                List<User> users = Enumerable.Range(offset, batch).Select(i => User.CreateForTelegram(700_000_000 + i, now)).ToList();
                db.Users.AddRange(users);
                await db.SaveChangesAsync();

                foreach (User user in users)
                {
                    user.MarkTrialUsed(now);
                    db.Subscriptions.Add(Subscription.StartTrial(user.Id, tariff, now));
                }
                await db.SaveChangesAsync();
            }
        }

        private async Task<int> DrainOutboxAsync()
        {
            OutboxProcessor processor = stand.Services.GetRequiredService<OutboxProcessor>();
            int total = 0;
            for (int processed; (processed = await processor.ProcessBatchAsync(CancellationToken.None)) > 0;)
            {
                total += processed;
            }
            return total;
        }

        private async Task<PanelReconciliationSummary> ReconcileAsync()
        {
            await using AsyncServiceScope scope = stand.Services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ReconcilePanelCommand, PanelReconciliationSummary>>();
            return (await handler.Handle(new ReconcilePanelCommand(), CancellationToken.None)).Value;
        }

        private async Task TamperInPanelAsync(int count)
        {
            List<int> panelIds = await WithDbAsync(db => db.Subscriptions
                .Where(s => s.PanelUserId != null)
                .OrderBy(s => s.Id)
                .Select(s => s.PanelUserId!.Value)
                .Take(count)
                .ToListAsync());

            foreach (int id in panelIds)
            {
                PanelUser user = (await stand.Panel.Client.GetUserAsync(id, CancellationToken.None))!;
                await stand.Panel.Client.UpdateUserAsync(id, new PanelUserSpec(
                    user.Username, user.ShortUuid, user.VlessUuid, user.ExpireAt.AddDays(100), user.TrafficLimitBytes,
                    user.HwidDeviceLimit ?? 0, user.InternalSquads, Enabled: true), CancellationToken.None);
            }
        }

        private Task<int> CountAsync(System.Linq.Expressions.Expression<Func<Subscription, bool>> predicate) =>
            WithDbAsync(db => db.Subscriptions.CountAsync(predicate));

        private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        {
            await using AsyncServiceScope scope = stand.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        private void Report(string line) => output.WriteLine(line);
    }
}
