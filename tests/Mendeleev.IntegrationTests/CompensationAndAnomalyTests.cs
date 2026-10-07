using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Admin.Compensation;
using Mendeleev.Application.Admin.Payments;
using Mendeleev.Application.Admin.Stats;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Application.Subscriptions;
using Mendeleev.Application.Subscriptions.Maintenance;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Traffic;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// Stage 1.5: mass compensation (FR-SUB-16, FR-ADM-16), the reminder before the archive (FR-SUB-17) and the
    /// anomaly report in the daily summary (FR-ADM-17).
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class CompensationAndAnomalyTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private const long AdminChat = 9301;

        private TestApp _app = null!;
        private long _admin;

        private IFakePaymentSimulator Aggregator => _app.Provider.GetRequiredService<IFakePaymentSimulator>();

        public async Task InitializeAsync()
        {
            _app = await TestApp.CreateAsync(postgres);
            _admin = await _app.AddStaffAsync(AdminChat, StaffRole.Admin);
        }

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task ActiveNow_ExtendsEveryoneWithAccess_TellsThemWhy_AndAuditsEach()
        {
            long paid = await PaidUserAsync(6001);
            long trial = await TrialUserAsync(6002);
            DateTime paidUntil = (await SubscriptionOfAsync(paid)).ExpiresAt;
            DateTime trialUntil = (await SubscriptionOfAsync(trial)).ExpiresAt;
            _app.Messenger.Notifications.Clear();

            var spec = new MassCompensationSpec(3, CompensationSegment.ActiveNow, IncludeTrial: false);
            (await CountAsync(spec)).ShouldBe(1);
            (await CountAsync(spec with { IncludeTrial = true })).ShouldBe(2);

            MassCompensationResult result = (await CompensateAsync(spec with { IncludeTrial = true }, "сбой нод 07.10")).Value;
            await _app.ProcessOutboxAsync();

            result.Extended.ShouldBe(2);
            result.Skipped.ShouldBe(0);
            (await SubscriptionOfAsync(paid)).ExpiresAt.ShouldBe(paidUntil.AddDays(3));
            (await SubscriptionOfAsync(trial)).ExpiresAt.ShouldBe(trialUntil.AddDays(3));

            _app.Messenger.Notifications.Count.ShouldBe(2);
            _app.Messenger.Notifications.ShouldAllBe(n => n.Message.Kind == NotificationKind.MassCompensated
                && n.Message.Values[SubscriptionChangedDomainEventHandler.ReasonKey] == "сбой нод 07.10");

            List<AuditLogEntry> audit = await _app.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(a => a.StaffId == _admin).ToListAsync());
            audit.Count(a => a.Action == AuditActions.SubscriptionExtend).ShouldBe(2);
            string massDetails = audit.Single(a => a.Action == AuditActions.SubscriptionMassExtend).Details!;
            System.Text.Json.JsonDocument.Parse(massDetails).RootElement.GetProperty("extended").GetInt32().ShouldBe(2);
        }

        [Fact]
        public async Task DuringTheOutage_BringsBackThoseWhoseTermRanOutInIt()
        {
            DateTime outageFrom = _app.Clock.UtcNow;
            long active = await PaidUserAsync(6011);

            // Its trial ends in the middle of the outage.
            long endedInOutage = await TrialUserAsync(6012);
            _app.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(5));
            DateTime outageTo = _app.Clock.UtcNow;
            (await _app.SendAsync<ExpireSubscriptionsCommand, int>(new ExpireSubscriptionsCommand())).IsSuccess.ShouldBeTrue();
            (await SubscriptionOfAsync(endedInOutage)).Status.ShouldBe(SubscriptionStatus.Expired);

            _app.Clock.Advance(TimeSpan.FromHours(1));
            long newcomer = await PaidUserAsync(6013);

            var spec = new MassCompensationSpec(5, CompensationSegment.ActiveDuringOutage, IncludeTrial: true, outageFrom.AddMinutes(-1), outageTo);
            MassCompensationResult result = (await CompensateAsync(spec, "сбой")).Value;

            result.Extended.ShouldBe(2);
            Subscription back = await SubscriptionOfAsync(endedInOutage);
            back.Status.ShouldBe(SubscriptionStatus.Trial);
            back.ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(5));
            (await SubscriptionOfAsync(newcomer)).ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(30));
            (await SubscriptionOfAsync(active)).ExpiresAt.ShouldBe(outageFrom.AddDays(35));
        }

        [Fact]
        public async Task OnlyAdmins_AndOnlyAnOrderedWindow()
        {
            long support = await _app.AddStaffAsync(9302, StaffRole.Support);
            var spec = new MassCompensationSpec(3, CompensationSegment.ActiveNow, IncludeTrial: false);

            (await _app.SendAsync<MassCompensateCommand, MassCompensationResult>(new MassCompensateCommand(support, spec, "сбой нод"))).Error.ShouldBe(StaffErrors.NotAllowed);
            (await CompensateAsync(spec with { Segment = CompensationSegment.ActiveDuringOutage }, "сбой нод")).Error.Type.ShouldBe(ErrorType.Validation);
            (await CompensateAsync(spec, "x")).Error.Type.ShouldBe(ErrorType.Validation);
        }

        [Fact]
        public async Task ThreeDaysBeforeTheArchive_TheUserIsWarnedOnce()
        {
            long userId = await PaidUserAsync(6021);
            Subscription subscription = await SubscriptionOfAsync(userId);
            _app.Clock.UtcNow = subscription.ExpiresAt.AddMinutes(1);
            (await _app.SendAsync<ExpireSubscriptionsCommand, int>(new ExpireSubscriptionsCommand())).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            _app.Messenger.Notifications.Clear();

            // Day 26: too early.
            _app.Clock.UtcNow = subscription.ExpiresAt.AddDays(26).Date.AddHours(9);
            await RemindAsync();
            _app.Messenger.Notifications.ShouldBeEmpty();

            // Day 27, outside the quiet hours: once.
            _app.Clock.UtcNow = subscription.ExpiresAt.AddDays(27).AddHours(1);
            if (MoscowTime.FromUtc(_app.Clock.UtcNow).Hour is >= 23 or < 9)
            {
                _app.Clock.UtcNow = _app.Clock.UtcNow.Date.AddHours(9);
            }
            await RemindAsync();
            await RemindAsync();
            _app.Messenger.Notifications.ShouldHaveSingleItem().Message.Kind.ShouldBe(NotificationKind.ArchiveSoon);
        }

        [Fact]
        public async Task NoWarningBeforeTheArchive_AfterARefund()
        {
            long userId = await PaidUserAsync(6022);
            Guid paymentId = await _app.WithDbAsync(db => db.Payments.Where(p => p.UserId == userId).Select(p => p.Id).SingleAsync());
            (await _app.SendAsync<RefundPaymentCommand, RefundResult>(
                new RefundPaymentCommand(_admin, paymentId, RefundKind.UnusedDays, "заявка"))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            _app.Messenger.Notifications.Clear();

            _app.Clock.UtcNow = (await SubscriptionOfAsync(userId)).ExpiresAt.AddDays(27).Date.AddHours(9);
            await RemindAsync();

            _app.Messenger.Notifications.ShouldBeEmpty();
        }

        [Fact]
        public async Task TheDailySummary_PointsAtAnomalies_AndTheCardFlagsThem()
        {
            long resetter = await PaidUserAsync(6031);
            long heavy = await PaidUserAsync(6032);
            long torrent = await PaidUserAsync(6033);
            long unpaid = await NewUserAsync(6034);
            var quiet = new List<long>();
            for (int i = 0; i < 5; i++)
            {
                quiet.Add(await PaidUserAsync(6040 + i));
            }

            DateTime now = _app.Clock.UtcNow;
            DateOnly yesterday = MoscowTime.Today(now).AddDays(-1);
            await _app.WithDbAsync(async db =>
            {
                db.DeviceResets.Add(DeviceReset.ByUser(resetter, 2, now.AddDays(-10)));
                db.DeviceResets.Add(DeviceReset.ByUser(resetter, 1, now.AddHours(-2)));
                foreach (long userId in quiet.Append(resetter).Append(torrent))
                {
                    long subscriptionId = await db.Subscriptions.Where(s => s.UserId == userId).Select(s => s.Id).SingleAsync();
                    db.TrafficDaily.Add(TrafficDaily.Create(subscriptionId, yesterday, 300L * 1024 * 1024, 0));
                }
                long heavySubscription = await db.Subscriptions.Where(s => s.UserId == heavy).Select(s => s.Id).SingleAsync();
                db.TrafficDaily.Add(TrafficDaily.Create(heavySubscription, yesterday, 40L * 1024 * 1024 * 1024, 0));
                db.AuditLog.Add(AuditLogEntry.BySystem(AuditActions.TorrentBlockerReport, torrent, null, now.AddHours(-3)));
                return await db.SaveChangesAsync();
            });
            for (int i = 0; i < 3; i++)
            {
                PaymentLink link = (await _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(unpaid, i % 2 == 0 ? "basic_1m" : "basic_3m"))).Value;
                (await _app.SendAsync(new HandlePaymentWebhookCommand("fake", Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Canceled)!))).IsSuccess.ShouldBeTrue();
            }
            _app.Messenger.Texts.Clear();

            (await _app.SendAsync<DailySummaryCommand, int>(new DailySummaryCommand())).Value.ShouldBe(1);

            string summary = _app.Messenger.Texts.Single(t => t.ChatId == AdminChat).Html;
            summary.ShouldContain("Аномалии за сутки");
            summary.ShouldContain($"u{resetter} — 2");
            summary.ShouldContain($"u{heavy} — 40,0 ГБ");
            summary.ShouldContain($"u{torrent} — 1");
            summary.ShouldContain($"u{unpaid} — 3");
            foreach (long userId in quiet)
            {
                summary.ShouldNotContain($"u{userId} ");
            }

            UserCard card = (await _app.SendAsync<GetUserCardCommand, UserCard>(new GetUserCardCommand(_admin, heavy))).Value;
            card.Anomalies!.Spike!.Bytes.ShouldBe(40L * 1024 * 1024 * 1024);
            (await _app.SendAsync<GetUserCardCommand, UserCard>(new GetUserCardCommand(_admin, quiet[0]))).Value.Anomalies!.Any.ShouldBeFalse();
            (await _app.SendAsync<GetUserCardCommand, UserCard>(new GetUserCardCommand(_admin, resetter))).Value.Anomalies!.FrequentResets.ShouldBeTrue();
        }

        [Fact]
        public async Task AQuietDay_SaysThereAreNoAnomalies()
        {
            await PaidUserAsync(6051);
            _app.Messenger.Texts.Clear();

            (await _app.SendAsync<DailySummaryCommand, int>(new DailySummaryCommand())).IsSuccess.ShouldBeTrue();

            _app.Messenger.Texts.Single(t => t.ChatId == AdminChat).Html.ShouldContain("<b>Аномалии</b>: нет.");
        }

        private Task<Result<MassCompensationResult>> CompensateAsync(MassCompensationSpec spec, string reason) =>
            _app.SendAsync<MassCompensateCommand, MassCompensationResult>(new MassCompensateCommand(_admin, spec, reason));

        private async Task<int> CountAsync(MassCompensationSpec spec) =>
            (await _app.QueryAsync<CountMassCompensationQuery, int>(new CountMassCompensationQuery(_admin, spec))).Value;

        private async Task RemindAsync()
        {
            (await _app.SendAsync<SendRemindersCommand, int>(new SendRemindersCommand())).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
        }

        private async Task<long> NewUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private async Task<long> TrialUserAsync(long telegramId)
        {
            long userId = await NewUserAsync(telegramId);
            (await _app.SendAsync(new StartTrialCommand(userId))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            return userId;
        }

        private async Task<long> PaidUserAsync(long telegramId)
        {
            long userId = await NewUserAsync(telegramId);
            PaymentLink link = (await _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, "basic_1m"))).Value;
            (await _app.SendAsync(new HandlePaymentWebhookCommand("fake", Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded)!))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            return userId;
        }

        private Task<Subscription> SubscriptionOfAsync(long userId) =>
            _app.WithDbAsync(db => db.Subscriptions.AsNoTracking().Include(s => s.Tariff).SingleAsync(s => s.UserId == userId));
    }
}
