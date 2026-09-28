using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.LinkTelegram;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Mendeleev.Web.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mendeleev.Web.Pages.Cabinet
{
    /// <summary>
    /// Linking Telegram by the one-time code from the bot (FR-WEB-08, FR-ACC-09). When the web account
    /// is merged into the Telegram one (rule 2), the session is re-issued for the account that remains.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.LinkCode)]
    public sealed class LinkTelegramModel(ICommandHandler<LinkTelegramCommand, LinkedAccount> link) : CabinetPageModel
    {
        public string? Error { get; private set; }

        public bool Linked { get; private set; }

        public void OnGet()
        {
            Linked = Account.HasTelegram;
        }

        public async Task<IActionResult> OnPostAsync(string? code, CancellationToken cancellationToken)
        {
            Result<LinkedAccount> result = await link.Handle(new LinkTelegramCommand(UserId, code ?? string.Empty), cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Description;
                return Page();
            }

            await CabinetAuthentication.SignInAsync(HttpContext, result.Value.UserId, result.Value.SessionStamp);
            return RedirectToPage("/Cabinet/LinkTelegram");
        }
    }
}
