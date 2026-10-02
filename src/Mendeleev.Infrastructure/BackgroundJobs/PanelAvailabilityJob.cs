using Mendeleev.Infrastructure.Panel;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Mendeleev.Infrastructure.BackgroundJobs
{
    /// <summary>Runs <see cref="PanelAvailabilityMonitor"/> on the schedule; the monitor keeps the state.</summary>
    [DisallowConcurrentExecution]
    internal sealed class PanelAvailabilityJob(PanelAvailabilityMonitor monitor, ILogger<PanelAvailabilityJob> logger) : IJob
    {
        public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                await monitor.CheckAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Panel availability check failed");
            }
        }
    }
}
