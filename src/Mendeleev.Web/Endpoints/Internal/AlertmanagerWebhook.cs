using System.Text.Json;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Web.Endpoints.Webhooks;

namespace Mendeleev.Web.Endpoints.Internal
{
    /// <summary>
    /// <c>POST /internal/alertmanager</c>: the webhook of Alertmanager in the <c>monitoring</c> profile (ТЗ 40, «Мониторинг
    /// и алерты»). Prometheus alerts reach the alert bot and ntfy through the same <see cref="IAlertSink"/> as the
    /// service's own (ТЗ 30, «Алерты»). Internal only, as <c>/metrics</c>: Caddy does not route it, and a proxied
    /// request is refused (Program.cs).
    /// </summary>
    internal sealed class AlertmanagerWebhook : IEndpoint
    {
        public void MapEndpoint(IEndpointRouteBuilder app)
        {
            app.MapPost("internal/alertmanager", async (
                HttpRequest request,
                IAlertSink alerts,
                ILogger<AlertmanagerWebhook> logger,
                CancellationToken cancellationToken) =>
            {
                WebhookRequest? body = await WebhookRequestReader.ReadAsync(request, cancellationToken);
                if (body is null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                Notification? notification;
                try
                {
                    notification = JsonSerializer.Deserialize<Notification>(body.Body, JsonSerializerOptions.Web);
                }
                catch (JsonException)
                {
                    logger.LogWarning("Alertmanager sent an unreadable notification");
                    Count("rejected");
                    return Results.BadRequest();
                }

                foreach (Alert alert in ToAlerts(notification))
                {
                    await alerts.RaiseAsync(alert, cancellationToken);
                }
                Count("accepted");
                return Results.Ok();
            })
            .RequireRateLimiting(RateLimitPolicies.Webhooks)
            .ExcludeFromDescription();
        }

        /// <summary>
        /// Label <c>severity</c> (critical, warning, else info) and <c>audience: admin</c> for the alerts ТЗ 30 sends to the
        /// admin as well; the text is the <c>summary</c> annotation and the <c>description</c> after it. A resolved alert
        /// is news of its own: it has its own key, so the quiet window does not swallow it.
        /// </summary>
        internal static IReadOnlyList<Alert> ToAlerts(Notification? notification) =>
            (notification?.Alerts ?? [])
                .Select(a =>
                {
                    string name = Value(a.Labels, "alertname") ?? "alert";
                    string text = Value(a.Annotations, "summary") ?? name;
                    if (Value(a.Annotations, "description") is string description)
                    {
                        text += "\n" + description;
                    }

                    bool resolved = string.Equals(a.Status, "resolved", StringComparison.OrdinalIgnoreCase);
                    AlertSeverity severity = resolved ? AlertSeverity.Info : Value(a.Labels, "severity")?.ToLowerInvariant() switch
                    {
                        "critical" => AlertSeverity.Critical,
                        "warning" => AlertSeverity.Warning,
                        _ => AlertSeverity.Info,
                    };
                    AlertAudience audience = string.Equals(Value(a.Labels, "audience"), "admin", StringComparison.OrdinalIgnoreCase)
                        ? AlertAudience.TechAdminAndAdmin
                        : AlertAudience.TechAdmin;

                    return new Alert(
                        severity,
                        $"prometheus:{name}:{a.Fingerprint}:{(resolved ? "resolved" : "firing")}",
                        resolved ? "✅ Решено: " + text : text,
                        audience);
                })
                .ToList();

        private static string? Value(Dictionary<string, string>? values, string key) =>
            values is not null && values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        private static void Count(string result) =>
            AppMetrics.WebhookRequests.Add(1,
                new KeyValuePair<string, object?>("source", "alertmanager"),
                new KeyValuePair<string, object?>("result", result));

        /// <summary>Alertmanager's webhook payload, version 4: only what the alert text needs.</summary>
        internal sealed record Notification(List<NotificationAlert>? Alerts);

        internal sealed record NotificationAlert(
            string? Status,
            Dictionary<string, string>? Labels,
            Dictionary<string, string>? Annotations,
            string? Fingerprint);
    }
}
