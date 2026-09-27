using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Domain.Notifications;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Notifications
{
    /// <summary>
    /// Adds a notification to the current unit of work; the caller saves. Delivery happens from the
    /// outbox after the commit, so a message is never sent for a change that was rolled back.
    /// </summary>
    public interface INotificationScheduler
    {
        /// <returns>False if a notification with this dedup key already exists.</returns>
        Task<bool> ScheduleAsync(
            long userId,
            NotificationKind kind,
            string dedupKey,
            IReadOnlyDictionary<string, string>? values,
            CancellationToken cancellationToken);
    }

    internal sealed class NotificationScheduler(IApplicationDbContext db, IDateTimeProvider clock) : INotificationScheduler
    {
        public async Task<bool> ScheduleAsync(
            long userId,
            NotificationKind kind,
            string dedupKey,
            IReadOnlyDictionary<string, string>? values,
            CancellationToken cancellationToken)
        {
            bool exists = db.Notifications.Local.Any(n => n.DedupKey == dedupKey)
                || await db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, cancellationToken);
            if (exists)
            {
                return false;
            }

            string? data = values is { Count: > 0 } ? Json.Serialize(values) : null;
            db.Notifications.Add(Notification.Schedule(userId, kind, dedupKey, data, clock.UtcNow));
            return true;
        }
    }
}
