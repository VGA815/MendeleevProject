namespace Mendeleev.Domain.Devices
{
    /// <summary>
    /// A reset of all devices (HWID) of a user. Kept to enforce "not more than 2 user resets in 30 days"
    /// (ТЗ 24, «Устройства (HWID)») and for the anomaly report.
    /// </summary>
    public sealed class DeviceReset
    {
        public const int UserResetsPerWindow = 2;
        public static readonly TimeSpan Window = TimeSpan.FromDays(30);

        private DeviceReset() { }

        public long Id { get; private set; }

        public long UserId { get; private set; }

        public DeviceResetInitiator InitiatedBy { get; private set; }

        public long? StaffId { get; private set; }

        public int DevicesRemoved { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public static DeviceReset ByUser(long userId, int devicesRemoved, DateTime utcNow) => new()
        {
            UserId = userId,
            InitiatedBy = DeviceResetInitiator.User,
            DevicesRemoved = devicesRemoved,
            CreatedAt = utcNow,
        };

        public static DeviceReset ByStaff(long userId, long staffId, int devicesRemoved, DateTime utcNow) => new()
        {
            UserId = userId,
            InitiatedBy = DeviceResetInitiator.Staff,
            StaffId = staffId,
            DevicesRemoved = devicesRemoved,
            CreatedAt = utcNow,
        };

        /// <summary>
        /// When the user may reset again given their own resets inside the window, or null if now.
        /// </summary>
        public static DateTime? NextUserResetAvailableAt(IReadOnlyCollection<DateTime> userResetsInWindow, DateTime utcNow)
        {
            if (userResetsInWindow.Count < UserResetsPerWindow)
            {
                return null;
            }

            DateTime freesUpAt = userResetsInWindow
                .OrderByDescending(at => at)
                .ElementAt(UserResetsPerWindow - 1)
                .Add(Window);

            return freesUpAt > utcNow ? freesUpAt : null;
        }
    }
}
