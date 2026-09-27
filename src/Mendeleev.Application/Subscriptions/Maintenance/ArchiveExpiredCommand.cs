using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Subscriptions.Maintenance
{
    /// <summary>
    /// Hourly: 30 days after expiry the subscription goes to the archive and its panel user is deleted
    /// (FR-SUB-09, FR-PNL-12). The next payment issues a new link.
    /// </summary>
    public sealed record ArchiveExpiredCommand : ICommand<int>;

    internal sealed class ArchiveExpiredCommandHandler(
        IApplicationDbContext db,
        IDateTimeProvider clock,
        IOptions<SubscriptionOptions> options)
        : ICommandHandler<ArchiveExpiredCommand, int>
    {
        private const int BatchSize = 200;

        public async Task<Result<int>> Handle(ArchiveExpiredCommand command, CancellationToken cancellationToken)
        {
            int retentionDays = options.Value.RetentionAfterExpiryDays;
            DateTime threshold = clock.UtcNow.AddDays(-retentionDays);

            List<long> userIds = await db.Subscriptions
                .AsNoTracking()
                .Where(s => s.Status == SubscriptionStatus.Expired && s.ExpiresAt <= threshold)
                .Select(s => s.UserId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            int archived = 0;
            foreach (long userId in userIds)
            {
                await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
                await db.LockUserAsync(userId, cancellationToken);

                Subscription? subscription = await db.Subscriptions
                    .Include(s => s.Tariff)
                    .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

                if (subscription is not null && subscription.TryArchive(retentionDays, clock.UtcNow))
                {
                    await db.SaveChangesAsync(cancellationToken);
                    archived++;
                }

                await transaction.CommitAsync(cancellationToken);
            }

            return archived;
        }
    }
}
