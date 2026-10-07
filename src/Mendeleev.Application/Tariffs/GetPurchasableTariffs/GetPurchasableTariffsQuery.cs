using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Tariffs;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Tariffs.GetPurchasableTariffs
{
    /// <summary>The tariff list for «Купить / Продлить» and the prices page.</summary>
    public sealed record GetPurchasableTariffsQuery : IQuery<IReadOnlyList<TariffView>>;

    public sealed record TariffView(
        string Code,
        string Name,
        TariffTier Tier,
        decimal Price,
        int PeriodDays,
        int DeviceLimit,
        long? TrafficLimitBytes,
        decimal MonthlyEquivalent);

    internal sealed class GetPurchasableTariffsQueryHandler(IApplicationDbContext db)
        : IQueryHandler<GetPurchasableTariffsQuery, IReadOnlyList<TariffView>>
    {
        public async Task<Result<IReadOnlyList<TariffView>>> Handle(GetPurchasableTariffsQuery query, CancellationToken cancellationToken) =>
            Result.Success(await PurchasableTariffs.LoadAsync(db, cancellationToken));
    }

    internal static class PurchasableTariffs
    {
        public static async Task<IReadOnlyList<TariffView>> LoadAsync(IApplicationDbContext db, CancellationToken cancellationToken)
        {
            List<Tariff> tariffs = await db.Tariffs
                .AsNoTracking()
                .Where(t => t.IsActive && t.Tier != TariffTier.Trial && t.Price > 0)
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.PeriodDays)
                .ToListAsync(cancellationToken);

            return tariffs
                .Select(t => new TariffView(t.Code, t.Name, t.Tier, t.Price, t.PeriodDays, t.DeviceLimit, t.TrafficLimitBytes, t.MonthlyEquivalent))
                .ToList();
        }
    }
}
