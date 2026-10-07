using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Subscriptions
{
    /// <summary>
    /// Raised by every change of a subscription. The handler pushes the full desired state to the panel
    /// (FR-PNL-03) and then tells the user about it. The subscription is addressed by its user because a
    /// freshly created subscription has no id yet when the event is written to the outbox.
    /// </summary>
    /// <param name="NoticeKey">Makes the resulting notification unique (payment id, trial start, …).</param>
    /// <param name="Detail">Text for the user's message, e.g. the reason of a mass compensation (FR-SUB-16).</param>
    public sealed record SubscriptionChangedDomainEvent(
        long UserId,
        SubscriptionNotice Notice,
        string? NoticeKey,
        string? Detail = null) : IDomainEvent;
}
