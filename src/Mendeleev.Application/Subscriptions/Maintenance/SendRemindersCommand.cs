using System.Globalization;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Notifications;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Subscriptions.Maintenance
{
    /// <summary>
    /// Every 10 minutes: reminders 3 days and 1 day before the end (FR-SUB-07) and 3 days before the archive
    /// (FR-SUB-17). Each is sent once per expiry date; after a renewal the date changes and the reminders for the
    /// new date go out again.
    /// Quiet hours 23:00–09:00 МСК hold them until 09:00. After downtime the job catches up, but never
    /// sends «за 3 дня» when less than a day is left (ТЗ 22, «Напоминания»).
    /// </summary>
    public sealed record SendRemindersCommand : ICommand<int>;

    internal sealed class SendRemindersCommandHandler(
        IApplicationDbContext db,
        INotificationScheduler scheduler,
        IDateTimeProvider clock,
        IOptions<SubscriptionOptions> options)
        : ICommandHandler<SendRemindersCommand, int>
    {
        public async Task<Result<int>> Handle(SendRemindersCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            if (options.Value.IsQuietHour(MoscowTime.FromUtc(now).Hour))
            {
                return 0;
            }

            DateTime horizon = now.AddDays(3);
            List<Subscription> expiring = await db.Subscriptions
                .AsNoTracking()
                .Include(s => s.Tariff)
                .Where(s => (s.Status == SubscriptionStatus.Trial || s.Status == SubscriptionStatus.Active)
                    && s.ExpiresAt > now && s.ExpiresAt <= horizon)
                .ToListAsync(cancellationToken);

            int scheduled = 0;
            foreach (Subscription subscription in expiring)
            {
                TimeSpan left = subscription.ExpiresAt - now;
                NotificationKind? kind = null;

                if (left <= TimeSpan.FromDays(1) && left > TimeSpan.FromHours(1))
                {
                    kind = NotificationKind.Expiry1d;
                }
                else if (left > TimeSpan.FromDays(1) && subscription.Tariff.Tier != TariffTier.Trial)
                {
                    kind = NotificationKind.Expiry3d;
                }

                if (kind is null)
                {
                    continue;
                }

                string ticks = subscription.ExpiresAt.Ticks.ToString(CultureInfo.InvariantCulture);
                bool added = await scheduler.ScheduleAsync(
                    subscription.UserId,
                    kind.Value,
                    Notification.KeyFor(kind.Value, subscription.Id, ticks),
                    new Dictionary<string, string> { [NotificationScheduledDomainEventHandler.ExpiresTicksKey] = ticks },
                    cancellationToken);

                if (added)
                {
                    scheduled++;
                }
            }

            scheduled += await ScheduleArchiveRemindersAsync(now, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            return scheduled;
        }

        /// <summary>
        /// FR-SUB-17: on the 27th day after the end — three days before the archive takes the link away — «продлите,
        /// чтобы не настраивать заново». Not after a refund (the user left), and not when less than a day is left
        /// after downtime: «через 3 дня» would be wrong.
        /// </summary>
        private async Task<int> ScheduleArchiveRemindersAsync(DateTime now, CancellationToken cancellationToken)
        {
            SubscriptionOptions settings = options.Value;
            DateTime remindedUntil = now.AddDays(-(settings.RetentionAfterExpiryDays - settings.ArchiveReminderDaysBefore));
            DateTime lastDayStarts = now.AddDays(1 - settings.RetentionAfterExpiryDays);

            var expired = await db.Subscriptions
                .AsNoTracking()
                .Where(s => s.Status == SubscriptionStatus.Expired
                    && s.ExpiredReason != ExpiredReason.Refund
                    && s.ExpiresAt <= remindedUntil
                    && s.ExpiresAt > lastDayStarts)
                .Select(s => new { s.Id, s.UserId, s.ExpiresAt })
                .ToListAsync(cancellationToken);

            int scheduled = 0;
            foreach (var subscription in expired)
            {
                string ticks = subscription.ExpiresAt.Ticks.ToString(CultureInfo.InvariantCulture);
                if (await scheduler.ScheduleAsync(
                        subscription.UserId,
                        NotificationKind.ArchiveSoon,
                        Notification.KeyFor(NotificationKind.ArchiveSoon, subscription.Id, ticks),
                        new Dictionary<string, string> { [NotificationScheduledDomainEventHandler.ExpiresTicksKey] = ticks },
                        cancellationToken))
                {
                    scheduled++;
                }
            }

            return scheduled;
        }
    }
}
