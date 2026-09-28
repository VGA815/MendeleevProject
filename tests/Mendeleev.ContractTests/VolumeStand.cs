using System.Collections.Concurrent;
using Mendeleev.Application;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Infrastructure;
using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Panel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Mendeleev.ContractTests
{
    /// <summary>
    /// Runs only when <c>MENDELEEV_VOLUME=1</c> (<c>make test-volume</c>): minutes of work, not for every push.
    /// </summary>
    public sealed class VolumeFactAttribute : FactAttribute
    {
        public const string EnvironmentVariable = "MENDELEEV_VOLUME";

        public VolumeFactAttribute()
        {
            if (!IsEnabled)
            {
                Skip = $"Volume run: set {EnvironmentVariable}=1 (make test-volume).";
            }
        }

        public static bool IsEnabled => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";
    }

    /// <summary>
    /// The service's Application and Infrastructure over its own PostgreSQL and the pinned panel — the same
    /// wiring as production, only Telegram and the alerts are replaced. Hosted services are not started:
    /// the test drives the outbox and the reconciliation itself.
    /// </summary>
    public sealed class VolumeStand : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

        public RemnawavePanel Panel { get; } = new();

        public ServiceProvider Services { get; private set; } = null!;

        /// <summary>Every HTTP request the adapter sent to the panel.</summary>
        public PanelRequestCounter Requests { get; } = new();

        public ConcurrentQueue<Alert> Alerts { get; } = new();

        public async Task InitializeAsync()
        {
            if (!VolumeFactAttribute.IsEnabled)
            {
                return;
            }

            await Task.WhenAll(Panel.InitializeAsync(), _database.StartAsync());

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = _database.GetConnectionString(),
                    ["Accounts:KeyPepper"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
                    ["Remnawave:BaseUrl"] = Panel.BaseUrl,
                    ["Remnawave:ApiToken"] = Panel.ServiceToken,
                    ["Remnawave:WebhookSecret"] = Panel.WebhookSecret,
                    ["Payments:Enabled"] = "false",
                    ["Jobs:Enabled"] = "false",
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(configuration);
            services.AddApplication();
            services.AddInfrastructure(configuration);
            services.AddSingleton<IUserMessenger, SilentMessenger>();
            services.AddSingleton<IAlertSink>(new CollectingAlerts(Alerts));

            // The production pipeline of the adapter plus what Caddy adds in front of the panel, and a counter.
            services.AddHttpClient<IPanelClient, RemnawaveClient>()
                .AddHttpMessageHandler(() => new ReverseProxyHeaders())
                .AddHttpMessageHandler(() => new CountingHandler(Requests));

            Services = services.BuildServiceProvider();

            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        }

        public async Task DisposeAsync()
        {
            if (Services is not null)
            {
                await Services.DisposeAsync();
            }
            await Panel.DisposeAsync();
            await _database.DisposeAsync();
        }

        public sealed class PanelRequestCounter
        {
            private long _count;

            public long Count => Interlocked.Read(ref _count);

            public void Increment() => Interlocked.Increment(ref _count);
        }

        private sealed class CountingHandler(PanelRequestCounter counter) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                counter.Increment();
                return base.SendAsync(request, cancellationToken);
            }
        }

        private sealed class SilentMessenger : IUserMessenger
        {
            public Task<DeliveryResult> SendNotificationAsync(long chatId, NotificationMessage message, CancellationToken cancellationToken) =>
                Task.FromResult(DeliveryResult.Sent);

            public Task<DeliveryResult> SendBroadcastAsync(long chatId, string html, bool withUpdateButton, CancellationToken cancellationToken) =>
                Task.FromResult(DeliveryResult.Sent);

            public Task<DeliveryResult> SendTextAsync(long chatId, string html, CancellationToken cancellationToken) =>
                Task.FromResult(DeliveryResult.Sent);
        }

        private sealed class CollectingAlerts(ConcurrentQueue<Alert> alerts) : IAlertSink
        {
            public Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
            {
                alerts.Enqueue(alert);
                return Task.CompletedTask;
            }

            public Task RaiseOnSeriesAsync(Alert alert, int threshold, TimeSpan window, CancellationToken cancellationToken = default)
            {
                alerts.Enqueue(alert);
                return Task.CompletedTask;
            }
        }
    }

    [CollectionDefinition(nameof(VolumeStandCollection))]
    public sealed class VolumeStandCollection : ICollectionFixture<VolumeStand>;
}
