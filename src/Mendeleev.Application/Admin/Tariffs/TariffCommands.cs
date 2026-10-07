using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Tariffs;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Tariffs
{
    /// <summary>
    /// <c>/tariffs price</c>: an admin sets a new price after a confirmation (FR-ADM-14). Payments already created
    /// keep the amount and the days they were created with (ТЗ 22, «Правило продления»).
    /// </summary>
    public sealed record ChangeTariffPriceCommand(long StaffId, string Code, decimal Price) : ICommand<TariffAdminView>;

    /// <summary>
    /// <c>/tariffs on|off</c>: an admin takes a tariff off sale or back (FR-ADM-14). Switching the trial off stops
    /// new trials; subscriptions on a tariff keep it either way (ТЗ 22, «Бизнес-правила»).
    /// </summary>
    public sealed record SetTariffActiveCommand(long StaffId, string Code, bool IsActive) : ICommand<TariffAdminView>;

    internal sealed class ChangeTariffPriceCommandHandler(IApplicationDbContext db, IStaffAuthorizer authorizer, IDateTimeProvider clock)
        : ICommandHandler<ChangeTariffPriceCommand, TariffAdminView>
    {
        public async Task<Result<TariffAdminView>> Handle(ChangeTariffPriceCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.ManageTariffs, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            Tariff? tariff = await db.Tariffs.FirstOrDefaultAsync(t => t.Code == command.Code, cancellationToken);
            if (tariff is null)
            {
                return TariffErrors.NotFound(command.Code);
            }

            decimal before = tariff.Price;
            Result changed = tariff.ChangePrice(command.Price);
            if (changed.IsFailure)
            {
                return changed.Error;
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.TariffPrice,
                null,
                Json.Serialize(new { code = tariff.Code, before, after = tariff.Price }),
                clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);
            return TariffAdminViews.From(tariff);
        }
    }

    internal sealed class SetTariffActiveCommandHandler(IApplicationDbContext db, IStaffAuthorizer authorizer, IDateTimeProvider clock)
        : ICommandHandler<SetTariffActiveCommand, TariffAdminView>
    {
        public async Task<Result<TariffAdminView>> Handle(SetTariffActiveCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.ManageTariffs, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            Tariff? tariff = await db.Tariffs.FirstOrDefaultAsync(t => t.Code == command.Code, cancellationToken);
            if (tariff is null)
            {
                return TariffErrors.NotFound(command.Code);
            }

            Result changed = tariff.SetActive(command.IsActive);
            if (changed.IsFailure)
            {
                return changed.Error;
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.TariffActivity,
                null,
                Json.Serialize(new { code = tariff.Code, isActive = tariff.IsActive }),
                clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);
            return TariffAdminViews.From(tariff);
        }
    }

    internal static class TariffAdminViews
    {
        public static TariffAdminView From(Tariff t) =>
            new(t.Code, t.Name, t.Tier, t.Price, t.PeriodDays, t.DeviceLimit, t.TrafficLimitBytes, t.IsActive);
    }
}
