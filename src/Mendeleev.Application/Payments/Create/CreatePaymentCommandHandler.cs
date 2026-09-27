using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Payments.Create
{
    internal sealed class CreatePaymentCommandHandler(
        IApplicationDbContext db,
        IPaymentProviderRegistry providers,
        IAlertSink alerts,
        IDateTimeProvider clock,
        IOptions<PaymentOptions> paymentOptions,
        IOptions<ServiceOptions> serviceOptions,
        ILogger<CreatePaymentCommandHandler> logger)
        : ICommandHandler<CreatePaymentCommand, PaymentLink>
    {
        public async Task<Result<PaymentLink>> Handle(CreatePaymentCommand command, CancellationToken cancellationToken)
        {
            PaymentOptions options = paymentOptions.Value;
            if (!options.Enabled)
            {
                return PaymentErrors.PaymentsDisabled;
            }

            User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }
            if (user.IsBlocked)
            {
                return UserErrors.Blocked;
            }

            Tariff? tariff = await db.Tariffs.AsNoTracking().FirstOrDefaultAsync(t => t.Code == command.TariffCode, cancellationToken);
            if (tariff is null)
            {
                return TariffErrors.NotFound(command.TariffCode);
            }
            if (!tariff.IsPurchasable)
            {
                return TariffErrors.NotPurchasable;
            }

            DateTime now = clock.UtcNow;
            DateTime hourAgo = now.AddHours(-1);

            List<Payment> recent = await db.Payments
                .AsNoTracking()
                .Where(p => p.UserId == user.Id && p.CreatedAt > hourAgo)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(cancellationToken);

            Payment? reusable = recent.FirstOrDefault(p => p.IsReusable(tariff.Id, TimeSpan.FromMinutes(options.ReuseWithinMinutes), now));
            if (reusable is not null)
            {
                return new PaymentLink(reusable.Id, reusable.ConfirmationUrl!, reusable.Amount, tariff.Name, reusable.DaysGranted, Reused: true);
            }

            if (recent.Count >= options.MaxCreatesPerHour)
            {
                return PaymentErrors.TooManyPayments;
            }

            IPaymentProvider provider = providers.Active;
            var payment = Payment.Create(user.Id, tariff, provider.Code, now);
            db.Payments.Add(payment);
            db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, provider.Code, PaymentEventKind.Created, Json.Serialize(new { tariff = tariff.Code, amount = payment.Amount }), now));
            await db.SaveChangesAsync(cancellationToken);

            CreatedPayment created;
            try
            {
                created = await provider.CreateAsync(
                    new CreatePaymentRequest(
                        payment.Id,
                        payment.Amount,
                        payment.Currency,
                        options.DescriptionTemplate.Replace("{tariff}", tariff.Name, StringComparison.Ordinal),
                        $"{serviceOptions.Value.SiteBaseUrl}/pay/return?paymentId={payment.Id}",
                        user.Email),
                    cancellationToken);
            }
            catch (PaymentProviderException ex)
            {
                logger.LogWarning(ex, "Payment provider {Provider} failed to create payment {PaymentId}", provider.Code, payment.Id);

                payment.MarkCreationFailed(clock.UtcNow);
                db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, provider.Code, PaymentEventKind.ProviderError, Json.Serialize(new { error = ex.Message }), clock.UtcNow));
                await db.SaveChangesAsync(cancellationToken);

                AppMetrics.Payments.Add(1,
                    new KeyValuePair<string, object?>("status", "create_failed"),
                    new KeyValuePair<string, object?>("provider", provider.Code));

                // ТЗ 23: alert when there are more than 3 errors in 10 minutes.
                await alerts.RaiseOnSeriesAsync(
                    new Alert(AlertSeverity.Critical, $"payment-create-failed:{provider.Code}", $"Агрегатор {provider.Code} не создаёт платежи: {ex.Message}"),
                    threshold: 4,
                    window: TimeSpan.FromMinutes(10),
                    cancellationToken);

                return PaymentErrors.ProviderUnavailable;
            }

            payment.MarkPending(created.ProviderPaymentId, created.ConfirmationUrl, created.ExpiresAt, clock.UtcNow);
            db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, provider.Code, PaymentEventKind.ProviderCreated, Json.Serialize(new { providerPaymentId = created.ProviderPaymentId }), clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);

            AppMetrics.Payments.Add(1,
                new KeyValuePair<string, object?>("status", "pending"),
                new KeyValuePair<string, object?>("provider", provider.Code));

            return new PaymentLink(payment.Id, created.ConfirmationUrl, payment.Amount, tariff.Name, payment.DaysGranted, Reused: false);
        }
    }
}
