using Mendeleev.Web.Cabinet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mendeleev.Web.Pages
{
    /// <summary>Sign-out of this browser. POST only, with the antiforgery token: a link cannot sign anyone out.</summary>
    public sealed class LogoutModel : PageModel
    {
        public IActionResult OnGet() => RedirectToPage("/Index");

        public async Task<IActionResult> OnPostAsync()
        {
            await CabinetAuthentication.SignOutAsync(HttpContext);
            return RedirectToPage("/Index");
        }
    }
}
