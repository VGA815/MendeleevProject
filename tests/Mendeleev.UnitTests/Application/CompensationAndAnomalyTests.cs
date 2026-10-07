using Mendeleev.Application.Admin.Anomalies;
using Mendeleev.Application.Admin.Compensation;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Web.Bot.Handlers;
using static Mendeleev.UnitTests.TestData;

namespace Mendeleev.UnitTests.Application
{
    /// <summary>FR-SUB-16 (решение 07.10: активные сейчас или во время сбоя), FR-ADM-17.</summary>
    public class CompensationAndAnomalyTests
    {
        private static readonly DateTime Now = Msk(10, 8, 12);

        private static readonly MassCompensationSpec Outage = new(
            3, CompensationSegment.ActiveDuringOutage, IncludeTrial: false, OutageFrom: Msk(10, 7, 14), OutageTo: Msk(10, 7, 18));

        [Fact]
        public void ActiveNow_TakesOnlyAccessRightNow_AndTrialsOnlyWhenAsked()
        {
            var activeNow = new MassCompensationSpec(3, CompensationSegment.ActiveNow, IncludeTrial: false);
            Subscription paid = Subscription.CreatePaid(1, Basic1M(), 30, Guid.NewGuid(), Msk(10, 1, 10));
            Subscription trial = Subscription.StartTrial(2, Trial(), Msk(10, 7, 10));

            MassCompensationTargets.Matches(paid, activeNow, Now).ShouldBeTrue();
            MassCompensationTargets.Matches(trial, activeNow, Now).ShouldBeFalse();
            MassCompensationTargets.Matches(trial, activeNow with { IncludeTrial = true }, Now).ShouldBeTrue();
        }

        [Fact]
        public void DuringTheOutage_TakesThoseWhoseTermRanOutInIt_NotBeforeIt_NorNewcomers()
        {
            Subscription active = Subscription.CreatePaid(1, Basic1M(), 30, Guid.NewGuid(), Msk(10, 1, 10));

            Subscription endedInOutage = Subscription.CreatePaid(2, Basic1M(), 30, Guid.NewGuid(), Msk(9, 7, 15));
            endedInOutage.TryExpire(ExpiredReason.Time, Msk(10, 7, 16)).ShouldBeTrue();

            Subscription endedBefore = Subscription.CreatePaid(3, Basic1M(), 30, Guid.NewGuid(), Msk(9, 6, 10));
            endedBefore.TryExpire(ExpiredReason.Time, Msk(10, 6, 11)).ShouldBeTrue();

            Subscription newcomer = Subscription.CreatePaid(4, Basic1M(), 30, Guid.NewGuid(), Msk(10, 7, 19));

            Subscription refunded = Subscription.CreatePaid(5, Basic1M(), 30, Guid.NewGuid(), Msk(10, 1, 10));
            refunded.ApplyRefund(null, "refund", Msk(10, 7, 15)).ShouldBeTrue();

            MassCompensationTargets.Matches(active, Outage, Now).ShouldBeTrue();
            MassCompensationTargets.Matches(endedInOutage, Outage, Now).ShouldBeTrue();
            MassCompensationTargets.Matches(endedBefore, Outage, Now).ShouldBeFalse();
            MassCompensationTargets.Matches(newcomer, Outage, Now).ShouldBeFalse();
            MassCompensationTargets.Matches(refunded, Outage, Now).ShouldBeFalse();
        }

        [Fact]
        public void TheSpec_NeedsDaysAndAnOrderedWindow()
        {
            MassCompensationTargets.Validate(Outage).ShouldBeNull();
            MassCompensationTargets.Validate(Outage with { Days = 0 }).ShouldNotBeNull();
            MassCompensationTargets.Validate(Outage with { OutageTo = Outage.OutageFrom }).ShouldNotBeNull();
            MassCompensationTargets.Validate(Outage with { OutageFrom = null }).ShouldNotBeNull();
        }

        [Fact]
        public void AMassCompensation_CarriesItsReasonToTheMessage()
        {
            Subscription subscription = Subscription.CreatePaid(1, Basic1M(), 30, Guid.NewGuid(), Msk(10, 1, 10));
            subscription.ClearDomainEvents();

            subscription.ExtendByStaff(3, "mass:1", Now, "сбой нод 07.10").IsSuccess.ShouldBeTrue();

            SubscriptionChangedDomainEvent raised = subscription.DomainEvents.OfType<SubscriptionChangedDomainEvent>().Single();
            raised.Notice.ShouldBe(SubscriptionNotice.MassCompensated);
            raised.Detail.ShouldBe("сбой нод 07.10");
        }

        [Theory]
        [InlineData("07.10 14:00 07.10 18:30", "2026-10-07T11:00:00", "2026-10-07T15:30:00")]
        [InlineData("07.10.2026 9:05 – 08.10.2026 01:00", "2026-10-07T06:05:00", "2026-10-07T22:00:00")]
        [InlineData("31.12 22:00 31.12 23:00", "2025-12-31T19:00:00", "2025-12-31T20:00:00")]
        public void OutageWindow_IsMoscowTime(string text, string fromUtc, string toUtc)
        {
            StaffCompensationHandler.TryParseWindow(text, Now, out DateTime from, out DateTime to).ShouldBeTrue();

            from.ShouldBe(DateTime.Parse(fromUtc, System.Globalization.CultureInfo.InvariantCulture));
            to.ShouldBe(DateTime.Parse(toUtc, System.Globalization.CultureInfo.InvariantCulture));
        }

        [Theory]
        [InlineData("07.10 18:30 07.10 14:00")]
        [InlineData("07.10 14:00")]
        [InlineData("32.10 14:00 33.10 15:00")]
        [InlineData("вчера вечером")]
        public void OutageWindow_RefusesWhatItCannotRead(string text)
        {
            StaffCompensationHandler.TryParseWindow(text, Now, out _, out _).ShouldBeFalse();
        }

        [Theory]
        [InlineData(new long[] { 5 }, 5L)]
        [InlineData(new long[] { 9, 1, 5 }, 5L)]
        [InlineData(new long[] { 1, 2, 3, 10 }, 2L)]
        public void Median_OfADaysTraffic(long[] values, long median)
        {
            AnomalyDetector.Median(values).ShouldBe(median);
        }

        [Fact]
        public void TrafficThreshold_IsTenTimesTheMedian_ButNeverBelowTheFloor()
        {
            var options = new AnomalyOptions();
            AnomalyDetector.Median([]).ShouldBeNull();
            AnomalyDetector.Threshold(500L * 1024 * 1024, options).ShouldBe(5000L * 1024 * 1024);
            AnomalyDetector.Threshold(1024 * 1024, options).ShouldBe(options.TrafficFloorBytes);
        }
    }
}
