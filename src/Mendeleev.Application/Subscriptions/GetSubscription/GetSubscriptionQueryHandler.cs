using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mendeleev.Application.Subscriptions.GetSubscription
{
    internal sealed class GetSubscriptionQueryHandler(
        IApplicationDbContext db,
        IPanelClient panel,
        IDateTimeProvider clock,
        ILogger<GetSubscriptionQueryHandler> logger)
        : IQueryHandler<GetSubscriptionQuery, SubscriptionView>
    {
        public async Task<Result<SubscriptionView>> Handle(GetSubscriptionQuery query, CancellationToken cancellationToken)
        {
            Subscription? subscription = await db.Subscriptions
                .AsNoTracking()
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == query.UserId, cancellationToken);

            if (subscription is null || subscription.Status == SubscriptionStatus.Archived)
            {
                return SubscriptionView.None;
            }

            // Only the trial has a traffic limit, and only then is the panel asked for the usage.
            long? used = null;
            if (subscription.Tariff.TrafficLimitBytes is not null && subscription.PanelUserId is int panelUserId)
            {
                try
                {
                    used = (await panel.GetUserAsync(panelUserId, cancellationToken))?.UsedTrafficBytes;
                }
                catch (PanelException ex)
                {
                    logger.LogWarning(ex, "Could not read traffic usage for panel user {PanelUserId}", panelUserId);
                }
            }

            DateTime now = clock.UtcNow;
            return new SubscriptionView(
                Exists: true,
                subscription.Tariff.Name,
                subscription.Tariff.Tier,
                subscription.Status,
                subscription.ExpiresAt,
                subscription.DaysLeft(now),
                subscription.LinkVisible ? subscription.SubscriptionUrl : null,
                AccessPending: subscription.AccessPending,
                subscription.Tariff.TrafficLimitBytes,
                used,
                subscription.Tariff.DeviceLimit);
        }
    }
}
