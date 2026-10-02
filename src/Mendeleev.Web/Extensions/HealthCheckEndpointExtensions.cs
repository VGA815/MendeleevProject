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
        /// <c>/health/startup</c> — the process is up and the database answers: Docker's health check and the
        /// release script wait for it. Not <c>/health/ready</c>: that one is red while the panel is down
        /// (the outbox waits), and a release during a panel outage would roll back a working version.
        /// </summary>
        public static WebApplication MapHealthCheckEndpoints(this WebApplication app)
        {
            app.MapHealthChecks("health/live", new HealthCheckOptions { Predicate = _ => false });

            app.MapHealthChecks("health/startup", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("startup"),
                ResponseWriter = WriteTerseResponse,
            });

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
