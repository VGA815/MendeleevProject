namespace Mendeleev.Infrastructure.Outbox
{
    /// <summary>
    /// Wakes the processor right after a save with new messages, so "payment → access" does not wait for
    /// the next poll. Polling stays as the fallback for messages written by other instances.
    /// </summary>
    public sealed class OutboxSignal
    {
        private readonly SemaphoreSlim _semaphore = new(0, 1);

        public void Notify()
        {
            try
            {
                if (_semaphore.CurrentCount == 0)
                {
                    _semaphore.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
        }

        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _semaphore.WaitAsync(timeout, cancellationToken);
        }
    }
}
