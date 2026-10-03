using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Common;
using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Outbox;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mendeleev.Infrastructure.Panel
{
    /// <summary>
    /// Asks the panel once a minute whether it answers (ТЗ 24, «Обработка ошибок»). Unreachable for more
    /// than 5 minutes — an alert to the tech admin; reachable again — a message, once per outage.
    /// </summary>
    /// <remarks>
    /// While the panel answers, a sync that failed because it was unreachable goes out right away instead
    /// of at its next retry: the outbox schedule waits up to 15 minutes between attempts, and access paid
    /// for during an outage would otherwise come that late after the panel is back. A request the panel keeps
    /// answering with 5xx while pings pass therefore uses up its attempts in about ten minutes and ends with
    /// the outbox «не выполнена» alert. The state lives in memory: after a restart an outage is noticed again
    /// within 5 minutes.
    /// </remarks>
    internal sealed class PanelAvailabilityMonitor(
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider clock,
        OutboxSignal outboxSignal,
        ILogger<PanelAvailabilityMonitor> logger)
    {
        internal static readonly TimeSpan AlertAfter = TimeSpan.FromMinutes(5);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime? _unreachableSince;
        private bool _alerted;

        public async Task CheckAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IPanelClient panel = scope.ServiceProvider.GetRequiredService<IPanelClient>();
                IAlertSink alerts = scope.ServiceProvider.GetRequiredService<IAlertSink>();

                // The moment of the check, not of the answer: the client retries a failing ping for a few
                // seconds, and that jitter pushed the 5-minute mark to the next run (staging, 03.10.2026).
                DateTime checkedAt = clock.UtcNow;
                try
                {
                    await panel.PingAsync(cancellationToken);
                }
                catch (PanelException ex)
                {
                    await ReportUnreachableAsync(alerts, ex, checkedAt, cancellationToken);
                    return;
                }

                await ReportReachableAsync(alerts, checkedAt, cancellationToken);
                await ResumeWaitingSyncsAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ReportUnreachableAsync(IAlertSink alerts, PanelException ex, DateTime now, CancellationToken cancellationToken)
        {
            DateTime since = _unreachableSince ??= now;
            logger.LogWarning("Panel is unreachable since {Since:O}: {Error}", since, ex.Message);

            if (_alerted || now - since < AlertAfter)
            {
                return;
            }

            _alerted = true;
            await alerts.RaiseAsync(new Alert(
                AlertSeverity.Critical,
                "panel-down",
                $"Панель недоступна с {MoscowTime.FromUtc(since):HH:mm} МСК ({Minutes(now - since)} мин): {ex.Message}. "
                + "Оплаты принимаются, доступ выдастся после восстановления."),
                cancellationToken);
        }

        private async Task ReportReachableAsync(IAlertSink alerts, DateTime now, CancellationToken cancellationToken)
        {
            if (_unreachableSince is not DateTime since)
            {
                return;
            }

            _unreachableSince = null;
            logger.LogInformation("Panel is reachable again after {Minutes} min", Minutes(now - since));

            if (_alerted)
            {
                _alerted = false;
                await alerts.RaiseAsync(new Alert(
                    AlertSeverity.Info,
                    "panel-up",
                    $"Панель снова доступна, простой {Minutes(now - since)} мин. Отложенные изменения отправляются."),
                    cancellationToken);
            }
        }

        private async Task ResumeWaitingSyncsAsync(ApplicationDbContext db, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            string unreachable = nameof(PanelUnavailableException) + ":";

            int resumed = await db.OutboxMessages
                .Where(m => m.Status == OutboxStatus.Pending
                    && m.NextAttemptAt > now
                    && m.LastError != null
                    && m.LastError.StartsWith(unreachable))
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.NextAttemptAt, now), cancellationToken);

            if (resumed > 0)
            {
                logger.LogInformation("Panel answers: {Count} outbox task(s) waiting for it go out now", resumed);
                outboxSignal.Notify();
            }
        }

        private static int Minutes(TimeSpan span) => (int)Math.Round(span.TotalMinutes);
    }
}
