using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Admin.Promos
{
    /// <summary>
    /// <c>/promos new</c>: an admin creates a promo code (FR-ADM-15, ТЗ 22 «Промокоды»). The code is Latin, so it
    /// also works as <c>t.me/&lt;бот&gt;?start=promo_&lt;код&gt;</c>.
    /// </summary>
    public sealed record CreatePromoCodeCommand(
        long StaffId,
        string Code,
        PromoType Type,
        int Value,
        int? MaxUses,
        DateTime? ValidFrom,
        DateTime? ValidTo) : ICommand<PromoCodeView>;

    /// <summary>
    /// <c>/promos off</c>: the code stops working at once — for new entries and for payments not created yet; a
    /// discounted payment already created can still be paid (ТЗ 23: the user gets what they saw at creation).
    /// </summary>
    public sealed record DeactivatePromoCodeCommand(long StaffId, string Code) : ICommand;

    public sealed record PromoCodeView(
        long Id,
        string Code,
        PromoType Type,
        int Value,
        int? MaxUses,
        int UsedCount,
        DateTime? ValidFrom,
        DateTime? ValidTo,
        bool IsActive,
        DateTime CreatedAt)
    {
        internal static PromoCodeView From(PromoCode p) =>
            new(p.Id, p.Code, p.Type, p.Value, p.MaxUses, p.UsedCount, p.ValidFrom, p.ValidTo, p.IsActive, p.CreatedAt);
    }

    internal sealed class CreatePromoCodeCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : ICommandHandler<CreatePromoCodeCommand, PromoCodeView>
    {
        public async Task<Result<PromoCodeView>> Handle(CreatePromoCodeCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.ManagePromos, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            DateTime now = clock.UtcNow;
            Result<PromoCode> created = PromoCode.Create(
                command.Code, command.Type, command.Value, command.MaxUses, command.ValidFrom, command.ValidTo, staff.Value.Id, now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            PromoCode promo = created.Value;
            if (await db.PromoCodes.AnyAsync(p => p.Code == promo.Code, cancellationToken))
            {
                return PromoErrors.CodeExists(promo.Code);
            }

            db.PromoCodes.Add(promo);
            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.PromoCreate,
                null,
                Json.Serialize(new
                {
                    code = promo.Code,
                    type = promo.Type.ToString(),
                    value = promo.Value,
                    maxUses = promo.MaxUses,
                    validFrom = promo.ValidFrom,
                    validTo = promo.ValidTo,
                }),
                now));
            await db.SaveChangesAsync(cancellationToken);

            return PromoCodeView.From(promo);
        }
    }

    internal sealed class DeactivatePromoCodeCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : ICommandHandler<DeactivatePromoCodeCommand>
    {
        public async Task<Result> Handle(DeactivatePromoCodeCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.ManagePromos, cancellationToken);
            if (staff.IsFailure)
            {
                return Result.Failure(staff.Error);
            }

            string? code = PromoCode.Normalize(command.Code);
            long? promoCodeId = code is null
                ? null
                : await db.PromoCodes.Where(p => p.Code == code).Select(p => (long?)p.Id).FirstOrDefaultAsync(cancellationToken);
            if (promoCodeId is null)
            {
                return Result.Failure(PromoErrors.NotFound);
            }

            DateTime now = clock.UtcNow;
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            PromoCode promo = await db.LockPromoCodeAsync(promoCodeId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Promo code {promoCodeId} disappeared.");
            Result deactivated = promo.Deactivate(now);
            if (deactivated.IsFailure)
            {
                return deactivated;
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.PromoDeactivate,
                null,
                Json.Serialize(new { code = promo.Code, usedCount = promo.UsedCount }),
                now));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
    }
}
