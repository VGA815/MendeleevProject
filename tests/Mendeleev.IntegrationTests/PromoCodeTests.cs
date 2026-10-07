using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Accounts.RegisterWebAccount;
using Mendeleev.Application.Admin.Promos;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Application.Promos;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>ТЗ 22, «Промокоды (этап 1.5)»: FR-SUB-15, FR-PAY-15, FR-ADM-15.</summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class PromoCodeTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private TestApp _app = null!;
        private long _admin;

        private IFakePaymentSimulator Aggregator => _app.Provider.GetRequiredService<IFakePaymentSimulator>();

        public async Task InitializeAsync()
        {
            _app = await TestApp.CreateAsync(postgres);
            _admin = await _app.AddStaffAsync(9001, StaffRole.Admin);
        }

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task Discount_GoesIntoTheNextPayment_AndCountsOnlyOnceItIsPaid()
        {
            await CreatePromoAsync("AUTUMN", PromoType.DiscountPercent, 20, maxUses: 5);
            long userId = await NewUserAsync(4001);

            PromoApplied applied = (await ApplyAsync(userId, "autumn")).Value;
            applied.Code.ShouldBe("AUTUMN");
            applied.Type.ShouldBe(PromoType.DiscountPercent);

            Offer offer = await OfferAsync(userId);
            offer.Promo!.Code.ShouldBe("AUTUMN");
            offer.Find("basic_1m")!.FinalPrice.ShouldBe(159m);
            offer.Find("basic_3m")!.FinalPrice.ShouldBe(439m);

            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            link.Amount.ShouldBe(159m);
            link.PromoCode.ShouldBe("AUTUMN");
            link.FullPrice.ShouldBe(199m);

            // Entered but not paid: nothing is used up yet.
            (await PromoAsync("AUTUMN")).UsedCount.ShouldBe(0);

            (await PayAsync(link.PaymentId)).IsSuccess.ShouldBeTrue();

            (await PromoAsync("AUTUMN")).UsedCount.ShouldBe(1);
            PromoRedemption redemption = await _app.WithDbAsync(db => db.PromoRedemptions.AsNoTracking().SingleAsync());
            redemption.UserId.ShouldBe(userId);
            redemption.PaymentId.ShouldBe(link.PaymentId);
            (await UserAsync(userId)).SelectedPromoCodeId.ShouldBeNull();
            (await SubscriptionOfAsync(userId)).ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(30));

            // One discount per user: the next payment is at the full price, and the code is not taken again.
            (await CreatePaymentAsync(userId, "basic_3m")).Value.Amount.ShouldBe(549m);
            (await ApplyAsync(userId, "AUTUMN")).Error.ShouldBe(PromoErrors.AlreadyUsed);

            PromoCodeDetails details = (await _app.QueryAsync<GetPromoCodeQuery, PromoCodeDetails>(new GetPromoCodeQuery(_admin, "autumn"))).Value;
            details.PaidPayments.ShouldBe(1);
            details.PaidSum.ShouldBe(159m);
            details.LastUses.ShouldHaveSingleItem().Amount.ShouldBe(159m);

            UserCard card = (await _app.SendAsync<GetUserCardCommand, UserCard>(new GetUserCardCommand(_admin, userId))).Value;
            card.Payments.Single(p => p.Id == link.PaymentId).PromoCode.ShouldBe("AUTUMN");
        }

        [Fact]
        public async Task CodeThatStoppedWorking_IsDropped_AndThePaymentSaysSo()
        {
            await CreatePromoAsync("SHORT", PromoType.DiscountPercent, 50);
            long userId = await NewUserAsync(4002);
            (await ApplyAsync(userId, "SHORT")).IsSuccess.ShouldBeTrue();

            (await _app.SendAsync(new DeactivatePromoCodeCommand(_admin, "short"))).IsSuccess.ShouldBeTrue();

            (await OfferAsync(userId)).Promo.ShouldBeNull();
            PaymentLink link = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            link.Amount.ShouldBe(199m);
            link.PromoCode.ShouldBeNull();
            link.DroppedPromoCode.ShouldBe("SHORT");
            (await UserAsync(userId)).SelectedPromoCodeId.ShouldBeNull();
            (await ApplyAsync(userId, "SHORT")).Error.ShouldBe(PromoErrors.Inactive);

            int audited = await _app.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.PromoDeactivate));
            audited.ShouldBe(1);
        }

        [Fact]
        public async Task UnpaidPayment_IsReused_OnlyForTheSameCode()
        {
            await CreatePromoAsync("TEN", PromoType.DiscountPercent, 10);
            long userId = await NewUserAsync(4003);

            PaymentLink full = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            (await ApplyAsync(userId, "TEN")).IsSuccess.ShouldBeTrue();

            PaymentLink discounted = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            discounted.PaymentId.ShouldNotBe(full.PaymentId);
            discounted.Amount.ShouldBe(179m);
            discounted.Reused.ShouldBeFalse();

            PaymentLink again = (await CreatePaymentAsync(userId, "basic_1m")).Value;
            again.PaymentId.ShouldBe(discounted.PaymentId);
            again.Reused.ShouldBeTrue();
        }

        [Fact]
        public async Task Discount_NeverGoesBelowTheAggregatorMinimum()
        {
            await using TestApp app = await TestApp.CreateAsync(postgres, new Dictionary<string, string?> { ["Payments:MinAmount"] = "170" });
            long admin = await app.AddStaffAsync(9002, StaffRole.TechAdmin);
            (await app.SendAsync<CreatePromoCodeCommand, PromoCodeView>(
                new CreatePromoCodeCommand(admin, "HALF", PromoType.DiscountPercent, 50, null, null, null))).IsSuccess.ShouldBeTrue();
            long userId = (await app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(4004))).Value.UserId;
            (await app.SendAsync<ApplyPromoCodeCommand, PromoApplied>(new ApplyPromoCodeCommand(userId, "HALF"))).IsSuccess.ShouldBeTrue();

            Offer offer = (await app.QueryAsync<GetOfferQuery, Offer>(new GetOfferQuery(userId))).Value;
            offer.Find("basic_1m")!.FinalPrice.ShouldBe(170m);   // 50 % would be 100 ₽
            offer.Find("basic_3m")!.FinalPrice.ShouldBe(275m);   // 549 → 274.5 → 275, above the floor
            (await app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, "basic_1m"))).Value.Amount.ShouldBe(170m);
        }

        [Fact]
        public async Task BonusDays_ForAUserWithoutASubscription_StartTheBasicTariff()
        {
            await CreatePromoAsync("GIFT7", PromoType.BonusDays, 7);
            long userId = await NewUserAsync(4005);

            PromoApplied applied = (await ApplyAsync(userId, "gift7")).Value;
            applied.Type.ShouldBe(PromoType.BonusDays);
            applied.ExpiresAt.ShouldBe(_app.Clock.UtcNow.AddDays(7));
            await _app.ProcessOutboxAsync();

            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.Tariff.Code.ShouldBe("basic_1m");
            _app.Panel.Users.ShouldHaveSingleItem().ExpireAt.ShouldBe(subscription.ExpiresAt);
            _app.Messenger.Notifications.ShouldHaveSingleItem().Message.Kind.ShouldBe(NotificationKind.AccessIssued);

            // Access from a code is a subscription: no trial on top of it (FR-SUB-02).
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(4005))).Value.TrialAvailable.ShouldBeFalse();
            (await ApplyAsync(userId, "GIFT7")).Error.ShouldBe(PromoErrors.AlreadyUsed);

            AuditLogEntry audit = await _app.WithDbAsync(db => db.AuditLog.AsNoTracking().SingleAsync(a => a.Action == AuditActions.PromoBonus));
            audit.TargetUserId.ShouldBe(userId);
        }

        [Fact]
        public async Task BonusDays_OnTheTrial_GiveThePaidTariffWithoutTheTrafficLimit()
        {
            await CreatePromoAsync("TRIALPLUS", PromoType.BonusDays, 10);
            long userId = await NewUserAsync(4006);
            (await _app.SendAsync(new StartTrialCommand(userId))).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();
            DateTime trialEnd = (await SubscriptionOfAsync(userId)).ExpiresAt;
            _app.Messenger.Notifications.Clear();

            (await ApplyAsync(userId, "TRIALPLUS")).IsSuccess.ShouldBeTrue();
            await _app.ProcessOutboxAsync();

            Subscription subscription = await SubscriptionOfAsync(userId);
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.Tariff.Code.ShouldBe("basic_1m");
            subscription.ExpiresAt.ShouldBe(trialEnd.AddDays(10));
            _app.Panel.Users.Single().TrafficLimitBytes.ShouldBe(0);
            _app.Messenger.Notifications.ShouldHaveSingleItem().Message.Kind.ShouldBe(NotificationKind.PromoBonus);
        }

        [Fact]
        public async Task BonusDays_TheLastActivation_GoesToOneUserOnly()
        {
            await CreatePromoAsync("ONLYONE", PromoType.BonusDays, 3, maxUses: 1);
            long first = await NewUserAsync(4007);
            long second = await NewUserAsync(4008);

            Result<PromoApplied>[] results = await Task.WhenAll(ApplyAsync(first, "ONLYONE"), ApplyAsync(second, "ONLYONE"));

            results.Count(r => r.IsSuccess).ShouldBe(1);
            results.Single(r => r.IsFailure).Error.ShouldBe(PromoErrors.Exhausted);
            (await PromoAsync("ONLYONE")).UsedCount.ShouldBe(1);
            (await _app.WithDbAsync(db => db.Subscriptions.CountAsync())).ShouldBe(1);
        }

        [Fact]
        public async Task BonusDays_AreForTelegramAccounts_DiscountsForAnyone()
        {
            await CreatePromoAsync("WEBGIFT", PromoType.BonusDays, 7);
            await CreatePromoAsync("WEBSALE", PromoType.DiscountPercent, 10);
            long webUser = (await _app.SendAsync<RegisterWebAccountCommand, NewWebAccount>(new RegisterWebAccountCommand())).Value.UserId;

            (await ApplyAsync(webUser, "WEBGIFT")).Error.ShouldBe(PromoErrors.BonusNeedsTelegram);
            (await ApplyAsync(webUser, "WEBSALE")).IsSuccess.ShouldBeTrue();
            (await PromoAsync("WEBGIFT")).UsedCount.ShouldBe(0);
        }

        [Fact]
        public async Task Window_UnknownCodes_AndBlockedUsers()
        {
            await CreatePromoAsync("LATER", PromoType.BonusDays, 5, validFrom: _app.Clock.UtcNow.AddDays(1), validTo: _app.Clock.UtcNow.AddDays(2));
            long userId = await NewUserAsync(4009);

            (await ApplyAsync(userId, "LATER")).Error.ShouldBe(PromoErrors.NotStarted);
            (await ApplyAsync(userId, "NOSUCHCODE")).Error.ShouldBe(PromoErrors.NotFound);
            (await ApplyAsync(userId, "не код")).Error.ShouldBe(PromoErrors.NotFound);

            _app.Clock.Advance(TimeSpan.FromDays(3));
            (await ApplyAsync(userId, "LATER")).Error.ShouldBe(PromoErrors.Expired);

            (await _app.SendAsync(new BlockUserCommand(_admin, userId, "abuse"))).IsSuccess.ShouldBeTrue();
            (await ApplyAsync(userId, "LATER")).Error.ShouldBe(UserErrors.Blocked);
        }

        [Fact]
        public async Task OnlyAdmins_ManageCodes_AndACodeIsUniqueWhateverTheCase()
        {
            long support = await _app.AddStaffAsync(9003, StaffRole.Support);

            (await CreatePromoAsync("SPRING", PromoType.DiscountPercent, 15, staffId: support)).Error.ShouldBe(StaffErrors.NotAllowed);
            (await CreatePromoAsync("SPRING", PromoType.DiscountPercent, 15)).IsSuccess.ShouldBeTrue();
            (await CreatePromoAsync("spring", PromoType.BonusDays, 3)).Error.ShouldBe(PromoErrors.CodeExists("SPRING"));
            (await _app.SendAsync(new DeactivatePromoCodeCommand(support, "SPRING"))).Error.ShouldBe(StaffErrors.NotAllowed);
            (await _app.QueryAsync<ListPromoCodesQuery, IReadOnlyList<PromoCodeView>>(new ListPromoCodesQuery(support))).Error.ShouldBe(StaffErrors.NotAllowed);

            IReadOnlyList<PromoCodeView> list = (await _app.QueryAsync<ListPromoCodesQuery, IReadOnlyList<PromoCodeView>>(new ListPromoCodesQuery(_admin))).Value;
            list.ShouldHaveSingleItem().Code.ShouldBe("SPRING");
            (await _app.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.PromoCreate && a.StaffId == _admin))).ShouldBe(1);
        }

        private Task<Result<PromoCodeView>> CreatePromoAsync(
            string code,
            PromoType type,
            int value,
            int? maxUses = null,
            DateTime? validFrom = null,
            DateTime? validTo = null,
            long? staffId = null) =>
            _app.SendAsync<CreatePromoCodeCommand, PromoCodeView>(
                new CreatePromoCodeCommand(staffId ?? _admin, code, type, value, maxUses, validFrom, validTo));

        private Task<Result<PromoApplied>> ApplyAsync(long userId, string code) =>
            _app.SendAsync<ApplyPromoCodeCommand, PromoApplied>(new ApplyPromoCodeCommand(userId, code));

        private async Task<Offer> OfferAsync(long userId) =>
            (await _app.QueryAsync<GetOfferQuery, Offer>(new GetOfferQuery(userId))).Value;

        private Task<Result<PaymentLink>> CreatePaymentAsync(long userId, string tariff) =>
            _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, tariff));

        private Task<Result> PayAsync(Guid paymentId) =>
            _app.SendAsync(new HandlePaymentWebhookCommand("fake", Aggregator.Simulate(paymentId, ProviderPaymentState.Succeeded)!));

        private Task<PromoCode> PromoAsync(string code) =>
            _app.WithDbAsync(db => db.PromoCodes.AsNoTracking().SingleAsync(p => p.Code == code));

        private Task<User> UserAsync(long userId) =>
            _app.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId));

        private async Task<long> NewUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private Task<Subscription> SubscriptionOfAsync(long userId) =>
            _app.WithDbAsync(db => db.Subscriptions.AsNoTracking().Include(s => s.Tariff).SingleAsync(s => s.UserId == userId));
    }
}
