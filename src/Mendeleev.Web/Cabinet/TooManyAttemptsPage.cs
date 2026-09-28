using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Mendeleev.Web.Cabinet
{
    /// <summary>
    /// ТЗ 27, «Обработка ошибок»: when a limit is hit, a page «Попробуйте через N минут» with HTTP 429.
    /// Self-contained: the rate limiter answers before any page runs.
    /// </summary>
    internal static class TooManyAttemptsPage
    {
        public static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
        {
            HttpResponse response = context.HttpContext.Response;
            int minutes = 1;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
            }

            // Webhook senders get a bare 429.
            if (context.HttpContext.Request.Path.StartsWithSegments("/webhooks"))
            {
                return;
            }

            response.ContentType = "text/html; charset=utf-8";
            await response.WriteAsync(Html(minutes), cancellationToken);
        }

        private static string Html(int minutes) =>
            $$"""
            <!DOCTYPE html>
            <html lang="ru">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <meta name="robots" content="noindex, nofollow" />
            <title>Слишком много попыток</title>
            <style>
            :root { color-scheme: light dark; }
            body { margin: 0 auto; max-width: 760px; padding: 16px; font: 16px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
            </style>
            </head>
            <body>
            <h1>Слишком много попыток</h1>
            <p>Попробуйте через {{minutes}} {{MinutesWord(minutes)}}.</p>
            <p><a href="/">На главную</a></p>
            </body>
            </html>
            """;

        private static string MinutesWord(int n) => (n % 100) switch
        {
            >= 11 and <= 14 => "минут",
            _ => (n % 10) switch
            {
                1 => "минуту",
                >= 2 and <= 4 => "минуты",
                _ => "минут",
            },
        };
    }
}
