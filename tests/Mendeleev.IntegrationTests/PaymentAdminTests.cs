using System.Runtime.CompilerServices;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Admin.Payments;
using Mendeleev.Application.Admin.Stats;
using Mendeleev.Application.Admin.Tariffs;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.History;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Application.Tariffs.GetPurchasableTariffs;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// Stage 1.5, payments for admins: refund marks (FR-PAY-16, FR-ADM-18), the user's payment history (FR-PAY-17),
    /// the active aggregator and the fallback to the next one (FR-PAY-14), tariff prices (FR-ADM-14).
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class PaymentAdminTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private TestApp _app = null!;
        private long _admin;

        private IFakePaymentSimulator Aggregator => _app.Provider.GetRequiredService<IFakePaymentSimulator>();

        public async Task InitializeAsync()
        {
            // A second aggregator that refuses every invoice, registered after the fake one.
            _app = await TestApp.CreateAsync(postgres, configureServices: services => services.AddSingleton<IPaymentProvider, RefusingProvider>());
            _admin = await _app.AddStaffAsync(9201, StaffRole.Admin);
        }

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task UnusedDaysRefund_EndsTheAccessNow_AndIsAudited()
        {
            long userId = await NewUserAsync(5001);
            Guid paymentId = await PaidAsync(userId, "basic_1m");
            _app.Messenger.Notifications.Clear();
            _app.Clock.Advance(TimeSpan.FromDays(3));

            Result<RefundResult> refunded = await RefundAsync(paymentId, RefundKind.UnusedDays);
            await _app.ProcessOutboxAsync();

            refunded.Value.AccessEnded.ShouldBeTrue();
            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.Status.ShouldBe(SubscriptionStatus.Expired);
            subscription.ExpiredReason.ShouldBe(ExpiredReason.Refund);
            subscription.ExpiresAt.ShouldBe(_app.Clock.UtcNow);
            _app.Panel.Users.Single().ExpireAt.ShouldBe(_app.Clock.UtcNow);
            _app.Messenger.Notifications.ShouldHaveSingleItem().Message.Kind.ShouldBe(NotificationKind.RefundEnded);

            (await _app.WithDbAsync(db => db.Payments.SingleAsync(p => p.Id == paymentId))).Status.ShouldBe(PaymentStatus.Refunded);
            AuditLogEntry audit = await _app.WithDbAsync(db => db.AuditLog.AsNoTracking().SingleAsync(a => a.Action == AuditActions.PaymentRefund));
            audit.TargetUserId.ShouldBe(userId);
            audit.Details!.ShouldContain("UnusedDays");

            (await RefundAsync(paymentId, RefundKind.UnusedDays)).Error.ShouldBe(PaymentErrors.AlreadyRefunded);
            _app.Clock.Advance(TimeSpan.FromMinutes(1));
            SalesStats stats = (await _app.QueryAsync<GetStatsQuery, SalesStats>(new GetStatsQuery(_admin, StatsPeriod.Today))).Value;
            stats.Refunds.ShouldBe(1);
        }

        [Fact]
        public async Task ErroneousRefund_TakesThatPaymentsDays_AndOnlyAdminsMarkRefunds()
        {
            long userId = await NewUserAsync(5002);
            Guid first = await PaidAsync(userId, "basic_1m");
            await PaidAsync(userId, "basic_1m");
            DateTime twoMonths = (await SubscriptionOfAsync(userId)).ExpiresAt;
            twoMonths.ShouldBe(_app.Clock.UtcNow.AddDays(60));
            _app.Messenger.Notifications.Clear();

            long support = await _app.AddStaffAsync(9202, StaffRole.Support);
            (await _app.SendAsync<RefundPaymentCommand, RefundResult>(new RefundPaymentCommand(support, first, RefundKind.Erroneous, "повторный платёж")))
                .Error.ShouldBe(StaffErrors.NotAllowed);
            (await _app.SendAsync<RefundPaymentCommand, RefundResult>(new RefundPaymentCommand(_admin, first, RefundKind.Erroneous, "")))
                .Error.Type.ShouldBe(ErrorType.Validation);

            RefundResult refunded = (await RefundAsync(first, RefundKind.Erroneous)).Value;
            await _app.ProcessOutboxAsync();

            refunded.AccessEnded.ShouldBeFalse();
            refunded.ExpiresAt.ShouldBe(twoMonths.AddDays(-30));
            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ExpiresAt.ShouldBe(twoMonths.AddDays(-30));
            _app.Messenger.Notifications.ShouldHaveSingleItem().Message.Kind.ShouldBe(NotificationKind.RefundShortened);

            IReadOnlyList<RefundablePayment> left = (await _app.QueryAsync<ListRefundablePaymentsQuery, IReadOnlyList<RefundablePayment>>(
                new ListRefundablePaymentsQuery(_admin, userId))).Value;
            left.ShouldHaveSingleItem().Id.ShouldNotBe(first);
        }

        [Fact]
        public async Task History_ShowsPaidRefundedAndWaiting_NotExpiredInvoices()
        {
            long userId = await NewUserAsync(5003);
            Guid paid = await PaidAsync(userId, "basic_1m");
            PaymentLink waiting = (await CreatePaymentAsync(userId, "basic_3m")).Value;
            PaymentLink canceled = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            (await DeliverAsync(Aggregator.Simulate(canceled.PaymentId, ProviderPaymentState.Canceled)!)).IsSuccess.ShouldBeTrue();
            (await _app.SendAsync<RecordManualPaymentCommand, ManualPaymentRecorded>(
                new RecordManualPaymentCommand(_admin, userId, "basic_1m", 300, "перевод на карту"))).IsSuccess.ShouldBeTrue();
            (await RefundAsync(paid, RefundKind.Erroneous)).IsSuccess.ShouldBeTrue();

            IReadOnlyList<PaymentHistoryItem> history = (await _app.QueryAsync<GetPaymentHistoryQuery, IReadOnlyList<PaymentHistoryItem>>(
                new GetPaymentHistoryQuery(userId))).Value;

            history.Select(h => h.Id).ShouldNotContain(canceled.PaymentId);
            history.Single(h => h.Id == paid).Status.ShouldBe(PaymentStatus.Refunded);
            history.Single(h => h.Id == waiting.PaymentId).Status.ShouldBe(PaymentStatus.Pending);
            history.Single(h => h.Manual).Amount.ShouldBe(300m);
            history.Count.ShouldBe(3);

            // Only the user's own payments.
            long other = await NewUserAsync(5004);
            (await _app.QueryAsync<GetPaymentHistoryQuery, IReadOnlyList<PaymentHistoryItem>>(new GetPaymentHistoryQuery(other))).Value.ShouldBeEmpty();
        }

        [Fact]
        public async Task AnAdminSwitchesTheAggregator_AndARefusedInvoiceGoesToTheNextOne()
        {
            PaymentProvidersView before = (await ProvidersAsync()).Value;
            before.Active.ShouldBe("fake");
            before.SwitchedOn.ShouldBe(["fake", RefusingProvider.ProviderCode]);

            (await SwitchAsync("nosuch")).Error.ShouldBe(PaymentErrors.ProviderNotSwitchedOn);
            (await SwitchAsync("fake")).Error.ShouldBe(PaymentErrors.ProviderAlreadyActive);
            long support = await _app.AddStaffAsync(9203, StaffRole.Support);
            (await _app.SendAsync(new SwitchPaymentProviderCommand(support, RefusingProvider.ProviderCode))).Error.ShouldBe(StaffErrors.NotAllowed);

            (await SwitchAsync(RefusingProvider.ProviderCode)).IsSuccess.ShouldBeTrue();
            (await ProvidersAsync()).Value.Active.ShouldBe(RefusingProvider.ProviderCode);
            _app.Alerts.Raised.ShouldContain(a => a.Key.StartsWith("payment-provider-switch", StringComparison.Ordinal));
            (await _app.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.PaymentProviderSwitch))).ShouldBe(1);

            // The active one refuses: the same payment goes to the fake aggregator, and the user gets its link.
            long userId = await NewUserAsync(5005);
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;

            Payment payment = await _app.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == link.PaymentId));
            payment.Provider.ShouldBe("fake");
            payment.Status.ShouldBe(PaymentStatus.Pending);
            List<PaymentEventKind> events = await _app.WithDbAsync(db => db.PaymentEvents
                .Where(e => e.PaymentId == link.PaymentId).OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync());
            events.ShouldBe([PaymentEventKind.Created, PaymentEventKind.ProviderError, PaymentEventKind.ProviderFallback, PaymentEventKind.ProviderCreated]);

            // Paid through the fake one as usual.
            (await DeliverAsync(Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded)!)).IsSuccess.ShouldBeTrue();
            (await SubscriptionOfAsync(userId)).Status.ShouldBe(SubscriptionStatus.Active);

            // The fallback payment is not handed out again while the refusing one is active: each click tries it first.
            PaymentLink second = (await CreatePaymentAsync(userId, "basic_3m")).Value;
            PaymentLink third = (await CreatePaymentAsync(userId, "basic_3m")).Value;
            third.PaymentId.ShouldNotBe(second.PaymentId);
        }

        [Fact]
        public async Task AnAdminChangesPricesAndActivity_CreatedPaymentsKeepTheirAmount()
        {
            long userId = await NewUserAsync(5006);
            PaymentLink old = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            old.Amount.ShouldBe(199m);

            (await _app.SendAsync<ChangeTariffPriceCommand, TariffAdminView>(new ChangeTariffPriceCommand(_admin, "basic_1m", 249m))).Value.Price.ShouldBe(249m);
            (await _app.SendAsync<ChangeTariffPriceCommand, TariffAdminView>(new ChangeTariffPriceCommand(_admin, Tariff.TrialCode, 99m))).Error.ShouldBe(TariffErrors.TrialIsFree);
            (await _app.SendAsync<SetTariffActiveCommand, TariffAdminView>(new SetTariffActiveCommand(_admin, "basic_3m", false))).Value.IsActive.ShouldBeFalse();
            long support = await _app.AddStaffAsync(9204, StaffRole.Support);
            (await _app.SendAsync<ChangeTariffPriceCommand, TariffAdminView>(new ChangeTariffPriceCommand(support, "basic_1m", 1m))).Error.ShouldBe(StaffErrors.NotAllowed);

            IReadOnlyList<TariffView> onSale = (await _app.QueryAsync<GetPurchasableTariffsQuery, IReadOnlyList<TariffView>>(new GetPurchasableTariffsQuery())).Value;
            onSale.ShouldHaveSingleItem().Price.ShouldBe(249m);

            (await _app.WithDbAsync(db => db.Payments.SingleAsync(p => p.Id == old.PaymentId))).Amount.ShouldBe(199m);
            long other = await NewUserAsync(5007);
            (await CreatePaymentAsync(other, "basic_1m")).Value.Amount.ShouldBe(249m);
            (await CreatePaymentAsync(other, "basic_3m")).Error.ShouldBe(TariffErrors.NotPurchasable);

            List<string> audited = await _app.WithDbAsync(db => db.AuditLog.Where(a => a.StaffId == _admin).Select(a => a.Action).ToListAsync());
            audited.ShouldBe([AuditActions.TariffPrice, AuditActions.TariffActivity], ignoreOrder: true);
        }

        private Task<Result<RefundResult>> RefundAsync(Guid paymentId, RefundKind kind) =>
            _app.SendAsync<RefundPaymentCommand, RefundResult>(new RefundPaymentCommand(_admin, paymentId, kind, "заявка пользователя"));

        private Task<Result<PaymentProvidersView>> ProvidersAsync() =>
            _app.QueryAsync<GetPaymentProvidersQuery, PaymentProvidersView>(new GetPaymentProvidersQuery(_admin));

        private Task<Result> SwitchAsync(string code) =>
            _app.SendAsync(new SwitchPaymentProviderCommand(_admin, code));

        private async Task<Guid> PaidAsync(long userId, string tariff)
        {
            PaymentLink link = (await CreatePaymentAsync(userId, tariff)).Value;
            (await DeliverAsync(Aggregator.Simulate(link.PaymentId, ProviderPaymentState.Succeeded)!)).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            return link.PaymentId;
        }

        private Task<Result<PaymentLink>> CreatePaymentAsync(long userId, string tariff) =>
            _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, tariff));

        private Task<Result> DeliverAsync(WebhookRequest webhook) =>
            _app.SendAsync(new HandlePaymentWebhookCommand("fake", webhook));

        private async Task<long> NewUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private Task<Subscription> SubscriptionOfAsync(long userId) =>
            _app.WithDbAsync(db => db.Subscriptions.AsNoTracking().Include(s => s.Tariff).SingleAsync(s => s.UserId == userId));

        /// <summary>An aggregator that is down: every invoice is refused.</summary>
        private sealed class RefusingProvider : IPaymentProvider
        {
            public const string ProviderCode = "refusing";

            public string Code => ProviderCode;

            public bool SupportsListing => false;

            public Task<CreatedPayment> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
                throw new PaymentProviderException("503 from the aggregator");

            public Task<PaymentNotification> ParseWebhookAsync(WebhookRequest request, CancellationToken cancellationToken) =>
                throw new WebhookAuthenticationException("not expected");

            public Task<ProviderPaymentStatus> GetStatusAsync(Guid orderId, string? providerPaymentId, CancellationToken cancellationToken) =>
                throw new PaymentProviderException("503 from the aggregator");

            public async IAsyncEnumerable<ProviderPayment> ListAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }
}
