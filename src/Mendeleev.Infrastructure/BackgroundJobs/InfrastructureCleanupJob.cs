using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Outbox;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Mendeleev.Infrastructure.BackgroundJobs
{
    /// <summary>
    /// Technical tables: done outbox messages after 7 days, processed Telegram updates after 24 hours
    /// (ТЗ 12, «Хранение и очистка»; ТЗ 26, «Бизнес-правила»). Failed outbox messages are kept for analysis.
    /// </summary>
    [DisallowConcurrentExecution]
    internal sealed class InfrastructureCleanupJob(
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider clock,
        ILogger<InfrastructureCleanupJob> logger)
        : IJob
    {
        public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                DateTime now = clock.UtcNow;

                DateTime outboxBefore = now.AddDays(-7);
                int outbox = await db.OutboxMessages
                    .Where(m => m.Status == OutboxStatus.Done && m.ProcessedAt < outboxBefore)
                    .ExecuteDeleteAsync(cancellationToken);

                DateTime updatesBefore = now.AddHours(-24);
                int updates = await db.ProcessedTelegramUpdates
                    .Where(u => u.ReceivedAt < updatesBefore)
                    .ExecuteDeleteAsync(cancellationToken);

                logger.LogInformation("Cleanup removed {Outbox} outbox messages and {Updates} processed updates", outbox, updates);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Infrastructure cleanup failed");
            }
        }
    }
}
