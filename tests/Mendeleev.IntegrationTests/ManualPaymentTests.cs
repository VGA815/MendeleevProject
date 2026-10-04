using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Admin.Stats;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Application.Panel.Reconciliation;
using Mendeleev.Application.Payments.Reconciliation;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// ТЗ 23, «Если договор с агрегатором не готов к запуску»: the owner takes the money outside the system and an
    /// admin records it. The staff extension alone kept the trial's 10 GB, so a paying user lost access again.
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class ManualPaymentTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private TestApp _app = null!;

        public async Task InitializeAsync() => _app = await TestApp.CreateAsync(postgres);

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task TrialOutOfTraffic_PaidByHand_GetsThePaidTariffWithoutTheLimit()
        {
            long userId = await NewTrialUserAsync(3001);
            long admin = await _app.AddStaffAsync(9101, StaffRole.Admin);
            DateTime trialEnd = (await SubscriptionOfAsync(userId)).ExpiresAt;

            // The trial's 10 GB ran out: the panel limits the user, the reconciliation ends the trial by traffic.
            PanelUser limited = _app.Panel.Users.Single();
            _app.Panel.Tamper(limited.Id, u => u with { Status = PanelUserStatus.Limited });
            await _app.SendAsync<ReconcilePanelCommand, PanelReconciliationSummary>(new ReconcilePanelCommand());
            await _app.ProcessOutboxAsync();
            (await SubscriptionOfAsync(userId)).Status.ShouldBe(SubscriptionStatus.Expired);

            Result<ManualPaymentRecorded> recorded = await RecordAsync(admin, userId, "basic_1m", 300);
            await _app.ProcessOutboxAsync();

            recorded.IsSuccess.ShouldBeTrue();
            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.Tariff.Code.ShouldBe("basic_1m");
            subscription.ExpiresAt.ShouldBe(trialEnd.AddDays(30)); // FR-SUB-05: the rest of the trial is kept
            recorded.Value.ExpiresAt.ShouldBe(subscription.ExpiresAt);

            PanelUser panelUser = _app.Panel.Users.Single();
            panelUser.Status.ShouldBe(PanelUserStatus.Active);
            panelUser.TrafficLimitBytes.ShouldBe(0); // no limit
            panelUser.ShortUuid.ShouldBe(limited.ShortUuid); // the same link (FR-SUB-08)

            _app.Messenger.Notifications.ShouldContain(n => n.ChatId == 3001 && n.Message.Kind == NotificationKind.PaymentSucceeded);

            Payment payment = await _app.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync());
            payment.Provider.ShouldBe(Payment.ManualProvider);
            payment.Amount.ShouldBe(300);
            int audited = await _app.WithDbAsync(db => db.AuditLog.CountAsync(a => a.TargetUserId == userId && a.Action == AuditActions.PaymentManual));
            audited.ShouldBe(1);
        }

        [Fact]
        public async Task OnlyAdminsRecord_APaidTariff_ForAnUnblockedUser()
        {
            long userId = await NewTrialUserAsync(3002);
            long support = await _app.AddStaffAsync(9102, StaffRole.Support);
            long admin = await _app.AddStaffAsync(9103, StaffRole.Admin);

            (await RecordAsync(support, userId, "basic_1m", 300)).Error.ShouldBe(StaffErrors.NotAllowed);
            (await RecordAsync(admin, userId, Tariff.TrialCode, 300)).Error.ShouldBe(TariffErrors.TrialNotForSale);
            (await RecordAsync(admin, userId, "basic_1m", 0)).Error.Type.ShouldBe(ErrorType.Validation);

            (await _app.SendAsync(new BlockUserCommand(admin, userId, "abuse"))).IsSuccess.ShouldBeTrue();
            (await RecordAsync(admin, userId, "basic_1m", 300)).Error.ShouldBe(PaymentErrors.ManualForBlockedUser);

            (await _app.WithDbAsync(db => db.Payments.CountAsync())).ShouldBe(0);
            (await SubscriptionOfAsync(userId)).Tariff.Code.ShouldBe(Tariff.TrialCode);
        }

        [Fact]
        public async Task CountsInSales_AndTheAggregatorJobsLeaveItAlone()
        {
            long trialUser = await NewTrialUserAsync(3003);
            long newUser = await NewUserAsync(3004);
            long admin = await _app.AddStaffAsync(9104, StaffRole.TechAdmin);

            (await RecordAsync(admin, trialUser, "basic_1m", 300)).IsSuccess.ShouldBeTrue();
            (await RecordAsync(admin, newUser, "basic_3m", 800)).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();

            // A user who never had a subscription gets access and the link straight away.
            (await SubscriptionOfAsync(newUser)).Status.ShouldBe(SubscriptionStatus.Active);
            _app.Messenger.Notifications.ShouldContain(n => n.ChatId == 3004 && n.Message.Kind == NotificationKind.AccessIssued);

            _app.Clock.Advance(TimeSpan.FromMinutes(1));
            SalesStats stats = (await _app.QueryAsync<GetStatsQuery, SalesStats>(new GetStatsQuery(admin, StatsPeriod.Today))).Value;
            stats.PaymentsCount.ShouldBe(2);
            stats.PaymentsSum.ShouldBe(1100);
            stats.ManualCount.ShouldBe(2);
            stats.ManualSum.ShouldBe(1100);
            stats.TrialsConverted.ShouldBe(1);
            StatsFormatter.Format(stats).ShouldContain("из них вне системы: 2 на 1");

            // No aggregator id: the reconciliation and the pending check have nothing to ask about.
            (await _app.SendAsync<ReconcilePaymentsCommand, ReconciliationSummary>(new ReconcilePaymentsCommand())).Value.Mismatches.ShouldBe(0);
            (await _app.SendAsync<CheckPendingPaymentsCommand, int>(new CheckPendingPaymentsCommand())).IsSuccess.ShouldBeTrue();
            _app.Alerts.Raised.ShouldNotContain(a => a.Key.StartsWith("payment", StringComparison.Ordinal));
            (await _app.WithDbAsync(db => db.Payments.CountAsync(p => p.Status == PaymentStatus.Succeeded))).ShouldBe(2);
        }

        private Task<Result<ManualPaymentRecorded>> RecordAsync(long staffId, long userId, string tariff, decimal amount) =>
            _app.SendAsync<RecordManualPaymentCommand, ManualPaymentRecorded>(
                new RecordManualPaymentCommand(staffId, userId, tariff, amount, "перевод на карту"));

        private async Task<long> NewUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private async Task<long> NewTrialUserAsync(long telegramId)
        {
            long userId = await NewUserAsync(telegramId);
            (await _app.SendAsync(new StartTrialCommand(userId))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            return userId;
        }

        private Task<Subscription> SubscriptionOfAsync(long userId) =>
            _app.WithDbAsync(db => db.Subscriptions.AsNoTracking().Include(s => s.Tariff).SingleAsync(s => s.UserId == userId));
    }
}
