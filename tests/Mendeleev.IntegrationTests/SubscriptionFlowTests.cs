using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Application.Panel.Reconciliation;
using Mendeleev.Application.Subscriptions.Maintenance;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mendeleev.IntegrationTests
{
    [Collection(nameof(PostgresCollection))]
    public sealed class SubscriptionFlowTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private TestApp _app = null!;

        public async Task InitializeAsync() => _app = await TestApp.CreateAsync(postgres);

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task ParallelStart_CreatesExactlyOneAccount()
        {
            // FR-ACC-01: repeated and parallel /start from one Telegram ID → one row.
            Result<TelegramUserState>[] results = await Task.WhenAll(Enumerable.Range(0, 10)
                .Select(_ => _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(1001))));

            results.ShouldAllBe(r => r.IsSuccess);
            results.Select(r => r.Value.UserId).Distinct().Count().ShouldBe(1);
            results.Count(r => r.Value.IsNew).ShouldBe(1);
            results[0].Value.TrialAvailable.ShouldBeTrue();
        }

        [Fact]
        public async Task Trial_EndToEnd_PanelGetsPseudonymOnly_AndUserGetsTheLink()
        {
            long userId = await NewUserAsync(2001);

            (await _app.SendAsync(new StartTrialCommand(userId))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();

            // FR-PNL-02: the panel knows the user only as u<id>.
            PanelUser panelUser = _app.Panel.Users.ShouldHaveSingleItem();
            panelUser.Username.ShouldBe($"u{userId}");
            panelUser.HwidDeviceLimit.ShouldBe(3);
            panelUser.TrafficLimitBytes.ShouldBe(10L * 1024 * 1024 * 1024);
            panelUser.InternalSquads.ShouldBe([TestApp.BasicSquad]);

            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.Status.ShouldBe(SubscriptionStatus.Trial);
            subscription.SyncState.ShouldBe(SyncState.Synced);
            subscription.PanelUserId.ShouldBe(panelUser.Id);
            subscription.SubscriptionUrl.ShouldBe(panelUser.SubscriptionUrl);
            subscription.ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(2));

            // FR-BOT-04: the link arrives once access really works.
            (long chatId, NotificationMessage message) = _app.Messenger.Notifications.ShouldHaveSingleItem();
            chatId.ShouldBe(2001);
            message.Kind.ShouldBe(NotificationKind.AccessIssued);
            message.SubscriptionUrl.ShouldBe(panelUser.SubscriptionUrl);
        }

        [Fact]
        public async Task Trial_ParallelClicks_GiveOneTrial()
        {
            // FR-SUB-02: double or parallel click does not produce a second trial.
            long userId = await NewUserAsync(2002);

            Result[] results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _app.SendAsync(new StartTrialCommand(userId))));

            results.Count(r => r.IsSuccess).ShouldBe(1);
            results.Where(r => r.IsFailure).ShouldAllBe(r => r.Error == SubscriptionErrors.TrialAlreadyUsed);
            (await _app.WithDbAsync(db => db.Subscriptions.CountAsync(s => s.UserId == userId))).ShouldBe(1);

            (await _app.SendAsync(new StartTrialCommand(userId))).Error.ShouldBe(SubscriptionErrors.TrialAlreadyUsed);
        }

        [Fact]
        public async Task SupportCompensation_IsCappedPerActionAndPerMonth_AdminIsNot()
        {
            long userId = await NewTrialUserAsync(2003);
            long support = await _app.AddStaffAsync(9001, StaffRole.Support);
            long admin = await _app.AddStaffAsync(9002, StaffRole.Admin);

            // 7 days per action, 14 per user in 30 days (ТЗ 22, подтверждено 24.09).
            (await Compensate(support, userId, 8)).Error.Code.ShouldBe("Subscriptions.CompensationLimitExceeded");
            (await Compensate(support, userId, 7)).IsSuccess.ShouldBeTrue();
            (await Compensate(support, userId, 7)).IsSuccess.ShouldBeTrue();
            (await Compensate(support, userId, 1)).Error.Code.ShouldBe("Subscriptions.CompensationLimitExceeded");
            (await Compensate(admin, userId, 30)).IsSuccess.ShouldBeTrue();

            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(2 + 7 + 7 + 30));

            // FR-ADM-07: every compensation is in the audit log with the reason.
            int audited = await _app.WithDbAsync(db => db.AuditLog.CountAsync(a => a.TargetUserId == userId && a.Action == "subscription.extend"));
            audited.ShouldBe(3);
        }

        [Fact]
        public async Task AuditLog_CannotBeChangedOrDeleted()
        {
            long userId = await NewTrialUserAsync(2004);
            long admin = await _app.AddStaffAsync(9003, StaffRole.Admin);
            (await Compensate(admin, userId, 1)).IsSuccess.ShouldBeTrue();

            await Should.ThrowAsync<PostgresException>(() => _app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE audit_log SET action = 'forged'")));
            await Should.ThrowAsync<PostgresException>(() => _app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_log")));
        }

        [Fact]
        public async Task Block_SwitchesThePanelUserOff_Unblock_SwitchesItBack()
        {
            // FR-ACC-03, FR-SUB-13.
            long userId = await NewTrialUserAsync(2005);
            long admin = await _app.AddStaffAsync(9004, StaffRole.Admin);
            long support = await _app.AddStaffAsync(9005, StaffRole.Support);

            (await _app.SendAsync(new BlockUserCommand(support, userId, "abuse"))).Error.ShouldBe(StaffErrors.NotAllowed);

            (await _app.SendAsync(new BlockUserCommand(admin, userId, "abuse"))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            _app.Panel.Users.Single().Status.ShouldBe(PanelUserStatus.Disabled);
            (await SubscriptionOfAsync(userId)).Status.ShouldBe(SubscriptionStatus.Disabled);

            (await _app.SendAsync(new StartTrialCommand(userId))).IsFailure.ShouldBeTrue();

            (await _app.SendAsync(new UnblockUserCommand(admin, userId))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            _app.Panel.Users.Single().Status.ShouldBe(PanelUserStatus.Active);
            (await SubscriptionOfAsync(userId)).Status.ShouldBe(SubscriptionStatus.Trial);
        }

        [Fact]
        public async Task ManualEditInPanel_IsRolledBackByReconciliation()
        {
            // FR-PNL-05, FR-PNL-06.
            long userId = await NewTrialUserAsync(2006);
            PanelUser panelUser = _app.Panel.Users.Single();
            _app.Panel.Tamper(panelUser.Id, u => u with { ExpireAt = u.ExpireAt.AddDays(100), HwidDeviceLimit = 99 });

            PanelReconciliationSummary summary = (await _app.SendAsync<ReconcilePanelCommand, PanelReconciliationSummary>(new ReconcilePanelCommand())).Value;
            await _app.ProcessOutboxAsync();

            summary.Drifted.ShouldBe(1);
            PanelUser fixedUser = _app.Panel.Users.Single();
            fixedUser.ExpireAt.ShouldBe((await SubscriptionOfAsync(userId)).ExpiresAt);
            fixedUser.HwidDeviceLimit.ShouldBe(3);
            _app.Alerts.Raised.ShouldContain(a => a.Key == "panel-reconcile");
        }

        [Fact]
        public async Task Expiry_Archive_AndReturn_FollowTheLifecycle()
        {
            long userId = await NewTrialUserAsync(2007);
            string firstLink = (await SubscriptionOfAsync(userId)).PanelShortUuid;

            // FR-SUB-06: at the end of the term the subscription expires and the user is told.
            _app.Clock.Advance(TimeSpan.FromDays(2).Add(TimeSpan.FromMinutes(1)));
            (await _app.SendAsync<ExpireSubscriptionsCommand, int>(new ExpireSubscriptionsCommand())).Value.ShouldBe(1);
            await _app.ProcessOutboxAsync();
            (await SubscriptionOfAsync(userId)).Status.ShouldBe(SubscriptionStatus.Expired);
            _app.Messenger.Notifications.ShouldContain(n => n.Message.Kind == NotificationKind.Expired);

            // FR-SUB-09: 30 days later the panel user is deleted and the subscription is archived.
            _app.Clock.Advance(TimeSpan.FromDays(30));
            (await _app.SendAsync<ArchiveExpiredCommand, int>(new ArchiveExpiredCommand())).Value.ShouldBe(1);
            await _app.ProcessOutboxAsync();
            _app.Panel.Users.ShouldBeEmpty();
            Subscription archived = await SubscriptionOfAsync(userId);
            archived.Status.ShouldBe(SubscriptionStatus.Archived);
            archived.PanelUserId.ShouldBeNull();

            // Staff compensation after the archive brings a new link.
            long admin = await _app.AddStaffAsync(9006, StaffRole.Admin);
            (await Compensate(admin, userId, 5)).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            Subscription revived = await SubscriptionOfAsync(userId);
            revived.PanelShortUuid.ShouldNotBe(firstLink);
            _app.Panel.Users.ShouldHaveSingleItem().ShortUuid.ShouldBe(revived.PanelShortUuid);
        }

        [Fact]
        public async Task PanelDown_ChangeIsKept_AndSyncedOnceItIsBack()
        {
            // FR-PNL-04, NFR-10: nothing is lost while the panel is unreachable.
            long userId = await NewUserAsync(2008);
            _app.Panel.IsDown = true;

            (await _app.SendAsync(new StartTrialCommand(userId))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            (await SubscriptionOfAsync(userId)).SyncState.ShouldBe(SyncState.Pending);
            _app.Messenger.Notifications.ShouldBeEmpty();

            _app.Panel.IsDown = false;
            _app.Clock.Advance(TimeSpan.FromSeconds(3));
            await _app.ProcessOutboxAsync();

            (await SubscriptionOfAsync(userId)).SyncState.ShouldBe(SyncState.Synced);
            _app.Messenger.Notifications.ShouldHaveSingleItem().Message.Kind.ShouldBe(NotificationKind.AccessIssued);
        }

        private Task<Result<DateTime>> Compensate(long staffId, long userId, int days) =>
            _app.SendAsync<CompensateCommand, DateTime>(new CompensateCommand(staffId, userId, days, "сбой ноды"));

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
