using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Tariffs;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Tariffs
{
    /// <summary>
    /// <c>/tariffs</c>: current tariffs and prices; admins change them with <c>/tariffs price</c> and
    /// <c>/tariffs on|off</c> (FR-ADM-14, <see cref="ChangeTariffPriceCommand"/>).
    /// </summary>
    public sealed record ListTariffsQuery(long StaffId) : IQuery<IReadOnlyList<TariffAdminView>>;

    public sealed record TariffAdminView(string Code, string Name, TariffTier Tier, decimal Price, int PeriodDays, int DeviceLimit, long? TrafficLimitBytes, bool IsActive);

    internal sealed class ListTariffsQueryHandler(IApplicationDbContext db, IStaffAuthorizer authorizer)
        : IQueryHandler<ListTariffsQuery, IReadOnlyList<TariffAdminView>>
    {
        public async Task<Result<IReadOnlyList<TariffAdminView>>> Handle(ListTariffsQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.ViewTariffs, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            List<TariffAdminView> tariffs = await db.Tariffs
                .AsNoTracking()
                .OrderBy(t => t.SortOrder).ThenBy(t => t.PeriodDays)
                .Select(t => new TariffAdminView(t.Code, t.Name, t.Tier, t.Price, t.PeriodDays, t.DeviceLimit, t.TrafficLimitBytes, t.IsActive))
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<TariffAdminView>>(tariffs);
        }
    }
}
