using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Subscriptions.Maintenance
{
    /// <summary>
    /// Every minute: trial and active subscriptions past their term become <c>expired</c> (FR-SUB-06).
    /// The panel has already cut the traffic at <c>expireAt</c>; this sets our status and sends the
    /// «Подписка закончилась» message (right away, quiet hours do not apply).
    /// </summary>
    public sealed record ExpireSubscriptionsCommand : ICommand<int>;

    internal sealed class ExpireSubscriptionsCommandHandler(IApplicationDbContext db, IDateTimeProvider clock)
        : ICommandHandler<ExpireSubscriptionsCommand, int>
    {
        private const int BatchSize = 200;

        public async Task<Result<int>> Handle(ExpireSubscriptionsCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            List<long> userIds = await db.Subscriptions
                .AsNoTracking()
                .Where(s => (s.Status == SubscriptionStatus.Trial || s.Status == SubscriptionStatus.Active) && s.ExpiresAt <= now)
                .OrderBy(s => s.ExpiresAt)
                .Select(s => s.UserId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            int expired = 0;
            foreach (long userId in userIds)
            {
                // Per user, under the lock: a payment that lands at the same moment must win cleanly.
                await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
                await db.LockUserAsync(userId, cancellationToken);

                Subscription? subscription = await db.Subscriptions
                    .Include(s => s.Tariff)
                    .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

                if (subscription is not null && subscription.TryExpire(ExpiredReason.Time, clock.UtcNow))
                {
                    await db.SaveChangesAsync(cancellationToken);
                    expired++;
                }

                await transaction.CommitAsync(cancellationToken);
            }

            return expired;
        }
    }
}
