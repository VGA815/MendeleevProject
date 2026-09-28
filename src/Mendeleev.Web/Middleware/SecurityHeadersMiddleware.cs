namespace Mendeleev.Web.Middleware
{
    /// <summary>
    /// FR-WEB-10: own sources only, no framing, no Referer — the subscription link must not leak through
    /// it. No third-party scripts, fonts or analytics anywhere on the site.
    /// </summary>
    public sealed class SecurityHeadersMiddleware(RequestDelegate next)
    {
        public Task Invoke(HttpContext context)
        {
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
                headers["X-Frame-Options"] = "DENY";
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

                // The cabinet, sign-in and registration: not indexed and never cached — the key and the
                // subscription link must not stay in a shared cache (ТЗ 27, «Индексация»).
                PathString path = context.Request.Path;
                if (path.StartsWithSegments("/pay") || path.StartsWithSegments("/dev") || path.StartsWithSegments("/cabinet")
                    || path.StartsWithSegments("/login") || path.StartsWithSegments("/register") || path.StartsWithSegments("/logout"))
                {
                    headers["X-Robots-Tag"] = "noindex, nofollow";
                    headers["Cache-Control"] = "no-store";
                }

                return Task.CompletedTask;
            });

            return next(context);
        }
    }
}
