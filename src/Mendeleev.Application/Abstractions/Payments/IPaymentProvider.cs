namespace Mendeleev.Application.Abstractions.Payments
{
    /// <summary>
    /// An aggregator (FR-PAY-08). A second one is a new implementation, the business logic does not
    /// change. Card data never passes through the service: the user pays on the aggregator's page.
    /// </summary>
    public interface IPaymentProvider
    {
        /// <summary>Code used in the webhook path <c>/webhooks/payments/{code}</c> and stored with the payment.</summary>
        string Code { get; }

        /// <summary>Whether <see cref="ListAsync"/> works; if not, reconciliation re-queries statuses one by one.</summary>
        bool SupportsListing { get; }

        /// <exception cref="PaymentProviderException">The aggregator is unavailable or refused.</exception>
        Task<CreatedPayment> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Checks authenticity (signature, or confirmation through the API if the aggregator has no
        /// signature) and parses the notification.
        /// </summary>
        /// <exception cref="WebhookAuthenticationException">Signature is wrong or the body is not recognized.</exception>
        Task<PaymentNotification> ParseWebhookAsync(WebhookRequest request, CancellationToken cancellationToken);

        /// <exception cref="PaymentProviderException">The aggregator is unavailable.</exception>
        Task<ProviderPaymentStatus> GetStatusAsync(Guid orderId, string? providerPaymentId, CancellationToken cancellationToken);

        IAsyncEnumerable<ProviderPayment> ListAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);
    }

    public sealed record CreatePaymentRequest(
        Guid OrderId,
        decimal Amount,
        string Currency,
        string Description,
        string ReturnUrl,
        string? CustomerEmail);

    public sealed record CreatedPayment(
        string ProviderPaymentId,
        string ConfirmationUrl,
        DateTime? ExpiresAt);

    public sealed record PaymentNotification(
        string ProviderPaymentId,
        Guid? OrderId,
        ProviderPaymentState State,
        decimal? Amount,
        string? Currency);

    public sealed record ProviderPaymentStatus(
        string? ProviderPaymentId,
        ProviderPaymentState State,
        decimal? Amount,
        string? Currency);

    public sealed record ProviderPayment(
        string ProviderPaymentId,
        Guid? OrderId,
        ProviderPaymentState State,
        decimal Amount,
        string Currency,
        DateTime CreatedAt);

    public enum ProviderPaymentState
    {
        Pending,
        Succeeded,
        Canceled,
        Failed,
        Refunded,
        Unknown,
    }

    /// <summary>A webhook as the aggregator sent it, without ties to ASP.NET Core.</summary>
    public sealed record WebhookRequest(
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body,
        string? RemoteIp)
    {
        public string? Header(string name) =>
            Headers.TryGetValue(name, out string? value) ? value : null;
    }

    public sealed class PaymentProviderException(string message, Exception? innerException = null)
        : Exception(message, innerException);

    public sealed class WebhookAuthenticationException(string message) : Exception(message);
}
