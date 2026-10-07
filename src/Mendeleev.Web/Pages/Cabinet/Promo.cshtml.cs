using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Promos;
using Mendeleev.Domain.Promos;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Mendeleev.Web.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mendeleev.Web.Pages.Cabinet
{
    /// <summary>
    /// A promo code entered in the cabinet (FR-SUB-15): a discount goes back to the tariffs with the new prices,
    /// bonus days are shown here. Attempts are limited per account — codes are short and could be guessed.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.Promo)]
    public sealed class PromoModel(ICommandHandler<ApplyPromoCodeCommand, PromoApplied> applyPromo) : CabinetPageModel
    {
        public string? Code { get; private set; }

        public string? Error { get; private set; }

        public PromoApplied? Bonus { get; private set; }

        public IActionResult OnGet() => RedirectToPage("/Cabinet/Pay");

        public async Task<IActionResult> OnPostAsync(string? code, CancellationToken cancellationToken)
        {
            Code = code?.Trim();
            Result<PromoApplied> result = await applyPromo.Handle(new ApplyPromoCodeCommand(UserId, Code ?? string.Empty), cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Description;
                return Page();
            }

            if (result.Value.Type == PromoType.DiscountPercent)
            {
                return RedirectToPage("/Cabinet/Pay");
            }

            Bonus = result.Value;
            return Page();
        }
    }
}
