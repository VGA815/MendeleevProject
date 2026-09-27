using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mendeleev.Application.Payments.Webhook
{
    /// <summary>
    /// <c>POST /webhooks/payments/{provider}</c> (ТЗ 23, «Обработка вебхука»). A wrong signature is 401;
    /// an unknown payment or a wrong amount is still 200 — the aggregator must stop retrying, a human
    /// looks at it. Anything unexpected throws and becomes 500, so the aggregator retries.
    /// </summary>
    public sealed record HandlePaymentWebhookCommand(string Provider, WebhookRequest Request) : ICommand;

    internal sealed class HandlePaymentWebhookCommandHandler(
        IApplicationDbContext db,
        IPaymentProviderRegistry providers,
        IPaymentApplier applier,
        IAlertSink alerts,
        IDateTimeProvider clock,
        ILogger<HandlePaymentWebhookCommandHandler> logger)
        : ICommandHandler<HandlePaymentWebhookCommand>
    {
        public async Task<Result> Handle(HandlePaymentWebhookCommand command, CancellationToken cancellationToken)
        {
            IPaymentProvider? provider = providers.Find(command.Provider);
            if (provider is null)
            {
                return Result.Failure(PaymentErrors.UnknownProvider(command.Provider));
            }

            PaymentNotification notification;
            try
            {
                notification = await provider.ParseWebhookAsync(command.Request, cancellationToken);
            }
            catch (WebhookAuthenticationException ex)
            {
                logger.LogWarning("Rejected {Provider} webhook from {RemoteIp}: {Reason}", provider.Code, command.Request.RemoteIp, ex.Message);
                db.PaymentEvents.Add(PaymentEvent.Create(null, provider.Code, PaymentEventKind.WebhookRejected, Json.Serialize(new { reason = ex.Message, ip = command.Request.RemoteIp }), clock.UtcNow));
                await db.SaveChangesAsync(cancellationToken);
                Count("rejected");

                await alerts.RaiseOnSeriesAsync(
                    new Alert(AlertSeverity.Warning, $"payment-webhook-rejected:{provider.Code}", $"Серия вебхуков {provider.Code} с неверной подписью."),
                    threshold: 4,
                    window: TimeSpan.FromMinutes(10),
                    cancellationToken);

                return Result.Failure(PaymentErrors.InvalidSignature);
            }

            Payment? payment = await FindPaymentAsync(provider.Code, notification, cancellationToken);
            if (payment is null)
            {
                db.PaymentEvents.Add(PaymentEvent.Create(null, provider.Code, PaymentEventKind.WebhookUnknownPayment,
                    Json.Serialize(new { notification.ProviderPaymentId, notification.OrderId, state = notification.State.ToString() }), clock.UtcNow));
                await db.SaveChangesAsync(cancellationToken);
                Count("unknown");

                await alerts.RaiseAsync(new Alert(
                    AlertSeverity.Warning,
                    $"payment-webhook-unknown:{notification.ProviderPaymentId}",
                    $"Вебхук {provider.Code} по неизвестному платежу {notification.ProviderPaymentId}."),
                    cancellationToken);
                return Result.Success();
            }

            db.PaymentEvents.Add(PaymentEvent.Create(payment.Id, provider.Code, PaymentEventKind.WebhookReceived,
                Json.Serialize(new { notification.ProviderPaymentId, state = notification.State.ToString(), notification.Amount, notification.Currency }), clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);

            await PaymentStateTransitions.ApplyAsync(db, applier, alerts, clock, payment.Id, notification.ProviderPaymentId,
                notification.State, notification.Amount, notification.Currency, source: "webhook", cancellationToken);

            Count("accepted");
            return Result.Success();
        }

        private async Task<Payment?> FindPaymentAsync(string provider, PaymentNotification notification, CancellationToken cancellationToken)
        {
            Payment? payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(
                p => p.Provider == provider && p.ProviderPaymentId == notification.ProviderPaymentId,
                cancellationToken);

            if (payment is null && notification.OrderId is Guid orderId)
            {
                payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == orderId && p.Provider == provider, cancellationToken);
            }

            return payment;
        }

        private static void Count(string result) =>
            AppMetrics.WebhookRequests.Add(1,
                new KeyValuePair<string, object?>("source", "payments"),
                new KeyValuePair<string, object?>("result", result));
    }
}
