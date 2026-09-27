using System.Net.Http.Headers;
using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Abstractions.Telegram;
using Mendeleev.Application.Configuration;
using Mendeleev.Infrastructure.Accounts;
using Mendeleev.Infrastructure.Alerts;
using Mendeleev.Infrastructure.BackgroundJobs;
using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Health;
using Mendeleev.Infrastructure.Http;
using Mendeleev.Infrastructure.Outbox;
using Mendeleev.Infrastructure.Panel;
using Mendeleev.Infrastructure.Payments;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.Infrastructure.Seeding;
using Mendeleev.Infrastructure.Telegram;
using Mendeleev.Infrastructure.Time;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Mendeleev.Infrastructure
{
    public static class DependencyInjection
    {
        public const string TelegramHttpClientName = "telegram";

        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
            services
                .AddPersistence(configuration)
                .AddOptionsBinding(configuration)
                .AddOutboxProcessing(configuration)
                .AddPanel(configuration)
                .AddPayments(configuration)
                .AddTelegram(configuration)
                .AddAlerts()
                .AddBackgroundWork(configuration)
                .AddHealth(configuration);

        /// <summary>The database, the clock and the outbox writer. Enough for integration tests.</summary>
        public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
        {
            string connectionString = configuration.GetConnectionString("Database")
                ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

            services.TryAddSingleton<IDateTimeProvider, DateTimeProvider>();
            services.TryAddSingleton<OutboxSignal>();

            services.AddDbContext<ApplicationDbContext>(options => options
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schemas.Default))
                .UseSnakeCaseNamingConvention());

            services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
            services.AddScoped<ITelegramUpdateDeduplicator, TelegramUpdateDeduplicator>();

            services.AddOptions<AccountOptions>()
                .Bind(configuration.GetSection(AccountOptions.SectionName))
                .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPepper), "Accounts:KeyPepper is required (base64, 32+ bytes).")
                .ValidateOnStart();
            services.AddSingleton<IAccountKeyHasher, HmacAccountKeyHasher>();

            return services;
        }

        /// <summary>The outbox processor; tests call its batch method directly instead of running it.</summary>
        public static IServiceCollection AddOutboxProcessing(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
            services.AddSingleton<OutboxProcessor>();
            return services;
        }

        private static IServiceCollection AddOptionsBinding(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<ServiceOptions>(configuration.GetSection(ServiceOptions.SectionName));
            services.Configure<SubscriptionOptions>(configuration.GetSection(SubscriptionOptions.SectionName));
            services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.SectionName));
            services.Configure<AlertOptions>(configuration.GetSection(AlertOptions.SectionName));
            return services;
        }

        private static IServiceCollection AddPanel(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<RemnawaveOptions>(configuration.GetSection(RemnawaveOptions.SectionName));
            services.AddSingleton<IPanelWebhookParser, RemnawaveWebhookParser>();

            if (configuration.GetValue<bool>("Remnawave:UseInMemory"))
            {
                services.AddSingleton<IPanelClient>(new InMemoryPanelClient(
                    configuration.GetValue<string>("Remnawave:InMemorySubscriptionBaseUrl") ?? "https://sub.localhost"));
                return services;
            }

            services
                .AddHttpClient<IPanelClient, RemnawaveClient>((sp, client) =>
                {
                    RemnawaveOptions options = sp.GetRequiredService<IOptions<RemnawaveOptions>>().Value;
                    if (!options.IsConfigured)
                    {
                        throw new InvalidOperationException("Remnawave:BaseUrl and Remnawave:ApiToken are required (or set Remnawave:UseInMemory).");
                    }

                    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                })
                .AddStandardResilienceHandler(resilience =>
                {
                    // A create that timed out may have succeeded; the outbox retries non-idempotent calls
                    // with its own schedule and adopts an existing user on conflict.
                    resilience.Retry.DisableForUnsafeHttpMethods();
                    resilience.Retry.MaxRetryAttempts = 2;
                    resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                    resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                });

            return services;
        }

        private static IServiceCollection AddPayments(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<FakePaymentOptions>(configuration.GetSection(FakePaymentOptions.SectionName));

            if (configuration.GetValue<bool>($"{FakePaymentOptions.SectionName}:Enabled"))
            {
                services.AddSingleton<FakePaymentProvider>();
                services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>());
                services.AddSingleton<IFakePaymentSimulator>(sp => sp.GetRequiredService<FakePaymentProvider>());
            }

            // Real aggregators (Lava, Enot.io, FreeKassa…) are added here as further IPaymentProvider
            // registrations once the owner signs a contract (ТЗ 23, «Выбор агрегатора»).

            services.AddSingleton<IPaymentProviderRegistry, PaymentProviderRegistry>();
            return services;
        }

        private static IServiceCollection AddTelegram(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));

            services.AddHttpClient(TelegramHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(sp =>
                    ProxyHandlerFactory.Create(sp.GetRequiredService<IOptions<TelegramOptions>>().Value.ProxyUrl))
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(70));

            services.AddSingleton<ITelegramBotClient>(sp =>
            {
                TelegramOptions options = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
                if (!options.IsConfigured)
                {
                    throw new InvalidOperationException("Telegram:BotToken is not configured.");
                }

                HttpClient httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(TelegramHttpClientName);
                return new TelegramBotClient(new TelegramBotClientOptions(options.BotToken), httpClient);
            });

            return services;
        }

        private static IServiceCollection AddAlerts(this IServiceCollection services)
        {
            // Alerts go through the same proxy abroad as the bot: they are about Telegram being unreachable too.
            services.AddHttpClient(AlertSink.TelegramClientName)
                .ConfigurePrimaryHttpMessageHandler(sp =>
                    ProxyHandlerFactory.Create(sp.GetRequiredService<IOptions<TelegramOptions>>().Value.ProxyUrl))
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15));
            services.AddHttpClient(AlertSink.NtfyClientName)
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15));

            services.AddSingleton<IAlertSink, AlertSink>();
            return services;
        }

        private static IServiceCollection AddBackgroundWork(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddHostedService<DataSeeder>();

            JobsOptions jobs = configuration.GetSection(JobsOptions.SectionName).Get<JobsOptions>() ?? new JobsOptions();
            if (!jobs.Enabled)
            {
                return services;
            }

            services.AddHostedService(sp => sp.GetRequiredService<OutboxProcessor>());
            services.AddHostedService<BroadcastWorker>();
            services.AddScheduledJobs();
            return services;
        }

        private static IServiceCollection AddHealth(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddHealthChecks()
                .AddNpgSql(configuration.GetConnectionString("Database")!, name: "postgres", tags: ["ready"])
                .AddCheck<OutboxHealthCheck>("outbox", failureStatus: HealthStatus.Unhealthy, tags: ["ready"]);

            return services;
        }
    }
}
