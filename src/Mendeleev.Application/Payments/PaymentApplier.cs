using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Payments
{
    public enum ApplyOutcome
    {
        Applied,
        AlreadyApplied,
        AmountMismatch,
        NotFound,
    }

    /// <summary>
    /// Turns a confirmed payment into subscription days — the one place where this happens, whatever
    /// reported the payment (webhook, «Проверить оплату», the 5-minute check, the daily reconciliation).
    /// </summary>
    /// <remarks>
    /// One transaction: payment row lock → user row lock → payment <c>succeeded</c> → subscription
    /// extended → outbox (panel sync, then the user's message). Repeated and parallel notifications apply
    /// the payment exactly once (FR-PAY-03, FR-PAY-04).
    /// </remarks>
    public interface IPaymentApplier
    {
        Task<ApplyOutcome> ApplySucceededAsync(
            Guid paymentId,
            string? providerPaymentId,
            decimal? amount,
            string? currency,
            string source,
            CancellationToken cancellationToken);
    }

    internal sealed class PaymentApplier(
        IApplicationDbContext db,
        IAlertSink alerts,
        IDateTimeProvider clock)
        : IPaymentApplier
    {
        public async Task<ApplyOutcome> ApplySucceededAsync(
            Guid paymentId,
            string? providerPaymentId,
            decimal? amount,
            string? currency,
            string source,
            CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            Payment? payment = await db.LockPaymentAsync(paymentId, cancellationToken);
            if (payment is null)
            {
                return ApplyOutcome.NotFound;
            }

            if (payment.IsFinal)
            {
                db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, payment.Provider, PaymentEventKind.AlreadyApplied, Json.Serialize(new { source }), now));
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ApplyOutcome.AlreadyApplied;
            }

            if (!payment.Matches(amount, currency))
            {
                payment.FlagForReview(now);
                db.PaymentEvents.Add(PaymentEvent.Create(
                    payment.Id,
                    payment.Provider,
                    PaymentEventKind.AmountMismatch,
                    Json.Serialize(new { source, expected = payment.Amount, expectedCurrency = payment.Currency, amount, currency }),
                    now));
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                await alerts.RaiseAsync(new Alert(
                    AlertSeverity.Critical,
                    $"payment-mismatch:{payment.Id}",
                    $"Сумма или валюта оплаты не совпали: платёж {payment.Id}, ожидали {payment.Amount} {payment.Currency}, пришло {amount} {currency}. Подписка не продлена, нужен ручной разбор.",
                    AlertAudience.TechAdminAndAdmin),
                    cancellationToken);
                return ApplyOutcome.AmountMismatch;
            }

            payment.TryMarkSucceeded(providerPaymentId, now);

            User user = await db.LockUserAsync(payment.UserId, cancellationToken)
                ?? throw new InvalidOperationException($"Payment {payment.Id} references missing user {payment.UserId}.");

            Tariff tariff = await db.Tariffs.FirstAsync(t => t.Id == payment.TariffId, cancellationToken);

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

            if (subscription is null)
            {
                subscription = Subscription.CreatePaid(user.Id, tariff, payment.DaysGranted, payment.Id, now);
                db.Subscriptions.Add(subscription);
            }
            else
            {
                subscription.ApplyPayment(tariff, payment.DaysGranted, payment.Id, now);
            }

            if (user.IsBlocked)
            {
                // The payment counts, the subscription stays disabled; the admin decides on a refund (ТЗ 22).
                subscription.Disable(now);
                db.AuditLog.Add(AuditLogEntry.BySystem(AuditActions.PaymentFromBlockedUser, user.Id, Json.Serialize(new { paymentId = payment.Id }), now));
                db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, payment.Provider, PaymentEventKind.AppliedToBlockedUser, null, now));
            }

            db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, payment.Provider, PaymentEventKind.Applied, Json.Serialize(new { source, days = payment.DaysGranted }), now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            AppMetrics.Payments.Add(1,
                new KeyValuePair<string, object?>("status", "succeeded"),
                new KeyValuePair<string, object?>("provider", payment.Provider));

            if (user.IsBlocked)
            {
                await alerts.RaiseAsync(new Alert(
                    AlertSeverity.Warning,
                    $"payment-blocked-user:{payment.Id}",
                    $"Оплата {payment.Id} от заблокированного пользователя u{user.Id}: срок продлён, доступ остаётся отключённым. Решите вопрос возврата.",
                    AlertAudience.TechAdminAndAdmin),
                    cancellationToken);
            }

            return ApplyOutcome.Applied;
        }
    }
}
