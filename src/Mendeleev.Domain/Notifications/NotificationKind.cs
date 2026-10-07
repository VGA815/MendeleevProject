namespace Mendeleev.Domain.Notifications
{
    /// <summary>ТЗ 26, «Уведомления».</summary>
    public enum NotificationKind
    {
        AccessIssued = 0,
        PaymentSucceeded = 1,
        Expiry3d = 2,
        Expiry1d = 3,
        Expired = 4,
        TrialTrafficExhausted = 5,
        Onboarding = 6,
        LinkReissued = 7,
        Compensated = 8,
        Incident = 9,

        /// <summary>Paid, but the panel is unreachable: access follows once the sync gets through (ТЗ 23).</summary>
        AccessPending = 10,

        /// <summary>Bonus days of a promo code reached the panel (FR-SUB-15).</summary>
        PromoBonus = 11,
    }
}
