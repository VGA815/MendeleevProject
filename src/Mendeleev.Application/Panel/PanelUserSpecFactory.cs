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
        /// Trial or active by status, but the term is over: the job that expires it runs once a minute and
        /// has not got to it yet. For the panel such a subscription is already expired.
        /// </summary>
        public static bool HasLapsed(Subscription subscription, DateTime utcNow) =>
            subscription.GrantsAccess && subscription.ExpiresAt <= utcNow;

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

            if (subscription.Status == SubscriptionStatus.Expired || HasLapsed(subscription, utcNow))
            {
                // The panel cannot take an expireAt in the past, so the date is not compared: comparing it
                // pushed the same update back and forth until the expiry job ran. Drift is only a user the
                // panel still lets through: extended by hand (FR-PNL-06) or our term was moved back.
                return actual.Status == PanelUserStatus.Active && actual.ExpireAt > utcNow
                    ? $"active until {actual.ExpireAt:O} while expired"
                    : null;
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
