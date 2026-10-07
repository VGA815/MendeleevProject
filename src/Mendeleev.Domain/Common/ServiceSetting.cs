namespace Mendeleev.Domain.Common
{
    /// <summary>
    /// A setting staff change from the bot at runtime, kept across restarts: configuration stays the default,
    /// the database holds what an admin chose. Today only the active aggregator (FR-PAY-14).
    /// </summary>
    public sealed class ServiceSetting
    {
        /// <summary>The aggregator new payments go to first; empty — the one from <c>Payments:ActiveProvider</c>.</summary>
        public const string ActivePaymentProvider = "payments.active_provider";

        private ServiceSetting() { }

        public string Key { get; private set; } = string.Empty;

        public string Value { get; private set; } = string.Empty;

        public long? UpdatedByStaffId { get; private set; }

        public DateTime UpdatedAt { get; private set; }

        public static ServiceSetting Create(string key, string value, long? staffId, DateTime utcNow)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return new ServiceSetting { Key = key, Value = value, UpdatedByStaffId = staffId, UpdatedAt = utcNow };
        }

        public void Set(string value, long? staffId, DateTime utcNow)
        {
            Value = value;
            UpdatedByStaffId = staffId;
            UpdatedAt = utcNow;
        }
    }
}
