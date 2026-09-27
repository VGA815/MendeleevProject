using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Telegram;
using Mendeleev.Infrastructure.Telegram;
using Mendeleev.Web.Bot.Infrastructure;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Mendeleev.Web.Endpoints.Webhooks
{
    /// <summary>
    /// <c>POST /webhooks/telegram</c> (FR-BOT-01): the secret header is checked, a redelivered
    /// <c>update_id</c> is dropped, the update is queued and Telegram gets 200 at once.
    /// </summary>
    internal sealed class TelegramWebhook : IEndpoint
    {
        public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

        public void MapEndpoint(IEndpointRouteBuilder app)
        {
            app.MapPost("webhooks/telegram", async (
                HttpRequest request,
                IOptions<TelegramOptions> options,
                ITelegramUpdateDeduplicator deduplicator,
                BotUpdateQueue queue,
                CancellationToken cancellationToken) =>
            {
                if (!SecretMatches(request.Headers[SecretHeader].ToString(), options.Value.WebhookSecret))
                {
                    Count("rejected");
                    return Results.Unauthorized();
                }

                var body = await WebhookRequestReader.ReadAsync(request, cancellationToken);
                if (body is null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                Update? update;
                try
                {
                    update = JsonSerializer.Deserialize<Update>(body.Body, JsonBotAPI.Options);
                }
                catch (JsonException)
                {
                    Count("rejected");
                    return Results.BadRequest();
                }

                if (update is null)
                {
                    return Results.BadRequest();
                }

                if (await deduplicator.TryRegisterAsync(update.Id, cancellationToken))
                {
                    queue.Enqueue(update);
                }

                Count("accepted");
                return Results.Ok();
            })
            .RequireRateLimiting(RateLimitPolicies.Webhooks)
            .ExcludeFromDescription();
        }

        private static bool SecretMatches(string received, string expected) =>
            !string.IsNullOrEmpty(expected)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));

        private static void Count(string result) =>
            AppMetrics.WebhookRequests.Add(1,
                new KeyValuePair<string, object?>("source", "telegram"),
                new KeyValuePair<string, object?>("result", result));
    }
}
