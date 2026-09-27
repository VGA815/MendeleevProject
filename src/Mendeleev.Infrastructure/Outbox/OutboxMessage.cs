using System.Text.Json;
using Mendeleev.SharedKernel;

namespace Mendeleev.Infrastructure.Outbox
{
    /// <summary>A side effect written in the same transaction as the change that caused it (ТЗ 12, OutboxMessage).</summary>
    internal sealed class OutboxMessage
    {
        private OutboxMessage() { }

        public long Id { get; private set; }

        /// <summary>Short name of the domain event type (see <see cref="DomainEventTypes"/>).</summary>
        public string Type { get; private set; } = string.Empty;

        public string Payload { get; private set; } = string.Empty;

        public OutboxStatus Status { get; private set; }

        public int Attempts { get; private set; }

        public DateTime NextAttemptAt { get; private set; }

        /// <summary>Lease: another processor instance leaves the message alone until then.</summary>
        public DateTime? LockedUntil { get; private set; }

        public string? LastError { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public DateTime? ProcessedAt { get; private set; }

        public static OutboxMessage FromDomainEvent(IDomainEvent domainEvent, DateTime utcNow) => new()
        {
            Type = DomainEventTypes.NameOf(domainEvent.GetType()),
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), DomainEventTypes.SerializerOptions),
            Status = OutboxStatus.Pending,
            NextAttemptAt = utcNow,
            CreatedAt = utcNow,
        };

        public void MarkDone(DateTime utcNow)
        {
            Status = OutboxStatus.Done;
            ProcessedAt = utcNow;
            LockedUntil = null;
            LastError = null;
        }

        /// <returns>True if the message gave up for good.</returns>
        public bool MarkFailedAttempt(string error, bool permanent, DateTime utcNow)
        {
            Attempts++;
            LastError = error.Length > 2000 ? error[..2000] : error;
            LockedUntil = null;

            TimeSpan? delay = permanent ? null : OutboxRetrySchedule.DelayAfter(Attempts);
            if (delay is null)
            {
                Status = OutboxStatus.Failed;
                ProcessedAt = utcNow;
                return true;
            }

            NextAttemptAt = utcNow + delay.Value;
            return false;
        }
    }

    internal enum OutboxStatus
    {
        Pending = 0,
        Done = 1,
        Failed = 2,
    }

    /// <summary>
    /// ТЗ 24: 2 s, 5 s, 15 s, 1 min, 5 min, then every 15 min for an hour; after that — failed and an alert.
    /// </summary>
    internal static class OutboxRetrySchedule
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(15),
        ];

        public static TimeSpan? DelayAfter(int attempts) =>
            attempts >= 1 && attempts <= Delays.Length ? Delays[attempts - 1] : null;
    }
}
