using System.Net;
using System.Text.RegularExpressions;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Infrastructure.Database;
using Mendeleev.SharedKernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Mendeleev.IntegrationTests.Infrastructure
{
    /// <summary>
    /// The whole web host over a real database, served over https as in production: the cabinet cookie is
    /// Secure and <c>__Host-</c> bound only outside Development. The panel is in memory, there is no bot.
    /// </summary>
    public sealed partial class CabinetSite : WebApplicationFactory<Web.Program>
    {
        private readonly string _connectionString;

        private CabinetSite(string connectionString)
        {
            _connectionString = connectionString;
        }

        public static async Task<CabinetSite> CreateAsync(PostgresFixture postgres)
        {
            string database = "w_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(postgres.Container.GetConnectionString()))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
                await command.ExecuteNonQueryAsync();
            }

            return new CabinetSite(new NpgsqlConnectionStringBuilder(postgres.Container.GetConnectionString()) { Database = database }.ConnectionString);
        }

        /// <summary>A browser: its own cookies, its own IP (the rate limits are per IP), no auto-redirects.</summary>
        public Browser NewBrowser(string ip = "198.51.100.1")
        {
            HttpClient client = CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
            return new Browser(client);
        }

        public async Task<Result<TResult>> SendAsync<TCommand, TResult>(TCommand command)
            where TCommand : ICommand<TResult>
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>().Handle(command, CancellationToken.None);
        }

        public async Task<Result> SendAsync<TCommand>(TCommand command)
            where TCommand : ICommand
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand>>().Handle(command, CancellationToken.None);
        }

        public async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Database", _connectionString);
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("Accounts:KeyPepper", Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
            builder.UseSetting("Service:SiteBaseUrl", "https://localhost");
            builder.UseSetting("Remnawave:UseInMemory", "true");
            builder.UseSetting("Payments:Enabled", "false");
            builder.UseSetting("Jobs:Enabled", "false");
            builder.UseSetting("Site:SupportEmail", "support@site.test");
        }

        public sealed partial class Browser(HttpClient client) : IDisposable
        {
            public HttpClient Client { get; } = client;

            public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

            /// <summary>Opens the page with the form, then posts it with its antiforgery token.</summary>
            public async Task<HttpResponseMessage> SubmitAsync(string formPage, string? action = null, params (string Name, string Value)[] fields)
            {
                using HttpResponseMessage page = await Client.GetAsync(formPage);
                page.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {formPage}");
                string html = await page.Content.ReadAsStringAsync();
                string token = TokenRegex().Match(html).Groups[1].Value;
                token.ShouldNotBeEmpty();

                var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
                form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
                return await Client.PostAsync(action ?? formPage, new FormUrlEncodedContent(form));
            }

            public void Dispose() => Client.Dispose();

            [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
            private static partial Regex TokenRegex();
        }
    }
}
