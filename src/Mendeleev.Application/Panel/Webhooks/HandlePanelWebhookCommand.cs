using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Panel.Webhooks
{
    /// <summary>
    /// <c>POST /webhooks/remnawave</c>, events from ТЗ 24, «Вебхуки Remnawave». Handlers are idempotent,
    /// and a user event only triggers a resync when the panel really differs from the desired state —
    /// otherwise our own updates (which the panel reports as <c>user.modified</c>) would loop forever.
    /// </summary>
    public sealed record HandlePanelWebhookCommand(PanelWebhookEvent Event) : ICommand;

    internal sealed class HandlePanelWebhookCommandHandler(
        IApplicationDbContext db,
        IAlertSink alerts,
        IDateTimeProvider clock)
        : ICommandHandler<HandlePanelWebhookCommand>
    {
        public async Task<Result> Handle(HandlePanelWebhookCommand command, CancellationToken cancellationToken)
        {
            PanelWebhookEvent e = command.Event;

            switch (e.Event)
            {
                case "node.connection_lost":
                    await alerts.RaiseAsync(new Alert(AlertSeverity.Critical, $"node-lost:{e.Subject}", $"Панель потеряла связь с нодой {e.Subject}."), cancellationToken);
                    return Result.Success();

                case "node.connection_restored":
                    await alerts.RaiseAsync(new Alert(AlertSeverity.Info, $"node-restored:{e.Subject}", $"Связь с нодой {e.Subject} восстановлена."), cancellationToken);
                    return Result.Success();

                case "service.login_attempt_failed":
                    await alerts.RaiseAsync(new Alert(AlertSeverity.Critical, $"panel-login-failed:{e.Timestamp:O}", $"Неудачная попытка входа в панель: {e.Subject}."), cancellationToken);
                    return Result.Success();

                case "torrent_blocker.report":
                    return await RecordTorrentReportAsync(e, cancellationToken);
            }

            if (e.Scope != "user" || e.User is null || !User.TryParsePanelUsername(e.User.Username, out long userId))
            {
                return Result.Success();
            }

            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
            if (await db.LockUserAsync(userId, cancellationToken) is null)
            {
                await alerts.RaiseAsync(new Alert(AlertSeverity.Warning, $"panel-orphan:{e.User.Username}", $"В панели есть пользователь {e.User.Username}, которого нет в БД."), cancellationToken);
                return Result.Success();
            }

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
            if (subscription is null)
            {
                return Result.Success();
            }

            DateTime now = clock.UtcNow;
            switch (e.Event)
            {
                case "user.first_connected":
                    subscription.MarkFirstConnected(e.User.FirstConnectedAt ?? e.Timestamp);
                    break;

                case "user.limited":
                    // The trial's 10 GB are over: the subscription ends by traffic (ТЗ 22, «Триал»).
                    if (subscription.Status == SubscriptionStatus.Trial && subscription.Tariff.TrafficLimitBytes is not null)
                    {
                        subscription.TryExpire(ExpiredReason.Traffic, now);
                    }
                    else
                    {
                        // A paid tariff has no traffic limit: being limited is drift.
                        await ResyncIfDriftedAsync(subscription, e.User, now, cancellationToken);
                    }
                    break;

                case "user.deleted":
                    if (subscription.RequiresPanelUser && subscription.PanelUserId == e.User.Id)
                    {
                        subscription.ForgetPanelUser(now);
                        subscription.RequestResync(now);
                        await alerts.RaiseAsync(new Alert(AlertSeverity.Warning, $"panel-deleted:{userId}", $"Пользователя u{userId} удалили в панели вручную — создаём заново."), cancellationToken);
                    }
                    break;

                case "user.expired":
                case "user.disabled":
                case "user.enabled":
                case "user.modified":
                case "user.revoked":
                    await ResyncIfDriftedAsync(subscription, e.User, now, cancellationToken);
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }

        /// <summary>Manual edits in the panel UI are rolled back to our state (FR-PNL-06).</summary>
        private async Task ResyncIfDriftedAsync(Subscription subscription, PanelUser actual, DateTime now, CancellationToken cancellationToken)
        {
            if (subscription.PanelUserId != actual.Id || PanelUserSpecFactory.FindDrift(subscription, actual, now) is not string drift)
            {
                return;
            }

            subscription.RequestResync(now);
            db.AuditLog.Add(AuditLogEntry.BySystem(AuditActions.PanelDriftFixed, subscription.UserId, Json.Serialize(new { drift, via = "webhook" }), now));
            await alerts.RaiseAsync(new Alert(
                AlertSeverity.Warning,
                $"panel-drift:{subscription.UserId}",
                $"Пользователя u{subscription.UserId} изменили в панели ({drift}). Возвращаем состояние из БД."),
                cancellationToken);
        }

        private async Task<Result> RecordTorrentReportAsync(PanelWebhookEvent e, CancellationToken cancellationToken)
        {
            long? userId = e.User is not null && User.TryParsePanelUsername(e.User.Username, out long id) ? id : null;
            db.AuditLog.Add(AuditLogEntry.BySystem(AuditActions.TorrentBlockerReport, userId, Json.Serialize(new { at = e.Timestamp, node = e.Subject }), clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
