using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Payments.Check;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.Reconciliation;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Application.Subscriptions.Maintenance;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    [Collection(nameof(PostgresCollection))]
    public sealed class PaymentFlowTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private TestApp _app = null!;

        private IFakePaymentSimulator Aggregator => _app.Provider.GetRequiredService<IFakePaymentSimulator>();

        public async Task InitializeAsync() => _app = await TestApp.CreateAsync(postgres);

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task Webhook_RedeliveredAndParallel_ExtendsExactlyOnce()
        {
            // FR-PAY-03, NFR-10: the same webhook 5 times and 2 in parallel → one extension.
            long userId = await NewUserAsync(3001);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            WebhookRequest webhook = Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded)!;

            for (int i = 0; i < 5; i++)
            {
                (await DeliverAsync(webhook)).IsSuccess.ShouldBeTrue();
            }
            Result[] parallel = await Task.WhenAll(DeliverAsync(webhook), DeliverAsync(webhook));
            parallel.ShouldAllBe(r => r.IsSuccess);

            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(30));

            int applied = await _app.WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.PaymentId == link.PaymentId && e.Kind == PaymentEventKind.Applied));
            applied.ShouldBe(1);

            // FR-PAY-04: the outbox brings access and the message.
            await _app.ProcessOutboxAsync();
            _app.Panel.Users.ShouldHaveSingleItem().ExpireAt.ShouldBe(subscription.ExpiresAt);
            _app.Messenger.Notifications.ShouldHaveSingleItem().Message.Kind.ShouldBe(NotificationKind.AccessIssued);
        }

        [Fact]
        public async Task TwoDifferentPayments_AtOnce_AddUp()
        {
            // ТЗ 22: two simultaneous payments are applied one after the other.
            long userId = await NewUserAsync(3002);
            PaymentLink month = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            PaymentLink quarter = (await CreatePaymentAsync(userId, "basic_3m")).Value;

            await Task.WhenAll(
                DeliverAsync(Aggregator.Simulate(month.PaymentId, ProviderPaymentState.Succeeded)!),
                DeliverAsync(Aggregator.Simulate(quarter.PaymentId, ProviderPaymentState.Succeeded)!));

            (await SubscriptionOfAsync(userId)).ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(120));
        }

        [Fact]
        public async Task WrongSignature_IsRejected_AndNothingIsGranted()
        {
            long userId = await NewUserAsync(3003);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            WebhookRequest genuine = Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded)!;
            var forged = genuine with
            {
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Fake-Signature"] = new string('0', 64) },
            };

            (await DeliverAsync(forged)).Error.Type.ShouldBe(ErrorType.Unauthorized);
            (await _app.WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.UserId == userId))).ShouldBeFalse();
        }

        [Fact]
        public async Task WrongAmount_IsNotApplied_AndRaisesAnAlert()
        {
            // FR-PAY-02: a matching signature is not enough — amount and currency must match too.
            long userId = await NewUserAsync(3004);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            WebhookRequest webhook = SignedFakeWebhook(link.PaymentId, $"fake_{link.PaymentId:N}", ProviderPaymentState.Succeeded, amount: 1m);

            (await DeliverAsync(webhook)).IsSuccess.ShouldBeTrue();

            Payment payment = await _app.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == link.PaymentId));
            payment.Status.ShouldBe(PaymentStatus.Pending);
            payment.NeedsReview.ShouldBeTrue();
            (await _app.WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.UserId == userId))).ShouldBeFalse();
            _app.Alerts.Raised.ShouldContain(a => a.Key == $"payment-mismatch:{link.PaymentId}");
        }

        [Fact]
        public async Task SameTariffAgain_ReusesTheUnpaidPayment_AndTheSixthInAnHourIsRefused()
        {
            // FR-PAY-10.
            long userId = await NewUserAsync(3005);
            PaymentLink first = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            PaymentLink again = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            again.PaymentId.ShouldBe(first.PaymentId);
            again.Reused.ShouldBeTrue();

            await CancelAsync(first.PaymentId);
            for (int i = 0; i < 4; i++)
            {
                PaymentLink next = (await CreatePaymentAsync(userId, "basic_1m")).Value;
                await CancelAsync(next.PaymentId);
            }

            (await CreatePaymentAsync(userId, "basic_1m")).Error.ShouldBe(PaymentErrors.TooManyPayments);
        }

        [Fact]
        public async Task LostWebhook_IsCaughtByThePendingCheck()
        {
            // FR-PAY-07: the webhook never came, the 5-minute check finds the payment.
            long userId = await NewUserAsync(3006);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded);

            (await _app.SendAsync<CheckPendingPaymentsCommand, int>(new CheckPendingPaymentsCommand())).Value.ShouldBe(1);

            (await SubscriptionOfAsync(userId)).Status.ShouldBe(SubscriptionStatus.Active);
        }

        [Fact]
        public async Task CheckButton_AppliesThePayment_ButNotMoreOftenThanEvery10Seconds()
        {
            // FR-PAY-09.
            long userId = await NewUserAsync(3007);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;

            (await CheckAsync(userId, link.PaymentId)).Status.ShouldBe(PaymentStatus.Pending);

            Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded);
            (await CheckAsync(userId, link.PaymentId)).Status.ShouldBe(PaymentStatus.Pending); // throttled

            _app.Clock.Advance(TimeSpan.FromSeconds(11));
            PaymentCheckResult result = await CheckAsync(userId, link.PaymentId);
            result.Status.ShouldBe(PaymentStatus.Succeeded);
            result.AccessPending.ShouldBeTrue();
        }

        [Fact]
        public async Task Reconciliation_AppliesAPaymentMissedByEverythingElse()
        {
            // FR-PAY-06: canceled after 60 minutes, then paid late — the daily reconciliation applies it.
            long userId = await NewUserAsync(3008);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            _app.Clock.Advance(TimeSpan.FromMinutes(61));
            await _app.SendAsync<CheckPendingPaymentsCommand, int>(new CheckPendingPaymentsCommand());
            (await _app.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == link.PaymentId))).Status.ShouldBe(PaymentStatus.Canceled);

            Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded);
            ReconciliationSummary summary = (await _app.SendAsync<ReconcilePaymentsCommand, ReconciliationSummary>(new ReconcilePaymentsCommand())).Value;

            summary.AppliedMissed.ShouldBe(1);
            (await SubscriptionOfAsync(userId)).Status.ShouldBe(SubscriptionStatus.Active);
            _app.Alerts.Raised.ShouldContain(a => a.Key.StartsWith("payment-reconciliation", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Reminders_GoOutOncePerExpiryDate()
        {
            // FR-SUB-07: «за 3 дня» once, even when the job runs again.
            long userId = await NewUserAsync(3009);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            await DeliverAsync(Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded)!);
            await _app.ProcessOutboxAsync();

            _app.Clock.Advance(TimeSpan.FromDays(28));
            (await _app.SendAsync<SendRemindersCommand, int>(new SendRemindersCommand())).Value.ShouldBe(1);
            (await _app.SendAsync<SendRemindersCommand, int>(new SendRemindersCommand())).Value.ShouldBe(0);
            await _app.ProcessOutboxAsync();

            _app.Messenger.Notifications.Count(n => n.Message.Kind == NotificationKind.Expiry3d).ShouldBe(1);
        }

        private async Task<long> NewUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private Task<Result<PaymentLink>> CreatePaymentAsync(long userId, string tariff) =>
            _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, tariff));

        private Task<Result> DeliverAsync(WebhookRequest webhook) =>
            _app.SendAsync(new HandlePaymentWebhookCommand("fake", webhook));

        private async Task<PaymentCheckResult> CheckAsync(long userId, Guid paymentId) =>
            (await _app.SendAsync<CheckPaymentCommand, PaymentCheckResult>(new CheckPaymentCommand(userId, paymentId))).Value;

        private Task CancelAsync(Guid paymentId) =>
            DeliverAsync(Aggregator.Simulate(paymentId, ProviderPaymentState.Canceled)!);

        private Task<Subscription> SubscriptionOfAsync(long userId) =>
            _app.WithDbAsync(db => db.Subscriptions.AsNoTracking().SingleAsync(s => s.UserId == userId));

        private static WebhookRequest SignedFakeWebhook(Guid orderId, string providerPaymentId, ProviderPaymentState state, decimal amount)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new { paymentId = providerPaymentId, orderId, status = state, amount, currency = "RUB" },
                JsonSerializerOptions.Web);
            string signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestApp.FakeWebhookSecret), body));

            return new WebhookRequest(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Fake-Signature"] = signature },
                body,
                "127.0.0.1");
        }
    }
}
