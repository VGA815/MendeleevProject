using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Mendeleev.Infrastructure;
using Mendeleev.Infrastructure.Payments.TryBit;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mendeleev.UnitTests.Infrastructure
{
    /// <summary>The TryBit adapter against the answers and the POSTBACK of TryBit's documentation (ТЗ 23).</summary>
    public class TryBitPaymentProviderTests
    {
        private const string SecretKey = "trybit-secret-key";

        private static readonly DateTime Now = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

        private static readonly Guid OrderId = Guid.Parse("0199b8a4-6f0e-7c3b-9a52-3f1c2d4e5a60");

        [Fact]
        public async Task Postback_WithAValidToken_IsConfirmedThroughTheApi()
        {
            // FR-PAY-02: the token only says the POSTBACK is TryBit's; the state and the amount are the API's.
            var trybit = new StubTryBit((HttpStatusCode.OK, InfoAnswer("paid")));

            PaymentNotification notification = await Provider(trybit).ParseWebhookAsync(Postback(Token(SecretKey)), CancellationToken.None);

            notification.ShouldBe(new PaymentNotification("INV-ILRAJE1Q", OrderId, ProviderPaymentState.Succeeded, 199m, "RUB"));
            StubTryBit.Sent asked = trybit.Requests.ShouldHaveSingleItem();
            asked.Path.ShouldBe("/v2/invoice/merchant/info");
            asked.ContentType.ShouldBe("application/json");
            Encoding.UTF8.GetString(asked.Body).ShouldBe("""{"uuids":["INV-ILRAJE1Q"]}""");
        }

        [Fact]
        public async Task Postback_SaysPaid_ButTheApiSaysOtherwise_TheApiWins()
        {
            // Nothing in the POSTBACK body is signed: an underpaid invoice stays pending whatever the body says.
            var trybit = new StubTryBit((HttpStatusCode.OK, InfoAnswer("partial")));

            PaymentNotification notification = await Provider(trybit).ParseWebhookAsync(Postback(Token(SecretKey)), CancellationToken.None);

            notification.State.ShouldBe(ProviderPaymentState.Pending);
        }

        [Fact]
        public async Task Postback_AsAForm_IsReadToo()
        {
            var trybit = new StubTryBit((HttpStatusCode.OK, InfoAnswer("overpaid")));
            byte[] body = Encoding.UTF8.GetBytes($"status=success&invoice_id=ILRAJE1Q&amount_crypto=2.1&currency=USDT_TRC20&order_id={OrderId}&token={Uri.EscapeDataString(Token(SecretKey))}");

            PaymentNotification notification = await Provider(trybit).ParseWebhookAsync(
                Webhook(body, "application/x-www-form-urlencoded"), CancellationToken.None);

            notification.ShouldBe(new PaymentNotification("INV-ILRAJE1Q", OrderId, ProviderPaymentState.Succeeded, 199m, "RUB"));
        }

        [Fact]
        public async Task Postback_WithAWrongOrMissingToken_IsRejected_WithoutAskingTheApi()
        {
            // FR-PAY-02: the endpoint answers 401 to all of these.
            var trybit = new StubTryBit();
            TryBitPaymentProvider provider = Provider(trybit);
            string valid = Token(SecretKey);
            string[] parts = valid.Split('.');

            string[] tokens =
            [
                Token("another-project-key"),
                $"{parts[0]}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"id":1,"exp":1999999999}"""))}.{parts[2]}",
                Token(SecretKey, new { id = 13, exp = Seconds(Now.AddMinutes(-2)) }),
                Token(SecretKey, new { id = 13 }, alg: "HS512"),
                $"{Base64Url.EncodeToString("""{"alg":"none"}"""u8)}.{parts[1]}.",
                "Bearer something",
            ];

            foreach (string token in tokens)
            {
                await Should.ThrowAsync<WebhookAuthenticationException>(() => provider.ParseWebhookAsync(Postback(token), CancellationToken.None));
            }

            byte[] withoutToken = Encoding.UTF8.GetBytes("""{"status":"success","invoice_id":"ILRAJE1Q","order_id":null}""");
            await Should.ThrowAsync<WebhookAuthenticationException>(() => provider.ParseWebhookAsync(Webhook(withoutToken), CancellationToken.None));
            await Should.ThrowAsync<WebhookAuthenticationException>(() => provider.ParseWebhookAsync(Webhook("not json"u8.ToArray()), CancellationToken.None));
            trybit.Requests.ShouldBeEmpty();
        }

        [Fact]
        public void Token_IsCheckedWithinAMinuteOfClockSkew()
        {
            TryBitToken.IsValid(Token(SecretKey, new { exp = Seconds(Now.AddSeconds(-30)) }), SecretKey, Now).ShouldBeTrue();
            TryBitToken.IsValid(Token(SecretKey, new { nbf = Seconds(Now.AddSeconds(30)) }), SecretKey, Now).ShouldBeTrue();
            TryBitToken.IsValid(Token(SecretKey, new { nbf = Seconds(Now.AddMinutes(2)) }), SecretKey, Now).ShouldBeFalse();
            TryBitToken.IsValid(Token(SecretKey, new { exp = "soon" }), SecretKey, Now).ShouldBeFalse();
            TryBitToken.IsValid(Token(SecretKey, new { id = 13 }), SecretKey, Now).ShouldBeTrue();
        }

        [Fact]
        public async Task Postback_ThatNamesNoInvoiceOfThisProject_IsRejected()
        {
            byte[] noInvoice = Encoding.UTF8.GetBytes($$"""{"status":"success","invoice_id":null,"token":"{{Token(SecretKey)}}"}""");
            await Should.ThrowAsync<WebhookAuthenticationException>(() => Provider(new StubTryBit()).ParseWebhookAsync(Webhook(noInvoice), CancellationToken.None));

            var unknown = new StubTryBit((HttpStatusCode.OK, """{"status":"success","result":[]}"""));
            await Should.ThrowAsync<WebhookAuthenticationException>(() => Provider(unknown).ParseWebhookAsync(Postback(Token(SecretKey)), CancellationToken.None));
        }

        [Fact]
        public async Task Postback_WhileTheApiIsDown_IsAProviderError()
        {
            // Not a 401: the endpoint answers 500, TryBit can resend, and the 5-minute check asks again (FR-PAY-07).
            var down = new StubTryBit(new HttpRequestException("Connection refused"));

            await Should.ThrowAsync<PaymentProviderException>(() => Provider(down).ParseWebhookAsync(Postback(Token(SecretKey)), CancellationToken.None));
        }

        [Theory]
        [InlineData("created", ProviderPaymentState.Pending)]
        [InlineData("partial", ProviderPaymentState.Pending)]
        [InlineData("paid", ProviderPaymentState.Succeeded)]
        [InlineData("overpaid", ProviderPaymentState.Succeeded)]
        [InlineData("canceled", ProviderPaymentState.Canceled)]
        [InlineData("something_new", ProviderPaymentState.Unknown)]
        public void Statuses_MapToOurs(string status, ProviderPaymentState expected) =>
            TryBitPaymentProvider.MapStatus(status).ShouldBe(expected);

        [Fact]
        public async Task TestInvoice_CountsOnlyWhereTestInvoicesAreAccepted()
        {
            // Confirmed in the dashboard without any payment: on staging it is FR-PAY-04, in production nothing.
            string answer = InfoAnswer("paid", testMode: true);

            ProviderPaymentStatus production = await Provider(new StubTryBit((HttpStatusCode.OK, answer))).GetStatusAsync(OrderId, "INV-ILRAJE1Q", CancellationToken.None);
            ProviderPaymentStatus staging = await Provider(new StubTryBit((HttpStatusCode.OK, answer)), acceptTestInvoices: true).GetStatusAsync(OrderId, "INV-ILRAJE1Q", CancellationToken.None);

            production.State.ShouldBe(ProviderPaymentState.Unknown);
            staging.State.ShouldBe(ProviderPaymentState.Succeeded);
        }

        [Fact]
        public async Task Create_SendsOurOrderAndLifetime_AndReturnsTheRussianPage()
        {
            // FR-PAY-01: the ruble amount and our order id; FR-PAY-11: the invoice lives 60 minutes.
            var trybit = new StubTryBit((HttpStatusCode.OK, CreateAnswer("https://pay.trybit.com/89UX09KA")));

            CreatedPayment created = await Provider(trybit).CreateAsync(Request(), CancellationToken.None);

            created.ShouldBe(new CreatedPayment("INV-89UX09KA", "https://pay.trybit.com/89UX09KA?lang=ru", Now.AddMinutes(60)));

            StubTryBit.Sent sent = trybit.Requests.ShouldHaveSingleItem();
            sent.Path.ShouldBe("/v2/invoice/create");
            Encoding.UTF8.GetString(sent.Body).ShouldBe(
                """{"shop_id":"shop-1","amount":199,"currency":"RUB","order_id":"ORDER","add_fields":{"time_to_pay":{"hours":1,"minutes":0}}}"""
                    .Replace("ORDER", OrderId.ToString(), StringComparison.Ordinal));
        }

        [Fact]
        public async Task Create_TakesTheLinkWithOrWithoutTheScheme()
        {
            var trybit = new StubTryBit((HttpStatusCode.OK, CreateAnswer("pay.trybit.com/89UX09KA")));

            (await Provider(trybit).CreateAsync(Request(), CancellationToken.None)).ConfirmationUrl
                .ShouldBe("https://pay.trybit.com/89UX09KA?lang=ru");
        }

        [Fact]
        public async Task Create_InATestProject_IsRefusedWhereTestInvoicesDoNotCount()
        {
            // Real money sent to a test invoice is never credited.
            string answer = CreateAnswer("https://pay.trybit.com/89UX09KA", testMode: true);

            await Should.ThrowAsync<PaymentProviderException>(() => Provider(new StubTryBit((HttpStatusCode.OK, answer))).CreateAsync(Request(), CancellationToken.None));
            (await Provider(new StubTryBit((HttpStatusCode.OK, answer)), acceptTestInvoices: true).CreateAsync(Request(), CancellationToken.None))
                .ProviderPaymentId.ShouldBe("INV-89UX09KA");
        }

        [Fact]
        public async Task Create_RefusedOrUnreachable_IsAProviderError()
        {
            var refused = new StubTryBit((HttpStatusCode.Unauthorized, """{"status":"error","result":{"authorization":"Unauthorized request."}}"""));
            PaymentProviderException exception = await Should.ThrowAsync<PaymentProviderException>(() => Provider(refused).CreateAsync(Request(), CancellationToken.None));
            exception.Message.ShouldContain("401");
            exception.Message.ShouldContain("Unauthorized request.");

            var errorIn200 = new StubTryBit((HttpStatusCode.OK, """{"status":"error","result":{"amount":"Minimum amount is 0.1 USD"}}"""));
            (await Should.ThrowAsync<PaymentProviderException>(() => Provider(errorIn200).CreateAsync(Request(), CancellationToken.None)))
                .Message.ShouldContain("Minimum amount");

            var down = new StubTryBit(new HttpRequestException("Connection refused"));
            await Should.ThrowAsync<PaymentProviderException>(() => Provider(down).CreateAsync(Request(), CancellationToken.None));

            var noLink = new StubTryBit((HttpStatusCode.OK, """{"status":"success","result":{"uuid":"INV-89UX09KA","link":null,"status":"created"}}"""));
            await Should.ThrowAsync<PaymentProviderException>(() => Provider(noLink).CreateAsync(Request(), CancellationToken.None));

            var garbage = new StubTryBit((HttpStatusCode.OK, "<html>502</html>"));
            await Should.ThrowAsync<PaymentProviderException>(() => Provider(garbage).CreateAsync(Request(), CancellationToken.None));

            var noResult = new StubTryBit((HttpStatusCode.OK, """{"status":"success"}"""));
            await Should.ThrowAsync<PaymentProviderException>(() => Provider(noResult).CreateAsync(Request(), CancellationToken.None));
        }

        [Fact]
        public async Task Status_IsAskedByTheInvoiceNumber()
        {
            // FR-PAY-07, FR-PAY-09: by the number the create call returned.
            var trybit = new StubTryBit((HttpStatusCode.OK, InfoAnswer("canceled")));

            ProviderPaymentStatus status = await Provider(trybit).GetStatusAsync(OrderId, "INV-ILRAJE1Q", CancellationToken.None);

            status.ShouldBe(new ProviderPaymentStatus("INV-ILRAJE1Q", ProviderPaymentState.Canceled, 199m, "RUB"));
            Encoding.UTF8.GetString(trybit.Requests.ShouldHaveSingleItem().Body).ShouldBe("""{"uuids":["INV-ILRAJE1Q"]}""");
        }

        [Fact]
        public async Task Status_WithoutAnInvoiceNumber_OrOfAnInvoiceTryBitDoesNotKnow_IsPending()
        {
            // The create call failed: there is no number to ask about, and the link was never shown.
            var unused = new StubTryBit();
            (await Provider(unused).GetStatusAsync(OrderId, null, CancellationToken.None))
                .ShouldBe(new ProviderPaymentStatus(null, ProviderPaymentState.Pending, null, null));
            unused.Requests.ShouldBeEmpty();

            var unknown = new StubTryBit((HttpStatusCode.OK, """{"status":"success","result":[]}"""));
            (await Provider(unknown).GetStatusAsync(OrderId, "INV-GONE0000", CancellationToken.None))
                .ShouldBe(new ProviderPaymentStatus("INV-GONE0000", ProviderPaymentState.Pending, null, null));
        }

        [Fact]
        public void Startup_RefusesTryBitWithoutKeys_AndAnActiveProviderThatIsOff()
        {
            using ServiceProvider withoutKeys = Infrastructure(("Payments:TryBit:Enabled", "true"), ("Payments:TryBit:ShopId", "shop-1"));
            Should.Throw<OptionsValidationException>(() => withoutKeys.GetRequiredService<IOptions<TryBitPaymentOptions>>().Value)
                .Message.ShouldContain("Payments:TryBit:ShopId, Payments:TryBit:ApiKey and Payments:TryBit:SecretKey are required");

            using ServiceProvider tryBitOff = Infrastructure(("Payments:Enabled", "true"), ("Payments:ActiveProvider", "trybit"));
            Should.Throw<OptionsValidationException>(() => tryBitOff.GetRequiredService<IOptions<PaymentOptions>>().Value)
                .Message.ShouldContain("Payments:ActiveProvider must name a switched-on provider");

            using ServiceProvider tryBitOn = Infrastructure(
                ("Payments:Enabled", "true"), ("Payments:ActiveProvider", "trybit"), ("Payments:TryBit:Enabled", "true"),
                ("Payments:TryBit:ShopId", "shop-1"), ("Payments:TryBit:ApiKey", "api-key"), ("Payments:TryBit:SecretKey", SecretKey));
            tryBitOn.GetRequiredService<IOptions<PaymentOptions>>().Value.ActiveProvider.ShouldBe("trybit");
            tryBitOn.GetRequiredService<IPaymentProviderRegistry>().Active.ShouldBeOfType<TryBitPaymentProvider>();
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

        /// <summary>A POSTBACK as TryBit's documentation shows it, with the fields that must not be kept.</summary>
        private static WebhookRequest Postback(string token) => Webhook(Encoding.UTF8.GetBytes($$$"""
            {"status":"success","invoice_id":"ILRAJE1Q","amount_crypto":1000000,"currency":"USDT_TRC20","order_id":"{{{OrderId}}}","token":"{{{token}}}",
             "invoice_info":{"uuid":"INV-ILRAJE1Q","address":"TXq9fakeAddress","status":"paid","amount_in_fiat":1.0,"user_email":"a**********b@gmail.com","tx_list":["0x12fb"],"test_mode":false}}
            """));

        private static WebhookRequest Webhook(byte[] body, string contentType = "application/json") =>
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = contentType }, body, "203.0.113.5");

        private static string InfoAnswer(string status, bool testMode = false) => $$"""
            {"status":"success","result":[{"uuid":"INV-ILRAJE1Q","created":"2026-10-07 08:50:04.222439","address":"TXq9fakeAddress","status":"{{status}}","invoice_status":"success",
             "test_mode":{{(testMode ? "true" : "false")}},"user_email":"","order_id":"{{OrderId}}","amount_in_fiat":199.0,"fiat_currency":"RUB","amount":2.1,"amount_usd":2.41,"tx_list":[""]}]}
            """;

        private static string CreateAnswer(string link, bool testMode = false) => $$$"""
            {"status":"success","result":{"uuid":"INV-89UX09KA","created":"2026-10-07 09:00:00.958133","address":"","expiry_date":"2026-10-07 10:00:00.493361",
             "test_mode":{{{(testMode ? "true" : "false")}}},"amount_in_fiat":199.0,"fiat_currency":"RUB","status":"created","link":"{{{link}}}","invoice_id":null}}
            """;

        private static CreatePaymentRequest Request() =>
            new(OrderId, 199m, "RUB", "Подписка «Базовый, 1 месяц»", $"https://site.test/pay/return/{OrderId}", null);

        /// <summary>An HS256 JWT the way TryBit signs its POSTBACK; by default valid for 5 more minutes.</summary>
        private static string Token(string key, object? claims = null, string alg = "HS256")
        {
            string header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg }));
            string payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims ?? new { id = 13, exp = Seconds(Now.AddMinutes(5)) }));
            string signature = Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes($"{header}.{payload}")));
            return $"{header}.{payload}.{signature}";
        }

        private static long Seconds(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;

        private static TryBitPaymentProvider Provider(StubTryBit trybit, bool acceptTestInvoices = false) => new(
            trybit,
            Options.Create(new TryBitPaymentOptions
            {
                Enabled = true,
                ShopId = "shop-1",
                ApiKey = "api-key",
                SecretKey = SecretKey,
                AcceptTestInvoices = acceptTestInvoices,
            }),
            Options.Create(new PaymentOptions()),
            new FixedClock(Now),
            NullLogger<TryBitPaymentProvider>.Instance);

        /// <summary>TryBit's API: records what was sent and answers in turn, or fails like an unreachable host.</summary>
        private sealed class StubTryBit : HttpMessageHandler, IHttpClientFactory
        {
            private readonly Queue<(HttpStatusCode Status, string Body)> _answers;
            private readonly Exception? _failure;

            public StubTryBit(params (HttpStatusCode Status, string Body)[] answers) => _answers = new(answers);

            public StubTryBit(Exception failure) : this() => _failure = failure;

            public List<Sent> Requests { get; } = [];

            public HttpClient CreateClient(string name) =>
                new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.trybit.test/v2/") };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Requests.Add(new Sent(request.RequestUri!.AbsolutePath, body, request.Content?.Headers.ContentType?.ToString()));

                if (_failure is not null)
                {
                    throw _failure;
                }

                (HttpStatusCode status, string text) = _answers.Dequeue();
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            }

            public sealed record Sent(string Path, byte[] Body, string? ContentType);
        }

        private sealed class FixedClock(DateTime utcNow) : IDateTimeProvider
        {
            public DateTime UtcNow => utcNow;
        }
    }
}
