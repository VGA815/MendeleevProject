using System.Collections.Concurrent;
using Mendeleev.Application;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Infrastructure;
using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Outbox;
using Mendeleev.Infrastructure.Panel;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Mendeleev.IntegrationTests.Infrastructure
{
    /// <summary>One PostgreSQL container for the whole run; every test gets its own database.</summary>
    public sealed class PostgresFixture : IAsyncLifetime
    {
        public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:17-alpine").Build();

        public Task InitializeAsync() => Container.StartAsync();

        public Task DisposeAsync() => Container.DisposeAsync().AsTask();
    }

    [CollectionDefinition(nameof(PostgresCollection))]
    public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

    /// <summary>
    /// The real Application and Infrastructure over a real database; only the edges are replaced: the panel
    /// is in memory, Telegram and alerts are recorded, the clock is controlled by the test.
    /// </summary>
    public sealed class TestApp : IAsyncDisposable
    {
        public const string FakeWebhookSecret = "test-fake-secret";
        public static readonly Guid BasicSquad = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private TestApp(ServiceProvider provider, TestClock clock)
        {
            Provider = provider;
            Clock = clock;
            Panel = (InMemoryPanelClient)provider.GetRequiredService<IPanelClient>();
            Messenger = (RecordingMessenger)provider.GetRequiredService<IUserMessenger>();
            Alerts = (RecordingAlerts)provider.GetRequiredService<IAlertSink>();
        }

        public ServiceProvider Provider { get; }

        public TestClock Clock { get; }

        public InMemoryPanelClient Panel { get; }

        public RecordingMessenger Messenger { get; }

        public RecordingAlerts Alerts { get; }

        public static async Task<TestApp> CreateAsync(PostgresFixture postgres)
        {
            string database = "t_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(postgres.Container.GetConnectionString()))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
                await command.ExecuteNonQueryAsync();
            }

            string connectionString = new NpgsqlConnectionStringBuilder(postgres.Container.GetConnectionString()) { Database = database }.ConnectionString;

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString,
                    ["Accounts:KeyPepper"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
                    ["Service:SiteBaseUrl"] = "https://site.test",
                    ["Remnawave:UseInMemory"] = "true",
                    ["Payments:Enabled"] = "true",
                    ["Payments:ActiveProvider"] = "fake",
                    ["Payments:Fake:Enabled"] = "true",
                    ["Payments:Fake:WebhookSecret"] = FakeWebhookSecret,
                    ["Jobs:Enabled"] = "false",
                })
                .Build();

            // Whole seconds: PostgreSQL keeps microseconds, so comparisons after a round trip stay exact.
            // 09:00 UTC is 12:00 in Moscow — outside the quiet hours.
            var clock = new TestClock(DateTime.UtcNow.Date.AddHours(9));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IDateTimeProvider>(clock);
            services.AddApplication();
            services.AddInfrastructure(configuration);
            services.AddSingleton<IUserMessenger, RecordingMessenger>();
            services.AddSingleton<IAlertSink, RecordingAlerts>();

            ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var app = new TestApp(provider, clock);
            await app.PrepareDatabaseAsync();
            return app;
        }

        public async Task<Result<TResult>> SendAsync<TCommand, TResult>(TCommand command)
            where TCommand : ICommand<TResult>
        {
            await using AsyncServiceScope scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>().Handle(command, CancellationToken.None);
        }

        public async Task<Result> SendAsync<TCommand>(TCommand command)
            where TCommand : ICommand
        {
            await using AsyncServiceScope scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand>>().Handle(command, CancellationToken.None);
        }

        public async Task<Result<TResult>> QueryAsync<TQuery, TResult>(TQuery query)
            where TQuery : IQuery<TResult>
        {
            await using AsyncServiceScope scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().Handle(query, CancellationToken.None);
        }

        /// <summary>Runs the outbox until nothing is due — what the background processor does continuously.</summary>
        public async Task ProcessOutboxAsync()
        {
            OutboxProcessor processor = Provider.GetRequiredService<OutboxProcessor>();
            for (int i = 0; i < 50 && await processor.ProcessBatchAsync(CancellationToken.None) > 0; i++)
            {
            }
        }

        public async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        {
            await using AsyncServiceScope scope = Provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        public async Task<long> AddStaffAsync(long telegramId, StaffRole role)
        {
            return await WithDbAsync(async db =>
            {
                var member = StaffMember.Create(telegramId, role, $"{role} {telegramId}", null, Clock.UtcNow);
                db.Staff.Add(member);
                await db.SaveChangesAsync();
                return member.Id;
            });
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();

        private async Task PrepareDatabaseAsync()
        {
            await WithDbAsync(async db =>
            {
                await db.Database.MigrateAsync();
                db.Tariffs.AddRange(
                    Tariff.Create(Tariff.TrialCode, "Пробный", TariffTier.Trial, 0, 2, 3, 10L * 1024 * 1024 * 1024, [BasicSquad], true, 0),
                    Tariff.Create("basic_1m", "Базовый, 1 месяц", TariffTier.Basic, 199, 30, 3, null, [BasicSquad], true, 10),
                    Tariff.Create("basic_3m", "Базовый, 3 месяца", TariffTier.Basic, 549, 90, 3, null, [BasicSquad], true, 20));
                return await db.SaveChangesAsync();
            });
        }
    }

    public sealed class TestClock(DateTime start) : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    public sealed class RecordingMessenger : IUserMessenger
    {
        public ConcurrentQueue<(long ChatId, NotificationMessage Message)> Notifications { get; } = new();

        public ConcurrentQueue<(long ChatId, string Html)> Texts { get; } = new();

        /// <summary>Chats that blocked the bot: Telegram answers 403 to them.</summary>
        public ConcurrentDictionary<long, bool> BlockedChats { get; } = new();

        /// <summary>When each broadcast message left, by <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
        public ConcurrentQueue<long> BroadcastSentAt { get; } = new();

        public Task<DeliveryResult> SendNotificationAsync(long chatId, NotificationMessage message, CancellationToken cancellationToken)
        {
            Notifications.Enqueue((chatId, message));
            return Task.FromResult(DeliveryResult.Sent);
        }

        public Task<DeliveryResult> SendBroadcastAsync(long chatId, string html, bool withUpdateButton, CancellationToken cancellationToken)
        {
            BroadcastSentAt.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
            if (BlockedChats.ContainsKey(chatId))
            {
                return Task.FromResult(DeliveryResult.BotBlocked);
            }

            Texts.Enqueue((chatId, html));
            return Task.FromResult(DeliveryResult.Sent);
        }

        public Task<DeliveryResult> SendTextAsync(long chatId, string html, CancellationToken cancellationToken)
        {
            Texts.Enqueue((chatId, html));
            return Task.FromResult(DeliveryResult.Sent);
        }
    }

    public sealed class RecordingAlerts : IAlertSink
    {
        public ConcurrentQueue<Alert> Raised { get; } = new();

        public Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
        {
            Raised.Enqueue(alert);
            return Task.CompletedTask;
        }

        public Task RaiseOnSeriesAsync(Alert alert, int threshold, TimeSpan window, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
