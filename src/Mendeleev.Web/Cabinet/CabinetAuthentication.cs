using System.Globalization;
using System.Security.Claims;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.WebSessions;
using Mendeleev.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Mendeleev.Web.Cabinet
{
    /// <summary>
    /// Cookie session of the cabinet (ТЗ 27, «Безопасность»): HttpOnly, Secure, SameSite=Lax, sliding for
    /// 30 days, keys of Data Protection on a persistent volume. The cookie carries the user id and the
    /// session stamp; a changed stamp (key reissue, «выйти везде», merge) ends the session on the next request.
    /// </summary>
    internal static class CabinetAuthentication
    {
        public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

        private const string StampClaim = "stamp";

        public static IServiceCollection AddCabinetAuthentication(this IServiceCollection services, IHostEnvironment environment)
        {
            // Plain http on localhost in Development; everywhere else the cookie is Secure and __Host-bound.
            bool development = environment.IsDevelopment();

            services.AddScoped<CabinetCookieEvents>();
            services
                .AddAuthentication(Scheme)
                .AddCookie(Scheme, options =>
                {
                    options.Cookie.Name = development ? "cabinet" : "__Host-cabinet";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.Path = "/";
                    options.ExpireTimeSpan = TimeSpan.FromDays(30);
                    options.SlidingExpiration = true;
                    options.LoginPath = "/login";
                    options.LogoutPath = "/logout";
                    options.AccessDeniedPath = "/login";
                    options.ReturnUrlParameter = "returnUrl";
                    options.EventsType = typeof(CabinetCookieEvents);
                });

            services.AddAuthorization();

            services.AddAntiforgery(options =>
            {
                options.Cookie.Name = development ? "antiforgery" : "__Host-antiforgery";
                options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            });

            return services;
        }

        public static Task SignInAsync(HttpContext http, long userId, Guid sessionStamp)
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
                    new Claim(StampClaim, sessionStamp.ToString("N")),
                ],
                Scheme);

            return http.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
        }

        public static Task SignOutAsync(HttpContext http) => http.SignOutAsync(Scheme);

        public static long GetUserId(this ClaimsPrincipal principal) =>
            TryGetUserId(principal, out long userId) ? userId : throw new InvalidOperationException("Not signed in to the cabinet.");

        /// <summary>The account state loaded while validating this request's session.</summary>
        public static WebAccountState? GetCabinetAccount(this HttpContext http) =>
            http.Items.TryGetValue(typeof(WebAccountState), out object? value) ? value as WebAccountState : null;

        private static bool TryGetUserId(ClaimsPrincipal? principal, out long userId)
        {
            userId = 0;
            return principal?.FindFirstValue(ClaimTypes.NameIdentifier) is string value
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out userId);
        }

        internal sealed class CabinetCookieEvents(IQueryHandler<GetWebAccountQuery, WebAccountState> getAccount)
            : CookieAuthenticationEvents
        {
            public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
            {
                if (!TryGetUserId(context.Principal, out long userId))
                {
                    await RejectAsync(context);
                    return;
                }

                Result<WebAccountState> account = await getAccount.Handle(new GetWebAccountQuery(userId), context.HttpContext.RequestAborted);
                string? stamp = context.Principal?.FindFirstValue(StampClaim);
                if (account.IsFailure || stamp != account.Value.SessionStamp.ToString("N"))
                {
                    await RejectAsync(context);
                    return;
                }

                context.HttpContext.Items[typeof(WebAccountState)] = account.Value;
            }

            private static async Task RejectAsync(CookieValidatePrincipalContext context)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(Scheme);
            }

            /// <summary>The return page polls with fetch: a JSON caller gets 401 instead of the login page.</summary>
            public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
            {
                if (context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                return base.RedirectToLogin(context);
            }
        }
    }
}
