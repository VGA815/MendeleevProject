using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using static Mendeleev.UnitTests.TestData;

namespace Mendeleev.UnitTests.Domain
{
    /// <summary>ТЗ 22, «Правило продления»: <c>новое окончание = max(сейчас, текущее окончание) + период</c>.</summary>
    public class SubscriptionRenewalTests
    {
        private static readonly Guid PaymentId = Guid.NewGuid();

        [Fact]
        public void Payment_BeforeExpiry_AddsToCurrentExpiry()
        {
            // Активна до 10.10 12:00, 05.10 оплачен 1 месяц → до 09.11 12:00.
            Subscription subscription = Active(until: Msk(10, 10, 12));

            subscription.ApplyPayment(Basic1M(), 30, PaymentId, Msk(10, 5, 9));

            subscription.ExpiresAt.ShouldBe(Msk(11, 9, 12));
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
        }

        [Fact]
        public void Payment_AfterExpiry_CountsFromNow_AndKeepsTheLink()
        {
            // Истекла 01.10, оплата 05.10 в 15:00 → до 04.11 15:00, ссылка прежняя.
            Subscription subscription = Active(until: Msk(10, 1, 0));
            subscription.TryExpire(ExpiredReason.Time, Msk(10, 1, 0, 1)).ShouldBeTrue();
            string link = subscription.PanelShortUuid;

            subscription.ApplyPayment(Basic1M(), 30, PaymentId, Msk(10, 5, 15));

            subscription.ExpiresAt.ShouldBe(Msk(11, 4, 15));
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.PanelShortUuid.ShouldBe(link);
            subscription.ExpiredReason.ShouldBeNull();
        }

        [Fact]
        public void Payment_DuringTrial_KeepsTrialDays_AndSwitchesToPaidTariff()
        {
            // Триал до 06.10 10:00, оплата 1 месяца 05.10 → до 05.11 10:00, лимит трафика снят.
            Tariff trial = Trial();
            Subscription subscription = Subscription.StartTrial(1, trial, Msk(10, 4, 10));
            subscription.ExpiresAt.ShouldBe(Msk(10, 6, 10));

            Tariff basic = Basic1M();
            subscription.ApplyPayment(basic, 30, PaymentId, Msk(10, 5, 12));

            subscription.ExpiresAt.ShouldBe(Msk(11, 5, 10));
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.Tariff.TrafficLimitBytes.ShouldBeNull();
        }

        [Fact]
        public void Payment_AfterArchive_IssuesNewLink()
        {
            // Архив (удалена в панели 31.10), оплата 02.11 → новая подписка до 02.12, новая ссылка.
            Subscription subscription = Active(until: Msk(10, 1, 0));
            subscription.TryExpire(ExpiredReason.Time, Msk(10, 1, 1));
            subscription.MarkSynced(42, Guid.NewGuid(), "https://sub/old", Msk(10, 1, 1));
            subscription.TryArchive(30, Msk(10, 31, 1)).ShouldBeTrue();
            subscription.MarkPanelUserDeleted(Msk(10, 31, 1));
            string oldLink = subscription.PanelShortUuid;
            subscription.ClearDomainEvents();

            subscription.ApplyPayment(Basic1M(), 30, PaymentId, Msk(11, 2, 12));

            subscription.ExpiresAt.ShouldBe(Msk(12, 2, 12));
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.PanelShortUuid.ShouldNotBe(oldLink);
            subscription.SubscriptionUrl.ShouldBeNull();
            LastNotice(subscription).ShouldBe(SubscriptionNotice.AccessIssued);
        }

        [Fact]
        public void TwoPayments_ExtendTwice()
        {
            Subscription subscription = Active(until: Msk(10, 10, 12));

            subscription.ApplyPayment(Basic1M(), 30, Guid.NewGuid(), Msk(10, 5, 9));
            subscription.ApplyPayment(Basic1M(), 30, Guid.NewGuid(), Msk(10, 5, 9));

            subscription.ExpiresAt.ShouldBe(Msk(10, 10, 12).AddDays(60));
        }

        [Fact]
        public void Payment_ByBlockedUser_ExtendsButStaysDisabled()
        {
            Subscription subscription = Active(until: Msk(10, 10, 12));
            subscription.Disable(Msk(10, 5, 0));

            subscription.ApplyPayment(Basic1M(), 30, PaymentId, Msk(10, 5, 9));

            subscription.Status.ShouldBe(SubscriptionStatus.Disabled);
            subscription.ExpiresAt.ShouldBe(Msk(11, 9, 12));
        }

        [Fact]
        public void Expire_OnlyWhenTermIsOver()
        {
            Subscription subscription = Active(until: Msk(10, 10, 12));

            subscription.TryExpire(ExpiredReason.Time, Msk(10, 10, 11)).ShouldBeFalse();
            subscription.TryExpire(ExpiredReason.Time, Msk(10, 10, 12)).ShouldBeTrue();
            subscription.Status.ShouldBe(SubscriptionStatus.Expired);
            LastNotice(subscription).ShouldBe(SubscriptionNotice.Expired);
        }

        [Fact]
        public void TrialTraffic_EndsTrialBeforeTerm()
        {
            Subscription subscription = Subscription.StartTrial(1, Trial(), Msk(10, 4, 10));

            subscription.TryExpire(ExpiredReason.Traffic, Msk(10, 4, 12)).ShouldBeTrue();

            subscription.Status.ShouldBe(SubscriptionStatus.Expired);
            subscription.ExpiredReason.ShouldBe(ExpiredReason.Traffic);
            LastNotice(subscription).ShouldBe(SubscriptionNotice.TrafficExhausted);
        }

        [Fact]
        public void Unblock_ReturnsActiveOrExpiredByTerm()
        {
            Subscription stillValid = Active(until: Msk(10, 10, 12));
            stillValid.Disable(Msk(10, 5, 0));
            stillValid.Enable(Msk(10, 6, 0));
            stillValid.Status.ShouldBe(SubscriptionStatus.Active);

            Subscription runOut = Active(until: Msk(10, 10, 12));
            runOut.Disable(Msk(10, 5, 0));
            runOut.Enable(Msk(10, 11, 0));
            runOut.Status.ShouldBe(SubscriptionStatus.Expired);
        }

        [Fact]
        public void StaffCompensation_RevivesExpiredSubscription()
        {
            Subscription subscription = Active(until: Msk(10, 1, 0));
            subscription.TryExpire(ExpiredReason.Time, Msk(10, 1, 1));

            subscription.ExtendByStaff(3, "staff:1", Msk(10, 2, 12)).IsSuccess.ShouldBeTrue();

            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(Msk(10, 5, 12));
            LastNotice(subscription).ShouldBe(SubscriptionNotice.Compensated);
        }

        [Fact]
        public void StaffCompensation_RejectsNonPositiveDays()
        {
            Subscription subscription = Active(until: Msk(10, 10, 12));

            subscription.ExtendByStaff(0, "staff:1", Msk(10, 5, 0)).IsFailure.ShouldBeTrue();
        }

        [Fact]
        public void EveryChange_RequestsPanelSync()
        {
            Subscription subscription = Active(until: Msk(10, 10, 12));
            subscription.MarkSynced(7, Guid.NewGuid(), "https://sub/x", Msk(10, 1, 0));
            subscription.ClearDomainEvents();

            subscription.Disable(Msk(10, 5, 0));

            subscription.SyncState.ShouldBe(SyncState.Pending);
            subscription.DomainEvents.OfType<SubscriptionChangedDomainEvent>().ShouldHaveSingleItem().UserId.ShouldBe(1);
        }

        private static Subscription Active(DateTime until)
        {
            Tariff basic = Basic1M();
            Subscription subscription = Subscription.CreatePaid(1, basic, 1, Guid.NewGuid(), until.AddDays(-1));
            subscription.ExpiresAt.ShouldBe(until);
            return subscription;
        }

        private static SubscriptionNotice LastNotice(Subscription subscription) =>
            subscription.DomainEvents.OfType<SubscriptionChangedDomainEvent>().Last().Notice;
    }
}
