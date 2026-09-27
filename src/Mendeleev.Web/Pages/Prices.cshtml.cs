using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Tariffs.GetPurchasableTariffs;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mendeleev.Web.Pages
{
    /// <summary>Tariffs and prices from the database (ТЗ 27, «Публичные страницы»).</summary>
    public sealed class PricesModel(IQueryHandler<GetPurchasableTariffsQuery, IReadOnlyList<TariffView>> tariffs) : PageModel
    {
        public IReadOnlyList<TariffView> Tariffs { get; private set; } = [];

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            Tariffs = (await tariffs.Handle(new GetPurchasableTariffsQuery(), cancellationToken)).Value;
        }
    }
}
