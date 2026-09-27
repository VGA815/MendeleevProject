namespace Mendeleev.Application.Abstractions
{
    /// <summary>
    /// Marks an exception after which the outbox gives up at once instead of retrying on schedule,
    /// and raises an alert.
    /// </summary>
    public interface INonRetryableException;
}
