using Mendeleev.Application.Abstractions.Payments;

namespace Mendeleev.Application.Abstractions.Panel
{
    /// <summary>
    /// Verifies and parses a panel webhook: HMAC-SHA256 of the body with the shared secret, compared in
    /// constant time, and a timestamp not older than 5 minutes (FR-PNL-13).
    /// </summary>
    public interface IPanelWebhookParser
    {
        /// <exception cref="WebhookAuthenticationException">Signature or timestamp is wrong.</exception>
        PanelWebhookEvent Parse(WebhookRequest request);
    }

    /// <param name="Event">Full event name, e.g. <c>user.first_connected</c>.</param>
    /// <param name="User">Present for user-scoped events.</param>
    /// <param name="Subject">A readable subject for alerts (node name etc.), when there is one.</param>
    public sealed record PanelWebhookEvent(
        string Scope,
        string Event,
        DateTime Timestamp,
        PanelUser? User,
        string? Subject);
}
