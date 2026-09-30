using System.Net;
using Mendeleev.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// ТЗ 23: the fake aggregator turns a click on /dev/fake-pay into a paid subscription, so the host starts with it
    /// only in Development and Staging, and on staging only with a webhook secret that is not in Git.
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class FakePaymentsGuardTests(PostgresFixture postgres)
    {
        [Theory]
        [InlineData("Production")]
        [InlineData("Testing")]
        public void OutsideDevelopmentAndStaging_TheHostDoesNotStart(string environment)
        {
            using var host = new BareHost(environment, webhookSecret: "a-secret-from-env");

            OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => host.CreateClient());

            exception.Message.ShouldContain($"Payments:Fake:Enabled is allowed only in Development and Staging, not in {environment}");
        }

        [Fact]
        public void OnStaging_WithoutAWebhookSecret_TheHostDoesNotStart()
        {
            using var host = new BareHost("Staging", webhookSecret: "");

            OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => host.CreateClient());

            exception.Message.ShouldContain("Payments:Fake:WebhookSecret is required outside Development");
        }

        [Fact]
        public async Task OnStaging_ThePaymentPageIsServed()
        {
            // appsettings.Staging.json switches the fake aggregator on; the secret comes from the staging .env.
            await using CabinetSite site = await CabinetSite.CreateAsync(postgres);
            using WebApplicationFactory<Web.Program> staging = site.WithWebHostBuilder(builder => builder
                .UseEnvironment("Staging")
                .UseSetting("Payments:Fake:WebhookSecret", "a-secret-from-env"));
            using HttpClient client = staging.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

            using HttpResponseMessage response = await client.GetAsync($"/dev/fake-pay/{Guid.NewGuid()}");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldContain("Тестовая оплата");
        }

        /// <summary>The host with the fake aggregator on and no database: it must fail before touching one.</summary>
        private sealed class BareHost(string environment, string webhookSecret) : WebApplicationFactory<Web.Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment(environment);
                builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Database=unused");
                builder.UseSetting("Accounts:KeyPepper", Convert.ToBase64String(new byte[32]));
                builder.UseSetting("Remnawave:UseInMemory", "true");
                builder.UseSetting("Jobs:Enabled", "false");
                builder.UseSetting("Payments:Fake:Enabled", "true");
                builder.UseSetting("Payments:Fake:WebhookSecret", webhookSecret);
            }
        }
    }
}
