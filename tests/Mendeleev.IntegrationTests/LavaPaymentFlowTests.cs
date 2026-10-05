using System.Net;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.Reconciliation;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Infrastructure.Payments.Lava;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// ТЗ 23 with Lava as the active aggregator: the real use cases over a real database, Lava's API stubbed at the
    /// HTTP level, its webhooks signed with the additional key and sent in the <c>Authorization</c> header.
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class LavaPaymentFlowTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private const string WebhookKey = "lava-additional-key";

        private readonly LavaApi _lava = new();
        private TestApp _app = null!;

        public async Task InitializeAsync() => _app = await TestApp.CreateAsync(
            postgres,
            new Dictionary<string, string?>
            {
                ["Service:WebhookBaseUrl"] = "https://hooks.test",
                ["Payments:ActiveProvider"] = "lava",
                ["Payments:Lava:Enabled"] = "true",
                ["Payments:Lava:BaseUrl"] = "https://api.lava.test",
                ["Payments:Lava:ShopId"] = "shop-1",
                ["Payments:Lava:SecretKey"] = "lava-secret-key",
                ["Payments:Lava:WebhookKey"] = WebhookKey,
            },
            services => services.AddHttpClient(LavaPaymentProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _lava));

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task PaidThroughLava_ExtendsOnce_AndBringsAccess()
        {
            // FR-PAY-01…04: the invoice carries our payment id and return address; a repeated webhook changes nothing.
            long userId = await NewUserAsync(4001);
            PaymentLink link = await CreatePaymentAsync(userId, "inv-1");

            link.ConfirmationUrl.ShouldBe("https://pay.lava.ru/invoice/inv-1");
            Payment payment = await PaymentAsync(link.PaymentId);
            payment.Provider.ShouldBe("lava");
            payment.ProviderPaymentId.ShouldBe("inv-1");
            payment.LinkExpiresAt.ShouldBe(_app.Clock.UtcNow.AddMinutes(60));

            using (JsonDocument sent = JsonDocument.Parse(_lava.Requests.ShouldHaveSingleItem()))
            {
                sent.RootElement.GetProperty("orderId").GetString().ShouldBe(link.PaymentId.ToString());
                sent.RootElement.GetProperty("sum").GetDecimal().ShouldBe(199m);
                sent.RootElement.GetProperty("successUrl").GetString().ShouldBe($"https://site.test/pay/return/{link.PaymentId}");
                sent.RootElement.GetProperty("hookUrl").GetString().ShouldBe("https://hooks.test/webhooks/payments/lava");
            }

            WebhookRequest webhook = LavaWebhook("inv-1", link.PaymentId, "199.00");
            (await DeliverAsync(webhook)).IsSuccess.ShouldBeTrue();
            (await DeliverAsync(webhook)).IsSuccess.ShouldBeTrue();

            Subscription subscription = await _app.WithDbAsync(db => db.Subscriptions.AsNoTracking().SingleAsync(s => s.UserId == userId));
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(30));
            (await EventsAsync(link.PaymentId)).Count(e => e.Kind == PaymentEventKind.Applied).ShouldBe(1);

            // FR-PAY-05: the masked card number of the webhook is kept nowhere.
            (await EventsAsync(link.PaymentId)).ShouldAllBe(e => e.Details == null || !e.Details.Contains("4213"));

            await _app.ProcessOutboxAsync();
            _app.Panel.Users.ShouldHaveSingleItem().ExpireAt.ShouldBe(subscription.ExpiresAt);
        }

        [Fact]
        public async Task LostWebhook_IsPickedUpByThePendingCheck()
        {
            // FR-PAY-07: Lava is asked by our order id.
            long userId = await NewUserAsync(4002);
            PaymentLink link = await CreatePaymentAsync(userId, "inv-2");
            _lava.Answer(HttpStatusCode.OK, $$"""{"data":{"status":"success","error_message":null,"id":"inv-2","shop_id":"shop-1","amount":199,"order_id":"{{link.PaymentId}}"},"status":200,"status_check":true}""");

            (await _app.SendAsync<CheckPendingPaymentsCommand, int>(new CheckPendingPaymentsCommand())).Value.ShouldBe(1);

            (await PaymentAsync(link.PaymentId)).Status.ShouldBe(PaymentStatus.Succeeded);
            using JsonDocument asked = JsonDocument.Parse(_lava.Requests.Last());
            asked.RootElement.GetProperty("orderId").GetString().ShouldBe(link.PaymentId.ToString());
        }

        [Fact]
        public async Task Webhook_WithAnotherAmount_DoesNotExtend()
        {
            // FR-PAY-02: Lava names no currency, the amount is still compared.
            long userId = await NewUserAsync(4003);
            PaymentLink link = await CreatePaymentAsync(userId, "inv-3");

            (await DeliverAsync(LavaWebhook("inv-3", link.PaymentId, "19.90"))).IsSuccess.ShouldBeTrue();

            Payment payment = await PaymentAsync(link.PaymentId);
            payment.Status.ShouldBe(PaymentStatus.Pending);
            payment.NeedsReview.ShouldBeTrue();
            (await EventsAsync(link.PaymentId)).ShouldContain(e => e.Kind == PaymentEventKind.AmountMismatch);
            (await _app.WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.UserId == userId))).ShouldBeFalse();
        }

        private async Task<long> NewUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private async Task<PaymentLink> CreatePaymentAsync(long userId, string invoiceId)
        {
            _lava.Answer(HttpStatusCode.OK, $$"""{"data":{"id":"{{invoiceId}}","amount":199,"expired":"2026-10-05 13:00:00","status":"created","shop_id":"shop-1","url":"https://pay.lava.ru/invoice/{{invoiceId}}","comment":null,"merchantName":null,"exclude_service":null,"include_service":null},"status":200,"status_check":true}""");
            return (await _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, "basic_1m"))).Value;
        }

        private Task<Result> DeliverAsync(WebhookRequest webhook) =>
            _app.SendAsync(new HandlePaymentWebhookCommand("lava", webhook));

        private Task<Payment> PaymentAsync(Guid paymentId) =>
            _app.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId));

        private Task<List<PaymentEvent>> EventsAsync(Guid paymentId) =>
            _app.WithDbAsync(db => db.PaymentEvents.AsNoTracking().Where(e => e.PaymentId == paymentId).ToListAsync());

        /// <summary>The invoice webhook as Lava sends it, with the fields that must not be kept.</summary>
        private static WebhookRequest LavaWebhook(string invoiceId, Guid orderId, string amount)
        {
            byte[] body = Encoding.UTF8.GetBytes($$"""{"invoice_id":"{{invoiceId}}","status":"success","pay_time":"2026-10-05 12:01:00","amount":"{{amount}}","order_id":"{{orderId}}","pay_service":"card","payer_details":"553691******4213","custom_fields":null,"type":1,"credited":"190.00"}""");
            return new WebhookRequest(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = LavaSignature.Sign(body, WebhookKey) },
                body,
                "203.0.113.7");
        }

        /// <summary>Lava's API: answers in turn and keeps the bodies it was sent.</summary>
        private sealed class LavaApi : HttpMessageHandler
        {
            private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

            public List<string> Requests { get; } = [];

            public void Answer(HttpStatusCode status, string body) => _answers.Enqueue((status, body));

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
                (HttpStatusCode status, string body) = _answers.Dequeue();
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            // The client factory disposes its handlers; this one lives as long as the test.
            protected override void Dispose(bool disposing)
            {
            }
        }
    }
}
