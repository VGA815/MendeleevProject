using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Panel.Webhooks;

namespace Mendeleev.Web.Endpoints.Webhooks
{
    /// <summary><c>POST /webhooks/remnawave</c> (FR-PNL-13): HMAC and age are checked before anything else.</summary>
    internal sealed class RemnawaveWebhook : IEndpoint
    {
        public void MapEndpoint(IEndpointRouteBuilder app)
        {
            app.MapPost("webhooks/remnawave", async (
                HttpRequest request,
                IPanelWebhookParser parser,
                ICommandHandler<HandlePanelWebhookCommand> handler,
                ILogger<RemnawaveWebhook> logger,
                CancellationToken cancellationToken) =>
            {
                var body = await WebhookRequestReader.ReadAsync(request, cancellationToken);
                if (body is null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                PanelWebhookEvent panelEvent;
                try
                {
                    panelEvent = parser.Parse(body);
                }
                catch (WebhookAuthenticationException ex)
                {
                    logger.LogWarning("Rejected panel webhook from {RemoteIp}: {Reason}", body.RemoteIp, ex.Message);
                    Count("rejected");
                    return Results.Unauthorized();
                }

                await handler.Handle(new HandlePanelWebhookCommand(panelEvent), cancellationToken);
                Count("accepted");
                return Results.Ok();
            })
            .RequireRateLimiting(RateLimitPolicies.Webhooks)
            .ExcludeFromDescription();
        }

        private static void Count(string result) =>
            AppMetrics.WebhookRequests.Add(1,
                new KeyValuePair<string, object?>("source", "remnawave"),
                new KeyValuePair<string, object?>("result", result));
    }
}
