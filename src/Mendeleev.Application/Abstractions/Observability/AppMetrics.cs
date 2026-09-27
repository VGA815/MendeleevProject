using System.Diagnostics.Metrics;

namespace Mendeleev.Application.Abstractions.Observability
{
    /// <summary>
    /// Service metrics from ТЗ 30, «Метрики». Exported on <c>/metrics</c>, which is closed to the outside.
    /// </summary>
    public static class AppMetrics
    {
        public const string MeterName = "Mendeleev";

        private static readonly Meter Meter = new(MeterName);

        /// <summary>NFR-01: from the payment webhook to working access on all nodes.</summary>
        public static readonly Histogram<double> PaymentToAccessSeconds =
            Meter.CreateHistogram<double>("payment_to_access_seconds", unit: "s");

        public static readonly Counter<long> PanelSyncFailures =
            Meter.CreateCounter<long>("panel_sync_failures_total");

        /// <summary>Tag <c>source</c>: panel or aggregator.</summary>
        public static readonly Counter<long> ReconcileDrift =
            Meter.CreateCounter<long>("reconcile_drift_total");

        /// <summary>Tags <c>source</c> (telegram, payments, remnawave) and <c>result</c> (accepted, rejected, error).</summary>
        public static readonly Counter<long> WebhookRequests =
            Meter.CreateCounter<long>("webhook_requests_total");

        /// <summary>Tags <c>status</c> and <c>provider</c>.</summary>
        public static readonly Counter<long> Payments =
            Meter.CreateCounter<long>("payments_total");

        /// <summary>NFR-05.</summary>
        public static readonly Histogram<double> BotUpdateDurationSeconds =
            Meter.CreateHistogram<double>("bot_update_duration_seconds", unit: "s");

        /// <summary>Tag <c>result</c>: sent, bot_blocked, failed.</summary>
        public static readonly Counter<long> BroadcastMessages =
            Meter.CreateCounter<long>("broadcast_messages_total");

        private static long _outboxPending;
        private static double _outboxOldestAgeSeconds;

        static AppMetrics()
        {
            Meter.CreateObservableGauge("outbox_pending", () => Interlocked.Read(ref _outboxPending));
            Meter.CreateObservableGauge("outbox_oldest_age_seconds", () => Volatile.Read(ref _outboxOldestAgeSeconds), unit: "s");
        }

        public static void SetOutboxState(long pending, double oldestAgeSeconds)
        {
            Interlocked.Exchange(ref _outboxPending, pending);
            Volatile.Write(ref _outboxOldestAgeSeconds, oldestAgeSeconds);
        }
    }
}
