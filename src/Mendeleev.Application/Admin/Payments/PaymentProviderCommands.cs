using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Payments;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Payments
{
    /// <summary><c>/aggregator</c>: which aggregators are switched on and which one takes new payments (FR-PAY-14).</summary>
    public sealed record GetPaymentProvidersQuery(long StaffId) : IQuery<PaymentProvidersView>;

    /// <param name="ConfiguredDefault"><c>Payments:ActiveProvider</c> — active until an admin chooses another.</param>
    public sealed record PaymentProvidersView(string Active, string ConfiguredDefault, IReadOnlyList<string> SwitchedOn);

    /// <summary>
    /// An admin makes another switched-on aggregator the one new payments go to first (FR-PAY-14). The choice is
    /// kept in the database, so it survives a restart; payments already created stay with their aggregator.
    /// </summary>
    public sealed record SwitchPaymentProviderCommand(long StaffId, string Code) : ICommand;

    internal sealed class GetPaymentProvidersQueryHandler(
        IApplicationDbContext db,
        IPaymentProviderRegistry registry,
        IStaffAuthorizer authorizer)
        : IQueryHandler<GetPaymentProvidersQuery, PaymentProvidersView>
    {
        public async Task<Result<PaymentProvidersView>> Handle(GetPaymentProvidersQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.SwitchAggregator, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            IPaymentProvider active = await PaymentProviderSelection.ActiveAsync(db, registry, cancellationToken);
            return new PaymentProvidersView(active.Code, registry.Active.Code, registry.All.Select(p => p.Code).ToList());
        }
    }

    internal sealed class SwitchPaymentProviderCommandHandler(
        IApplicationDbContext db,
        IPaymentProviderRegistry registry,
        IStaffAuthorizer authorizer,
        IAlertSink alerts,
        IDateTimeProvider clock)
        : ICommandHandler<SwitchPaymentProviderCommand>
    {
        public async Task<Result> Handle(SwitchPaymentProviderCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.SwitchAggregator, cancellationToken);
            if (staff.IsFailure)
            {
                return Result.Failure(staff.Error);
            }

            if (registry.Find(command.Code) is not IPaymentProvider target)
            {
                return Result.Failure(PaymentErrors.ProviderNotSwitchedOn);
            }

            IPaymentProvider current = await PaymentProviderSelection.ActiveAsync(db, registry, cancellationToken);
            if (ReferenceEquals(current, target))
            {
                return Result.Failure(PaymentErrors.ProviderAlreadyActive);
            }

            DateTime now = clock.UtcNow;
            ServiceSetting? setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == ServiceSetting.ActivePaymentProvider, cancellationToken);
            if (setting is null)
            {
                db.Settings.Add(ServiceSetting.Create(ServiceSetting.ActivePaymentProvider, target.Code, staff.Value.Id, now));
            }
            else
            {
                setting.Set(target.Code, staff.Value.Id, now);
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.PaymentProviderSwitch,
                null,
                Json.Serialize(new { from = current.Code, to = target.Code }),
                now));
            await db.SaveChangesAsync(cancellationToken);

            await alerts.RaiseAsync(new Alert(
                AlertSeverity.Warning,
                $"payment-provider-switch:{now.Ticks}",
                $"Основной агрегатор переключён: {current.Code} → {target.Code} ({staff.Value.DisplayName}). Новые платежи идут через {target.Code}.",
                AlertAudience.TechAdminAndAdmin),
                cancellationToken);

            return Result.Success();
        }
    }
}
