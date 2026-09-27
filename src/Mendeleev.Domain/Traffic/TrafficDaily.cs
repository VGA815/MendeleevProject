namespace Mendeleev.Domain.Traffic
{
    /// <summary>
    /// Daily traffic of a subscription — the only traffic data in our database: no IPs, no destinations
    /// (NFR-11). <see cref="LifetimeBytes"/> is the panel counter at collection time; the day's volume is
    /// the difference with the previous day.
    /// </summary>
    public sealed class TrafficDaily
    {
        private TrafficDaily() { }

        public long SubscriptionId { get; private set; }

        public DateOnly Day { get; private set; }

        public long Bytes { get; private set; }

        public long LifetimeBytes { get; private set; }

        public static TrafficDaily Create(long subscriptionId, DateOnly day, long bytes, long lifetimeBytes) => new()
        {
            SubscriptionId = subscriptionId,
            Day = day,
            Bytes = Math.Max(0, bytes),
            LifetimeBytes = lifetimeBytes,
        };
    }
}
