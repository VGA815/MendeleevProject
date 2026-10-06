using System.Net;
using System.Text.RegularExpressions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.IntegrationTests.Infrastructure;
using Mendeleev.Web.Bot.Content;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// ТЗ 27, «Публичные страницы»: what the aggregator's moderation checks on the site (lava.ru/site-requirements) —
    /// support contacts, filled-in tariff cards with prices, the refund terms, the personal data policy, and that
    /// every link leads to a working page. The seller's name, requisites and address are not published (05.10).
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed partial class PublicPagesTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private CabinetSite _site = null!;

        public async Task InitializeAsync() => _site = await CabinetSite.CreateAsync(postgres);

        public async Task DisposeAsync() => await _site.DisposeAsync();

        [Fact]
        public async Task EveryLink_LeadsToAWorkingPage_ForAGuestAndInTheCabinet()
        {
            await AddTariffsAsync();

            using CabinetSite.Browser guest = _site.NewBrowser();
            new[] { "/prices", "/offer", "/privacy", "/contacts", "/login", "/register" }.ShouldBeSubsetOf(await CrawlAsync(guest, "/"));

            using CabinetSite.Browser user = _site.NewBrowser("198.51.100.3");
            (await user.SubmitAsync("/register")).StatusCode.ShouldBe(HttpStatusCode.OK);
            new[] { "/cabinet/pay", "/cabinet/instructions", "/cabinet/devices", "/offer" }.ShouldBeSubsetOf(await CrawlAsync(user, "/cabinet"));
        }

        [Fact]
        public async Task TheTariffCards_HaveThePriceAndTheTerms()
        {
            await AddTariffsAsync();
            using CabinetSite.Browser guest = _site.NewBrowser();

            string html = WebUtility.HtmlDecode(await (await guest.GetAsync("/prices")).Content.ReadAsStringAsync());

            html.ShouldContain("Базовый тест");
            html.ShouldContain("199 ₽");
            html.ShouldContain("без ограничений");
            html.ShouldContain("Лёгкий тест");
            html.ShouldContain("10,0 ГБ");
            html.ShouldContain("физической доставки нет");
            html.ShouldContain("href=\"/offer#refund\"");
        }

        [Fact]
        public async Task InProductionWithPayments_TheSupportContacts_AreOnTheContactsTheOfferAndThePolicy()
        {
            using WebApplicationFactory<Web.Program> production = _site.WithWebHostBuilder(builder => TakingPaymentsInProduction(builder)
                .UseSetting("Site:Phone", "+7 (999) 123-45-67")
                .UseSetting("Service:BotUsername", "mendeleev_test_bot"));
            using HttpClient client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

            // The support account is the bot's one from content/bot.yaml, which outranks the settings of the test.
            string supportUrl = production.Services.GetRequiredService<IOptionsMonitor<BotContent>>().CurrentValue.SupportUrl;
            supportUrl.ShouldStartWith("https://t.me/");
            string supportLink = $"<a href=\"{supportUrl}\" rel=\"noopener\">@{supportUrl["https://t.me/".Length..]}</a>";

            foreach (string path in new[] { "/contacts", "/offer", "/privacy" })
            {
                string html = WebUtility.HtmlDecode(await client.GetStringAsync(path));
                html.ShouldContain("href=\"tel:+79991234567\"", customMessage: path);
                html.ShouldContain("href=\"mailto:support@site.test\"", customMessage: path);
                html.ShouldContain(supportLink, customMessage: path);
                html.ShouldContain("href=\"https://t.me/mendeleev_test_bot\"", customMessage: path);
            }

            string offer = WebUtility.HtmlDecode(await client.GetStringAsync("/offer"));
            offer.ShouldContain("Администрация сервиса «Mendeleev» (далее — Исполнитель)");
            offer.ShouldContain("<h2 id=\"refund\">6. Отмена оплаты и возврат денег</h2>");
            offer.ShouldContain("отменяется автоматически через 60 мин.");
            (await client.GetStringAsync("/privacy")).ShouldContain("<h1>Политика обработки персональных данных</h1>");
        }

        [Fact]
        public void InProductionWithPayments_WithoutASupportEmail_TheHostDoesNotStart()
        {
            using var host = new BareProductionHost();

            OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => host.CreateClient());

            exception.Message.ShouldContain("Site:SupportEmail is required in Production when Payments:Enabled");
        }

        /// <summary>
        /// Follows every link of the site from <paramref name="start"/>: each page and file answers 200, and a link to
        /// a part of a page (<c>/offer#refund</c>) finds that part. Other sites, mail and phone links are not opened.
        /// Returns the pages it went through.
        /// </summary>
        private static async Task<IReadOnlyCollection<string>> CrawlAsync(CabinetSite.Browser browser, string start)
        {
            var queue = new Queue<string>([start]);
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var pages = new Dictionary<string, string>(StringComparer.Ordinal);
            var fragments = new List<(string From, string Page, string Id)>();

            while (queue.TryDequeue(out string? path))
            {
                using HttpResponseMessage response = await browser.GetAsync(path);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {path}");
                if (response.Content.Headers.ContentType?.MediaType != "text/html")
                {
                    continue;
                }

                pages[path] = await response.Content.ReadAsStringAsync();
                foreach (Match link in LinkRegex().Matches(pages[path]))
                {
                    string href = WebUtility.HtmlDecode(link.Groups[1].Value);
                    if (href.StartsWith('#'))
                    {
                        fragments.Add((path, path, href[1..]));
                        continue;
                    }

                    if (!href.StartsWith('/') || href.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string[] parts = href.Split('#', 2);
                    if (parts.Length == 2)
                    {
                        fragments.Add((path, parts[0], parts[1]));
                    }

                    if (seen.Add(parts[0]))
                    {
                        queue.Enqueue(parts[0]);
                    }
                }
            }

            foreach ((string from, string page, string id) in fragments)
            {
                pages[page].ShouldContain($"id=\"{id}\"", customMessage: $"{from} links to {page}#{id}");
            }

            return pages.Keys;
        }

        private Task<int> AddTariffsAsync() => _site.WithDbAsync(db =>
        {
            db.Tariffs.Add(Tariff.Create("public_basic", "Базовый тест", TariffTier.Basic, 199, 30, 3, null, [Guid.NewGuid()], true, 90));
            db.Tariffs.Add(Tariff.Create("public_light", "Лёгкий тест", TariffTier.Basic, 99, 30, 1, 10L * 1024 * 1024 * 1024, [Guid.NewGuid()], true, 91));
            return db.SaveChangesAsync();
        });

        /// <summary>Production with a real aggregator switched on (the keys are not used by the pages).</summary>
        private static IWebHostBuilder TakingPaymentsInProduction(IWebHostBuilder builder) => builder
            .UseEnvironment("Production")
            .UseSetting("Payments:Enabled", "true")
            .UseSetting("Payments:ActiveProvider", "lava")
            .UseSetting("Payments:Lava:Enabled", "true")
            .UseSetting("Payments:Lava:ShopId", "shop")
            .UseSetting("Payments:Lava:SecretKey", "secret")
            .UseSetting("Payments:Lava:WebhookKey", "webhook");

        [GeneratedRegex("href=\"([^\"]*)\"")]
        private static partial Regex LinkRegex();

        /// <summary>Production taking payments, the support email left empty as in appsettings.json, no database: it must fail first.</summary>
        private sealed class BareProductionHost : WebApplicationFactory<Web.Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                TakingPaymentsInProduction(builder);
                builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Database=unused");
                builder.UseSetting("Accounts:KeyPepper", Convert.ToBase64String(new byte[32]));
                builder.UseSetting("Remnawave:UseInMemory", "true");
                builder.UseSetting("Jobs:Enabled", "false");
            }
        }
    }
}
