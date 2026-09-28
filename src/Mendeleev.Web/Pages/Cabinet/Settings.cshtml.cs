using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.IssueAccountKey;
using Mendeleev.Application.Accounts.WebSessions;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Microsoft.AspNetCore.Mvc;

namespace Mendeleev.Web.Pages.Cabinet
{
    /// <summary>
    /// FR-WEB-12 without email (stage 1.5): reissue of the key and «выйти на всех устройствах». A reissue
    /// ends every session (ТЗ 27, «Безопасность»); this browser is signed in again with the new key.
    /// </summary>
    public sealed class SettingsModel(
        ICommandHandler<IssueAccountKeyCommand, string> issueKey,
        ICommandHandler<SignOutEverywhereCommand> signOutEverywhere,
        IQueryHandler<GetWebAccountQuery, WebAccountState> getAccount)
        : CabinetPageModel
    {
        public string? NewKey { get; private set; }

        public string? Error { get; private set; }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostReissueKeyAsync(CancellationToken cancellationToken)
        {
            Result<string> result = await issueKey.Handle(new IssueAccountKeyCommand(UserId), cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Description;
                return Page();
            }

            Result<WebAccountState> account = await getAccount.Handle(new GetWebAccountQuery(UserId), cancellationToken);
            if (account.IsSuccess)
            {
                await CabinetAuthentication.SignInAsync(HttpContext, UserId, account.Value.SessionStamp);
            }

            NewKey = result.Value;
            return Page();
        }

        public async Task<IActionResult> OnPostSignOutEverywhereAsync(CancellationToken cancellationToken)
        {
            Result result = await signOutEverywhere.Handle(new SignOutEverywhereCommand(UserId), cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Description;
                return Page();
            }

            await CabinetAuthentication.SignOutAsync(HttpContext);
            return RedirectToPage("/Login");
        }
    }
}
