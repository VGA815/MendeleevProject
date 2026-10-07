using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Promos;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Promos;
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

            User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
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

            // FR-PAY-15: the discount of the entered code. A code that stopped applying is removed, and the payment is
            // at the full price — the pay screen says so, the user does not pay the full price unawares.
            (PromoCode? promo, PromoCode? stale) = await PromoPricing.ResolveSelectedAsync(db, user, now, cancellationToken);
            if (stale is not null)
            {
                user.ClearSelectedPromo(stale.Id, now);
                await db.SaveChangesAsync(cancellationToken);
            }

            decimal amount = PromoPricing.FinalPrice(tariff.Price, promo, options.MinAmount);
            string? droppedCode = stale?.Code;

            List<Payment> recent = await db.Payments
                .AsNoTracking()
                .Where(p => p.UserId == user.Id && p.CreatedAt > hourAgo)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(cancellationToken);

            // FR-PAY-14: the active aggregator first, the others to fall back to. An unpaid payment is shown again only if
            // it is at the active one: after a switch away from a failing aggregator, its links are not handed out.
            IReadOnlyList<IPaymentProvider> candidates = await PaymentProviderSelection.CandidatesAsync(db, providers, cancellationToken);
            Payment? reusable = recent.FirstOrDefault(p =>
                string.Equals(p.Provider, candidates[0].Code, StringComparison.OrdinalIgnoreCase)
                && p.IsReusable(tariff.Id, promo?.Id, TimeSpan.FromMinutes(options.ReuseWithinMinutes), now));
            if (reusable is not null)
            {
                return Link(reusable, tariff, promo, reused: true, droppedCode);
            }

            // Invoices the aggregators refused to create do not count against the limit (FR-PAY-10).
            if (recent.Count(p => p.Status != PaymentStatus.Failed) >= options.MaxCreatesPerHour)
            {
                return PaymentErrors.TooManyPayments;
            }

            var payment = Payment.Create(user.Id, tariff, candidates[0].Code, now, promo?.Id, promo is null ? null : amount);
            db.Payments.Add(payment);
            db.PaymentEvents.Add(PaymentEvent.Create(
                payment.Id,
                payment.Provider,
                PaymentEventKind.Created,
                Json.Serialize(new { tariff = tariff.Code, amount = payment.Amount, promo = promo?.Code }),
                now));
            await db.SaveChangesAsync(cancellationToken);

            var request = new CreatePaymentRequest(
                payment.Id,
                payment.Amount,
                payment.Currency,
                options.DescriptionTemplate.Replace("{tariff}", tariff.Name, StringComparison.Ordinal),
                // In the path, not in a query string, which some aggregators refuse. TryBit takes no address per
                // payment at all: the project's own is /pay/return, and the cabinet finds the payment there.
                $"{serviceOptions.Value.SiteBaseUrl}/pay/return/{payment.Id}",
                user.Email);

            foreach (IPaymentProvider provider in candidates)
            {
                if (!string.Equals(payment.Provider, provider.Code, StringComparison.OrdinalIgnoreCase))
                {
                    // FR-PAY-14: the previous aggregator refused, the same payment goes to the next one.
                    db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, provider.Code, PaymentEventKind.ProviderFallback, Json.Serialize(new { from = payment.Provider }), clock.UtcNow));
                    payment.SwitchProvider(provider.Code, clock.UtcNow);
                    await db.SaveChangesAsync(cancellationToken);
                }

                CreatedPayment created;
                try
                {
                    created = await provider.CreateAsync(request, cancellationToken);
                }
                catch (PaymentProviderException ex)
                {
                    await RecordProviderErrorAsync(payment, provider, ex, cancellationToken);
                    continue;
                }

                payment.MarkPending(created.ProviderPaymentId, created.ConfirmationUrl, created.ExpiresAt, clock.UtcNow);
                db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, provider.Code, PaymentEventKind.ProviderCreated, Json.Serialize(new { providerPaymentId = created.ProviderPaymentId }), clock.UtcNow));
                await db.SaveChangesAsync(cancellationToken);

                AppMetrics.Payments.Add(1,
                    new KeyValuePair<string, object?>("status", "pending"),
                    new KeyValuePair<string, object?>("provider", provider.Code));

                return Link(payment, tariff, promo, reused: false, droppedCode);
            }

            return PaymentErrors.ProviderUnavailable;
        }

        private async Task RecordProviderErrorAsync(Payment payment, IPaymentProvider provider, PaymentProviderException ex, CancellationToken cancellationToken)
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
        }

        private static PaymentLink Link(Payment payment, Tariff tariff, PromoCode? promo, bool reused, string? droppedCode) =>
            new(
                payment.Id,
                payment.ConfirmationUrl!,
                payment.Amount,
                tariff.Name,
                payment.DaysGranted,
                reused,
                promo?.Code,
                promo is null ? null : tariff.Price,
                droppedCode);
    }
}
