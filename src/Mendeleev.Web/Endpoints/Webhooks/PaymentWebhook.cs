using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.SharedKernel;

namespace Mendeleev.Web.Endpoints.Webhooks
{
    /// <summary>
    /// <c>POST /webhooks/payments/{provider}</c> (ТЗ 23, «Обработка вебхука»): 401 for a bad signature,
    /// 200 once the notification is recorded (even an unknown payment — a human looks at it), 500 when
    /// something is down so that the aggregator retries.
    /// </summary>
    internal sealed class PaymentWebhook : IEndpoint
    {
        public void MapEndpoint(IEndpointRouteBuilder app)
        {
            app.MapPost("webhooks/payments/{provider}", async (
                string provider,
                HttpRequest request,
                ICommandHandler<HandlePaymentWebhookCommand> handler,
                CancellationToken cancellationToken) =>
            {
                var body = await WebhookRequestReader.ReadAsync(request, cancellationToken);
                if (body is null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                Result result = await handler.Handle(new HandlePaymentWebhookCommand(provider, body), cancellationToken);

                return result.IsSuccess
                    ? Results.Ok()
                    : result.Error.Type switch
                    {
                        ErrorType.Unauthorized => Results.Unauthorized(),
                        ErrorType.NotFound => Results.NotFound(),
                        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                    };
            })
            .RequireRateLimiting(RateLimitPolicies.Webhooks)
            .ExcludeFromDescription();
        }
    }
}
