using System.Globalization;
using System.Text;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Admin.Anomalies;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Payments.Reconciliation;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Admin.Stats
{
    /// <summary>
    /// The daily summary to admins at 10:00 МСК: yesterday's numbers, the result of the night's payment
    /// reconciliation (ТЗ 28, «Статистика») and the anomaly report (FR-ADM-17) — one message to admins and tech admins.
    /// </summary>
    public sealed record DailySummaryCommand : ICommand<int>;

    internal sealed class DailySummaryCommandHandler(
        IApplicationDbContext db,
        IUserMessenger messenger,
        IDateTimeProvider clock,
        IOptions<AnomalyOptions> anomalyOptions,
        ILogger<DailySummaryCommandHandler> logger)
        : ICommandHandler<DailySummaryCommand, int>
    {
        public async Task<Result<int>> Handle(DailySummaryCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            SalesStats stats = await StatsCalculator.CalculateAsync(db, StatsPeriod.Yesterday, now, cancellationToken);

            string? reconciliationJson = await db.AuditLog
                .AsNoTracking()
                .Where(a => a.Action == AuditActions.PaymentsReconciled && a.CreatedAt > now.AddHours(-24))
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => a.Details)
                .FirstOrDefaultAsync(cancellationToken);
            ReconciliationSummary? reconciliation = Json.Deserialize<ReconciliationSummary>(reconciliationJson);

            var text = new StringBuilder();
            text.Append("<b>Сводка за вчера</b>\n");
            text.Append(StatsFormatter.Format(stats));
            text.Append('\n');
            text.Append(reconciliation is null
                ? "Сверка платежей: не выполнялась за последние сутки ⚠️"
                : string.Create(CultureInfo.InvariantCulture,
                    $"Сверка платежей: проверено {reconciliation.Checked}, применено пропущенных {reconciliation.AppliedMissed}, расхождений {reconciliation.Mismatches}"));

            AnomalyReport anomalies = await AnomalyDetector.DailyAsync(db, anomalyOptions.Value, now, cancellationToken);
            text.Append("\n\n").Append(AnomalyFormatter.Format(anomalies, anomalyOptions.Value.ReportListLimit));

            List<long> admins = await db.Staff
                .AsNoTracking()
                .Where(s => s.IsActive && (s.Role == StaffRole.Admin || s.Role == StaffRole.TechAdmin))
                .Select(s => s.TelegramId)
                .ToListAsync(cancellationToken);

            int sent = 0;
            foreach (long chatId in admins)
            {
                try
                {
                    if (await messenger.SendTextAsync(chatId, text.ToString(), cancellationToken) == DeliveryResult.Sent)
                    {
                        sent++;
                    }
                }
                catch (DeliveryDeferredException ex)
                {
                    logger.LogWarning(ex, "Daily summary not delivered to a staff chat");
                }
            }

            return sent;
        }
    }

    public static class StatsFormatter
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public static string Format(SalesStats s)
        {
            var text = new StringBuilder();
            text.Append(Ru, $"Новых пользователей: {s.NewUsers}\n");
            text.Append(Ru, $"Выдано триалов: {s.TrialsIssued}, из них оплатили: {s.TrialsConverted} ({s.TrialConversion:P0})\n");
            text.Append(Ru, $"Платежей: {s.PaymentsCount} на {s.PaymentsSum:N0} ₽");
            text.Append(s.ManualCount > 0 ? string.Create(Ru, $", из них вне системы: {s.ManualCount} на {s.ManualSum:N0} ₽\n") : "\n");
            foreach (TariffSales sale in s.Sales)
            {
                text.Append(Ru, $"  • {System.Net.WebUtility.HtmlEncode(sale.TariffName)}: {sale.Count} на {sale.Sum:N0} ₽\n");
            }
            text.Append(Ru, $"Активных подписок: {s.ActivePaid} платных, {s.ActiveTrials} триалов\n");
            text.Append(Ru, $"Заканчиваются в ближайшие 7 дней: {s.ExpiringIn7Days}\n");
            text.Append(Ru, $"Не продлили после окончания: {s.NotRenewed}\n");
            text.Append(Ru, $"Возвраты: {s.Refunds}");
            return text.ToString();
        }
    }
}
