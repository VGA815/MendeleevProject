using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Traffic;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Panel.Traffic
{
    /// <summary>
    /// Once a day, shortly after midnight МСК: the day's traffic per subscription, as the difference of the
    /// panel's lifetime counter (FR-PNL-15). Only volumes — no IPs, no destinations.
    /// </summary>
    public sealed record CollectTrafficCommand : ICommand<int>;

    internal sealed class CollectTrafficCommandHandler(
        IApplicationDbContext db,
        IPanelClient panel,
        IDateTimeProvider clock)
        : ICommandHandler<CollectTrafficCommand, int>
    {
        public async Task<Result<int>> Handle(CollectTrafficCommand command, CancellationToken cancellationToken)
        {
            DateOnly day = MoscowTime.Today(clock.UtcNow).AddDays(-1);

            Dictionary<long, long> subscriptionByUser = await db.Subscriptions
                .AsNoTracking()
                .ToDictionaryAsync(s => s.UserId, s => s.Id, cancellationToken);

            HashSet<long> alreadyCollected = (await db.TrafficDaily
                .AsNoTracking()
                .Where(t => t.Day == day)
                .Select(t => t.SubscriptionId)
                .ToListAsync(cancellationToken)).ToHashSet();

            Dictionary<long, long> previous = await db.TrafficDaily
                .AsNoTracking()
                .Where(t => t.Day < day)
                .GroupBy(t => t.SubscriptionId)
                .Select(g => new { SubscriptionId = g.Key, Lifetime = g.OrderByDescending(t => t.Day).Select(t => t.LifetimeBytes).First() })
                .ToDictionaryAsync(x => x.SubscriptionId, x => x.Lifetime, cancellationToken);

            int written = 0;
            await foreach (PanelUser panelUser in panel.ListUsersAsync(cancellationToken))
            {
                if (!User.TryParsePanelUsername(panelUser.Username, out long userId)
                    || !subscriptionByUser.TryGetValue(userId, out long subscriptionId)
                    || alreadyCollected.Contains(subscriptionId))
                {
                    continue;
                }

                long lifetime = panelUser.LifetimeUsedTrafficBytes;
                long bytes = previous.TryGetValue(subscriptionId, out long before) && lifetime >= before
                    ? lifetime - before
                    : 0;

                db.TrafficDaily.Add(TrafficDaily.Create(subscriptionId, day, bytes, lifetime));
                written++;
            }

            await db.SaveChangesAsync(cancellationToken);
            return written;
        }
    }
}
