using Mendeleev.Domain.Notifications;

namespace Mendeleev.Application.Abstractions.Delivery
{
    /// <summary>
    /// Delivers messages to people in Telegram. Rendering (texts from the content file, buttons) is the
    /// bot's business, so the implementation lives in the presentation layer.
    /// </summary>
    /// <remarks>
    /// A temporary failure (Bot API unreachable, 429) is thrown as <see cref="DeliveryDeferredException"/>
    /// so the caller retries; a user who blocked the bot is a normal outcome, not an exception.
    /// </remarks>
    public interface IUserMessenger
    {
        Task<DeliveryResult> SendNotificationAsync(long chatId, NotificationMessage message, CancellationToken cancellationToken);

        Task<DeliveryResult> SendBroadcastAsync(long chatId, string html, bool withUpdateButton, CancellationToken cancellationToken);

        /// <summary>Service messages to staff: reports, daily summary.</summary>
        Task<DeliveryResult> SendTextAsync(long chatId, string html, CancellationToken cancellationToken);
    }

    public enum DeliveryResult
    {
        Sent,

        /// <summary>403 from Telegram: the user blocked the bot (FR-BOT-14).</summary>
        BotBlocked,

        /// <summary>Telegram refused this particular message (for example, broken markup); retrying will not help.</summary>
        Rejected,
    }

    /// <summary>Everything a notification text may need; filled from the current state at send time.</summary>
    public sealed record NotificationMessage(
        NotificationKind Kind,
        string? TariffName,
        DateTime? ExpiresAtUtc,
        int? DaysLeft,
        string? SubscriptionUrl,
        IReadOnlyDictionary<string, string> Values);

    public sealed class DeliveryDeferredException(string message, TimeSpan? retryAfter = null, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
