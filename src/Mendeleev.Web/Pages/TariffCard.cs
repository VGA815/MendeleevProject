using Mendeleev.Application.Tariffs.GetPurchasableTariffs;

namespace Mendeleev.Web.Pages
{
    /// <summary>A tariff on the public prices page and in the cabinet, where it also has the pay button (FR-WEB-05).</summary>
    public sealed record TariffCard(TariffView Tariff, bool CanPay);
}
