using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Web.Bot.Handlers;
using static Mendeleev.UnitTests.TestData;

namespace Mendeleev.UnitTests.Domain
{
    /// <summary>FR-PAY-16 (решение 07.10 — как в оферте, раздел 6), FR-PAY-14, FR-ADM-14.</summary>
    public class RefundAndTariffTests
    {
        private static readonly DateTime Now = Msk(10, 7, 12);

        [Fact]
        public void UnusedDays_EndTheAccessNow()
        {
            Subscription subscription = ActiveFor(days: 40);

            subscription.ApplyRefund(null, "refund", Now).ShouldBeTrue();

            subscription.ExpiresAt.ShouldBe(Now);
            subscription.Status.ShouldBe(SubscriptionStatus.Expired);
            subscription.ExpiredReason.ShouldBe(ExpiredReason.Refund);
            LastNotice(subscription).ShouldBe(SubscriptionNotice.RefundEnded);
        }

        [Fact]
        public void ErroneousPayment_TakesItsDays_AndKeepsTheRest()
        {
            Subscription subscription = ActiveFor(days: 60);

            subscription.ApplyRefund(30, "refund", Now).ShouldBeTrue();

            subscription.ExpiresAt.ShouldBe(Now.AddDays(30));
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            LastNotice(subscription).ShouldBe(SubscriptionNotice.RefundShortened);
        }

        [Fact]
        public void ErroneousPayment_NeverEndsBeforeNow()
        {
            Subscription subscription = ActiveFor(days: 10);

            subscription.ApplyRefund(30, "refund", Now).ShouldBeTrue();

            subscription.ExpiresAt.ShouldBe(Now);
            subscription.Status.ShouldBe(SubscriptionStatus.Expired);
            LastNotice(subscription).ShouldBe(SubscriptionNotice.RefundEnded);
        }

        [Fact]
        public void AnExpiredTerm_IsNeitherShortenedNorExtended()
        {
            Subscription subscription = ActiveFor(days: 1);
            subscription.TryExpire(ExpiredReason.Time, Now.AddDays(2)).ShouldBeTrue();
            DateTime expiredAt = subscription.ExpiresAt;

            subscription.ApplyRefund(30, "refund", Now.AddDays(3)).ShouldBeFalse();
            subscription.ApplyRefund(null, "refund", Now.AddDays(3)).ShouldBeFalse();

            subscription.ExpiresAt.ShouldBe(expiredAt);
            subscription.ExpiredReason.ShouldBe(ExpiredReason.Time);
        }

        [Fact]
        public void ABlockedUser_KeepsTheDisabledStatus_WithTheShorterTerm()
        {
            Subscription subscription = ActiveFor(days: 60);
            subscription.Disable(Now);

            subscription.ApplyRefund(30, "refund", Now).ShouldBeTrue();

            subscription.Status.ShouldBe(SubscriptionStatus.Disabled);
            subscription.ExpiresAt.ShouldBe(Now.AddDays(30));
        }

        [Fact]
        public void OnlyAPaidPayment_IsRefunded_Once()
        {
            var payment = Payment.Create(1, Basic1M(), "fake", Now);
            payment.Refund(Now).Error.ShouldBe(PaymentErrors.NotRefundable);

            payment.MarkPending("p-1", "https://pay", null, Now);
            payment.TryMarkSucceeded("p-1", Now).ShouldBeTrue();

            payment.Refund(Now).IsSuccess.ShouldBeTrue();
            payment.Status.ShouldBe(PaymentStatus.Refunded);
            payment.Refund(Now).Error.ShouldBe(PaymentErrors.AlreadyRefunded);
        }

        [Fact]
        public void APayment_GoesToTheNextAggregator_OnlyBeforeAnyHasIt()
        {
            var payment = Payment.Create(1, Basic1M(), "broken", Now);
            payment.MarkCreationFailed(Now);

            payment.SwitchProvider("fake", Now);
            payment.Provider.ShouldBe("fake");
            payment.Status.ShouldBe(PaymentStatus.Created);

            payment.MarkPending("p-1", "https://pay", null, Now);
            Should.Throw<InvalidOperationException>(() => payment.SwitchProvider("other", Now));
        }

        [Fact]
        public void Price_IsWholeRubles_AndTheTrialStaysFree()
        {
            Tariff basic = Basic1M();
            basic.ChangePrice(249m).IsSuccess.ShouldBeTrue();
            basic.Price.ShouldBe(249m);
            basic.ChangePrice(0m).Error.ShouldBe(TariffErrors.InvalidPrice);
            basic.ChangePrice(199.5m).Error.ShouldBe(TariffErrors.InvalidPrice);

            Trial().ChangePrice(100m).Error.ShouldBe(TariffErrors.TrialIsFree);
        }

        [Fact]
        public void Activity_ChangesOnlyOnce()
        {
            Tariff basic = Basic1M();
            basic.SetActive(true).Error.ShouldBe(TariffErrors.AlreadyActive);
            basic.SetActive(false).IsSuccess.ShouldBeTrue();
            basic.IsPurchasable.ShouldBeFalse();
            basic.SetActive(false).Error.ShouldBe(TariffErrors.AlreadyInactive);
        }

        [Theory]
        [InlineData("249", true, 249)]
        [InlineData("249₽", true, 249)]
        [InlineData("0", false, 0)]
        [InlineData("199.50", false, 0)]
        [InlineData("-5", false, 0)]
        public void TariffsCommand_TakesWholeRubles(string text, bool parsed, int rubles)
        {
            StaffPaymentsHandler.TryParseRubles(text, out decimal value).ShouldBe(parsed);
            if (parsed)
            {
                value.ShouldBe(rubles);
            }
        }

        private static Subscription ActiveFor(int days)
        {
            Subscription subscription = Subscription.CreatePaid(1, Basic1M(), days, Guid.NewGuid(), Now);
            subscription.ClearDomainEvents();
            return subscription;
        }

        private static SubscriptionNotice LastNotice(Subscription subscription) =>
            subscription.DomainEvents.OfType<SubscriptionChangedDomainEvent>().Last().Notice;
    }
}
