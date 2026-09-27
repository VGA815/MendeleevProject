using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Mendeleev.Web.Extensions
{
    public static class HealthCheckEndpointExtensions
    {
        /// <summary>
        /// <c>/health/live</c> — the process is up; <c>/health/ready</c> — the database answers and the oldest
        /// outbox task is younger than 5 minutes (ТЗ 40). Uptime Kuma polls <c>/health/ready</c> from outside,
        /// so the body is terse: no descriptions, no exception texts.
        /// </summary>
        public static WebApplication MapHealthCheckEndpoints(this WebApplication app)
        {
            app.MapHealthChecks("health/live", new HealthCheckOptions { Predicate = _ => false });

            app.MapHealthChecks("health/ready", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready"),
                ResponseWriter = WriteTerseResponse,
            });

            return app;
        }

        private static Task WriteTerseResponse(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(entry => new { name = entry.Key, status = entry.Value.Status.ToString() }),
            });
        }
    }
}
