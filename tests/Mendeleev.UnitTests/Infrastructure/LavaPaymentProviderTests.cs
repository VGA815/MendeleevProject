using System.Net;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.Infrastructure;
using Mendeleev.Infrastructure.Payments.Lava;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mendeleev.UnitTests.Infrastructure
{
    /// <summary>The Lava adapter against Lava's documented answers and the official SDK's test vector (ТЗ 23).</summary>
    public class LavaPaymentProviderTests
    {
        private const string SecretKey = "test-secret-key";

        /// <summary>The additional key of the SDK's webhook test (lava-payment/lava, tests/Feature/Webhook/WebhookTest.php).</summary>
        private const string WebhookKey = "f4b91efb9b8da35737fcd97ab123c74566f9a654";

        private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task Webhook_SignedTheWayLavasSdkChecks_IsAccepted()
        {
            // The SDK's vector: the body in Lava's order, the signature over its sorted json_encode form.
            byte[] body = Encoding.UTF8.GetBytes("""{"invoice_id":"18cf0c0b-6539-4d7c-b3e9-479e4922b87c","status":"success","pay_time":"2022-11-08 11:26:46","amount":"1.00","order_id":"636a3c2f3e82b","pay_service":"card","payer_details":"553691******8079","custom_fields":"test","type":1,"credited":"1.00"}""");

            PaymentNotification notification = await Provider(new StubLava()).ParseWebhookAsync(
                Webhook(body, ("Authorization", "b0b011552beb994cc04401e088db7b296796a07fc76976b632518fe146ffa330")), CancellationToken.None);

            notification.ProviderPaymentId.ShouldBe("18cf0c0b-6539-4d7c-b3e9-479e4922b87c");
            notification.OrderId.ShouldBeNull();
            notification.State.ShouldBe(ProviderPaymentState.Succeeded);
            notification.Amount.ShouldBe(1.00m);
            notification.Currency.ShouldBeNull();
        }

        [Theory]
        [InlineData("Authorization")]
        [InlineData("Signature")]
        public async Task Webhook_SignedOverTheRawBody_IsAccepted(string header)
        {
            Guid orderId = Guid.NewGuid();
            byte[] body = Encoding.UTF8.GetBytes($$"""{"invoice_id":"inv-1","order_id":"{{orderId}}","status":"success","amount":"199.00","custom_fields":null}""");

            PaymentNotification notification = await Provider(new StubLava()).ParseWebhookAsync(
                Webhook(body, (header, LavaSignature.Sign(body, WebhookKey))), CancellationToken.None);

            notification.ShouldBe(new PaymentNotification("inv-1", orderId, ProviderPaymentState.Succeeded, 199m, null));
        }

        [Fact]
        public async Task Webhook_SortedForm_IsPrintedAsPhpPrintsIt()
        {
            // json_encode escapes "/" and everything outside ASCII as lowercase \uXXXX; 100.0 comes back as 100.
            byte[] body = Encoding.UTF8.GetBytes("""{"status":"success","invoice_id":"inv-2","amount":"10.00","custom_fields":"Оплата/1","rate":1.50,"sum":100.0,"type":1}""");
            // "Оплата" escaped; spelled with U+ so that the expected text keeps the backslash-u escapes themselves.
            string printed = """{"amount":"10.00","custom_fields":"U+041eU+043fU+043bU+0430U+0442U+0430\/1","invoice_id":"inv-2","rate":1.5,"status":"success","sum":100,"type":1}"""
                .Replace("U+", "\\u", StringComparison.Ordinal);

            LavaSignature.PhpCanonicalJson(body).ShouldBe(printed);

            PaymentNotification notification = await Provider(new StubLava()).ParseWebhookAsync(
                Webhook(body, ("Authorization", LavaSignature.Sign(Encoding.UTF8.GetBytes(printed), WebhookKey))), CancellationToken.None);
            notification.State.ShouldBe(ProviderPaymentState.Succeeded);
        }

        [Fact]
        public async Task Webhook_WithAWrongOrMissingSignature_IsRejected()
        {
            // FR-PAY-02: the endpoint answers 401 to all of these.
            byte[] body = Encoding.UTF8.GetBytes("""{"invoice_id":"inv-1","order_id":"x","status":"success","amount":"199.00"}""");
            byte[] tampered = Encoding.UTF8.GetBytes("""{"invoice_id":"inv-1","order_id":"x","status":"success","amount":"1.00"}""");
            LavaPaymentProvider provider = Provider(new StubLava());

            await Should.ThrowAsync<WebhookAuthenticationException>(() => provider.ParseWebhookAsync(Webhook(body, ("Authorization", LavaSignature.Sign(body, SecretKey))), CancellationToken.None));
            await Should.ThrowAsync<WebhookAuthenticationException>(() => provider.ParseWebhookAsync(Webhook(tampered, ("Authorization", LavaSignature.Sign(body, WebhookKey))), CancellationToken.None));
            await Should.ThrowAsync<WebhookAuthenticationException>(() => provider.ParseWebhookAsync(Webhook(body, ("Authorization", "Bearer something")), CancellationToken.None));
            await Should.ThrowAsync<WebhookAuthenticationException>(() => provider.ParseWebhookAsync(Webhook(body), CancellationToken.None));
        }

        [Fact]
        public async Task Webhook_ThatIsNotAboutAnInvoice_IsRejected()
        {
            // A payoff notification (type 3) is signed the same way but has no invoice.
            byte[] body = Encoding.UTF8.GetBytes("""{"payoff_id":"p-1","status":"success","type":3,"credited":"10.00"}""");

            await Should.ThrowAsync<WebhookAuthenticationException>(() => Provider(new StubLava()).ParseWebhookAsync(
                Webhook(body, ("Authorization", LavaSignature.Sign(body, WebhookKey))), CancellationToken.None));
        }

        [Theory]
        [InlineData("created", ProviderPaymentState.Pending)]
        [InlineData("success", ProviderPaymentState.Succeeded)]
        [InlineData("fail", ProviderPaymentState.Failed)]
        [InlineData("error", ProviderPaymentState.Failed)]
        [InlineData("expired", ProviderPaymentState.Canceled)]
        [InlineData("refund", ProviderPaymentState.Refunded)]
        [InlineData("something_new", ProviderPaymentState.Unknown)]
        public void Statuses_MapToOurs(string status, ProviderPaymentState expected) =>
            LavaPaymentProvider.MapStatus(status).ShouldBe(expected);

        [Fact]
        public async Task Create_SignsTheExactBody_AndReturnsLavasLink()
        {
            // FR-PAY-01: sum, our order id, description and the return address; FR-PAY-11: the link lives 60 minutes.
            var lava = new StubLava((HttpStatusCode.OK, """{"data":{"id":"inv-1","amount":199,"expired":"2026-10-05 13:00:00","status":"created","shop_id":"shop-1","url":"https://pay.lava.ru/invoice/inv-1","comment":"Подписка «Базовый, 1 месяц»","merchantName":null,"exclude_service":null,"include_service":null},"status":200,"status_check":true}"""));
            Guid orderId = Guid.NewGuid();

            CreatedPayment created = await Provider(lava).CreateAsync(Request(orderId), CancellationToken.None);

            created.ShouldBe(new CreatedPayment("inv-1", "https://pay.lava.ru/invoice/inv-1", Now.AddMinutes(60)));

            StubLava.Sent sent = lava.Requests.ShouldHaveSingleItem();
            sent.Path.ShouldBe("/business/invoice/create");
            sent.ContentType.ShouldBe("application/json");
            sent.Signature.ShouldBe(LavaSignature.Sign(sent.Body, SecretKey));
            Encoding.UTF8.GetString(sent.Body).ShouldContain("Подписка «Базовый, 1 месяц»");

            using JsonDocument json = JsonDocument.Parse(sent.Body);
            JsonElement root = json.RootElement;
            root.GetProperty("sum").GetDecimal().ShouldBe(199m);
            root.GetProperty("orderId").GetString().ShouldBe(orderId.ToString());
            root.GetProperty("shopId").GetString().ShouldBe("shop-1");
            root.GetProperty("hookUrl").GetString().ShouldBe("https://hooks.test/webhooks/payments/lava");
            root.GetProperty("successUrl").GetString().ShouldBe($"https://site.test/pay/return/{orderId}");
            root.GetProperty("failUrl").GetString().ShouldBe($"https://site.test/pay/return/{orderId}");
            root.GetProperty("expire").GetInt32().ShouldBe(60);
            root.TryGetProperty("includeService", out _).ShouldBeFalse();
        }

        [Fact]
        public async Task Create_WithChosenMethods_ListsThem()
        {
            var lava = new StubLava((HttpStatusCode.OK, """{"data":{"id":"inv-1","url":"https://pay.lava.ru/invoice/inv-1","status":"created"},"status":200,"status_check":true}"""));

            await Provider(lava, ["card", "sbp"]).CreateAsync(Request(Guid.NewGuid()), CancellationToken.None);

            using JsonDocument json = JsonDocument.Parse(lava.Requests.ShouldHaveSingleItem().Body);
            json.RootElement.GetProperty("includeService").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "card", "sbp" });
        }

        [Fact]
        public async Task Create_RefusedOrUnreachable_IsAProviderError()
        {
            var refused = new StubLava((HttpStatusCode.UnprocessableEntity, """{"error":"Ошибочный формат ссылки","data":null,"status":null,"status_check":null}"""));
            PaymentProviderException exception = await Should.ThrowAsync<PaymentProviderException>(() => Provider(refused).CreateAsync(Request(Guid.NewGuid()), CancellationToken.None));
            exception.Message.ShouldContain("422");
            exception.Message.ShouldContain("Ошибочный формат ссылки");

            var down = new StubLava(new HttpRequestException("Connection refused"));
            await Should.ThrowAsync<PaymentProviderException>(() => Provider(down).CreateAsync(Request(Guid.NewGuid()), CancellationToken.None));

            var noLink = new StubLava((HttpStatusCode.OK, """{"data":{"id":"inv-1","url":null},"status":200,"status_check":true}"""));
            await Should.ThrowAsync<PaymentProviderException>(() => Provider(noLink).CreateAsync(Request(Guid.NewGuid()), CancellationToken.None));

            var unused = new StubLava();
            await Should.ThrowAsync<PaymentProviderException>(() => Provider(unused).CreateAsync(Request(Guid.NewGuid()) with { Currency = "USD" }, CancellationToken.None));
            unused.Requests.ShouldBeEmpty();
        }

        [Fact]
        public async Task Status_IsAskedByOurOrderId()
        {
            // FR-PAY-07, FR-PAY-09: our order id works even when the create call timed out and no invoice id is known.
            var lava = new StubLava((HttpStatusCode.OK, """{"data":{"status":"success","error_message":null,"id":"inv-1","shop_id":"shop-1","amount":199,"expire":"2026-10-05 13:00:00","order_id":"o","fail_url":null,"success_url":null,"hook_url":null,"custom_fields":null,"include_service":null,"exclude_service":null},"status":200,"status_check":true}"""));
            Guid orderId = Guid.NewGuid();

            ProviderPaymentStatus status = await Provider(lava).GetStatusAsync(orderId, null, CancellationToken.None);

            status.ShouldBe(new ProviderPaymentStatus("inv-1", ProviderPaymentState.Succeeded, 199m, null));
            StubLava.Sent sent = lava.Requests.ShouldHaveSingleItem();
            sent.Path.ShouldBe("/business/invoice/status");
            Encoding.UTF8.GetString(sent.Body).ShouldBe($$"""{"shopId":"shop-1","orderId":"{{orderId}}"}""");
            sent.Signature.ShouldBe(LavaSignature.Sign(sent.Body, SecretKey));
        }

        [Fact]
        public async Task Status_OfAnInvoiceLavaNeverGot_IsPending()
        {
            var lava = new StubLava((HttpStatusCode.NotFound, """{"error":"Счёт не найден","data":null,"status":null,"status_check":null}"""));

            ProviderPaymentStatus status = await Provider(lava).GetStatusAsync(Guid.NewGuid(), "inv-9", CancellationToken.None);

            status.ShouldBe(new ProviderPaymentStatus("inv-9", ProviderPaymentState.Pending, null, null));
        }

        [Fact]
        public void Startup_RefusesLavaWithoutKeys_AndAnActiveProviderThatIsOff()
        {
            using ServiceProvider withoutKeys = Infrastructure(("Payments:Lava:Enabled", "true"));
            Should.Throw<OptionsValidationException>(() => withoutKeys.GetRequiredService<IOptions<LavaPaymentOptions>>().Value)
                .Message.ShouldContain("Payments:Lava:ShopId, Payments:Lava:SecretKey and Payments:Lava:WebhookKey are required");

            using ServiceProvider lavaOff = Infrastructure(("Payments:Enabled", "true"), ("Payments:ActiveProvider", "lava"));
            Should.Throw<OptionsValidationException>(() => lavaOff.GetRequiredService<IOptions<PaymentOptions>>().Value)
                .Message.ShouldContain("Payments:ActiveProvider must name a switched-on provider");

            using ServiceProvider lavaOn = Infrastructure(
                ("Payments:Enabled", "true"), ("Payments:ActiveProvider", "lava"), ("Payments:Lava:Enabled", "true"),
                ("Payments:Lava:ShopId", "shop-1"), ("Payments:Lava:SecretKey", SecretKey), ("Payments:Lava:WebhookKey", WebhookKey));
            lavaOn.GetRequiredService<IOptions<PaymentOptions>>().Value.ActiveProvider.ShouldBe("lava");
            lavaOn.GetRequiredService<IPaymentProviderRegistry>().Active.ShouldBeOfType<LavaPaymentProvider>();
        }

        private static ServiceProvider Infrastructure(params (string Key, string Value)[] settings)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings
                    .Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))
                    .Append(new KeyValuePair<string, string?>("ConnectionStrings:Database", "Host=localhost;Database=unused")))
                .Build();

            return new ServiceCollection()
                .AddLogging()
                .AddInfrastructure(configuration)
                .BuildServiceProvider();
        }

        private static CreatePaymentRequest Request(Guid orderId) =>
            new(orderId, 199m, "RUB", "Подписка «Базовый, 1 месяц»", $"https://site.test/pay/return/{orderId}", null);

        private static WebhookRequest Webhook(byte[] body, params (string Name, string Value)[] headers) =>
            new(headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase), body, "203.0.113.5");

        private static LavaPaymentProvider Provider(StubLava lava, string[]? includeServices = null) => new(
            lava,
            Options.Create(new LavaPaymentOptions
            {
                Enabled = true,
                ShopId = "shop-1",
                SecretKey = SecretKey,
                WebhookKey = WebhookKey,
                IncludeServices = includeServices ?? [],
            }),
            Options.Create(new PaymentOptions()),
            Options.Create(new ServiceOptions { SiteBaseUrl = "https://site.test", WebhookBaseUrl = "https://hooks.test" }),
            new FixedClock(Now),
            NullLogger<LavaPaymentProvider>.Instance);

        /// <summary>Lava's API: records what was sent and answers in turn, or fails like an unreachable host.</summary>
        private sealed class StubLava : HttpMessageHandler, IHttpClientFactory
        {
            private readonly Queue<(HttpStatusCode Status, string Body)> _answers;
            private readonly Exception? _failure;

            public StubLava(params (HttpStatusCode Status, string Body)[] answers) => _answers = new(answers);

            public StubLava(Exception failure) : this() => _failure = failure;

            public List<Sent> Requests { get; } = [];

            public HttpClient CreateClient(string name) =>
                new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.lava.test/") };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Requests.Add(new Sent(
                    request.RequestUri!.AbsolutePath,
                    body,
                    request.Headers.TryGetValues("Signature", out IEnumerable<string>? values) ? values.Single() : null,
                    request.Content?.Headers.ContentType?.ToString()));

                if (_failure is not null)
                {
                    throw _failure;
                }

                (HttpStatusCode status, string text) = _answers.Dequeue();
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            }

            public sealed record Sent(string Path, byte[] Body, string? Signature, string? ContentType);
        }

        private sealed class FixedClock(DateTime utcNow) : IDateTimeProvider
        {
            public DateTime UtcNow => utcNow;
        }
    }
}
