namespace Mendeleev.Domain.Common
{
    /// <summary>
    /// A setting kept across restarts: configuration stays the default, the database holds what an admin chose (the
    /// active aggregator, FR-PAY-14) or what the service did (the last automatic node replacement, FR-NODE-18).
    /// </summary>
    public sealed class ServiceSetting
    {
        /// <summary>The aggregator new payments go to first; empty — the one from <c>Payments:ActiveProvider</c>.</summary>
        public const string ActivePaymentProvider = "payments.active_provider";

        /// <summary>When the node guard last switched a node to the reserve (FR-NODE-18): the cooldown counts from it.</summary>
        public const string NodeGuardLastReplacement = "nodeguard.last_replacement";

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
