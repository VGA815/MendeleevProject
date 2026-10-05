using System.Globalization;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Mendeleev.Application.Configuration;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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
            await response.WriteAsync(Html(context.HttpContext, minutes), cancellationToken);
        }

        private static string Html(HttpContext context, int minutes)
        {
            // The site's own stylesheet: static files are served before the rate limiter, so it loads even now.
            string stylesheet = HtmlEncoder.Default.Encode(context.RequestServices.GetRequiredService<IFileVersionProvider>()
                .AddFileVersionToPath(context.Request.PathBase, "/css/site.css"));
            string name = HtmlEncoder.Default.Encode(context.RequestServices.GetRequiredService<IOptions<ServiceOptions>>().Value.Name);
            return $$"""
                <!DOCTYPE html>
                <html lang="ru">
                <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <meta name="robots" content="noindex, nofollow" />
                <title>Слишком много попыток</title>
                <link rel="icon" href="/favicon.svg" type="image/svg+xml" />
                <link rel="stylesheet" href="{{stylesheet}}" />
                </head>
                <body>
                <header class="site-header"><div class="container"><a class="brand" href="/"><img src="/favicon.svg" width="32" height="32" alt="" /><span>{{name}}</span></a></div></header>
                <main class="site-main grid-band"><div class="container"><div class="auth"><div class="panel">
                <h1>Слишком много попыток</h1>
                <p>Попробуйте через {{minutes}} {{MinutesWord(minutes)}}.</p>
                <p><a href="/">На главную</a></p>
                </div></div></div></main>
                </body>
                </html>
                """;
        }

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
