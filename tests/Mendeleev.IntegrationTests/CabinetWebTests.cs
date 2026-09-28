using System.Net;
using System.Text.RegularExpressions;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Accounts.LinkTelegram;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Users;
using Mendeleev.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.IntegrationTests
{
    /// <summary>ТЗ 27, «Критерии приёмки»: the cabinet through HTTP, as a browser sees it.</summary>
    [Collection(nameof(PostgresCollection))]
    public sealed partial class CabinetWebTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private CabinetSite _site = null!;

        public async Task InitializeAsync() => _site = await CabinetSite.CreateAsync(postgres);

        public async Task DisposeAsync() => await _site.DisposeAsync();

        [Theory]
        [InlineData("/cabinet")]
        [InlineData("/cabinet/instructions")]
        [InlineData("/cabinet/devices")]
        public async Task CabinetAndInstructions_NeedSignIn(string path)
        {
            // FR-WEB-07: without sign-in the cabinet and the instructions are not available.
            using CabinetSite.Browser browser = _site.NewBrowser();

            using HttpResponseMessage response = await browser.GetAsync(path);

            response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            response.Headers.Location!.PathAndQuery.ShouldBe($"/login?returnUrl={Uri.EscapeDataString(path)}");
        }

        [Fact]
        public async Task Registration_ShowsTheKeyOnce_SignsIn_AndTheCabinetIsSelfContained()
        {
            using CabinetSite.Browser browser = _site.NewBrowser();

            string key = await RegisterAsync(browser);

            using HttpResponseMessage cabinet = await browser.GetAsync("/cabinet");
            cabinet.StatusCode.ShouldBe(HttpStatusCode.OK);
            string html = await cabinet.Content.ReadAsStringAsync();
            html.ShouldContain("У вас пока нет подписки");
            html.ShouldNotContain(key);

            // FR-WEB-10: security headers, noindex, and nothing loaded from other domains.
            cabinet.Headers.GetValues("Content-Security-Policy").Single().ShouldStartWith("default-src 'self'");
            cabinet.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
            cabinet.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
            cabinet.Headers.GetValues("X-Robots-Tag").Single().ShouldBe("noindex, nofollow");
            cabinet.Headers.CacheControl!.NoStore.ShouldBeTrue();
            ExternalResourceRegex().IsMatch(html).ShouldBeFalse();

            // The session cookie: __Host-, HttpOnly, Secure, SameSite=Lax.
            using var fresh = _site.NewBrowser("198.51.100.2");
            using HttpResponseMessage registered = await fresh.SubmitAsync("/register");
            string cookie = registered.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-cabinet=", StringComparison.Ordinal));
            cookie.ShouldContain("httponly", Case.Insensitive);
            cookie.ShouldContain("secure", Case.Insensitive);
            cookie.ShouldContain("samesite=lax", Case.Insensitive);
        }

        [Fact]
        public async Task SixthSignInAttemptInAMinuteFromOneIp_Gets429()
        {
            // FR-WEB-10, FR-ACC-11: 5 attempts a minute from an IP.
            using CabinetSite.Browser browser = _site.NewBrowser("203.0.113.7");
            var statuses = new List<HttpStatusCode>();
            for (int i = 0; i < 6; i++)
            {
                using HttpResponseMessage response = await browser.SubmitAsync("/login", fields: ("accountKey", "0000 0000 0000 000" + i));
                statuses.Add(response.StatusCode);
            }

            statuses.ShouldBe([.. Enumerable.Repeat(HttpStatusCode.OK, 5), HttpStatusCode.TooManyRequests]);

            // Another IP is not affected.
            using CabinetSite.Browser other = _site.NewBrowser("203.0.113.8");
            using HttpResponseMessage allowed = await other.SubmitAsync("/login", fields: ("accountKey", "0000 0000 0000 0000"));
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Fact]
        public async Task SignInWithTheKey_OnAnotherDevice_SeesTheSameAccount_UntilSignOutEverywhere()
        {
            using CabinetSite.Browser first = _site.NewBrowser();
            string key = await RegisterAsync(first);

            using CabinetSite.Browser second = _site.NewBrowser();
            using HttpResponseMessage signIn = await second.SubmitAsync("/login", fields: ("accountKey", key.Replace(" ", "-", StringComparison.Ordinal)));
            signIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            signIn.Headers.Location!.OriginalString.ShouldBe("/cabinet");
            (await second.GetAsync("/cabinet")).StatusCode.ShouldBe(HttpStatusCode.OK);

            // FR-WEB-12: «Выйти на всех устройствах» ends every session, this one included.
            using HttpResponseMessage signOut = await first.SubmitAsync("/cabinet/settings", "/cabinet/settings?handler=SignOutEverywhere");
            signOut.StatusCode.ShouldBe(HttpStatusCode.Redirect);

            (await second.GetAsync("/cabinet")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
            (await first.GetAsync("/cabinet")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        [Fact]
        public async Task KeyReissue_KeepsThisBrowser_EndsTheOthers_AndTheOldKeyStopsWorking()
        {
            using CabinetSite.Browser first = _site.NewBrowser();
            string oldKey = await RegisterAsync(first);
            using CabinetSite.Browser second = _site.NewBrowser();
            (await second.SubmitAsync("/login", fields: ("accountKey", oldKey))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

            using HttpResponseMessage reissued = await first.SubmitAsync("/cabinet/settings", "/cabinet/settings?handler=ReissueKey");
            reissued.StatusCode.ShouldBe(HttpStatusCode.OK);
            string newKey = SecretRegex().Match(await reissued.Content.ReadAsStringAsync()).Groups[1].Value;
            newKey.ShouldNotBe(oldKey);

            (await first.GetAsync("/cabinet")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await second.GetAsync("/cabinet")).StatusCode.ShouldBe(HttpStatusCode.Redirect);

            using CabinetSite.Browser third = _site.NewBrowser();
            (await third.SubmitAsync("/login", fields: ("accountKey", oldKey))).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await third.SubmitAsync("/login", fields: ("accountKey", newKey))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        [Fact]
        public async Task BlockedUser_SeesOnlyTheBlockMessage()
        {
            using CabinetSite.Browser browser = _site.NewBrowser();
            string key = await RegisterAsync(browser);
            await _site.WithDbAsync(async db =>
            {
                User user = await db.Users.SingleAsync(u => u.AccountKeyHash != null);
                user.Block(DateTime.UtcNow);
                return await db.SaveChangesAsync();
            });

            string html = await (await browser.GetAsync("/cabinet")).Content.ReadAsStringAsync();
            html.ShouldContain("Аккаунт заблокирован, обратитесь в поддержку.");
            html.ShouldContain("support@site.test");
            html.ShouldNotContain("/cabinet/pay");

            using HttpResponseMessage devices = await browser.GetAsync("/cabinet/devices");
            devices.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            devices.Headers.Location!.OriginalString.ShouldBe("/cabinet");
            key.ShouldNotBeEmpty();
        }

        [Fact]
        public async Task LinkingTelegram_WithATrial_SwitchesTheSessionToTheTelegramAccount()
        {
            // FR-WEB-08 and merge rule 2: the web account is empty, the Telegram one has the trial.
            long telegramUser = (await _site.SendAsync<EnsureTelegramUserCommand, TelegramUserState>(new EnsureTelegramUserCommand(7001))).Value.UserId;
            (await _site.SendAsync(new StartTrialCommand(telegramUser))).IsSuccess.ShouldBeTrue();
            string code = (await _site.SendAsync<IssueLinkCodeCommand, IssuedLinkCode>(new IssueLinkCodeCommand(telegramUser))).Value.Code;

            using CabinetSite.Browser browser = _site.NewBrowser();
            string key = await RegisterAsync(browser);

            using HttpResponseMessage wrong = await browser.SubmitAsync("/cabinet/link-telegram", fields: ("code", "00000000"));
            (await wrong.Content.ReadAsStringAsync()).ShouldContain("Код неверный или устарел");

            using HttpResponseMessage linked = await browser.SubmitAsync("/cabinet/link-telegram", fields: ("code", code));
            linked.StatusCode.ShouldBe(HttpStatusCode.Redirect);

            (await (await browser.GetAsync("/cabinet/link-telegram")).Content.ReadAsStringAsync()).ShouldContain("Telegram привязан");
            (await (await browser.GetAsync("/cabinet")).Content.ReadAsStringAsync()).ShouldContain("Пробный");

            // The key from the site now opens the Telegram account.
            User merged = await _site.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.TelegramId == 7001));
            merged.Id.ShouldBe(telegramUser);
            merged.AccountKeyHash.ShouldNotBeNull();
            key.ShouldNotBeEmpty();
        }

        private static async Task<string> RegisterAsync(CabinetSite.Browser browser)
        {
            using HttpResponseMessage response = await browser.SubmitAsync("/register");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            string key = SecretRegex().Match(await response.Content.ReadAsStringAsync()).Groups[1].Value;
            key.ShouldMatch(@"^\d{4} \d{4} \d{4} \d{4}$");
            return key;
        }

        [GeneratedRegex("<p class=\"secret\">([^<]+)</p>")]
        private static partial Regex SecretRegex();

        /// <summary>Anything the browser would load by itself from an absolute address.</summary>
        [GeneratedRegex("<(script|img|link|iframe)[^>]+(src|href)=\"(https?:)?//", RegexOptions.IgnoreCase)]
        private static partial Regex ExternalResourceRegex();
    }
}
