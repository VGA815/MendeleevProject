using Mendeleev.Application.Admin.Broadcasts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mendeleev.Infrastructure.BackgroundJobs
{
    /// <summary>Picks up started broadcasts and delivers them at Telegram's pace.</summary>
    internal sealed class BroadcastWorker(IServiceScopeFactory scopeFactory, ILogger<BroadcastWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool worked = false;
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    worked = await scope.ServiceProvider.GetRequiredService<IBroadcastDeliveryService>().RunNextAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Broadcast delivery failed; will resume from the saved cursor");
                }

                if (!worked)
                {
                    try
                    {
                        await Task.Delay(IdleDelay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
    }
}
