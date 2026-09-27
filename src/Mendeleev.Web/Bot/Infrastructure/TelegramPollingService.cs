using Mendeleev.Application.Abstractions.Telegram;
using Mendeleev.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Mendeleev.Web.Bot.Infrastructure
{
    /// <summary>
    /// Long polling through the proxy — the fallback when incoming webhooks to the Russian DC are unreliable
    /// (FR-BOT-16). Enabled by <c>Telegram:Mode = Polling</c>; updates go the same way as webhook ones.
    /// </summary>
    internal sealed class TelegramPollingService(
        ITelegramBotClient bot,
        BotUpdateQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramPollingService> logger)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (options.Value.Mode != TelegramUpdateMode.Polling)
            {
                return;
            }

            int offset = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Update[] updates = await bot.GetUpdates(offset, limit: 100, timeout: 30, allowedUpdates: TelegramStartup.AllowedUpdates, cancellationToken: stoppingToken);

                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    ITelegramUpdateDeduplicator deduplicator = scope.ServiceProvider.GetRequiredService<ITelegramUpdateDeduplicator>();

                    foreach (Update update in updates)
                    {
                        offset = update.Id + 1;
                        if (await deduplicator.TryRegisterAsync(update.Id, stoppingToken))
                        {
                            queue.Enqueue(update);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Long polling failed; retrying");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
    }
}
