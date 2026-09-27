using System.Globalization;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Notifications;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Subscriptions.Maintenance
{
    /// <summary>
    /// Every 5 minutes: if 30 minutes after access was issued there is still no first connection, the bot
    /// sends «Не получилось?» once per link (FR-BOT-07). Before nudging, the panel is asked directly in
    /// case the <c>user.first_connected</c> webhook was lost.
    /// </summary>
    public sealed record OnboardingNudgeCommand : ICommand<int>;

    internal sealed class OnboardingNudgeCommandHandler(
        IApplicationDbContext db,
        IPanelClient panel,
        INotificationScheduler scheduler,
        IDateTimeProvider clock,
        IOptions<SubscriptionOptions> options,
        ILogger<OnboardingNudgeCommandHandler> logger)
        : ICommandHandler<OnboardingNudgeCommand, int>
    {
        public async Task<Result<int>> Handle(OnboardingNudgeCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            DateTime issuedBefore = now.AddMinutes(-options.Value.OnboardingDelayMinutes);
            DateTime issuedAfter = now.AddHours(-options.Value.OnboardingWindowHours);

            List<Subscription> candidates = await db.Subscriptions
                .Where(s => (s.Status == SubscriptionStatus.Trial || s.Status == SubscriptionStatus.Active)
                    && s.FirstConnectedAt == null
                    && s.AccessIssuedAt != null
                    && s.AccessIssuedAt <= issuedBefore
                    && s.AccessIssuedAt > issuedAfter)
                .ToListAsync(cancellationToken);

            int nudged = 0;
            foreach (Subscription subscription in candidates)
            {
                string key = Notification.KeyFor(
                    NotificationKind.Onboarding,
                    subscription.Id,
                    subscription.AccessIssuedAt!.Value.Ticks.ToString(CultureInfo.InvariantCulture));

                if (await db.Notifications.AnyAsync(n => n.DedupKey == key, cancellationToken))
                {
                    continue;
                }

                if (subscription.PanelUserId is int panelUserId)
                {
                    try
                    {
                        PanelUser? panelUser = await panel.GetUserAsync(panelUserId, cancellationToken);
                        if (panelUser?.FirstConnectedAt is DateTime connectedAt)
                        {
                            subscription.MarkFirstConnected(connectedAt);
                            continue;
                        }
                    }
                    catch (PanelException ex)
                    {
                        // Better one unnecessary hint than none.
                        logger.LogWarning(ex, "Could not check first connection of panel user {PanelUserId}", panelUserId);
                    }
                }

                if (await scheduler.ScheduleAsync(subscription.UserId, NotificationKind.Onboarding, key, null, cancellationToken))
                {
                    nudged++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return nudged;
        }
    }
}
