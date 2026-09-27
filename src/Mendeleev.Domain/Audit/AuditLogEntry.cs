namespace Mendeleev.Domain.Audit
{
    /// <summary>
    /// Append-only journal of staff and system actions on users, including card views (FR-ADM-07,
    /// FR-ADM-13). The database refuses UPDATE and DELETE of recent rows, whatever the application does.
    /// </summary>
    public sealed class AuditLogEntry
    {
        private AuditLogEntry() { }

        public long Id { get; private set; }

        public AuditActorType ActorType { get; private set; }

        public long? StaffId { get; private set; }

        /// <summary>See <see cref="AuditActions"/>.</summary>
        public string Action { get; private set; } = string.Empty;

        public long? TargetUserId { get; private set; }

        /// <summary>JSON: parameters, reason, before and after.</summary>
        public string? Details { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public static AuditLogEntry ByStaff(long staffId, string action, long? targetUserId, string? details, DateTime utcNow) => new()
        {
            ActorType = AuditActorType.Staff,
            StaffId = staffId,
            Action = action,
            TargetUserId = targetUserId,
            Details = details,
            CreatedAt = utcNow,
        };

        public static AuditLogEntry BySystem(string action, long? targetUserId, string? details, DateTime utcNow) => new()
        {
            ActorType = AuditActorType.System,
            Action = action,
            TargetUserId = targetUserId,
            Details = details,
            CreatedAt = utcNow,
        };
    }
}
