namespace Mendeleev.Domain.Staff
{
    /// <summary>
    /// A staff member working through the bot (ТЗ 28). The role is re-read on every command, so a
    /// deactivated member loses access with the next command (FR-ADM-10).
    /// </summary>
    public sealed class StaffMember
    {
        private StaffMember() { }

        public long Id { get; private set; }

        public long TelegramId { get; private set; }

        public StaffRole Role { get; private set; }

        /// <summary>Internal name for the audit log.</summary>
        public string DisplayName { get; private set; } = string.Empty;

        public bool IsActive { get; private set; }

        public long? CreatedByStaffId { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public DateTime UpdatedAt { get; private set; }

        public static StaffMember Create(long telegramId, StaffRole role, string displayName, long? createdByStaffId, DateTime utcNow)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

            return new StaffMember
            {
                TelegramId = telegramId,
                Role = role,
                DisplayName = displayName.Trim(),
                IsActive = true,
                CreatedByStaffId = createdByStaffId,
                CreatedAt = utcNow,
                UpdatedAt = utcNow,
            };
        }

        public void ChangeRole(StaffRole role, DateTime utcNow)
        {
            Role = role;
            UpdatedAt = utcNow;
        }

        public void SetActive(bool isActive, DateTime utcNow)
        {
            IsActive = isActive;
            UpdatedAt = utcNow;
        }
    }
}
