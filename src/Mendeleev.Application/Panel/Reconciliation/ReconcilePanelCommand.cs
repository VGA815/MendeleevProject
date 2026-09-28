using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Panel.Reconciliation
{
    /// <summary>
    /// Every 10 minutes (FR-PNL-05): reads all panel users page by page and compares them with the
    /// database. Differences in term, status, limits and squads are fixed from our data (a resync goes
    /// through the outbox), subscriptions missing in the panel are created, and a panel user without a
    /// match here is only reported, never deleted automatically. A link the panel now builds on another
    /// domain (the subscriptions domain was switched) is taken over as is.
    /// </summary>
    public sealed record ReconcilePanelCommand : ICommand<PanelReconciliationSummary>;

    public sealed record PanelReconciliationSummary(int PanelUsers, int Drifted, int Missing, int Orphans, int LinksRefreshed);

    internal sealed class ReconcilePanelCommandHandler(
        IApplicationDbContext db,
        IPanelClient panel,
        IAlertSink alerts,
        IDateTimeProvider clock)
        : ICommandHandler<ReconcilePanelCommand, PanelReconciliationSummary>
    {
        private static readonly TimeSpan InFlightGrace = TimeSpan.FromMinutes(15);

        public async Task<Result<PanelReconciliationSummary>> Handle(ReconcilePanelCommand command, CancellationToken cancellationToken)
        {
            var panelUsers = new Dictionary<long, PanelUser>();
            var orphans = new List<string>();

            try
            {
                await foreach (PanelUser panelUser in panel.ListUsersAsync(cancellationToken))
                {
                    if (User.TryParsePanelUsername(panelUser.Username, out long userId))
                    {
                        panelUsers[userId] = panelUser;
                    }
                    else
                    {
                        orphans.Add(panelUser.Username);
                    }
                }
            }
            catch (PanelException ex)
            {
                await alerts.RaiseAsync(new Alert(AlertSeverity.Warning, "panel-reconcile-unavailable", $"Сверка с панелью не удалась: {ex.Message}"), cancellationToken);
                return Result.Failure<PanelReconciliationSummary>(Error.ServiceUnavailable("Panel.Unavailable", ex.Message));
            }

            List<Subscription> subscriptions = await db.Subscriptions
                .AsNoTracking()
                .Include(s => s.Tariff)
                .Where(s => s.Status != SubscriptionStatus.Archived || s.PanelUserId != null)
                .ToListAsync(cancellationToken);

            DateTime now = clock.UtcNow;
            var drifted = new List<(long UserId, string Drift)>();
            int missing = 0;
            var matched = new HashSet<long>();
            var newLinks = new List<(long UserId, string Url)>();

            foreach (Subscription subscription in subscriptions)
            {
                matched.Add(subscription.UserId);

                // A fresh change is still on its way through the outbox; it is not drift yet.
                if (subscription.SyncState == SyncState.Pending && subscription.UpdatedAt > now - InFlightGrace)
                {
                    continue;
                }

                panelUsers.TryGetValue(subscription.UserId, out PanelUser? actual);

                string? drift = (actual, subscription.RequiresPanelUser) switch
                {
                    (null, true) => "нет в панели",
                    (null, false) => null,
                    _ when subscription.Status == SubscriptionStatus.Archived => "должен быть удалён",
                    _ when subscription.Status == SubscriptionStatus.Trial && actual!.Status == PanelUserStatus.Limited => "trial-limited",
                    _ when subscription.PanelUserId != actual!.Id => "другой id в панели",
                    _ => PanelUserSpecFactory.FindDrift(subscription, actual!, now),
                };

                if (drift is null)
                {
                    if (actual is { SubscriptionUrl.Length: > 0 } && subscription.SubscriptionUrl is not null
                        && actual.ShortUuid == subscription.PanelShortUuid
                        && !string.Equals(actual.SubscriptionUrl, subscription.SubscriptionUrl, StringComparison.Ordinal))
                    {
                        newLinks.Add((subscription.UserId, actual.SubscriptionUrl));
                    }
                    continue;
                }

                if (actual is null)
                {
                    missing++;
                }

                await FixAsync(subscription.UserId, drift, now, cancellationToken);
                if (drift != "trial-limited")
                {
                    drifted.Add((subscription.UserId, drift));
                }
            }

            int linksRefreshed = 0;
            foreach ((long userId, string url) in newLinks)
            {
                linksRefreshed += await RefreshLinkAsync(userId, url, now, cancellationToken) ? 1 : 0;
            }

            orphans.AddRange(panelUsers.Keys.Where(id => !matched.Contains(id)).Select(User.PanelUsernameFor));

            if (drifted.Count > 0)
            {
                AppMetrics.ReconcileDrift.Add(drifted.Count, new KeyValuePair<string, object?>("source", "panel"));
            }

            if (drifted.Count > 0 || orphans.Count > 0)
            {
                string text = "Сверка с панелью:\n"
                    + string.Join("\n", drifted.Take(15).Select(d => $"{User.PanelUsernameFor(d.UserId)}: {d.Drift}"))
                    + (orphans.Count > 0 ? $"\nВ панели без пары в БД ({orphans.Count}): {string.Join(", ", orphans.Take(15))}" : string.Empty);
                await alerts.RaiseAsync(new Alert(AlertSeverity.Warning, "panel-reconcile", text), cancellationToken);
            }

            return new PanelReconciliationSummary(panelUsers.Count + orphans.Count, drifted.Count, missing, orphans.Count, linksRefreshed);
        }

        private async Task<bool> RefreshLinkAsync(long userId, string url, DateTime now, CancellationToken cancellationToken)
        {
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
            await db.LockUserAsync(userId, cancellationToken);

            Subscription? subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
            if (subscription is null || !subscription.RefreshSubscriptionUrl(url, now))
            {
                return false;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        private async Task FixAsync(long userId, string drift, DateTime now, CancellationToken cancellationToken)
        {
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
            await db.LockUserAsync(userId, cancellationToken);

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
            if (subscription is null)
            {
                return;
            }

            if (drift == "trial-limited")
            {
                // The limited webhook was lost: end the trial by traffic now.
                subscription.TryExpire(ExpiredReason.Traffic, now);
            }
            else
            {
                if (drift == "нет в панели")
                {
                    subscription.ForgetPanelUser(now);
                }
                subscription.RequestResync(now);
                db.AuditLog.Add(AuditLogEntry.BySystem(AuditActions.PanelDriftFixed, userId, Json.Serialize(new { drift, via = "reconciliation" }), now));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
