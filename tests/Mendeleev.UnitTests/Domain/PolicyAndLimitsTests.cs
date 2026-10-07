using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using static Mendeleev.UnitTests.TestData;

namespace Mendeleev.UnitTests.Domain
{
    public class PolicyAndLimitsTests
    {
        [Theory]
        [InlineData(StaffPermission.FindUsers, true)]
        [InlineData(StaffPermission.Compensate, true)]
        [InlineData(StaffPermission.ManageDevices, true)]
        [InlineData(StaffPermission.ReissueLink, true)]
        [InlineData(StaffPermission.BlockUsers, false)]
        [InlineData(StaffPermission.Broadcast, false)]
        [InlineData(StaffPermission.ViewStats, false)]
        [InlineData(StaffPermission.ManageStaff, false)]
        [InlineData(StaffPermission.ViewAudit, false)]
        [InlineData(StaffPermission.RecordManualPayments, false)]
        public void Support_HasOnlyTheSupportPermissions(StaffPermission permission, bool allowed)
        {
            // ТЗ 31, «Матрица ролей и прав».
            StaffPolicy.Allows(StaffRole.Support, permission).ShouldBe(allowed);
            StaffPolicy.Allows(StaffRole.Admin, permission).ShouldBeTrue();
            StaffPolicy.Allows(StaffRole.TechAdmin, permission).ShouldBeTrue();
        }

        [Fact]
        public void OnlyTechAdmin_ManagesTechAdmins()
        {
            StaffPolicy.CanManage(StaffRole.Admin, StaffRole.Support).ShouldBeTrue();
            StaffPolicy.CanManage(StaffRole.Admin, StaffRole.Admin).ShouldBeTrue();
            StaffPolicy.CanManage(StaffRole.Admin, StaffRole.TechAdmin).ShouldBeFalse();
            StaffPolicy.CanManage(StaffRole.TechAdmin, StaffRole.TechAdmin).ShouldBeTrue();
            StaffPolicy.CanManage(StaffRole.Support, StaffRole.Support).ShouldBeFalse();
        }

        [Fact]
        public void DeviceResets_TwoIn30Days_ThenTheDateWhenTheOlderOneExpires()
        {
            DateTime now = Msk(10, 20, 12);

            DeviceReset.NextUserResetAvailableAt([now.AddDays(-5)], now).ShouldBeNull();

            DateTime? next = DeviceReset.NextUserResetAvailableAt([now.AddDays(-5), now.AddDays(-20)], now);
            next.ShouldBe(now.AddDays(-20).AddDays(30));

            DeviceReset.NextUserResetAvailableAt([now.AddDays(-5), now.AddDays(-31)], now).ShouldBeNull();
        }

        [Theory]
        [InlineData(22, false)]
        [InlineData(23, true)]
        [InlineData(0, true)]
        [InlineData(8, true)]
        [InlineData(9, false)]
        [InlineData(14, false)]
        public void QuietHours_Are23To09Moscow(int hour, bool quiet)
        {
            // Reminders 3 and 1 day before are held until 09:00 (ТЗ 22, подтверждено 24.09).
            new SubscriptionOptions().IsQuietHour(hour).ShouldBe(quiet);
        }

        [Fact]
        public void Payment_IsAppliedOnlyOnce_AndLatePaymentStillCounts()
        {
            var payment = Payment.Create(1, Basic1M(), "fake", Msk(10, 1, 10));
            payment.MarkPending("p-1", "https://pay", null, Msk(10, 1, 10));
            payment.Cancel(Msk(10, 1, 11));

            payment.TryMarkSucceeded("p-1", Msk(10, 1, 12)).ShouldBeTrue();
            payment.TryMarkSucceeded("p-1", Msk(10, 1, 12)).ShouldBeFalse();
            payment.Status.ShouldBe(PaymentStatus.Succeeded);
        }

        [Fact]
        public void ManualPayment_IsAlreadyPaid_ForTheTariffsTerm_AndNeverForTheTrial()
        {
            // ТЗ 23: the money came outside the system; the record only grants the tariff, once.
            DateTime now = Msk(10, 5, 12);
            var payment = Payment.RecordManual(1, Basic1M(), 300m, now);

            payment.Provider.ShouldBe(Payment.ManualProvider);
            payment.Status.ShouldBe(PaymentStatus.Succeeded);
            payment.PaidAt.ShouldBe(now);
            payment.Amount.ShouldBe(300m);
            payment.DaysGranted.ShouldBe(30);
            payment.ProviderPaymentId.ShouldBeNull();
            payment.TryMarkSucceeded("late", now).ShouldBeFalse();

            Should.Throw<InvalidOperationException>(() => Payment.RecordManual(1, Trial(), 300m, now));
            Should.Throw<ArgumentOutOfRangeException>(() => Payment.RecordManual(1, Basic1M(), 0m, now));
        }

        [Fact]
        public void Payment_MatchesOnlyItsAmountAndCurrency()
        {
            var payment = Payment.Create(1, Basic1M(), "fake", Msk(10, 1, 10));

            payment.Matches(199m, "RUB").ShouldBeTrue();
            payment.Matches(199m, "rub").ShouldBeTrue();
            payment.Matches(null, null).ShouldBeTrue();
            payment.Matches(1m, "RUB").ShouldBeFalse();
            payment.Matches(199m, "USD").ShouldBeFalse();
        }

        [Fact]
        public void Payment_IsReusedOnlyWhilePendingAndFresh()
        {
            DateTime now = Msk(10, 1, 10);
            var payment = Payment.Create(1, Basic1M(), "fake", now);
            payment.IsReusable(payment.TariffId, null, TimeSpan.FromMinutes(60), now).ShouldBeFalse();

            payment.MarkPending("p-1", "https://pay", now.AddMinutes(30), now);
            payment.IsReusable(payment.TariffId, null, TimeSpan.FromMinutes(60), now.AddMinutes(10)).ShouldBeTrue();
            payment.IsReusable(payment.TariffId, null, TimeSpan.FromMinutes(60), now.AddMinutes(27)).ShouldBeFalse();
            payment.IsReusable(payment.TariffId + 1, null, TimeSpan.FromMinutes(60), now).ShouldBeFalse();

            // A code entered after the payment was created gets its own, discounted payment (FR-PAY-15).
            payment.IsReusable(payment.TariffId, 7, TimeSpan.FromMinutes(60), now.AddMinutes(10)).ShouldBeFalse();
        }

        [Fact]
        public void Payment_WithADiscount_KeepsThePromoCode_AndAnAmountWithinThePrice()
        {
            DateTime now = Msk(10, 1, 10);
            var payment = Payment.Create(1, Basic1M(), "fake", now, promoCodeId: 7, discountedAmount: 169m);
            payment.Amount.ShouldBe(169m);
            payment.PromoCodeId.ShouldBe(7);

            Should.Throw<ArgumentOutOfRangeException>(() => Payment.Create(1, Basic1M(), "fake", now, promoCodeId: 7, discountedAmount: 0m));
            Should.Throw<ArgumentOutOfRangeException>(() => Payment.Create(1, Basic1M(), "fake", now, promoCodeId: 7, discountedAmount: 200m));
            Should.Throw<ArgumentOutOfRangeException>(() => Payment.Create(1, Basic1M(), "fake", now, promoCodeId: null, discountedAmount: 169m));
        }
    }
}
