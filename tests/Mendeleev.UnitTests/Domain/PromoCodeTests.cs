using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.SharedKernel;
using static Mendeleev.UnitTests.TestData;

namespace Mendeleev.UnitTests.Domain
{
    /// <summary>ТЗ 22, «Промокоды (этап 1.5)»; FR-SUB-15, FR-PAY-15.</summary>
    public class PromoCodeTests
    {
        private static readonly DateTime Now = Msk(10, 7, 12);

        [Theory]
        [InlineData("autumn", "AUTUMN")]
        [InlineData("  Gift_7-x ", "GIFT_7-X")]
        [InlineData("ab", null)]
        [InlineData("осень", null)]
        [InlineData("TWO WORDS", null)]
        [InlineData("A23456789012345678901234567890123", null)]
        [InlineData("", null)]
        public void Code_IsLatinUpperCase_ThatFitsADeepLink(string input, string? normalized)
        {
            PromoCode.Normalize(input).ShouldBe(normalized);
        }

        [Fact]
        public void Create_ChecksTheValues()
        {
            Create("OKK", PromoType.DiscountPercent, 20).IsSuccess.ShouldBeTrue();
            Create("OKK", PromoType.DiscountPercent, 0).Error.ShouldBe(PromoErrors.InvalidDiscount);
            Create("OKK", PromoType.DiscountPercent, 100).Error.ShouldBe(PromoErrors.InvalidDiscount);
            Create("OKK", PromoType.BonusDays, 366).Error.ShouldBe(PromoErrors.InvalidBonusDays);
            Create("x", PromoType.BonusDays, 7).Error.ShouldBe(PromoErrors.InvalidCode);
            Create("OKK", PromoType.BonusDays, 7, maxUses: 0).Error.ShouldBe(PromoErrors.InvalidMaxUses);
            Create("OKK", PromoType.BonusDays, 7, validTo: Now).Error.ShouldBe(PromoErrors.InvalidWindow);
            Create("OKK", PromoType.BonusDays, 7, validFrom: Now.AddDays(2), validTo: Now.AddDays(1)).Error.ShouldBe(PromoErrors.InvalidWindow);
        }

        [Fact]
        public void Usable_OnlyInsideTheWindow_WhileActive_AndNotUsedUp()
        {
            PromoCode promo = Create("WINDOW", PromoType.BonusDays, 7, maxUses: 1, validFrom: Now.AddDays(1), validTo: Now.AddDays(3)).Value;

            promo.CheckUsable(Now).Error.ShouldBe(PromoErrors.NotStarted);
            promo.CheckUsable(Now.AddDays(2)).IsSuccess.ShouldBeTrue();
            promo.CheckUsable(Now.AddDays(3)).Error.ShouldBe(PromoErrors.Expired);

            promo.RegisterUse(Now.AddDays(2));
            promo.CheckUsable(Now.AddDays(2)).Error.ShouldBe(PromoErrors.Exhausted);

            PromoCode other = Create("OTHER", PromoType.BonusDays, 7).Value;
            other.Deactivate(Now).IsSuccess.ShouldBeTrue();
            other.CheckUsable(Now).Error.ShouldBe(PromoErrors.Inactive);
            other.Deactivate(Now).Error.ShouldBe(PromoErrors.AlreadyDeactivated);
        }

        [Theory]
        [InlineData(15, 199, 169)]
        [InlineData(20, 549, 439)]
        [InlineData(50, 199, 100)]
        [InlineData(99, 199, 2)]
        [InlineData(99, 50, 1)]
        public void Discount_IsInWholeRubles_AndNeverZero(int percent, int price, int expected)
        {
            PromoCode promo = Create("SALE", PromoType.DiscountPercent, percent).Value;

            promo.Discount(price).ShouldBe(expected);
        }

        [Fact]
        public void BonusDays_OnTheTrial_GiveThePaidTariff_KeepingTheTrialDays()
        {
            Subscription subscription = Subscription.StartTrial(1, Trial(), Msk(10, 6, 10));
            subscription.ClearDomainEvents();

            subscription.ApplyBonusDays(Basic1M(), 7, "promo:1", Msk(10, 7, 10));

            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(Msk(10, 15, 10)); // 08.10 10:00 + 7 days
            subscription.Tariff.Tier.ShouldBe(TariffTier.Basic);
            subscription.Tariff.TrafficLimitBytes.ShouldBeNull();
            LastNotice(subscription).ShouldBe(SubscriptionNotice.PromoBonus);
        }

        [Fact]
        public void BonusDays_AfterExpiry_CountFromNow_WithTheSameLink()
        {
            Subscription subscription = Subscription.CreatePaid(1, Basic1M(), 30, Guid.NewGuid(), Msk(9, 1, 10));
            subscription.TryExpire(ExpiredReason.Time, Msk(10, 1, 10)).ShouldBeTrue();
            string link = subscription.PanelShortUuid;

            subscription.ApplyBonusDays(Basic1M(), 7, "promo:1", Msk(10, 7, 12));

            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(Msk(10, 14, 12));
            subscription.PanelShortUuid.ShouldBe(link);
        }

        [Fact]
        public void BonusDays_AfterArchive_IssueANewLink()
        {
            Subscription subscription = Subscription.CreatePaid(1, Basic1M(), 30, Guid.NewGuid(), Msk(8, 1, 10));
            subscription.TryExpire(ExpiredReason.Time, Msk(8, 31, 11));
            subscription.MarkSynced(42, Guid.NewGuid(), "https://sub/old", Msk(8, 31, 11));
            subscription.TryArchive(30, Msk(10, 1, 11)).ShouldBeTrue();
            subscription.MarkPanelUserDeleted(Msk(10, 1, 11));
            subscription.ClearDomainEvents();

            subscription.ApplyBonusDays(Basic1M(), 7, "promo:1", Msk(10, 7, 12));

            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(Msk(10, 14, 12));
            LastNotice(subscription).ShouldBe(SubscriptionNotice.AccessIssued);
        }

        [Fact]
        public void BonusDays_WithoutASubscription_StartAnActiveOne()
        {
            Subscription subscription = Subscription.CreateFromPromo(1, Basic1M(), 7, "promo:1", Msk(10, 7, 12));

            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(Msk(10, 14, 12));
            LastNotice(subscription).ShouldBe(SubscriptionNotice.AccessIssued);
            Should.Throw<ArgumentException>(() => Subscription.CreateFromPromo(1, Trial(), 7, "promo:1", Msk(10, 7, 12)));
        }

        private static Result<PromoCode> Create(
            string code,
            PromoType type,
            int value,
            int? maxUses = null,
            DateTime? validFrom = null,
            DateTime? validTo = null) =>
            PromoCode.Create(code, type, value, maxUses, validFrom, validTo, createdByStaffId: 1, Now);

        private static SubscriptionNotice LastNotice(Subscription subscription) =>
            subscription.DomainEvents.OfType<SubscriptionChangedDomainEvent>().Last().Notice;
    }
}
