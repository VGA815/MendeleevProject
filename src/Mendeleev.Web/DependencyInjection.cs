using System.Threading.RateLimiting;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.Infrastructure.Telegram;
using Mendeleev.Web.Bot;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Handlers;
using Mendeleev.Web.Bot.Infrastructure;
using Mendeleev.Web.Cabinet;
using Mendeleev.Web.Endpoints;
using Mendeleev.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Metrics;

namespace Mendeleev.Web
{
    public static class DependencyInjection
    {
        private static readonly TimeSpan LinkCodeWindow = Domain.Users.LinkCode.TelegramCodeLifetime;

        public static IServiceCollection AddPresentation(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
        {
            services.AddRazorPages(options =>
            {
                // The cabinet is only for the signed-in user (FR-WEB-07: instructions only after sign-in too).
                options.Conventions.AuthorizeFolder("/Cabinet");
                options.Conventions.AddFolderApplicationModelConvention("/Cabinet", model => model.Filters.Add(new BlockedAccountFilter()));

                // Every page gets the general limit unless it declares a stricter one (sign-in, registration).
                options.Conventions.AddFolderApplicationModelConvention("/", model =>
                {
                    if (!model.HandlerTypeAttributes.OfType<EnableRateLimitingAttribute>().Any())
                    {
                        model.EndpointMetadata.Add(new EnableRateLimitingAttribute(RateLimitPolicies.Pages));
                    }
                });
            });
            services.AddCabinetAuthentication(environment);
            services.AddMemoryCache();

            // Cyrillic stays as is in the pages instead of &#x…; entities — the encoder still escapes markup.
            services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
                options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
            services.Configure<Pages.SiteOptions>(configuration.GetSection(Pages.SiteOptions.SectionName));

            // The fake aggregator turns a click on /dev/fake-pay into a paid subscription (ТЗ 23): Development and
            // Staging only. Outside Development its webhooks must be signed with a secret that is not in Git.
            services.AddOptions<FakePaymentOptions>()
                .Validate(o => !o.Enabled || environment.IsDevelopment() || environment.IsStaging(),
                    $"Payments:Fake:Enabled is allowed only in Development and Staging, not in {environment.EnvironmentName}.")
                .Validate(o => !o.Enabled || environment.IsDevelopment() || !string.IsNullOrWhiteSpace(o.WebhookSecret),
                    "Payments:Fake:WebhookSecret is required outside Development.")
                .ValidateOnStart();

            services.AddExceptionHandler<GlobalExceptionHandler>();
            services.AddProblemDetails();

            // Session cookies of the cabinet must survive restarts: keys on a persistent volume (ТЗ 27).
            IDataProtectionBuilder dataProtection = services.AddDataProtection().SetApplicationName("mendeleev");
            string? keysPath = configuration["DataProtection:KeysPath"];
            if (!string.IsNullOrWhiteSpace(keysPath))
            {
                dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
            }

            // Caddy is the only ingress; the app port is not published, so the proxy is trusted.
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                options.ForwardLimit = 1;
            });

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = TooManyAttemptsPage.OnRejectedAsync;
                options.AddPolicy(RateLimitPolicies.Webhooks, context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
                options.AddPolicy(RateLimitPolicies.Pages, context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));

                // Attempts are the form posts; showing the form is free.
                options.AddPolicy(RateLimitPolicies.Login, context => HttpMethods.IsPost(context.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) })
                    : RateLimitPartition.GetNoLimiter("read"));
                options.AddPolicy(RateLimitPolicies.Register, context => HttpMethods.IsPost(context.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromHours(1) })
                    : RateLimitPartition.GetNoLimiter("read"));
                options.AddPolicy(RateLimitPolicies.LinkCode, context => HttpMethods.IsPost(context.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = LinkCodeWindow })
                    : RateLimitPartition.GetNoLimiter("read"));
            });

            services.AddOpenTelemetry().WithMetrics(metrics => metrics
                .AddMeter(AppMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddPrometheusExporter());

            return services.AddBot(configuration);
        }

        private static IServiceCollection AddBot(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<BotContent>(configuration.GetSection(BotContent.SectionName));

            services.AddSingleton<BotUpdateQueue>();
            services.AddSingleton<BotRateLimiter>();
            services.AddSingleton<ConversationStore>();

            TelegramOptions telegram = configuration.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new TelegramOptions();
            if (!telegram.IsConfigured)
            {
                // Local run without a bot: notifications go to the log instead of Telegram.
                services.AddSingleton<IUserMessenger, LoggingUserMessenger>();
                return services;
            }

            services.AddSingleton<IUserMessenger, TelegramUserMessenger>();
            services.AddSingleton<StaffCommandsPublisher>();
            services.AddScoped<BotResponder>();
            services.AddScoped<UserHandler>();
            services.AddScoped<StaffHandler>();
            services.AddScoped<UpdateRouter>();

            services.AddHostedService<BotUpdateWorker>();
            services.AddHostedService<TelegramStartup>();
            services.AddHostedService<TelegramPollingService>();

            return services;
        }
    }
}
