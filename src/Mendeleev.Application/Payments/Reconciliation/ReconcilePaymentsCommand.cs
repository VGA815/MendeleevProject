using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Payments.Reconciliation
{
    /// <summary>
    /// Daily at 04:00 МСК (FR-PAY-06). Compares the last 48 hours with the aggregator:
    /// paid there but not here → applied and alerted; paid here but not there → critical alert, the
    /// subscription is not revoked automatically; amounts differ → alert.
    /// </summary>
    /// <remarks>
    /// Our payment row is created before the aggregator is called, so every started payment has our order
    /// id. Aggregators without a listing API are reconciled by re-querying each of our payments.
    /// </remarks>
    public sealed record ReconcilePaymentsCommand : ICommand<ReconciliationSummary>;

    public sealed record ReconciliationSummary(
        int Checked,
        int AppliedMissed,
        int Mismatches,
        int SucceededCount,
        decimal SucceededSum);

    internal sealed class ReconcilePaymentsCommandHandler(
        IApplicationDbContext db,
        IPaymentProviderRegistry providers,
        IPaymentApplier applier,
        IAlertSink alerts,
        IDateTimeProvider clock,
        IOptions<PaymentOptions> options,
        ILogger<ReconcilePaymentsCommandHandler> logger)
        : ICommandHandler<ReconcilePaymentsCommand, ReconciliationSummary>
    {
        public async Task<Result<ReconciliationSummary>> Handle(ReconcilePaymentsCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            DateTime since = now.AddHours(-options.Value.ReconciliationWindowHours);

            List<Payment> ours = await db.Payments
                .AsNoTracking()
                .Where(p => p.CreatedAt > since && p.ProviderPaymentId != null)
                .ToListAsync(cancellationToken);

            int checkedCount = 0, applied = 0, mismatches = 0;
            var problems = new List<string>();

            foreach (IGrouping<string, Payment> group in ours.GroupBy(p => p.Provider))
            {
                if (providers.Find(group.Key) is not IPaymentProvider provider)
                {
                    continue;
                }

                Dictionary<string, ProviderPayment>? listed = null;
                if (provider.SupportsListing)
                {
                    listed = [];
                    await foreach (ProviderPayment remote in provider.ListAsync(since, now, cancellationToken))
                    {
                        listed[remote.ProviderPaymentId] = remote;
                    }

                    var known = group.Select(p => p.ProviderPaymentId!).ToHashSet();
                    foreach (ProviderPayment remote in listed.Values.Where(r => r.State == ProviderPaymentState.Succeeded && !known.Contains(r.ProviderPaymentId)))
                    {
                        mismatches++;
                        problems.Add($"у агрегатора оплачен неизвестный нам платёж {remote.ProviderPaymentId} ({remote.Amount} {remote.Currency})");
                    }
                }

                foreach (Payment payment in group)
                {
                    checkedCount++;
                    try
                    {
                        (ProviderPaymentState state, decimal? amount, string? currency) = listed is not null && listed.TryGetValue(payment.ProviderPaymentId!, out ProviderPayment? remote)
                            ? (remote.State, remote.Amount, remote.Currency)
                            : await StatusAsync(provider, payment, cancellationToken);

                        if (payment.Status == PaymentStatus.Succeeded && state is not (ProviderPaymentState.Succeeded or ProviderPaymentState.Refunded))
                        {
                            mismatches++;
                            problems.Add($"у нас оплачен {payment.Id}, у агрегатора — {state}");
                            db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, payment.Provider, PaymentEventKind.ReconciliationMismatch, Json.Serialize(new { remote = state.ToString() }), now));
                            continue;
                        }

                        if (payment.Status != PaymentStatus.Succeeded && payment.Status != PaymentStatus.Refunded && state == ProviderPaymentState.Succeeded)
                        {
                            ApplyOutcome outcome = await applier.ApplySucceededAsync(payment.Id, payment.ProviderPaymentId, amount, currency, "reconciliation", cancellationToken);
                            if (outcome == ApplyOutcome.Applied)
                            {
                                applied++;
                                problems.Add($"применена пропущенная оплата {payment.Id}");
                            }
                            else if (outcome == ApplyOutcome.AmountMismatch)
                            {
                                mismatches++;
                            }
                        }
                    }
                    catch (PaymentProviderException ex)
                    {
                        logger.LogWarning(ex, "Reconciliation could not check payment {PaymentId}", payment.Id);
                    }
                }
            }

            DateTime dayAgo = now.AddHours(-24);
            var succeeded = await db.Payments
                .AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Succeeded && p.PaidAt > dayAgo)
                .Select(p => p.Amount)
                .ToListAsync(cancellationToken);

            var summary = new ReconciliationSummary(checkedCount, applied, mismatches, succeeded.Count, succeeded.Sum());

            db.AuditLog.Add(AuditLogEntry.BySystem(AuditActions.PaymentsReconciled, null, Json.Serialize(summary), now));
            await db.SaveChangesAsync(cancellationToken);

            if (applied + mismatches > 0)
            {
                AppMetrics.ReconcileDrift.Add(applied + mismatches, new KeyValuePair<string, object?>("source", "aggregator"));
                await alerts.RaiseAsync(new Alert(
                    mismatches > 0 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    $"payment-reconciliation:{now:yyyyMMdd}",
                    "Сверка платежей нашла расхождения:\n" + string.Join("\n", problems.Take(20)),
                    AlertAudience.TechAdminAndAdmin),
                    cancellationToken);
            }

            return summary;
        }

        private static async Task<(ProviderPaymentState, decimal?, string?)> StatusAsync(IPaymentProvider provider, Payment payment, CancellationToken cancellationToken)
        {
            ProviderPaymentStatus status = await provider.GetStatusAsync(payment.Id, payment.ProviderPaymentId, cancellationToken);
            return (status.State, status.Amount, status.Currency);
        }
    }
}
