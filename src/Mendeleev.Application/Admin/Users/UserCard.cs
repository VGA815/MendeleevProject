using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Admin.Anomalies;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;

namespace Mendeleev.Application.Admin.Users
{
    /// <summary>
    /// The user card for staff (ТЗ 28, «Карточка пользователя»). No IPs (we do not store them), no account
    /// key (only its hash exists), no node settings or secrets (FR-ADM-12).
    /// </summary>
    public sealed record UserCard(
        long UserId,
        long? TelegramId,
        UserStatus Status,
        DateTime CreatedAt,
        bool TrialUsed,
        bool BotBlocked,
        bool HasAccountKey,
        SubscriptionCard? Subscription,
        IReadOnlyList<PaymentCard> Payments,
        DevicesCard? Devices,
        int ResetsLast30Days,
        UserAnomalies? Anomalies = null);

    public sealed record SubscriptionCard(
        string TariffName,
        SubscriptionStatus Status,
        DateTime ExpiresAt,
        int DaysLeft,
        DateTime? FirstConnectedAt,
        SyncState SyncState,
        DateTime UpdatedAt,
        bool HasLink);

    public sealed record PaymentCard(
        Guid Id,
        DateTime CreatedAt,
        decimal Amount,
        PaymentStatus Status,
        string Provider,
        string? ProviderPaymentId,
        bool NeedsReview,
        string? PromoCode = null);

    /// <param name="Available">False when the panel could not be asked.</param>
    public sealed record DevicesCard(bool Available, int Limit, IReadOnlyList<PanelDevice> Items);
}
