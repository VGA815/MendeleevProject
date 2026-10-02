using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Mendeleev.Application.Panel
{
    public interface IPanelSynchronizer
    {
        /// <summary>
        /// Brings the panel in line with the user's subscription: creates, updates, disables or deletes
        /// the panel user. Always reads the current state from the database, never from the event, so a
        /// stale event cannot roll back a fresh change (ТЗ 24, «Синхронизация с панелью»).
        /// </summary>
        /// <param name="afterSync">
        /// Runs inside the same transaction after a successful sync — used to schedule the notification
        /// that must only go out once access actually works.
        /// </param>
        Task<PanelSyncOutcome> SyncAsync(
            long userId,
            Func<Subscription, Task>? afterSync,
            CancellationToken cancellationToken);
    }

    public enum PanelSyncOutcome
    {
        NoSubscription,
        Synced,
        Deleted,
        NothingToDo,
    }

    /// <remarks>
    /// The whole sync runs under the user's row lock, including the HTTP calls to the panel. A payment for
    /// the same user waits for it instead of racing it; at our volumes this costs nothing, and it keeps
    /// "last writer wins" out of the picture.
    /// </remarks>
    internal sealed class PanelSynchronizer(
        IApplicationDbContext db,
        IPanelClient panel,
        IDateTimeProvider clock,
        IAlertSink alerts,
        ILogger<PanelSynchronizer> logger)
        : IPanelSynchronizer
    {
        public async Task<PanelSyncOutcome> SyncAsync(
            long userId,
            Func<Subscription, Task>? afterSync,
            CancellationToken cancellationToken)
        {
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            if (await db.LockUserAsync(userId, cancellationToken) is null)
            {
                return PanelSyncOutcome.NoSubscription;
            }

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
            if (subscription is null)
            {
                return PanelSyncOutcome.NoSubscription;
            }

            PanelSyncOutcome outcome;
            try
            {
                outcome = await SyncCoreAsync(subscription, cancellationToken);
            }
            catch (PanelContractException ex)
            {
                AppMetrics.PanelSyncFailures.Add(1);
                subscription.MarkSyncFailed(clock.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await alerts.RaiseAsync(new Alert(
                    AlertSeverity.Critical,
                    $"panel-contract:{userId}",
                    $"Панель отклонила запрос для {User.PanelUsernameFor(userId)}: {ex.Message}. Похоже на ошибку в коде или несовместимую версию панели."),
                    cancellationToken);
                throw;
            }
            catch (PanelException)
            {
                AppMetrics.PanelSyncFailures.Add(1);
                // The transaction is rolled back; a later save in this scope must not write half a sync.
                db.DiscardChanges();
                throw;
            }

            if (afterSync is not null && outcome is PanelSyncOutcome.Synced or PanelSyncOutcome.NothingToDo)
            {
                await afterSync(subscription);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return outcome;
        }

        private async Task<PanelSyncOutcome> SyncCoreAsync(Subscription subscription, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;

            if (subscription.Status == SubscriptionStatus.Archived)
            {
                if (subscription.PanelUserId is not int archivedPanelUserId)
                {
                    subscription.MarkNothingToSync(now);
                    return PanelSyncOutcome.NothingToDo;
                }

                await panel.DeleteUserAsync(archivedPanelUserId, cancellationToken);
                subscription.MarkPanelUserDeleted(now);
                db.AuditLog.Add(AuditLogEntry.BySystem(
                    AuditActions.PanelUserDeleted,
                    subscription.UserId,
                    Json.Serialize(new { panelUserId = archivedPanelUserId }),
                    now));
                return PanelSyncOutcome.Deleted;
            }

            PanelUserSpec spec = PanelUserSpecFactory.Create(subscription);

            if (subscription.Status == SubscriptionStatus.Expired || PanelUserSpecFactory.HasLapsed(subscription, now))
            {
                // The panel expires users by itself at expireAt; we only step in if it still lets the user
                // through (extended by hand, or our term was moved back and a past date cannot be sent).
                if (subscription.PanelUserId is int expiredPanelUserId)
                {
                    PanelUser? actual = await panel.GetUserAsync(expiredPanelUserId, cancellationToken);
                    if (actual is not null && PanelUserSpecFactory.FindDrift(subscription, actual, now) is string drift)
                    {
                        await panel.UpdateUserAsync(expiredPanelUserId, spec with { Enabled = false }, cancellationToken);
                        await ReportDriftAsync(subscription.UserId, drift, cancellationToken);
                    }
                }
                subscription.MarkNothingToSync(now);
                return PanelSyncOutcome.NothingToDo;
            }

            PanelUser result;
            if (subscription.PanelUserId is not int panelUserId)
            {
                result = await CreateOrAdoptAsync(spec, cancellationToken);
            }
            else
            {
                try
                {
                    result = await panel.UpdateUserAsync(panelUserId, spec, cancellationToken);
                }
                catch (PanelUserNotFoundException)
                {
                    // Recreated with the same short UUID, so the user's link keeps working.
                    subscription.ForgetPanelUser(now);
                    result = await CreateOrAdoptAsync(spec, cancellationToken);
                    db.AuditLog.Add(AuditLogEntry.BySystem(
                        AuditActions.PanelUserRecreated,
                        subscription.UserId,
                        Json.Serialize(new { oldPanelUserId = panelUserId, newPanelUserId = result.Id }),
                        now));
                    await alerts.RaiseAsync(new Alert(
                        AlertSeverity.Warning,
                        $"panel-user-missing:{subscription.UserId}",
                        $"Пользователь {User.PanelUsernameFor(subscription.UserId)} пропал из панели и создан заново."),
                        cancellationToken);
                }
            }

            if (!string.Equals(result.ShortUuid, subscription.PanelShortUuid, StringComparison.Ordinal))
            {
                result = await panel.RevokeSubscriptionAsync(result.Id, subscription.PanelShortUuid, cancellationToken);
            }

            subscription.MarkSynced(result.Id, result.VlessUuid, result.SubscriptionUrl, now);
            return PanelSyncOutcome.Synced;
        }

        /// <summary>
        /// A create that timed out may still have succeeded; the retry then hits "username exists" and
        /// adopts the existing user instead of failing forever.
        /// </summary>
        private async Task<PanelUser> CreateOrAdoptAsync(PanelUserSpec spec, CancellationToken cancellationToken)
        {
            try
            {
                return await panel.CreateUserAsync(spec, cancellationToken);
            }
            catch (PanelConflictException)
            {
                PanelUser existing = await panel.GetUserByUsernameAsync(spec.Username, cancellationToken)
                    ?? throw new PanelUnavailableException($"Panel reported '{spec.Username}' as existing but cannot find it.");

                logger.LogWarning("Adopting existing panel user {PanelUserId} for {Username}", existing.Id, spec.Username);
                return await panel.UpdateUserAsync(existing.Id, spec, cancellationToken);
            }
        }

        private async Task ReportDriftAsync(long userId, string drift, CancellationToken cancellationToken)
        {
            AppMetrics.ReconcileDrift.Add(1, new KeyValuePair<string, object?>("source", "panel"));
            db.AuditLog.Add(AuditLogEntry.BySystem(
                AuditActions.PanelDriftFixed,
                userId,
                Json.Serialize(new { drift }),
                clock.UtcNow));
            await alerts.RaiseAsync(new Alert(
                AlertSeverity.Warning,
                $"panel-drift:{userId}",
                $"Расхождение с панелью у {User.PanelUsernameFor(userId)}: {drift}. Исправлено по данным БД."),
                cancellationToken);
        }
    }
}
