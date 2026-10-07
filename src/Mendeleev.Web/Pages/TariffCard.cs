using Mendeleev.Application.Tariffs.GetPurchasableTariffs;

namespace Mendeleev.Web.Pages
{
    /// <summary>A tariff on the public prices page and in the cabinet, where it also has the pay button (FR-WEB-05).</summary>
    /// <param name="FinalPrice">The price with the user's promo discount (FR-PAY-15), when it differs.</param>
    public sealed record TariffCard(TariffView Tariff, bool CanPay, decimal? FinalPrice = null)
    {
        public bool Discounted => FinalPrice is decimal final && final < Tariff.Price;
    }
}
