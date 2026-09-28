using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Accounts.IssueAccountKey;
using Mendeleev.Application.Accounts.LinkTelegram;
using Mendeleev.Application.Accounts.RegisterWebAccount;
using Mendeleev.Application.Accounts.SignIn;
using Mendeleev.Application.Accounts.WebSessions;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Users;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>ТЗ 21: registration and sign-in on the site, the key, linking Telegram and the merge rules.</summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class AccountFlowTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private TestApp _app = null!;

        public async Task InitializeAsync() => _app = await TestApp.CreateAsync(postgres);

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task WebAccount_SignsInByKey_WithOrWithoutSeparators_AndOnlyTheHashIsStored()
        {
            // FR-ACC-05, FR-ACC-06: the key is shown once; the database keeps only its HMAC.
            NewWebAccount account = (await _app.SendAsync<RegisterWebAccountCommand, NewWebAccount>(new RegisterWebAccountCommand())).Value;
            string digits = account.AccountKey.Replace(" ", string.Empty, StringComparison.Ordinal);

            User stored = await UserAsync(account.UserId);
            stored.TelegramId.ShouldBeNull();
            stored.AccountKeyHash.ShouldNotBeNull();
            stored.AccountKeyHash.ShouldNotContain(digits);

            foreach (string typed in new[] { account.AccountKey, digits, $"{digits[..4]}-{digits[4..8]}-{digits[8..12]}-{digits[12..]}" })
            {
                Result<WebSession> session = await _app.SendAsync<SignInWithKeyCommand, WebSession>(new SignInWithKeyCommand(typed));
                session.IsSuccess.ShouldBeTrue();
                session.Value.ShouldBe(new WebSession(account.UserId, account.SessionStamp));
            }

            string wrong = digits[..15] + (digits[15] == '0' ? '1' : '0');
            (await _app.SendAsync<SignInWithKeyCommand, WebSession>(new SignInWithKeyCommand(wrong))).Error.ShouldBe(UserErrors.WrongAccountKey);
            (await _app.SendAsync<SignInWithKeyCommand, WebSession>(new SignInWithKeyCommand("12345"))).Error.ShouldBe(UserErrors.InvalidAccountKey);
        }

        [Fact]
        public async Task KeyReissue_KillsTheOldKey_AndEverySession()
        {
            // FR-ACC-04 and ТЗ 27: a reissue makes the old key invalid and ends all cabinet sessions.
            NewWebAccount account = (await _app.SendAsync<RegisterWebAccountCommand, NewWebAccount>(new RegisterWebAccountCommand())).Value;

            string newKey = (await _app.SendAsync<IssueAccountKeyCommand, string>(new IssueAccountKeyCommand(account.UserId))).Value;

            (await _app.SendAsync<SignInWithKeyCommand, WebSession>(new SignInWithKeyCommand(account.AccountKey))).IsFailure.ShouldBeTrue();
            WebSession session = (await _app.SendAsync<SignInWithKeyCommand, WebSession>(new SignInWithKeyCommand(newKey))).Value;
            session.UserId.ShouldBe(account.UserId);
            session.SessionStamp.ShouldNotBe(account.SessionStamp);

            (await _app.SendAsync(new SignOutEverywhereCommand(account.UserId))).IsSuccess.ShouldBeTrue();
            WebAccountState state = (await _app.QueryAsync<GetWebAccountQuery, WebAccountState>(new GetWebAccountQuery(account.UserId))).Value;
            state.SessionStamp.ShouldNotBe(session.SessionStamp);
        }

        [Fact]
        public async Task TelegramUsers_GetASessionStamp_FromTheDatabaseDefault()
        {
            // /start inserts the row with raw SQL (FR-ACC-01); the stamp must still be there.
            long userId = await TelegramUserAsync(5001);
            (await UserAsync(userId)).SessionStamp.ShouldNotBe(Guid.Empty);
        }

        [Fact]
        public async Task Link_Rule1_EmptyTelegramAccount_MovesIntoThePayingWebAccount()
        {
            NewWebAccount web = await RegisterAsync();
            await PayAsync(web.UserId);
            long telegram = await TelegramUserAsync(5101);
            string code = await LinkCodeAsync(telegram);

            Result<LinkedAccount> result = await LinkAsync(web.UserId, code);

            result.IsSuccess.ShouldBeTrue();
            result.Value.ShouldBe(new LinkedAccount(web.UserId, web.SessionStamp, AccountMergeRule.TelegramIntoWeb));

            User merged = await UserAsync(web.UserId);
            merged.TelegramId.ShouldBe(5101);
            (await _app.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == telegram))).ShouldBeFalse();

            // The bot now finds the web account by this Telegram ID.
            TelegramUserState state = (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(5101))).Value;
            state.UserId.ShouldBe(web.UserId);
            state.IsNew.ShouldBeFalse();
            state.HasSubscription.ShouldBeTrue();

            AuditLogEntry audit = await _app.WithDbAsync(db => db.AuditLog.SingleAsync(a => a.Action == AuditActions.AccountsMerged));
            audit.TargetUserId.ShouldBe(web.UserId);
            audit.ActorType.ShouldBe(AuditActorType.System);

            // The code went with its account: it cannot be used again.
            NewWebAccount other = await RegisterAsync();
            (await LinkAsync(other.UserId, code)).Error.ShouldBe(UserErrors.InvalidLinkCode);
        }

        [Fact]
        public async Task Link_Rule2_EmptyWebAccount_GivesItsKeyToTheTelegramAccount()
        {
            long telegram = await TelegramUserAsync(5201);
            (await _app.SendAsync(new StartTrialCommand(telegram))).IsSuccess.ShouldBeTrue();
            Guid telegramStamp = (await UserAsync(telegram)).SessionStamp;
            NewWebAccount web = await RegisterAsync();
            string code = await LinkCodeAsync(telegram);

            Result<LinkedAccount> result = await LinkAsync(web.UserId, $"{code[..4]} {code[4..]}");

            result.IsSuccess.ShouldBeTrue();
            result.Value.UserId.ShouldBe(telegram);
            result.Value.Rule.ShouldBe(AccountMergeRule.WebIntoTelegram);
            result.Value.SessionStamp.ShouldNotBe(telegramStamp);

            (await _app.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == web.UserId))).ShouldBeFalse();
            WebSession session = (await _app.SendAsync<SignInWithKeyCommand, WebSession>(new SignInWithKeyCommand(web.AccountKey))).Value;
            session.ShouldBe(new WebSession(telegram, result.Value.SessionStamp));

            LinkCode used = await _app.WithDbAsync(db => db.LinkCodes.SingleAsync(c => c.UserId == telegram));
            used.UsedAt.ShouldNotBeNull();
        }

        [Fact]
        public async Task Link_Rule3_BothAccountsHaveData_IsRefused_AndNothingChanges()
        {
            // FR-ACC-10: manual merge is stage 2 — the user is sent to support.
            long telegram = await TelegramUserAsync(5301);
            (await _app.SendAsync(new StartTrialCommand(telegram))).IsSuccess.ShouldBeTrue();
            NewWebAccount web = await RegisterAsync();
            (await _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(web.UserId, "basic_1m"))).IsSuccess.ShouldBeTrue();
            string code = await LinkCodeAsync(telegram);

            (await LinkAsync(web.UserId, code)).Error.ShouldBe(UserErrors.MergeNeedsSupport);

            (await UserAsync(web.UserId)).TelegramId.ShouldBeNull();
            (await UserAsync(telegram)).TelegramId.ShouldBe(5301);
            (await _app.WithDbAsync(db => db.LinkCodes.SingleAsync(c => c.UserId == telegram))).UsedAt.ShouldBeNull();
        }

        [Fact]
        public async Task LinkCode_Expires_After10Minutes_AndANewCodeReplacesTheOld()
        {
            long telegram = await TelegramUserAsync(5401);
            NewWebAccount web = await RegisterAsync();

            string first = await LinkCodeAsync(telegram);
            string second = await LinkCodeAsync(telegram);
            if (first != second)
            {
                (await LinkAsync(web.UserId, first)).Error.ShouldBe(UserErrors.InvalidLinkCode);
            }

            _app.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
            (await LinkAsync(web.UserId, second)).Error.ShouldBe(UserErrors.InvalidLinkCode);
            (await LinkAsync(web.UserId, "123")).Error.ShouldBe(UserErrors.InvalidLinkCode);
        }

        [Fact]
        public async Task BlockedAccounts_CannotLink()
        {
            long telegram = await TelegramUserAsync(5501);
            NewWebAccount web = await RegisterAsync();
            string code = await LinkCodeAsync(telegram);
            await _app.WithDbAsync(async db =>
            {
                User user = await db.Users.SingleAsync(u => u.Id == web.UserId);
                user.Block(_app.Clock.UtcNow);
                return await db.SaveChangesAsync();
            });

            (await LinkAsync(web.UserId, code)).Error.ShouldBe(UserErrors.Blocked);
            (await _app.QueryAsync<GetWebAccountQuery, WebAccountState>(new GetWebAccountQuery(web.UserId))).Value.IsBlocked.ShouldBeTrue();
        }

        [Fact]
        public async Task OneCode_TwoWebAccountsAtOnce_ExactlyOneLinks()
        {
            long telegram = await TelegramUserAsync(5601);
            NewWebAccount first = await RegisterAsync();
            NewWebAccount second = await RegisterAsync();
            string code = await LinkCodeAsync(telegram);

            Result<LinkedAccount>[] results = await Task.WhenAll(LinkAsync(first.UserId, code), LinkAsync(second.UserId, code));

            results.Count(r => r.IsSuccess).ShouldBe(1);
            results.Single(r => r.IsFailure).Error.ShouldBe(UserErrors.InvalidLinkCode);
            (await _app.WithDbAsync(db => db.Users.CountAsync(u => u.TelegramId == 5601))).ShouldBe(1);
        }

        private async Task<NewWebAccount> RegisterAsync() =>
            (await _app.SendAsync<RegisterWebAccountCommand, NewWebAccount>(new RegisterWebAccountCommand())).Value;

        private async Task<long> TelegramUserAsync(long telegramId) =>
            (await _app.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(telegramId))).Value.UserId;

        private async Task<string> LinkCodeAsync(long telegramUserId)
        {
            Result<IssuedLinkCode> code = await _app.SendAsync<IssueLinkCodeCommand, IssuedLinkCode>(new IssueLinkCodeCommand(telegramUserId));
            code.IsSuccess.ShouldBeTrue();
            code.Value.Code.Length.ShouldBe(LinkCode.TelegramCodeLength);
            return code.Value.Code;
        }

        private Task<Result<LinkedAccount>> LinkAsync(long webUserId, string code) =>
            _app.SendAsync<LinkTelegramCommand, LinkedAccount>(new LinkTelegramCommand(webUserId, code));

        private Task<User> UserAsync(long userId) =>
            _app.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId));

        private async Task PayAsync(long userId)
        {
            PaymentLink link = (await _app.SendAsync<CreatePaymentCommand, PaymentLink>(new CreatePaymentCommand(userId, "basic_1m"))).Value;
            WebhookRequest webhook = _app.Provider.GetRequiredService<IFakePaymentSimulator>().Simulate(link.PaymentId, ProviderPaymentState.Succeeded)!;
            (await _app.SendAsync(new HandlePaymentWebhookCommand("fake", webhook))).IsSuccess.ShouldBeTrue();
        }
    }
}
