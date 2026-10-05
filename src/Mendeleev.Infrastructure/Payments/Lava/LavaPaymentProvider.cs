using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mendeleev.Infrastructure.Payments.Lava
{
    /// <summary>
    /// Lava Business API (developer.lava.ru): an invoice per payment with our payment id as <c>orderId</c>, the user
    /// pays by card or СБП on Lava's page (FR-PAY-01, FR-PAY-05), Lava notifies <c>/webhooks/payments/lava</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lava has no listing of invoices for a period, so the daily reconciliation re-asks every payment of ours
    /// (FR-PAY-06). The status is asked by our order id: it exists even when the create call timed out.
    /// </para>
    /// <para>
    /// Lava invoices are in rubles only and neither the webhook nor the status names a currency, so the currency
    /// check of FR-PAY-02 is <see cref="CreateAsync"/> refusing anything but RUB; the amount is checked as usual.
    /// </para>
    /// <para>Lava has no test mode: staging is checked with real payments (docs/staging.md, «Lava»).</para>
    /// </remarks>
    internal sealed class LavaPaymentProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<LavaPaymentOptions> options,
        IOptions<PaymentOptions> paymentOptions,
        IOptions<ServiceOptions> serviceOptions,
        IDateTimeProvider clock,
        ILogger<LavaPaymentProvider> logger)
        : IPaymentProvider
    {
        public const string ProviderCode = "lava";
        public const string HttpClientName = "lava";
        public const string RequestSignatureHeader = "Signature";

        /// <summary>Where the official SDK and working integrations find the webhook signature; the documentation says <c>Signature</c>.</summary>
        public const string WebhookSignatureHeader = "Authorization";

        /// <summary>Lava's limit for <c>expire</c>: 5 days.</summary>
        private const int MaxInvoiceLifetimeMinutes = 7200;

        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,

            // Cyrillic goes as UTF-8, as in Lava's own examples; the signature covers these exact bytes anyway.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };

        public string Code => ProviderCode;

        public bool SupportsListing => false;

        public async Task<CreatedPayment> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Currency, "RUB", StringComparison.OrdinalIgnoreCase))
            {
                throw new PaymentProviderException($"Lava invoices are in rubles only, not {request.Currency}.");
            }

            LavaPaymentOptions settings = options.Value;

            // The link dies when our payment is canceled (FR-PAY-11); a late payment still counts by reconciliation.
            int lifetimeMinutes = Math.Clamp(paymentOptions.Value.CancelAfterMinutes, 1, MaxInvoiceLifetimeMinutes);

            var body = new LavaCreateInvoiceRequest(
                request.Amount,
                request.OrderId.ToString(),
                settings.ShopId,
                $"{serviceOptions.Value.WebhookBaseUrl}/webhooks/payments/{ProviderCode}",
                request.ReturnUrl,
                request.ReturnUrl,
                lifetimeMinutes,
                request.Description,
                settings.IncludeServices.Length > 0 ? settings.IncludeServices : null);

            (HttpStatusCode status, LavaEnvelope<LavaInvoice>? answer) = await PostAsync<LavaInvoice>("business/invoice/create", body, cancellationToken);
            LavaInvoice invoice = answer?.Data ?? throw Rejected("invoice create", status, answer?.Error);

            if (string.IsNullOrEmpty(invoice.Id) || string.IsNullOrEmpty(invoice.Url))
            {
                throw new PaymentProviderException("Lava created an invoice without an id or a payment link.");
            }

            // Lava's own expiry comes without a time zone, so the lifetime is counted here.
            return new CreatedPayment(invoice.Id, invoice.Url, clock.UtcNow.AddMinutes(lifetimeMinutes));
        }

        public Task<PaymentNotification> ParseWebhookAsync(WebhookRequest request, CancellationToken cancellationToken)
        {
            string key = options.Value.WebhookKey;
            if (string.IsNullOrEmpty(key))
            {
                throw new WebhookAuthenticationException("Lava webhook key is not configured.");
            }

            bool signed = new[] { request.Header(WebhookSignatureHeader), request.Header(RequestSignatureHeader) }
                .Any(signature => !string.IsNullOrWhiteSpace(signature) && LavaSignature.IsValidWebhook(request.Body, signature, key));
            if (!signed)
            {
                throw new WebhookAuthenticationException("Invalid Lava webhook signature.");
            }

            LavaWebhook webhook;
            try
            {
                webhook = JsonSerializer.Deserialize<LavaWebhook>(request.Body, JsonOptions)
                    ?? throw new WebhookAuthenticationException("Empty Lava webhook.");
            }
            catch (JsonException ex)
            {
                throw new WebhookAuthenticationException($"Unreadable Lava webhook: {ex.Message}");
            }

            if (string.IsNullOrEmpty(webhook.InvoiceId) || string.IsNullOrEmpty(webhook.Status))
            {
                throw new WebhookAuthenticationException("Not a Lava invoice notification.");
            }

            return Task.FromResult(new PaymentNotification(
                webhook.InvoiceId,
                Guid.TryParse(webhook.OrderId, out Guid orderId) ? orderId : null,
                MapStatus(webhook.Status),
                webhook.Amount,
                Currency: null));
        }

        public async Task<ProviderPaymentStatus> GetStatusAsync(Guid orderId, string? providerPaymentId, CancellationToken cancellationToken)
        {
            (HttpStatusCode status, LavaEnvelope<LavaInvoice>? answer) = await PostAsync<LavaInvoice>(
                "business/invoice/status",
                new LavaInvoiceStatusRequest(options.Value.ShopId, orderId.ToString()),
                cancellationToken);

            // No invoice with our order id: the create never reached Lava. It stays pending until it is canceled.
            if (status == HttpStatusCode.NotFound)
            {
                return new ProviderPaymentStatus(providerPaymentId, ProviderPaymentState.Pending, null, null);
            }

            LavaInvoice invoice = answer?.Data ?? throw Rejected("invoice status", status, answer?.Error);
            return new ProviderPaymentStatus(invoice.Id ?? providerPaymentId, MapStatus(invoice.Status), invoice.Amount, null);
        }

        public IAsyncEnumerable<ProviderPayment> ListAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Lava has no listing of invoices for a period.");

        /// <summary>
        /// The invoice statuses (<c>created</c>, <c>success</c>, <c>fail</c>, <c>expired</c>, <c>refund</c>) and
        /// <c>error</c> of the older webhooks.
        /// </summary>
        internal static ProviderPaymentState MapStatus(string? status) => status?.Trim().ToLowerInvariant() switch
        {
            "created" => ProviderPaymentState.Pending,
            "success" => ProviderPaymentState.Succeeded,
            "fail" or "error" => ProviderPaymentState.Failed,
            "expired" => ProviderPaymentState.Canceled,
            "refund" => ProviderPaymentState.Refunded,
            _ => ProviderPaymentState.Unknown,
        };

        /// <summary>Sends the body signed over its exact bytes; any HTTP status comes back, transport failures throw.</summary>
        private async Task<(HttpStatusCode Status, LavaEnvelope<T>? Answer)> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
            where T : class
        {
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(content) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add(RequestSignatureHeader, LavaSignature.Sign(content, options.Value.SecretKey));

            try
            {
                using HttpResponseMessage response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
                string text = await response.Content.ReadAsStringAsync(cancellationToken);
                return (response.StatusCode, Read<T>(text));
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                           or TaskCanceledException { InnerException: TimeoutException })
            {
                throw new PaymentProviderException($"Lava request {path} failed: {ex.Message}", ex);
            }
        }

        private static LavaEnvelope<T>? Read<T>(string text)
            where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<LavaEnvelope<T>>(text, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private PaymentProviderException Rejected(string operation, HttpStatusCode status, string? error)
        {
            string reason = string.IsNullOrWhiteSpace(error) ? "no error text" : error.Length > 300 ? error[..300] : error;

            // A 4xx is a problem on our side (keys, shop, request) and does not pass by itself.
            if ((int)status is >= 400 and < 500 and not 429)
            {
                logger.LogError("Lava rejected {Operation} with {Status}: {Reason}", operation, (int)status, reason);
            }

            return new PaymentProviderException($"Lava {operation} returned {(int)status}: {reason}");
        }
    }
}
