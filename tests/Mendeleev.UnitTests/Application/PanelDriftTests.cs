using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Panel;
using Mendeleev.Domain.Subscriptions;
using static Mendeleev.UnitTests.TestData;

namespace Mendeleev.UnitTests.Application
{
    /// <summary>ТЗ 24, «Синхронизация с панелью»: what counts as a difference between the panel and our state.</summary>
    public class PanelDriftTests
    {
        private static readonly DateTime Until = Msk(10, 10, 12);

        [Fact]
        public void LapsedTerm_ThePanelExpiringTheUserByItself_IsNotDrift()
        {
            // The term is over, the expiry job has not run yet. The panel cannot take a past date: comparing
            // it pushed the same update back and forth until the job ran (E2E 03.10.2026, scenario 4).
            Subscription subscription = SyncedActive();
            DateTime now = Until.AddSeconds(30);

            PanelUserSpecFactory.HasLapsed(subscription, now).ShouldBeTrue();
            PanelUserSpecFactory.FindDrift(subscription, PanelUserOf(subscription, PanelUserStatus.Active, Until), now).ShouldBeNull();
            PanelUserSpecFactory.FindDrift(subscription, PanelUserOf(subscription, PanelUserStatus.Expired, Until), now).ShouldBeNull();
            PanelUserSpecFactory.FindDrift(subscription, PanelUserOf(subscription, PanelUserStatus.Disabled, Until.AddDays(90)), now).ShouldBeNull();
        }

        [Fact]
        public void LapsedTerm_ThePanelStillLettingTheUserThrough_IsDrift()
        {
            // Our term was moved back, or the user was extended by hand in the panel (FR-PNL-06).
            Subscription subscription = SyncedActive();

            PanelUserSpecFactory.FindDrift(subscription, PanelUserOf(subscription, PanelUserStatus.Active, Until.AddDays(90)), Until.AddSeconds(30))
                .ShouldNotBeNull().ShouldContain("while expired");
        }

        [Fact]
        public void RunningTerm_AnotherDate_IsStillDrift()
        {
            Subscription subscription = SyncedActive();

            PanelUserSpecFactory.FindDrift(subscription, PanelUserOf(subscription, PanelUserStatus.Active, Until.AddDays(90)), Until.AddDays(-1))
                .ShouldNotBeNull().ShouldStartWith("expireAt");
            PanelUserSpecFactory.FindDrift(subscription, PanelUserOf(subscription, PanelUserStatus.Active, Until), Until.AddDays(-1))
                .ShouldBeNull();
        }

        [Fact]
        public void AccessPending_UntilTheChangeReachesThePanel()
        {
            // ТЗ 23, «Панель недоступна после оплаты»: «Доступ активируется в течение нескольких минут».
            Subscription subscription = Subscription.CreatePaid(1, Basic1M(), 30, Guid.NewGuid(), Until.AddDays(-30));
            subscription.AccessPending.ShouldBeTrue();

            subscription.MarkSynced(7, subscription.PanelVlessUuid, "https://sub.test/abc", Until.AddDays(-30));
            subscription.AccessPending.ShouldBeFalse();

            // A renewal keeps the link, but the panel has not got the new term yet.
            subscription.ApplyPayment(Basic1M(), 30, Guid.NewGuid(), Until.AddDays(-1));
            subscription.AccessPending.ShouldBeTrue();

            subscription.Disable(Until.AddDays(-1));
            subscription.AccessPending.ShouldBeFalse();
        }

        private static Subscription SyncedActive()
        {
            Subscription subscription = Subscription.CreatePaid(1, Basic1M(), 30, Guid.NewGuid(), Until.AddDays(-30));
            subscription.MarkSynced(7, subscription.PanelVlessUuid, "https://sub.test/abc", Until.AddDays(-30));
            return subscription;
        }

        private static PanelUser PanelUserOf(Subscription subscription, PanelUserStatus status, DateTime expireAt) => new(
            7, "u01", subscription.PanelShortUuid, subscription.PanelVlessUuid, status, expireAt,
            0, subscription.Tariff.DeviceLimit, subscription.Tariff.PanelSquads, "https://sub.test/abc", 0, 0, null);
    }
}
