using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;

namespace Mendeleev.Application.Subscriptions.GetSubscription
{
    /// <summary>«Моя подписка» (FR-SUB-14, FR-BOT-05): tariff, expiry, days left, trial traffic left, link.</summary>
    public sealed record GetSubscriptionQuery(long UserId) : IQuery<SubscriptionView>;

    public sealed record SubscriptionView(
        bool Exists,
        string? TariffName,
        TariffTier? Tier,
        SubscriptionStatus? Status,
        DateTime? ExpiresAt,
        int DaysLeft,
        string? SubscriptionUrl,
        bool AccessPending,
        long? TrafficLimitBytes,
        long? TrafficUsedBytes,
        int? DeviceLimit)
    {
        public static readonly SubscriptionView None = new(false, null, null, null, null, 0, null, false, null, null, null);

        public long? TrafficLeftBytes =>
            TrafficLimitBytes is long limit && TrafficUsedBytes is long used ? Math.Max(0, limit - used) : null;
    }
}
