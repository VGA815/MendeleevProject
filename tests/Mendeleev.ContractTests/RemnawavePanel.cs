using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Infrastructure;
using Mendeleev.Infrastructure.Panel;
using Mendeleev.Infrastructure.Time;
using Mendeleev.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Mendeleev.ContractTests
{
    /// <summary>
    /// The pinned Remnawave panel in Docker with its own PostgreSQL and Valkey, as in deploy/panel. The panel
    /// gets an admin, the squad <c>basic</c> and an API token with only the scopes the service needs; its
    /// webhooks come back to a receiver in this process. All containers sit on the default bridge and
    /// talk by IP: that is also where the host port forwarding for the webhooks lives.
    /// </summary>
    public sealed class RemnawavePanel : IAsyncLifetime
    {
        /// <summary>The token the service gets in production (README, «Развёртывание»).</summary>
        public static readonly string[] ServiceTokenScopes = ["users:*", "hwid-user-devices:*"];

        public const string SubscriptionDomain = "sub.contract.test";

        private const string DatabasePassword = "remnawave-contract";
        private const string AdminUsername = "contract_admin";

        private readonly string _webhookSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        private WebApplication? _receiver;
        private PostgreSqlContainer? _database;
        private IContainer? _valkey;
        private IContainer? _backend;
        private ServiceProvider? _services;
        private string _serviceToken = string.Empty;

        public static string Tag => Environment.GetEnvironmentVariable("REMNAWAVE_TAG") is { Length: > 0 } tag ? tag : PinnedTag();

        public string BaseUrl { get; private set; } = string.Empty;

        /// <summary>The token with only <see cref="ServiceTokenScopes"/>, as <c>Remnawave__ApiToken</c> of the service.</summary>
        public string ServiceToken => _serviceToken;

        public string WebhookSecret => _webhookSecret;

        public Guid BasicSquad { get; private set; }

        /// <summary>Webhooks as the panel delivered them: raw body and headers, like the service receives them.</summary>
        public ConcurrentQueue<WebhookRequest> Webhooks { get; } = new();

        /// <summary>Admin access for arranging what a real client would do (registering a device).</summary>
        public HttpClient Admin { get; private set; } = null!;

        public IPanelClient Client => _services!.GetRequiredService<IPanelClient>();

        public IPanelWebhookParser WebhookParser => _services!.GetRequiredService<IPanelWebhookParser>();

        public async Task InitializeAsync()
        {
            ushort receiverPort = await StartWebhookReceiverAsync();
            await TestcontainersSettings.ExposeHostPortsAsync(receiverPort);

            _database = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("remnawave")
                .WithUsername("remnawave")
                .WithPassword(DatabasePassword)
                .Build();
            _valkey = new ContainerBuilder("valkey/valkey:8-alpine")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("valkey-cli", "ping"))
                .Build();
            await Task.WhenAll(_database.StartAsync(), _valkey.StartAsync());

            _backend = new ContainerBuilder($"remnawave/backend:{Tag}")
                .WithEnvironment(new Dictionary<string, string>
                {
                    ["APP_PORT"] = "3000",
                    ["METRICS_PORT"] = "3001",
                    ["API_INSTANCES"] = "1",
                    ["DATABASE_URL"] = $"postgresql://remnawave:{DatabasePassword}@{_database.IpAddress}:5432/remnawave",
                    ["REDIS_HOST"] = _valkey.IpAddress,
                    ["REDIS_PORT"] = "6379",
                    ["APP_SECRET"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(64)),
                    ["FRONT_END_DOMAIN"] = "*",
                    ["PANEL_DOMAIN"] = "panel.contract.test",
                    ["SUB_PUBLIC_DOMAIN"] = SubscriptionDomain,
                    ["METRICS_USER"] = "metrics",
                    ["METRICS_PASS"] = "metrics",
                    ["IS_TELEGRAM_NOTIFICATIONS_ENABLED"] = "false",
                    ["WEBHOOK_ENABLED"] = "true",
                    ["WEBHOOK_URL"] = $"http://host.testcontainers.internal:{receiverPort}/webhooks/remnawave",
                    ["WEBHOOK_SECRET_HEADER"] = _webhookSecret,
                    // As deploy/panel/.env.example.
                    ["SHORT_UUID_METHOD"] = "nanoid",
                    ["SHORT_UUID_LENGTH"] = "16",
                })
                .WithPortBinding(3000, true)
                .WithPortBinding(3001, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                    request => request.ForPort(3001).ForPath("/health"),
                    wait => wait.WithTimeout(TimeSpan.FromMinutes(3))))
                .Build();
            await _backend.StartAsync();

            BaseUrl = $"http://{_backend.Hostname}:{_backend.GetMappedPublicPort(3000)}";
            Admin = new HttpClient(new ReverseProxyHeaders { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri(BaseUrl + "/") };

            // The admin JWT works only for the panel's own UI; the setup below is what the operator does there.
            Admin.DefaultRequestHeaders.Add("X-Remnawave-Client-Type", "browser");

            string adminToken = await RegisterAdminAsync();
            Admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

            BasicSquad = await CreateSquadAsync("basic");
            _serviceToken = await CreateApiTokenAsync(ServiceTokenScopes);

            _services = BuildAdapter(pageSize: 250);
        }

        public async Task DisposeAsync()
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
            Admin?.Dispose();
            foreach (IAsyncDisposable? resource in new IAsyncDisposable?[] { _backend, _valkey, _database, _receiver })
            {
                if (resource is not null)
                {
                    await resource.DisposeAsync();
                }
            }
        }

        /// <summary>An adapter with another page size, to walk the pagination over a few users.</summary>
        public IPanelClient ClientWithPageSize(int pageSize) => BuildAdapter(pageSize).GetRequiredService<IPanelClient>();

        public async Task<WebhookRequest> WaitForWebhookAsync(Func<PanelWebhookEvent, bool> match, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            var seen = new HashSet<WebhookRequest>();
            while (!cancellation.IsCancellationRequested)
            {
                foreach (WebhookRequest request in Webhooks.Where(seen.Add))
                {
                    if (match(WebhookParser.Parse(request)))
                    {
                        return request;
                    }
                }

                await Task.Delay(200, CancellationToken.None);
            }

            throw new TimeoutException($"No matching panel webhook in {timeout}; received {Webhooks.Count}.");
        }

        /// <summary>Registers a device as the client app would on a subscription request.</summary>
        public async Task AddDeviceAsync(int panelUserId, string hwid, string platform)
        {
            using HttpResponseMessage response = await Admin.PostAsJsonAsync("api/hwid/devices", new
            {
                hwid,
                userId = panelUserId,
                platform,
                osVersion = "14",
                deviceModel = "Contract Phone",
                userAgent = "Happ/contract",
            });
            await EnsureSuccessAsync(response, "create HWID device");
        }

        private ServiceProvider BuildAdapter(int pageSize)
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
            services.AddSingleton(Options.Create(new RemnawaveOptions
            {
                BaseUrl = BaseUrl,
                ApiToken = _serviceToken,
                WebhookSecret = _webhookSecret,
                PageSize = pageSize,
            }));
            services.AddSingleton<IPanelWebhookParser, RemnawaveWebhookParser>();

            // The production pipeline; Caddy in front of the panel adds what the handler adds here.
            services.AddRemnawaveClient().AddHttpMessageHandler(() => new ReverseProxyHeaders());
            return services.BuildServiceProvider();
        }

        private async Task<string> RegisterAdminAsync()
        {
            string password = "Contract" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "A1";
            using HttpResponseMessage response = await Admin.PostAsJsonAsync("api/auth/register", new { username = AdminUsername, password });
            return (await ReadResponseAsync(response, "register admin")).GetProperty("accessToken").GetString()!;
        }

        private async Task<Guid> CreateSquadAsync(string name)
        {
            using HttpResponseMessage response = await Admin.PostAsJsonAsync("api/internal-squads", new { name, inbounds = Array.Empty<Guid>() });
            return (await ReadResponseAsync(response, "create squad")).GetProperty("uuid").GetGuid();
        }

        private async Task<string> CreateApiTokenAsync(string[] scopes)
        {
            using HttpResponseMessage response = await Admin.PostAsJsonAsync("api/tokens", new { name = "mendeleev-contract", expiresInDays = 1, scopes });
            return (await ReadResponseAsync(response, "create API token")).GetProperty("token").GetString()!;
        }

        private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, string operation)
        {
            await EnsureSuccessAsync(response, operation);
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("response").Clone();
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Panel {operation} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
        }

        private async Task<ushort> StartWebhookReceiverAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            _receiver = builder.Build();
            _receiver.MapPost("/webhooks/remnawave", async (HttpRequest request) =>
            {
                using var body = new MemoryStream();
                await request.Body.CopyToAsync(body);
                Webhooks.Enqueue(new WebhookRequest(
                    request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    body.ToArray(),
                    RemoteIp: null));
                return Results.Ok();
            });
            await _receiver.StartAsync();

            string address = _receiver.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return (ushort)new Uri(address).Port;
        }

        /// <summary>The value pinned in deploy/panel/.env.example — the one place the version is set.</summary>
        private static string PinnedTag()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                string env = Path.Combine(directory.FullName, "deploy", "panel", ".env.example");
                if (File.Exists(env))
                {
                    return File.ReadLines(env)
                        .Select(line => line.Trim())
                        .Single(line => line.StartsWith("REMNAWAVE_TAG=", StringComparison.Ordinal))["REMNAWAVE_TAG=".Length..];
                }
            }

            throw new InvalidOperationException("deploy/panel/.env.example not found above the test directory.");
        }
    }

    [CollectionDefinition(nameof(RemnawavePanelCollection))]
    public sealed class RemnawavePanelCollection : ICollectionFixture<RemnawavePanel>;
}
