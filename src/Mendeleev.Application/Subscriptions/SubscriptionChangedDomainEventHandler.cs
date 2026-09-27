using System.Globalization;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Notifications;
using Mendeleev.Application.Panel;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Subscriptions
{
    /// <summary>
    /// Pushes a changed subscription to the panel and, once access really works there, tells the user.
    /// This is the path "оплата → доступ на всех нодах ≤ 30 с" (FR-PAY-04, NFR-01).
    /// </summary>
    internal sealed class SubscriptionChangedDomainEventHandler(
        IPanelSynchronizer synchronizer,
        INotificationScheduler notifications,
        IApplicationDbContext db,
        IDateTimeProvider clock)
        : IDomainEventHandler<SubscriptionChangedDomainEvent>
    {
        public async Task Handle(SubscriptionChangedDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            await synchronizer.SyncAsync(
                domainEvent.UserId,
                subscription => ScheduleNoticeAsync(domainEvent, subscription, cancellationToken),
                cancellationToken);

            await RecordPaymentToAccessAsync(domainEvent, cancellationToken);
        }

        private async Task ScheduleNoticeAsync(SubscriptionChangedDomainEvent domainEvent, Subscription subscription, CancellationToken cancellationToken)
        {
            NotificationKind? kind = domainEvent.Notice switch
            {
                SubscriptionNotice.AccessIssued => NotificationKind.AccessIssued,
                SubscriptionNotice.PaymentSucceeded => NotificationKind.PaymentSucceeded,
                SubscriptionNotice.Compensated => NotificationKind.Compensated,
                SubscriptionNotice.LinkReissued => NotificationKind.LinkReissued,
                SubscriptionNotice.Expired => NotificationKind.Expired,
                SubscriptionNotice.TrafficExhausted => NotificationKind.TrialTrafficExhausted,
                _ => null,
            };
            if (kind is null)
            {
                return;
            }

            string discriminator = domainEvent.NoticeKey
                ?? subscription.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture);

            await notifications.ScheduleAsync(
                subscription.UserId,
                kind.Value,
                Notification.KeyFor(kind.Value, subscription.UserId, discriminator),
                values: null,
                cancellationToken);
        }

        private async Task RecordPaymentToAccessAsync(SubscriptionChangedDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            if (domainEvent.Notice is not (SubscriptionNotice.PaymentSucceeded or SubscriptionNotice.AccessIssued)
                || !Guid.TryParseExact(domainEvent.NoticeKey, "N", out Guid paymentId))
            {
                return;
            }

            DateTime? paidAt = await db.Payments
                .Where(p => p.Id == paymentId)
                .Select(p => p.PaidAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (paidAt is DateTime at)
            {
                AppMetrics.PaymentToAccessSeconds.Record((clock.UtcNow - at).TotalSeconds);
            }
        }
    }
}
