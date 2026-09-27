using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Payments
{
    /// <summary>Applies a state reported by the aggregator to our payment — shared by all the paths that learn it.</summary>
    internal static class PaymentStateTransitions
    {
        public static async Task<ProviderPaymentState> ApplyAsync(
            IApplicationDbContext db,
            IPaymentApplier applier,
            IAlertSink alerts,
            IDateTimeProvider clock,
            Guid paymentId,
            string? providerPaymentId,
            ProviderPaymentState state,
            decimal? amount,
            string? currency,
            string source,
            CancellationToken cancellationToken)
        {
            switch (state)
            {
                case ProviderPaymentState.Succeeded:
                    await applier.ApplySucceededAsync(paymentId, providerPaymentId, amount, currency, source, cancellationToken);
                    break;

                case ProviderPaymentState.Canceled:
                case ProviderPaymentState.Failed:
                    await CloseAsync(db, clock, paymentId, state, cancellationToken);
                    break;

                case ProviderPaymentState.Refunded:
                    await MarkRefundedAsync(db, clock, paymentId, cancellationToken);
                    await alerts.RaiseAsync(new Alert(
                        AlertSeverity.Warning,
                        $"payment-refunded:{paymentId}",
                        $"Агрегатор сообщил о возврате платежа {paymentId}. Подписка не изменена: решение по доступу — за админом.",
                        AlertAudience.TechAdminAndAdmin),
                        cancellationToken);
                    break;
            }

            return state;
        }

        private static async Task CloseAsync(IApplicationDbContext db, IDateTimeProvider clock, Guid paymentId, ProviderPaymentState state, CancellationToken cancellationToken)
        {
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
            Payment? payment = await db.LockPaymentAsync(paymentId, cancellationToken);
            if (payment is null || payment.IsFinal)
            {
                return;
            }

            DateTime now = clock.UtcNow;
            if (state == ProviderPaymentState.Canceled)
            {
                payment.Cancel(now);
                db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, payment.Provider, PaymentEventKind.Canceled, null, now));
            }
            else
            {
                payment.MarkFailed(now);
                db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, payment.Provider, PaymentEventKind.Failed, null, now));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        private static async Task MarkRefundedAsync(IApplicationDbContext db, IDateTimeProvider clock, Guid paymentId, CancellationToken cancellationToken)
        {
            Payment? payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
            if (payment is null || payment.Status != PaymentStatus.Succeeded)
            {
                return;
            }

            DateTime now = clock.UtcNow;
            payment.MarkRefunded(now);
            db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, payment.Provider, PaymentEventKind.Refunded, null, now));
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
