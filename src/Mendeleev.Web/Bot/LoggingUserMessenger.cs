using Mendeleev.Application.Abstractions.Delivery;

namespace Mendeleev.Web.Bot
{
    /// <summary>
    /// Stand-in when no bot token is configured (local runs, tests): the flow still completes and the
    /// messages are visible in the log. Only the kind and the chat are logged, never the text.
    /// </summary>
    internal sealed class LoggingUserMessenger(ILogger<LoggingUserMessenger> logger) : IUserMessenger
    {
        public Task<DeliveryResult> SendNotificationAsync(long chatId, NotificationMessage message, CancellationToken cancellationToken)
        {
            logger.LogInformation("[no bot] notification {Kind} to chat {ChatId}", message.Kind, chatId);
            return Task.FromResult(DeliveryResult.Sent);
        }

        public Task<DeliveryResult> SendBroadcastAsync(long chatId, string html, bool withUpdateButton, CancellationToken cancellationToken)
        {
            logger.LogInformation("[no bot] broadcast message to chat {ChatId}", chatId);
            return Task.FromResult(DeliveryResult.Sent);
        }

        public Task<DeliveryResult> SendTextAsync(long chatId, string html, CancellationToken cancellationToken)
        {
            logger.LogInformation("[no bot] service message to chat {ChatId}", chatId);
            return Task.FromResult(DeliveryResult.Sent);
        }
    }
}
