namespace Mendeleev.Application.Configuration
{
    /// <summary>
    /// Thresholds of the daily anomaly report and the flags in the user card (FR-ADM-17; ТЗ 28, «Аномалии и абуз»,
    /// пороги подтверждены 24.09).
    /// </summary>
    public sealed class AnomalyOptions
    {
        public const string SectionName = "Anomalies";

        /// <summary>Device resets within 30 days that make a user stand out.</summary>
        public int ResetsIn30Days { get; init; } = 2;

        /// <summary>A day's traffic this many times the median of all subscriptions that day.</summary>
        public int TrafficTimesMedian { get; init; } = 10;

        /// <summary>
        /// Below this a day is never an anomaly, whatever the median: with few users and a low median, ordinary
        /// days would fill the report.
        /// </summary>
        public long TrafficFloorBytes { get; init; } = 1L * 1024 * 1024 * 1024;

        /// <summary>Invoices left unpaid or refused within a day that count as a series (ТЗ 28: «серии неудачных платежей»).</summary>
        public int FailedPaymentsPerDay { get; init; } = 3;

        /// <summary>Users shown per kind of anomaly in the report; the rest are counted.</summary>
        public int ReportListLimit { get; init; } = 10;
    }
}
