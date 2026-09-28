using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.SignIn;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Mendeleev.Web.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Mendeleev.Web.Pages
{
    /// <summary>Sign-in by the account key (FR-WEB-03); 5 attempts a minute from an IP (FR-WEB-10).</summary>
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public sealed class LoginModel(ICommandHandler<SignInWithKeyCommand, WebSession> signIn) : PageModel
    {
        public string? Error { get; private set; }

        public IActionResult OnGet() =>
            User.Identity?.IsAuthenticated == true ? RedirectToPage("/Cabinet/Index") : Page();

        public async Task<IActionResult> OnPostAsync(string? accountKey, string? returnUrl, CancellationToken cancellationToken)
        {
            Result<WebSession> result = await signIn.Handle(new SignInWithKeyCommand(accountKey ?? string.Empty), cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Description;
                return Page();
            }

            await CabinetAuthentication.SignInAsync(HttpContext, result.Value.UserId, result.Value.SessionStamp);

            // Only a local address: an open redirect would make the sign-in page a phishing tool.
            return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Cabinet/Index");
        }
    }
}
