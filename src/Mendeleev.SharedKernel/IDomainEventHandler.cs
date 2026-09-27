namespace Mendeleev.SharedKernel
{
    /// <summary>
    /// Handles a domain event delivered from the outbox. Delivery is at-least-once: a handler may run
    /// again after a crash or after another handler of the same event failed, so it must be idempotent.
    /// </summary>
    public interface IDomainEventHandler<in T> where T : IDomainEvent
    {
        Task Handle(T domainEvent, CancellationToken cancellationToken);
    }
}
