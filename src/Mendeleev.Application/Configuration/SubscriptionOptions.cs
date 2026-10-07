namespace Mendeleev.Application.Configuration
{
    /// <summary>
    /// Business parameters that change without a code change (ТЗ 22, «Настраиваемые параметры»).
    /// Tariffs themselves live in the database.
    /// </summary>
    public sealed class SubscriptionOptions
    {
        public const string SectionName = "Subscriptions";

        /// <summary>How long the account and the link live after expiry (FR-SUB-08).</summary>
        public int RetentionAfterExpiryDays { get; init; } = 30;

        /// <summary>Quiet hours for the 3-day and 1-day reminders, Moscow time: 23:00–09:00.</summary>
        public int QuietHoursStartHour { get; init; } = 23;

        public int QuietHoursEndHour { get; init; } = 9;

        /// <summary>No first connection this long after access was issued → «Не получилось?» (FR-BOT-07).</summary>
        public int OnboardingDelayMinutes { get; init; } = 30;

        /// <summary>Older subscriptions are not nudged (for example after a long downtime).</summary>
        public int OnboardingWindowHours { get; init; } = 24;

        /// <summary>Support compensation limit per action (ТЗ 22, подтверждено 24.09).</summary>
        public int SupportCompensationPerActionDays { get; init; } = 7;

        /// <summary>Support compensation limit per user within 30 days.</summary>
        public int SupportCompensationPer30Days { get; init; } = 14;

        /// <summary>
        /// The tariff that bonus days of a promo code are days of, for a user without a subscription or on the
        /// trial (ТЗ 22: «создаётся active на Базовом тарифе»). It may be switched off for sale.
        /// </summary>
        public string PromoBonusTariffCode { get; init; } = "basic_1m";

        public bool IsQuietHour(int moscowHour) =>
            QuietHoursStartHour > QuietHoursEndHour
                ? moscowHour >= QuietHoursStartHour || moscowHour < QuietHoursEndHour
                : moscowHour >= QuietHoursStartHour && moscowHour < QuietHoursEndHour;
    }
}
