using System.Diagnostics;
using Mendeleev.Application.Abstractions.Observability;
using Telegram.Bot.Types;

namespace Mendeleev.Web.Bot.Infrastructure
{
    /// <summary>Consumes the update partitions; each update gets its own DI scope.</summary>
    internal sealed class BotUpdateWorker(
        BotUpdateQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<BotUpdateWorker> logger)
        : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.WhenAll(Enumerable.Range(0, BotUpdateQueue.Partitions).Select(p => ConsumeAsync(p, stoppingToken)));

        private async Task ConsumeAsync(int partition, CancellationToken stoppingToken)
        {
            try
            {
                await foreach (Update update in queue.Reader(partition).ReadAllAsync(stoppingToken))
                {
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<UpdateRouter>().HandleAsync(update, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        // Only the update id is logged: no texts, no personal data (ТЗ 26, «Обработка ошибок»).
                        logger.LogError(ex, "Failed to handle update {UpdateId}", update.Id);
                    }
                    finally
                    {
                        AppMetrics.BotUpdateDurationSeconds.Record(stopwatch.Elapsed.TotalSeconds);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }
}
