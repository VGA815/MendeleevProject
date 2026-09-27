using Mendeleev.Application.Configuration;
using Mendeleev.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Mendeleev.Web.Bot.Infrastructure
{
    /// <summary>
    /// Registers the webhook with its secret (or removes it for long polling) and publishes the command
    /// menus. Retries in the background: the Bot API may be unreachable from the Russian DC at start-up,
    /// and the service must come up anyway (ТЗ 40: «вебхук Telegram переустанавливается при старте»).
    /// </summary>
    internal sealed class TelegramStartup(
        ITelegramBotClient bot,
        StaffCommandsPublisher commands,
        IOptions<TelegramOptions> telegramOptions,
        IOptions<ServiceOptions> serviceOptions,
        ILogger<TelegramStartup> logger)
        : BackgroundService
    {
        public static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember];

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            TelegramOptions options = telegramOptions.Value;
            if (!options.RegisterOnStartup)
            {
                return;
            }

            TimeSpan delay = TimeSpan.FromSeconds(5);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (options.Mode == TelegramUpdateMode.Webhook)
                    {
                        string url = $"{serviceOptions.Value.WebhookBaseUrl}/webhooks/telegram";
                        await bot.SetWebhook(url, allowedUpdates: AllowedUpdates, secretToken: options.WebhookSecret, cancellationToken: stoppingToken);
                        logger.LogInformation("Telegram webhook registered");
                    }
                    else
                    {
                        await bot.DeleteWebhook(cancellationToken: stoppingToken);
                        logger.LogInformation("Telegram webhook removed, long polling mode");
                    }

                    await commands.PublishAllAsync(stoppingToken);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Telegram registration failed, retrying in {Delay}", delay);
                    await Task.Delay(delay, stoppingToken);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
                }
            }
        }
    }
}
