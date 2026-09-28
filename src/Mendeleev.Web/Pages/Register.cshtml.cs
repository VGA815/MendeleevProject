using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.RegisterWebAccount;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Mendeleev.Web.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Mendeleev.Web.Pages
{
    /// <summary>
    /// Registration without Telegram (FR-WEB-02): the key is shown once, right in the response to the
    /// form — never in a URL or a redirect — and the user is signed in at once.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.Register)]
    public sealed class RegisterModel(ICommandHandler<RegisterWebAccountCommand, NewWebAccount> register) : PageModel
    {
        public string? AccountKey { get; private set; }

        public string? Error { get; private set; }

        public IActionResult OnGet() =>
            User.Identity?.IsAuthenticated == true ? RedirectToPage("/Cabinet/Index") : Page();

        public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
        {
            Result<NewWebAccount> result = await register.Handle(new RegisterWebAccountCommand(), cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Description;
                return Page();
            }

            await CabinetAuthentication.SignInAsync(HttpContext, result.Value.UserId, result.Value.SessionStamp);
            AccountKey = result.Value.AccountKey;
            return Page();
        }
    }
}
