using System.Reflection;
using Mendeleev.Application;
using Mendeleev.Infrastructure;
using Mendeleev.Web;
using Mendeleev.Web.Extensions;
using Mendeleev.Web.Middleware;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Bot texts and instructions: a YAML file mounted into the container, re-read on change (FR-BOT-17).
string contentPath = builder.Configuration["BotContent:Path"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "content", "bot.yaml");
builder.Configuration.AddYamlFile(contentPath, optional: false, reloadOnChange: true);

builder.Host.UseSerilog((context, logger) => logger.ReadFrom.Configuration(context.Configuration));

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddPresentation(builder.Configuration, builder.Environment)
    .AddEndpoints(Assembly.GetExecutingAssembly());

WebApplication app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    app.ApplyMigrations();
}

// /metrics is internal only: Caddy does not route it, and a proxied request is refused here too. This runs
// before UseForwardedHeaders, which consumes X-Forwarded-For.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/metrics") && context.Request.Headers.ContainsKey("X-Forwarded-For"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
    return next(context);
});

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();

// Only method and path are logged: no query strings, no bodies, no IPs (ТЗ 31, «Логи и приватность»).
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0} ms";
    options.GetLevel = (context, _, exception) =>
        exception is not null || context.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/metrics") ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information;
});

// The site's own styles, fonts, icons and the cabinet's script: no third-party scripts, fonts or analytics (FR-WEB-10).
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapEndpoints();
app.MapRazorPages();
app.MapHealthCheckEndpoints();

app.MapPrometheusScrapingEndpoint("/metrics");

app.MapGet("robots.txt", () => Results.Text("User-agent: *\nDisallow: /cabinet\nDisallow: /pay\nDisallow: /login\nDisallow: /register\nDisallow: /dev\n", "text/plain"))
    .ExcludeFromDescription();

await app.RunAsync();

namespace Mendeleev.Web
{
    public partial class Program;
}
