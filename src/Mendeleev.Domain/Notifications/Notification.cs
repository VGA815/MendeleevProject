using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Notifications
{
    /// <summary>
    /// A message to a user. The unique <see cref="DedupKey"/> guarantees each reminder is sent once per
    /// expiry date, including after a job restart (FR-SUB-07). Creating one raises
    /// <see cref="NotificationScheduledDomainEvent"/>, and the outbox delivers it.
    /// </summary>
    public sealed class Notification : Entity
    {
        private Notification() { }

        public long Id { get; private set; }

        public long UserId { get; private set; }

        public NotificationKind Kind { get; private set; }

        public string DedupKey { get; private set; } = string.Empty;

        public NotificationStatus Status { get; private set; }

        /// <summary>JSON with values for the text (amount, days, next reset date…).</summary>
        public string? Data { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public DateTime? SentAt { get; private set; }

        public static Notification Schedule(long userId, NotificationKind kind, string dedupKey, string? data, DateTime utcNow)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);

            var notification = new Notification
            {
                UserId = userId,
                Kind = kind,
                DedupKey = dedupKey,
                Status = NotificationStatus.Pending,
                Data = data,
                CreatedAt = utcNow,
            };
            notification.Raise(new NotificationScheduledDomainEvent(dedupKey));
            return notification;
        }

        public static string KeyFor(NotificationKind kind, long subjectId, string discriminator) =>
            $"{kind}:{subjectId}:{discriminator}";

        public void MarkSent(DateTime utcNow)
        {
            Status = NotificationStatus.Sent;
            SentAt = utcNow;
        }

        public void MarkSkipped()
        {
            Status = NotificationStatus.Skipped;
        }

        public void MarkFailed()
        {
            Status = NotificationStatus.Failed;
        }
    }
}
