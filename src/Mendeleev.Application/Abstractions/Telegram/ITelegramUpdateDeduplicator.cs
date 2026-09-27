namespace Mendeleev.Application.Abstractions.Telegram
{
    /// <summary>
    /// Remembers processed Telegram <c>update_id</c>s for 24 hours so that a redelivered update is not
    /// processed twice (FR-BOT-01).
    /// </summary>
    public interface ITelegramUpdateDeduplicator
    {
        /// <returns>True the first time the id is seen.</returns>
        Task<bool> TryRegisterAsync(long updateId, CancellationToken cancellationToken);
    }
}
