using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;

namespace Mendeleev.Application.Panel
{
    /// <summary>
    /// Maps our subscription to the panel user (ТЗ 24, «Соответствие полей»): the pseudonym instead of
    /// Telegram ID and email, the absolute expiry date, limits and squads from the tariff.
    /// </summary>
    internal static class PanelUserSpecFactory
    {
        public static PanelUserSpec Create(Subscription subscription) => new(
            User.PanelUsernameFor(subscription.UserId),
            subscription.PanelShortUuid,
            subscription.PanelVlessUuid,
            subscription.ExpiresAt,
            // 0 is "no limit" in Remnawave.
            subscription.Tariff.TrafficLimitBytes ?? 0,
            subscription.Tariff.DeviceLimit,
            subscription.Tariff.PanelSquads,
            Enabled: subscription.Status != SubscriptionStatus.Disabled);

        /// <summary>
        /// Compares the panel with the desired state. Returns a short description of the first
        /// difference, or null if they agree. The panel expires and limits users by itself, so an
        /// EXPIRED or LIMITED panel user is fine for a subscription that no longer grants access.
        /// </summary>
        public static string? FindDrift(Subscription subscription, PanelUser actual, DateTime utcNow)
        {
            PanelUserSpec desired = Create(subscription);

            if (!string.Equals(actual.ShortUuid, desired.ShortUuid, StringComparison.Ordinal))
            {
                return "shortUuid";
            }

            switch (subscription.Status)
            {
                case SubscriptionStatus.Disabled:
                    if (actual.Status != PanelUserStatus.Disabled)
                    {
                        return $"status {actual.Status}, expected DISABLED";
                    }
                    break;

                case SubscriptionStatus.Trial:
                case SubscriptionStatus.Active:
                    if (actual.Status != PanelUserStatus.Active)
                    {
                        return $"status {actual.Status}, expected ACTIVE";
                    }
                    if (Math.Abs((actual.ExpireAt - desired.ExpireAt).TotalSeconds) > 1)
                    {
                        return $"expireAt {actual.ExpireAt:O}, expected {desired.ExpireAt:O}";
                    }
                    break;

                case SubscriptionStatus.Expired:
                    // Somebody extended the user by hand in the panel UI (FR-PNL-06).
                    if (actual.Status == PanelUserStatus.Active && actual.ExpireAt > utcNow)
                    {
                        return $"active until {actual.ExpireAt:O} while expired";
                    }
                    return null;
            }

            if (actual.TrafficLimitBytes != desired.TrafficLimitBytes)
            {
                return $"trafficLimitBytes {actual.TrafficLimitBytes}, expected {desired.TrafficLimitBytes}";
            }
            if (actual.HwidDeviceLimit != desired.HwidDeviceLimit)
            {
                return $"hwidDeviceLimit {actual.HwidDeviceLimit}, expected {desired.HwidDeviceLimit}";
            }
            if (!actual.InternalSquads.ToHashSet().SetEquals(desired.InternalSquads))
            {
                return "internal squads";
            }

            return null;
        }
    }
}
