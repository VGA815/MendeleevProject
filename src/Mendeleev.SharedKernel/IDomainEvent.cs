namespace Mendeleev.SharedKernel
{
    /// <summary>
    /// A fact raised by an entity. Unlike DevStart, events are not dispatched in-process after the
    /// save: the context writes them to the outbox in the same transaction as the change, and the
    /// outbox processor delivers them with retries (ТЗ: «Transactional outbox для побочных эффектов»).
    /// Events are therefore serialized to JSON — keep them flat records of primitives.
    /// </summary>
    public interface IDomainEvent;
}
