using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mendeleev.Infrastructure.Payments.TryBit
{
    /// <summary>
    /// TryBit API v2 (docs.trybit.com): an invoice per payment with our payment id as <c>order_id</c>; the user pays
    /// it in cryptocurrency on TryBit's page (FR-PAY-01, FR-PAY-05) — TryBit takes no cards and no СБП — and TryBit
    /// sends its POSTBACK to the notification URL of the project, <c>/webhooks/payments/trybit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notification URL and the return addresses are settings of the project in the TryBit dashboard, not of an
    /// invoice: users come back to <c>/pay/return</c> without the payment id, and a cabinet user sees the latest
    /// payment there.
    /// </para>
    /// <para>
    /// The POSTBACK carries a JWT signed with the project's SECRET KEY (HS256, 5 minutes), but the token covers none
    /// of the notification's fields. So the token only says the POSTBACK is TryBit's — a wrong one is 401 (FR-PAY-02) —
    /// and the state, the amount and the currency are always taken from TryBit's API by the invoice number.
    /// </para>
    /// <para>
    /// The invoice is in rubles and converted to cryptocurrency at a rate fixed for its lifetime, so the amount and the
    /// currency compared with ours (FR-PAY-02) are the invoice's, not those of the cryptocurrency sent. An invoice paid
    /// in part stays pending: the user can pay the rest, or an admin confirms it in the dashboard.
    /// </para>
    /// <para>
    /// TryBit is asked about an invoice by its number (INV-…) from the create call, never by our order id. A payment
    /// whose create call failed has no number, but its link was never shown either, so nobody could pay it. The
    /// listing has day granularity and loose paging, so the daily reconciliation re-asks every payment (FR-PAY-06).
    /// </para>
    /// </remarks>
    internal sealed class TryBitPaymentProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<TryBitPaymentOptions> options,
        IOptions<PaymentOptions> paymentOptions,
        IDateTimeProvider clock,
        ILogger<TryBitPaymentProvider> logger)
        : IPaymentProvider
    {
        public const string ProviderCode = "trybit";
        public const string HttpClientName = "trybit";

        /// <summary>TryBit's page otherwise follows the browser's language; the service speaks Russian.</summary>
        private const string PageLanguage = "ru";

        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        public string Code => ProviderCode;

        public bool SupportsListing => false;

        public async Task<CreatedPayment> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
        {
            // The link dies when our payment is canceled (FR-PAY-11); a late payment still counts by reconciliation.
            int lifetimeMinutes = Math.Max(1, paymentOptions.Value.CancelAfterMinutes);

            var body = new TryBitCreateInvoiceRequest(
                options.Value.ShopId,
                request.Amount,
                request.Currency,
                request.OrderId.ToString(),
                new TryBitInvoiceFields(new TryBitTimeToPay(lifetimeMinutes / 60, lifetimeMinutes % 60)));

            TryBitInvoice invoice = await PostAsync<TryBitInvoice>("invoice/create", body, "invoice create", cancellationToken);

            string? link = PaymentPage(invoice.Link);
            if (string.IsNullOrEmpty(invoice.Uuid) || link is null)
            {
                throw new PaymentProviderException("TryBit created an invoice without a number or a payment link.");
            }

            if (invoice.TestMode == true && !options.Value.AcceptTestInvoices)
            {
                // Money sent to a test invoice is never credited: «Оплата временно недоступна» and an alert are better.
                logger.LogError("The TryBit project is in test mode, its invoices are refused");
                throw new PaymentProviderException("The TryBit project is in test mode: its invoices are not paid for real.");
            }

            return new CreatedPayment(InvoiceNumber(invoice.Uuid), link, clock.UtcNow.AddMinutes(lifetimeMinutes));
        }

        public async Task<PaymentNotification> ParseWebhookAsync(WebhookRequest request, CancellationToken cancellationToken)
        {
            string key = options.Value.SecretKey;
            if (string.IsNullOrEmpty(key))
            {
                throw new WebhookAuthenticationException("TryBit secret key is not configured.");
            }

            TryBitPostback postback = ReadPostback(request.Body);
            if (string.IsNullOrWhiteSpace(postback.Token))
            {
                throw new WebhookAuthenticationException("TryBit POSTBACK without a token.");
            }
            if (!TryBitToken.IsValid(postback.Token, key, clock.UtcNow))
            {
                throw new WebhookAuthenticationException("Invalid or expired TryBit POSTBACK token.");
            }
            if (string.IsNullOrWhiteSpace(postback.InvoiceId))
            {
                throw new WebhookAuthenticationException("Not a TryBit invoice notification.");
            }

            // The token covers none of the fields: the state and the amount are TryBit's answer, not the POSTBACK's.
            TryBitInvoice invoice = await FindAsync(postback.InvoiceId, cancellationToken)
                ?? throw new WebhookAuthenticationException("The invoice of the TryBit POSTBACK is not in this project.");

            return new PaymentNotification(
                invoice.Uuid!,
                OrderIdOf(invoice.OrderId) ?? OrderIdOf(postback.OrderId),
                StateOf(invoice),
                invoice.AmountInFiat,
                invoice.FiatCurrency);
        }

        public async Task<ProviderPaymentStatus> GetStatusAsync(Guid orderId, string? providerPaymentId, CancellationToken cancellationToken)
        {
            // Without TryBit's number there is nothing to ask; the link of such a payment was never shown.
            if (string.IsNullOrEmpty(providerPaymentId))
            {
                return new ProviderPaymentStatus(null, ProviderPaymentState.Pending, null, null);
            }

            TryBitInvoice? invoice = await FindAsync(providerPaymentId, cancellationToken);
            if (invoice is null)
            {
                logger.LogWarning("TryBit does not know invoice {Invoice} of payment {PaymentId}", providerPaymentId, orderId);
                return new ProviderPaymentStatus(providerPaymentId, ProviderPaymentState.Pending, null, null);
            }

            return new ProviderPaymentStatus(invoice.Uuid, StateOf(invoice), invoice.AmountInFiat, invoice.FiatCurrency);
        }

        public IAsyncEnumerable<ProviderPayment> ListAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException("TryBit payments are reconciled one by one.");

        /// <summary>TryBit's statuses: <c>created</c>, <c>paid</c>, <c>partial</c>, <c>overpaid</c> and <c>canceled</c>.</summary>
        internal static ProviderPaymentState MapStatus(string? status) => status?.Trim().ToLowerInvariant() switch
        {
            "created" => ProviderPaymentState.Pending,

            // Less than the invoice arrived, usually short of the network's fee: the user can pay the rest on the same
            // page, or an admin confirms the invoice in the dashboard and TryBit reports it paid.
            "partial" => ProviderPaymentState.Pending,
            "paid" or "overpaid" => ProviderPaymentState.Succeeded,
            "canceled" or "cancelled" => ProviderPaymentState.Canceled,
            _ => ProviderPaymentState.Unknown,
        };

        private ProviderPaymentState StateOf(TryBitInvoice invoice)
        {
            if (invoice.TestMode == true && !options.Value.AcceptTestInvoices)
            {
                // Confirmed in the dashboard without any payment: it must not buy anything here.
                logger.LogWarning("TryBit test invoice {Invoice} is ignored", invoice.Uuid);
                return ProviderPaymentState.Unknown;
            }

            return MapStatus(invoice.Status);
        }

        private async Task<TryBitInvoice?> FindAsync(string number, CancellationToken cancellationToken)
        {
            number = InvoiceNumber(number);
            List<TryBitInvoice> invoices = await PostAsync<List<TryBitInvoice>>(
                "invoice/merchant/info", new TryBitInvoiceInfoRequest([number]), "invoice info", cancellationToken);

            return invoices
                .Where(i => !string.IsNullOrWhiteSpace(i.Uuid))
                .Select(i => i with { Uuid = InvoiceNumber(i.Uuid!) })
                .FirstOrDefault(i => string.Equals(i.Uuid, number, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The API names an invoice <c>INV-XXXXXXXX</c>, the POSTBACK names it without the prefix.</summary>
        internal static string InvoiceNumber(string id)
        {
            id = id.Trim();
            return id.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) ? id : $"INV-{id}";
        }

        private static Guid? OrderIdOf(string? value) => Guid.TryParse(value, out Guid orderId) ? orderId : null;

        /// <summary>The link as an absolute https address in Russian; null if there is none.</summary>
        private static string? PaymentPage(string? link)
        {
            if (string.IsNullOrWhiteSpace(link))
            {
                return null;
            }

            // The documentation shows the link both with and without the scheme.
            link = link.Trim();
            if (!link.Contains("://", StringComparison.Ordinal))
            {
                link = "https://" + link;
            }
            if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            return uri.Query.Contains("lang=", StringComparison.OrdinalIgnoreCase)
                ? link
                : $"{link}{(string.IsNullOrEmpty(uri.Query) ? '?' : '&')}lang={PageLanguage}";
        }

        /// <summary>The POSTBACK comes as JSON or as a form, as the project is set; only its token and ids are read.</summary>
        private static TryBitPostback ReadPostback(byte[] body)
        {
            string text = Encoding.UTF8.GetString(body).Trim();
            if (!text.StartsWith('{'))
            {
                Dictionary<string, string> form = ParseForm(text);
                return new TryBitPostback(form.GetValueOrDefault("invoice_id"), form.GetValueOrDefault("order_id"), form.GetValueOrDefault("token"));
            }

            try
            {
                return JsonSerializer.Deserialize<TryBitPostback>(text, JsonOptions)
                    ?? throw new WebhookAuthenticationException("Empty TryBit POSTBACK.");
            }
            catch (JsonException ex)
            {
                throw new WebhookAuthenticationException($"Unreadable TryBit POSTBACK: {ex.Message}");
            }
        }

        private static Dictionary<string, string> ParseForm(string text)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=');
                string name = Unescape(separator < 0 ? pair : pair[..separator]);
                fields.TryAdd(name, separator < 0 ? string.Empty : Unescape(pair[(separator + 1)..]));
            }

            return fields;

            static string Unescape(string value)
            {
                try
                {
                    return Uri.UnescapeDataString(value.Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    return value;
                }
            }
        }

        /// <summary>Sends a request; transport failures and anything but a <c>success</c> answer throw.</summary>
        private async Task<T> PostAsync<T>(string path, object body, string operation, CancellationToken cancellationToken)
            where T : class
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpStatusCode status;
            string text;
            try
            {
                using HttpResponseMessage response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
                status = response.StatusCode;
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                           or TaskCanceledException { InnerException: TimeoutException })
            {
                throw new PaymentProviderException($"TryBit request {path} failed: {ex.Message}", ex);
            }

            TryBitAnswer? answer = Read(text);
            if ((int)status is < 200 or >= 300 || !string.Equals(answer?.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                throw Rejected(operation, status, answer);
            }

            if (answer!.Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                throw new PaymentProviderException($"TryBit {operation} answered without a result.");
            }

            try
            {
                return answer.Result.Deserialize<T>(JsonOptions)
                    ?? throw new PaymentProviderException($"TryBit {operation} answered without a result.");
            }
            catch (JsonException)
            {
                throw new PaymentProviderException($"TryBit {operation} answered with a result of an unknown shape.");
            }
        }

        private static TryBitAnswer? Read(string text)
        {
            try
            {
                return JsonSerializer.Deserialize<TryBitAnswer>(text, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private PaymentProviderException Rejected(string operation, HttpStatusCode status, TryBitAnswer? answer)
        {
            string reason = answer is null || answer.Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "no error text"
                : answer.Result.GetRawText();
            if (reason.Length > 300)
            {
                reason = reason[..300];
            }

            // A 4xx is a problem on our side (keys, shop, request) and does not pass by itself.
            if ((int)status is >= 400 and < 500 and not 429)
            {
                logger.LogError("TryBit rejected {Operation} with {Status}: {Reason}", operation, (int)status, reason);
            }

            return new PaymentProviderException($"TryBit {operation} returned {(int)status}: {reason}");
        }
    }
}
