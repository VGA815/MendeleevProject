using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Domain.Notifications;
using Mendeleev.Web.Bot.Content;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Mendeleev.Web.Bot
{
    /// <summary>
    /// <see cref="IUserMessenger"/> over the Bot API. Maps Telegram's answers to delivery outcomes:
    /// 403 → the user blocked the bot; 429 → retry after <c>retry_after</c>; network trouble → retry later.
    /// </summary>
    internal sealed class TelegramUserMessenger(
        ITelegramBotClient bot,
        IOptionsMonitor<BotContent> content,
        ILogger<TelegramUserMessenger> logger)
        : IUserMessenger
    {
        private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

        public Task<DeliveryResult> SendNotificationAsync(long chatId, NotificationMessage message, CancellationToken cancellationToken)
        {
            BotContent c = content.CurrentValue;
            string template = c.Notifications.GetValueOrDefault(message.Kind.ToString()) ?? message.Kind.ToString();

            var values = new List<(string, object?)>
            {
                ("date", message.ExpiresAtUtc),
                ("tariff", message.TariffName),
                ("days", message.DaysLeft),
                ("url", message.SubscriptionUrl),
            };
            values.AddRange(message.Values.Select(v => (v.Key, (object?)v.Value)));

            string text = TextRenderer.Render(template, [.. values]);
            return SendAsync(chatId, text, KeyboardFor(c, message), cancellationToken);
        }

        public Task<DeliveryResult> SendBroadcastAsync(long chatId, string html, bool withUpdateButton, CancellationToken cancellationToken)
        {
            BotContent c = content.CurrentValue;
            InlineKeyboardMarkup? keyboard = withUpdateButton
                ? Keyboards.Of([Keyboards.Callback(c.Button("HowToUpdate"), Cb.Faq + c.UpdateSubscriptionFaqId)])
                : null;
            return SendAsync(chatId, html, keyboard, cancellationToken);
        }

        public Task<DeliveryResult> SendTextAsync(long chatId, string html, CancellationToken cancellationToken) =>
            SendAsync(chatId, html, null, cancellationToken);

        private static InlineKeyboardMarkup? KeyboardFor(BotContent c, NotificationMessage message)
        {
            InlineKeyboardButton subscription = Keyboards.Callback(c.Button("MySubscription"), Cb.Subscription);
            InlineKeyboardButton renew = Keyboards.Callback(c.Button("Renew"), Cb.Buy);

            switch (message.Kind)
            {
                case NotificationKind.AccessIssued:
                case NotificationKind.LinkReissued:
                    var rows = new List<InlineKeyboardButton[]>(Keyboards.Platforms(c));
                    if (message.SubscriptionUrl is string url)
                    {
                        rows.Add([Keyboards.Url(c.Button("AddToHapp"), Keyboards.ImportUrl(c, url)), Keyboards.Copy(c.Button("CopyLink"), url)]);
                    }
                    rows.Add([subscription]);
                    return Keyboards.Of(rows);

                case NotificationKind.Expiry3d:
                case NotificationKind.Expiry1d:
                case NotificationKind.Expired:
                    return Keyboards.Of([renew]);

                case NotificationKind.TrialTrafficExhausted:
                    return Keyboards.Of([Keyboards.Callback(c.Button("Tariffs"), Cb.Buy)]);

                case NotificationKind.Onboarding:
                    return Keyboards.Of([Keyboards.Callback(c.Button("Faq"), Cb.Help)], Keyboards.Support(c));

                case NotificationKind.Incident:
                    return Keyboards.Of([Keyboards.Callback(c.Button("HowToUpdate"), Cb.Faq + c.UpdateSubscriptionFaqId)]);

                default:
                    return Keyboards.Of([subscription]);
            }
        }

        private async Task<DeliveryResult> SendAsync(long chatId, string html, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken)
        {
            try
            {
                await bot.SendMessage(chatId, html, ParseMode.Html, replyMarkup: keyboard, linkPreviewOptions: NoPreview, cancellationToken: cancellationToken);
                return DeliveryResult.Sent;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 403
                || ex.ErrorCode == 400 && ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            {
                return DeliveryResult.BotBlocked;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                int seconds = ex.Parameters?.RetryAfter ?? 5;
                throw new DeliveryDeferredException("Telegram rate limit", TimeSpan.FromSeconds(seconds), ex);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400)
            {
                logger.LogWarning("Telegram rejected a message: {Reason}", ex.Message);
                return DeliveryResult.Rejected;
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new DeliveryDeferredException($"Bot API unavailable: {ex.Message}", null, ex);
            }
        }
    }
}
