using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.Reconciliation;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Infrastructure.Payments.TryBit;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// ТЗ 23 with TryBit as the active aggregator: the real use cases over a real database, TryBit's API stubbed at the
    /// HTTP level behind the real client pipeline, its POSTBACK signed with the project's secret key.
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class TryBitPaymentFlowTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private const string SecretKey = "trybit-secret-key";

        private readonly TryBitApi _trybit = new();
        private TestApp _app = null!;

        public async Task InitializeAsync() => _app = await TestApp.CreateAsync(
            postgres,
            new Dictionary<string, string?>
            {
                ["Service:WebhookBaseUrl"] = "https://hooks.test",
                ["Payments:ActiveProvider"] = "trybit",
                ["Payments:TryBit:Enabled"] = "true",
                ["Payments:TryBit:BaseUrl"] = "https://api.trybit.test/v2",
                ["Payments:TryBit:ShopId"] = "shop-1",
                ["Payments:TryBit:ApiKey"] = "trybit-api-key",
                ["Payments:TryBit:SecretKey"] = SecretKey,
            },
            services => services.AddHttpClient(TryBitPaymentProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _trybit));

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task PaidThroughTryBit_ExtendsOnce_AndBringsAccess()
        {
            // FR-PAY-01…04: the invoice carries our payment id and the ruble amount; a repeated POSTBACK changes nothing.
            long userId = await NewUserAsync(5001);
            PaymentLink link = await CreatePaymentAsync(userId, "INV-AAAA0001");

            link.ConfirmationUrl.ShouldBe("https://pay.trybit.com/AAAA0001?lang=ru");
            Payment payment = await PaymentAsync(link.PaymentId);
            payment.Provider.ShouldBe("trybit");
            payment.ProviderPaymentId.ShouldBe("INV-AAAA0001");
            payment.LinkExpiresAt.ShouldBe(_app.Clock.UtcNow.AddMinutes(60));

            TryBitApi.Sent create = _trybit.Requests.ShouldHaveSingleItem();
            create.Path.ShouldBe("/v2/invoice/create");
            create.Authorization.ShouldBe("Token trybit-api-key");
            using (JsonDocument sent = JsonDocument.Parse(create.Body))
            {
                sent.RootElement.GetProperty("shop_id").GetString().ShouldBe("shop-1");
                sent.RootElement.GetProperty("order_id").GetString().ShouldBe(link.PaymentId.ToString());
                sent.RootElement.GetProperty("amount").GetDecimal().ShouldBe(199m);
                sent.RootElement.GetProperty("currency").GetString().ShouldBe("RUB");
            }

            // Each POSTBACK is confirmed through the API: the state and the amount are TryBit's answer.
            _trybit.Answer(HttpStatusCode.OK, InfoAnswer("INV-AAAA0001", link.PaymentId, "paid", 199));
            _trybit.Answer(HttpStatusCode.OK, InfoAnswer("INV-AAAA0001", link.PaymentId, "paid", 199));
            WebhookRequest postback = Postback("AAAA0001", link.PaymentId);
            (await DeliverAsync(postback)).IsSuccess.ShouldBeTrue();
            (await DeliverAsync(postback)).IsSuccess.ShouldBeTrue();

            Subscription subscription = await _app.WithDbAsync(db => db.Subscriptions.AsNoTracking().SingleAsync(s => s.UserId == userId));
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(30));
            List<PaymentEvent> events = await EventsAsync(link.PaymentId);
            events.Count(e => e.Kind == PaymentEventKind.Applied).ShouldBe(1);

            // FR-PAY-05: the payment address, the transaction and the payer's email of TryBit's answers are kept nowhere.
            events.ShouldAllBe(e => e.Details == null || (!e.Details.Contains("TXq9") && !e.Details.Contains("0x12fb") && !e.Details.Contains("gmail")));

            await _app.ProcessOutboxAsync();
            _app.Panel.Users.ShouldHaveSingleItem().ExpireAt.ShouldBe(subscription.ExpiresAt);
        }

        [Fact]
        public async Task LostPostback_IsPickedUpByThePendingCheck()
        {
            // FR-PAY-07: TryBit is asked by the invoice number the create call returned.
            long userId = await NewUserAsync(5002);
            PaymentLink link = await CreatePaymentAsync(userId, "INV-AAAA0002");
            _trybit.Answer(HttpStatusCode.OK, InfoAnswer("INV-AAAA0002", link.PaymentId, "paid", 199));

            (await _app.SendAsync<CheckPendingPaymentsCommand, int>(new CheckPendingPaymentsCommand())).Value.ShouldBe(1);

            (await PaymentAsync(link.PaymentId)).Status.ShouldBe(PaymentStatus.Succeeded);
            TryBitApi.Sent asked = _trybit.Requests.Last();
            asked.Path.ShouldBe("/v2/invoice/merchant/info");
            Encoding.UTF8.GetString(asked.Body).ShouldBe("""{"uuids":["INV-AAAA0002"]}""");
        }

        [Fact]
        public async Task Postback_ForAnInvoiceOfAnotherAmount_DoesNotExtend()
        {
            // FR-PAY-02: the invoice's ruble amount and currency are compared with ours.
            long userId = await NewUserAsync(5003);
            PaymentLink link = await CreatePaymentAsync(userId, "INV-AAAA0003");
            _trybit.Answer(HttpStatusCode.OK, InfoAnswer("INV-AAAA0003", link.PaymentId, "paid", 19.9m));

            (await DeliverAsync(Postback("AAAA0003", link.PaymentId))).IsSuccess.ShouldBeTrue();

            Payment payment = await PaymentAsync(link.PaymentId);
            payment.Status.ShouldBe(PaymentStatus.Pending);
            payment.NeedsReview.ShouldBeTrue();
            (await EventsAsync(link.PaymentId)).ShouldContain(e => e.Kind == PaymentEventKind.AmountMismatch);
            (await _app.WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.UserId == userId))).ShouldBeFalse();
        }

        [Fact]
        public async Task Postback_SignedWithAnotherKey_IsRejected_AndNothingIsAsked()
        {
            // FR-PAY-02: 401 and a record in the journal; TryBit's API is not even asked.
            long userId = await NewUserAsync(5004);
            PaymentLink link = await CreatePaymentAsync(userId, "INV-AAAA0004");
            int requests = _trybit.Requests.Count;

            Result result = await DeliverAsync(Postback("AAAA0004", link.PaymentId, key: "another-project-key"));

            result.Error.ShouldBe(PaymentErrors.InvalidSignature);
            _trybit.Requests.Count.ShouldBe(requests);
            (await _app.WithDbAsync(db => db.PaymentEvents.AnyAsync(e => e.Kind == PaymentEventKind.WebhookRejected && e.Provider == "trybit"))).ShouldBeTrue();
            (await PaymentAsync(link.PaymentId)).Status.ShouldBe(PaymentStatus.Pending);
        }

        private async Task<long> NewUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private async Task<PaymentLink> CreatePaymentAsync(long userId, string invoice)
        {
            _trybit.Answer(HttpStatusCode.OK, $$$"""
                {"status":"success","result":{"uuid":"{{{invoice}}}","created":"2026-10-07 09:00:00.958133","address":"","expiry_date":"2026-10-07 10:00:00.493361",
                 "amount_in_fiat":199.0,"fiat_currency":"RUB","status":"created","link":"https://pay.trybit.com/{{{invoice[4..]}}}","invoice_id":null,"test_mode":false}}
                """);
            return (await _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, "basic_1m"))).Value;
        }

        private Task<Result> DeliverAsync(WebhookRequest webhook) =>
            _app.SendAsync(new HandlePaymentWebhookCommand("trybit", webhook));

        private Task<Payment> PaymentAsync(Guid paymentId) =>
            _app.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId));

        private Task<List<PaymentEvent>> EventsAsync(Guid paymentId) =>
            _app.WithDbAsync(db => db.PaymentEvents.AsNoTracking().Where(e => e.PaymentId == paymentId).ToListAsync());

        private static string InfoAnswer(string invoice, Guid orderId, string status, decimal amount) => $$"""
            {"status":"success","result":[{"uuid":"{{invoice}}","address":"TXq9fakeAddress","status":"{{status}}","invoice_status":"success","test_mode":false,
             "user_email":"a**********b@gmail.com","tx_list":["0x12fb"],"order_id":"{{orderId}}","amount_in_fiat":{{amount.ToString(CultureInfo.InvariantCulture)}},"fiat_currency":"RUB","amount":2.1}]}
            """;

        /// <summary>The POSTBACK as TryBit sends it in JSON: the invoice without the prefix and a fresh token.</summary>
        private WebhookRequest Postback(string invoiceId, Guid orderId, string key = SecretKey)
        {
            long expires = (long)(_app.Clock.UtcNow.AddMinutes(5) - DateTime.UnixEpoch).TotalSeconds;
            string header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"HS256"}"""u8);
            string payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($$"""{"id":13,"exp":{{expires}}}"""));
            string signature = Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes($"{header}.{payload}")));

            byte[] body = Encoding.UTF8.GetBytes($$"""{"status":"success","invoice_id":"{{invoiceId}}","amount_crypto":2.1,"currency":"USDT_TRC20","order_id":"{{orderId}}","token":"{{header}}.{{payload}}.{{signature}}"}""");
            return new WebhookRequest(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json" },
                body,
                "203.0.113.7");
        }

        /// <summary>TryBit's API: answers in turn and keeps what it was sent.</summary>
        private sealed class TryBitApi : HttpMessageHandler
        {
            private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

            public List<Sent> Requests { get; } = [];

            public void Answer(HttpStatusCode status, string body) => _answers.Enqueue((status, body));

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(new Sent(
                    request.RequestUri!.AbsolutePath,
                    request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken),
                    request.Headers.Authorization?.ToString()));

                (HttpStatusCode status, string body) = _answers.Dequeue();
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            // The client factory disposes its handlers; this one lives as long as the test.
            protected override void Dispose(bool disposing)
            {
            }

            public sealed record Sent(string Path, byte[] Body, string? Authorization);
        }
    }
}
