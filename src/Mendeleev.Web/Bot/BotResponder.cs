using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Mendeleev.Web.Bot
{
    /// <summary>Sends or edits the bot's answers. Link previews are off: links are data here.</summary>
    internal sealed class BotResponder(ITelegramBotClient bot, ILogger<BotResponder> logger)
    {
        private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

        /// <summary>Shows a screen: edits the message with the pressed button, or sends a new one.</summary>
        public async Task ShowAsync(BotContext context, string html, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken)
        {
            if (context.CallbackMessageId is int messageId)
            {
                try
                {
                    await bot.EditMessageText(context.ChatId, messageId, html, ParseMode.Html, keyboard, NoPreview, cancellationToken: cancellationToken);
                    return;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400)
                {
                    if (ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    // A photo, a too old message and similar cannot be edited: send a new one instead.
                }
            }

            await SendAsync(context.ChatId, html, keyboard, cancellationToken);
        }

        public async Task SendAsync(long chatId, string html, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken)
        {
            await bot.SendMessage(chatId, html, ParseMode.Html, replyMarkup: keyboard, linkPreviewOptions: NoPreview, cancellationToken: cancellationToken);
        }

        public async Task SendPhotoAsync(long chatId, byte[] png, string caption, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream(png);
            await bot.SendPhoto(chatId, InputFile.FromStream(stream, "qr.png"), caption, ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        public async Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
        {
            try
            {
                await bot.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: cancellationToken);
            }
            catch (ApiRequestException ex)
            {
                // Too late to answer (older than ~15 s) — harmless.
                logger.LogDebug(ex, "Callback query was not answered");
            }
        }
    }
}
