using System.Globalization;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Notifications
{
    /// <summary>
    /// Delivers a scheduled notification. The text is built from the state at send time; a reminder
    /// whose reason went away (the subscription was renewed meanwhile) is skipped.
    /// </summary>
    internal sealed class NotificationScheduledDomainEventHandler(
        IApplicationDbContext db,
        IUserMessenger messenger,
        IDateTimeProvider clock)
        : IDomainEventHandler<NotificationScheduledDomainEvent>
    {
        public const string ExpiresTicksKey = "expires_ticks";

        public async Task Handle(NotificationScheduledDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            Notification? notification = await db.Notifications
                .FirstOrDefaultAsync(n => n.DedupKey == domainEvent.DedupKey, cancellationToken);
            if (notification is null || notification.Status != NotificationStatus.Pending)
            {
                return;
            }

            User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == notification.UserId, cancellationToken);
            if (user?.TelegramId is not long chatId || user.BotBlocked || user.IsBlocked)
            {
                notification.MarkSkipped();
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            DateTime now = clock.UtcNow;
            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

            Dictionary<string, string> values = Json.Deserialize<Dictionary<string, string>>(notification.Data) ?? [];

            if (!IsStillRelevant(notification.Kind, subscription, values))
            {
                notification.MarkSkipped();
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var message = new NotificationMessage(
                notification.Kind,
                subscription?.Tariff.Name,
                subscription?.ExpiresAt,
                subscription?.DaysLeft(now),
                subscription is { LinkVisible: true } ? subscription.SubscriptionUrl : null,
                values);

            // A temporary Telegram failure throws and the outbox retries the whole delivery.
            DeliveryResult result = await messenger.SendNotificationAsync(chatId, message, cancellationToken);

            switch (result)
            {
                case DeliveryResult.BotBlocked:
                    user.MarkBotBlocked(now);
                    notification.MarkFailed();
                    break;
                case DeliveryResult.Rejected:
                    notification.MarkFailed();
                    break;
                default:
                    notification.MarkSent(now);
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        private static bool IsStillRelevant(NotificationKind kind, Subscription? subscription, IReadOnlyDictionary<string, string> values)
        {
            switch (kind)
            {
                case NotificationKind.Expiry3d:
                case NotificationKind.Expiry1d:
                    // Renewed in the meantime: the reminder for the old date is moot (ТЗ 22, «Напоминания»).
                    return subscription is { GrantsAccess: true }
                        && values.TryGetValue(ExpiresTicksKey, out string? ticks)
                        && ticks == subscription.ExpiresAt.Ticks.ToString(CultureInfo.InvariantCulture);

                case NotificationKind.Expired:
                case NotificationKind.TrialTrafficExhausted:
                    return subscription is { Status: SubscriptionStatus.Expired };

                case NotificationKind.Onboarding:
                    return subscription is { GrantsAccess: true, FirstConnectedAt: null };

                case NotificationKind.AccessIssued:
                case NotificationKind.LinkReissued:
                    return subscription is { LinkVisible: true };

                case NotificationKind.AccessPending:
                    // The sync got through first: the confirmation with the date is on its way instead.
                    return subscription is { AccessPending: true };

                default:
                    return true;
            }
        }
    }
}
