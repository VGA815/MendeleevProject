using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Payments.Reconciliation
{
    /// <summary>
    /// Every 5 minutes: asks the aggregator about pending payments of the last 2 hours, so a lost webhook
    /// does not wait for the daily reconciliation (FR-PAY-07), and cancels unpaid ones after 60 minutes
    /// (FR-PAY-11).
    /// </summary>
    public sealed record CheckPendingPaymentsCommand : ICommand<int>;

    internal sealed class CheckPendingPaymentsCommandHandler(
        IApplicationDbContext db,
        IPaymentProviderRegistry providers,
        IPaymentApplier applier,
        IAlertSink alerts,
        IDateTimeProvider clock,
        IOptions<PaymentOptions> options,
        ILogger<CheckPendingPaymentsCommandHandler> logger)
        : ICommandHandler<CheckPendingPaymentsCommand, int>
    {
        public async Task<Result<int>> Handle(CheckPendingPaymentsCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            DateTime since = now.AddHours(-options.Value.PendingCheckWindowHours);
            DateTime cancelBefore = now.AddMinutes(-options.Value.CancelAfterMinutes);

            List<Payment> pending = await db.Payments
                .AsNoTracking()
                .Where(p => (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Created) && p.CreatedAt > since)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(cancellationToken);

            int applied = 0;
            foreach (Payment payment in pending)
            {
                if (providers.Find(payment.Provider) is not IPaymentProvider provider)
                {
                    continue;
                }

                try
                {
                    ProviderPaymentStatus status = await provider.GetStatusAsync(payment.Id, payment.ProviderPaymentId, cancellationToken);
                    ProviderPaymentState state = status.State;

                    bool linkExpired = payment.LinkExpiresAt is DateTime expiresAt && expiresAt < now;
                    if (state == ProviderPaymentState.Pending && (payment.CreatedAt < cancelBefore || linkExpired))
                    {
                        // A late payment still counts: the daily reconciliation re-checks canceled ones.
                        state = ProviderPaymentState.Canceled;
                    }

                    await PaymentStateTransitions.ApplyAsync(db, applier, alerts, clock, payment.Id, status.ProviderPaymentId,
                        state, status.Amount, status.Currency, source: "pending_check", cancellationToken);

                    if (status.State == ProviderPaymentState.Succeeded)
                    {
                        applied++;
                    }
                }
                catch (PaymentProviderException ex)
                {
                    logger.LogWarning(ex, "Pending check of payment {PaymentId} failed", payment.Id);
                }
            }

            return applied;
        }
    }
}
