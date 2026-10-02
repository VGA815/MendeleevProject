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

namespace Mendeleev.Application.Payments.Check
{
    /// <summary>
    /// «Проверить оплату» (FR-PAY-09) and the cabinet's return page: asks the aggregator right away, but
    /// not more often than once in 10 seconds per payment.
    /// </summary>
    public sealed record CheckPaymentCommand(long UserId, Guid PaymentId) : ICommand<PaymentCheckResult>;

    public sealed record PaymentCheckResult(PaymentStatus Status, bool AccessPending);

    internal sealed class CheckPaymentCommandHandler(
        IApplicationDbContext db,
        IPaymentProviderRegistry providers,
        IPaymentApplier applier,
        IAlertSink alerts,
        IDateTimeProvider clock,
        IOptions<PaymentOptions> options,
        ILogger<CheckPaymentCommandHandler> logger)
        : ICommandHandler<CheckPaymentCommand, PaymentCheckResult>
    {
        public async Task<Result<PaymentCheckResult>> Handle(CheckPaymentCommand command, CancellationToken cancellationToken)
        {
            Payment? payment = await db.Payments.FirstOrDefaultAsync(
                p => p.Id == command.PaymentId && p.UserId == command.UserId,
                cancellationToken);
            if (payment is null)
            {
                return PaymentErrors.NotFound(command.PaymentId);
            }

            DateTime now = clock.UtcNow;
            bool throttled = payment.LastCheckedAt is DateTime last
                && now - last < TimeSpan.FromSeconds(options.Value.CheckThrottleSeconds);

            if (!payment.IsFinal && !throttled && providers.Find(payment.Provider) is IPaymentProvider provider)
            {
                payment.MarkChecked(now);
                await db.SaveChangesAsync(cancellationToken);

                try
                {
                    ProviderPaymentStatus status = await provider.GetStatusAsync(payment.Id, payment.ProviderPaymentId, cancellationToken);
                    await PaymentStateTransitions.ApplyAsync(db, applier, alerts, clock, payment.Id, status.ProviderPaymentId,
                        status.State, status.Amount, status.Currency, source: "user_check", cancellationToken);
                }
                catch (PaymentProviderException ex)
                {
                    logger.LogWarning(ex, "Status check of payment {PaymentId} failed", payment.Id);
                }
            }

            var current = await db.Payments
                .AsNoTracking()
                .Where(p => p.Id == payment.Id)
                .Select(p => new { p.Status })
                .FirstAsync(cancellationToken);

            bool accessPending = current.Status == PaymentStatus.Succeeded
                && await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == command.UserId, cancellationToken)
                    is { AccessPending: true };

            return new PaymentCheckResult(current.Status, accessPending);
        }
    }
}
