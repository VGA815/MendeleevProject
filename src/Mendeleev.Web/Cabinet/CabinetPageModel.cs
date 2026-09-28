using Mendeleev.Application.Accounts.WebSessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mendeleev.Web.Cabinet
{
    /// <summary>A page of the cabinet: only for the signed-in user and only with their own data (ТЗ 27).</summary>
    public abstract class CabinetPageModel : PageModel
    {
        public long UserId => User.GetUserId();

        /// <summary>Loaded and checked by the cookie validation of this very request.</summary>
        public WebAccountState Account => HttpContext.GetCabinetAccount()
            ?? throw new InvalidOperationException("The cabinet session was not validated.");
    }

    /// <summary>
    /// A blocked user sees only the block message and the support contact (ТЗ 27, «Бизнес-правила»):
    /// every cabinet page except the start page, which shows that message, redirects there.
    /// </summary>
    internal sealed class BlockedAccountFilter : IAsyncPageFilter
    {
        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

        public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            if (context.HttpContext.GetCabinetAccount() is { IsBlocked: true }
                && context.ActionDescriptor.ViewEnginePath != "/Cabinet/Index")
            {
                context.Result = new RedirectToPageResult("/Cabinet/Index");
                return;
            }

            await next();
        }
    }
}
