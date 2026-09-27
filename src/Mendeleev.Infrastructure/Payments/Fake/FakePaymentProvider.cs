using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Options;

namespace Mendeleev.Infrastructure.Payments.Fake
{
    public sealed class FakePaymentOptions
    {
        public const string SectionName = "Payments:Fake";

        /// <summary>Registers the provider and the dev payment page. Never in production.</summary>
        public bool Enabled { get; init; }

        /// <summary>HMAC key of the fake webhooks — the same shape as Lava's and Enot.io's signature.</summary>
        public string WebhookSecret { get; init; } = "fake-webhook-secret";
    }

    /// <summary>The dev payment page drives the fake aggregator through this.</summary>
    public interface IFakePaymentSimulator
    {
        FakePaymentView? Find(Guid orderId);

        /// <summary>Changes the state and returns the signed webhook the aggregator would send.</summary>
        WebhookRequest? Simulate(Guid orderId, ProviderPaymentState state);
    }

    /// <summary>
    /// A stand-in aggregator for local runs, tests and staging until the real one is chosen (ТЗ 23,
    /// «Выбор агрегатора»). It behaves like the real ones where it matters: a hosted payment page (ours,
    /// under <c>/dev/fake-pay</c>), signed webhooks, status queries and a listing for reconciliation.
    /// State lives in memory — a restart forgets unpaid payments, which the checks then cancel.
    /// </summary>
    internal sealed class FakePaymentProvider(
        IOptions<FakePaymentOptions> fakeOptions,
        IOptions<ServiceOptions> serviceOptions,
        IDateTimeProvider clock)
        : IPaymentProvider, IFakePaymentSimulator
    {
        public const string ProviderCode = "fake";
        public const string SignatureHeader = "X-Fake-Signature";

        private readonly ConcurrentDictionary<Guid, FakePayment> Payments = new();

        public string Code => ProviderCode;

        public bool SupportsListing => true;

        public Task<CreatedPayment> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
        {
            var payment = new FakePayment(request.OrderId, $"fake_{request.OrderId:N}", request.Amount, request.Currency, ProviderPaymentState.Pending, clock.UtcNow);
            Payments[request.OrderId] = payment;

            return Task.FromResult(new CreatedPayment(
                payment.ProviderPaymentId,
                $"{serviceOptions.Value.SiteBaseUrl}/dev/fake-pay/{request.OrderId}",
                clock.UtcNow.AddHours(1)));
        }

        public Task<PaymentNotification> ParseWebhookAsync(WebhookRequest request, CancellationToken cancellationToken)
        {
            string? signature = request.Header(SignatureHeader);
            if (signature is null || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(Sign(request.Body)),
                    TryFromHex(signature)))
            {
                throw new WebhookAuthenticationException("Invalid fake webhook signature.");
            }

            FakeWebhookBody body;
            try
            {
                body = JsonSerializer.Deserialize<FakeWebhookBody>(request.Body, JsonSerializerOptions.Web)
                    ?? throw new WebhookAuthenticationException("Empty body.");
            }
            catch (JsonException ex)
            {
                throw new WebhookAuthenticationException($"Unreadable body: {ex.Message}");
            }

            return Task.FromResult(new PaymentNotification(body.PaymentId, body.OrderId, body.Status, body.Amount, body.Currency));
        }

        public Task<ProviderPaymentStatus> GetStatusAsync(Guid orderId, string? providerPaymentId, CancellationToken cancellationToken)
        {
            ProviderPaymentStatus status = Payments.TryGetValue(orderId, out FakePayment? payment)
                ? new ProviderPaymentStatus(payment.ProviderPaymentId, payment.State, payment.Amount, payment.Currency)
                : new ProviderPaymentStatus(providerPaymentId, ProviderPaymentState.Pending, null, null);

            return Task.FromResult(status);
        }

        public async IAsyncEnumerable<ProviderPayment> ListAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (FakePayment payment in Payments.Values.Where(p => p.CreatedAt >= fromUtc && p.CreatedAt <= toUtc))
            {
                yield return new ProviderPayment(payment.ProviderPaymentId, payment.OrderId, payment.State, payment.Amount, payment.Currency, payment.CreatedAt);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// What the dev payment page does when "paid" or "canceled" is pressed: changes the state and
        /// returns the signed webhook the aggregator would send.
        /// </summary>
        public WebhookRequest? Simulate(Guid orderId, ProviderPaymentState state)
        {
            if (!Payments.TryGetValue(orderId, out FakePayment? payment))
            {
                return null;
            }

            payment = payment with { State = state };
            Payments[orderId] = payment;

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new FakeWebhookBody(payment.ProviderPaymentId, orderId, state, payment.Amount, payment.Currency),
                JsonSerializerOptions.Web);

            return new WebhookRequest(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [SignatureHeader] = Sign(body) },
                body,
                "127.0.0.1");
        }

        public FakePaymentView? Find(Guid orderId) =>
            Payments.TryGetValue(orderId, out FakePayment? payment)
                ? new FakePaymentView(payment.OrderId, payment.Amount, payment.Currency, payment.State)
                : null;

        private string Sign(byte[] body) =>
            Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(fakeOptions.Value.WebhookSecret), body));

        private static byte[] TryFromHex(string value)
        {
            try
            {
                return Convert.FromHexString(value);
            }
            catch (FormatException)
            {
                return [];
            }
        }

        private sealed record FakePayment(Guid OrderId, string ProviderPaymentId, decimal Amount, string Currency, ProviderPaymentState State, DateTime CreatedAt);

        private sealed record FakeWebhookBody(string PaymentId, Guid OrderId, ProviderPaymentState Status, decimal Amount, string Currency);
    }

    public sealed record FakePaymentView(Guid OrderId, decimal Amount, string Currency, ProviderPaymentState State);
}
