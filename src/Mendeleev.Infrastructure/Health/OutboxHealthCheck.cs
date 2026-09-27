using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Outbox;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Mendeleev.Infrastructure.Health
{
    /// <summary>
    /// Part of <c>/health/ready</c>: the oldest pending outbox task is younger than 5 minutes
    /// (ТЗ 40, «Управляющий VPS: состав»). A stuck panel sync shows up in Uptime Kuma this way.
    /// </summary>
    internal sealed class OutboxHealthCheck(ApplicationDbContext db, IDateTimeProvider clock) : IHealthCheck
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            DateTime? oldest = await db.OutboxMessages
                .Where(m => m.Status == OutboxStatus.Pending)
                .OrderBy(m => m.CreatedAt)
                .Select(m => (DateTime?)m.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (oldest is null)
            {
                return HealthCheckResult.Healthy();
            }

            TimeSpan age = clock.UtcNow - oldest.Value;
            return age <= MaxAge
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Oldest outbox task is {age.TotalMinutes:F0} minutes old.");
        }
    }
}
