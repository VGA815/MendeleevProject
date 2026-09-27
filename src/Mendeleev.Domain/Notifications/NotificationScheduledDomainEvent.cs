using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Notifications
{
    /// <summary>Carries the dedup key because the notification has no id yet when the event is stored.</summary>
    public sealed record NotificationScheduledDomainEvent(string DedupKey) : IDomainEvent;
}
