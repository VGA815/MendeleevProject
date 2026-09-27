using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Notifications;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Subscriptions.Maintenance
{
    /// <summary>
    /// Daily clean-up by the retention table (ТЗ 12, «Хранение и очистка»): daily traffic and
    /// notifications — 90 days, audit — 1 year. Payments are kept until the accountant answers how long.
    /// Technical tables (outbox, processed updates) are cleaned by the infrastructure itself.
    /// </summary>
    public sealed record RetentionCleanupCommand : ICommand<int>;

    internal sealed class RetentionCleanupCommandHandler(IApplicationDbContext db, IDateTimeProvider clock)
        : ICommandHandler<RetentionCleanupCommand, int>
    {
        public async Task<Result<int>> Handle(RetentionCleanupCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            DateOnly trafficBefore = DateOnly.FromDateTime(now.AddDays(-90));
            DateTime notificationsBefore = now.AddDays(-90);
            DateTime auditBefore = now.AddDays(-365);

            int deleted = 0;
            deleted += await db.TrafficDaily.Where(t => t.Day < trafficBefore).ExecuteDeleteAsync(cancellationToken);
            deleted += await db.Notifications
                .Where(n => n.CreatedAt < notificationsBefore && n.Status != NotificationStatus.Pending)
                .ExecuteDeleteAsync(cancellationToken);
            deleted += await db.AuditLog.Where(a => a.CreatedAt < auditBefore).ExecuteDeleteAsync(cancellationToken);

            return deleted;
        }
    }
}
