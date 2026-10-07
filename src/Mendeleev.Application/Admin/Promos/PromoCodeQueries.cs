using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Promos
{
    /// <summary><c>/promos</c>: the codes with their use counts (FR-ADM-15), active ones first.</summary>
    public sealed record ListPromoCodesQuery(long StaffId) : IQuery<IReadOnlyList<PromoCodeView>>;

    /// <summary><c>/promos &lt;код&gt;</c>: usage statistics of one code (FR-ADM-15).</summary>
    public sealed record GetPromoCodeQuery(long StaffId, string Code) : IQuery<PromoCodeDetails>;

    /// <param name="PaidPayments">Succeeded payments with the discount and their sum.</param>
    /// <param name="WaitingUsers">Users who entered the discount and have not paid yet.</param>
    public sealed record PromoCodeDetails(
        PromoCodeView Promo,
        int PaidPayments,
        decimal PaidSum,
        int BonusDaysGiven,
        int WaitingUsers,
        IReadOnlyList<PromoUseView> LastUses);

    public sealed record PromoUseView(long UserId, DateTime At, decimal? Amount, int? BonusDays);

    internal sealed class ListPromoCodesQueryHandler(IApplicationDbContext db, IStaffAuthorizer authorizer)
        : IQueryHandler<ListPromoCodesQuery, IReadOnlyList<PromoCodeView>>
    {
        private const int Shown = 30;

        public async Task<Result<IReadOnlyList<PromoCodeView>>> Handle(ListPromoCodesQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.ManagePromos, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            List<PromoCode> promos = await db.PromoCodes
                .AsNoTracking()
                .OrderByDescending(p => p.IsActive)
                .ThenByDescending(p => p.CreatedAt)
                .Take(Shown)
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<PromoCodeView>>(promos.Select(PromoCodeView.From).ToList());
        }
    }

    internal sealed class GetPromoCodeQueryHandler(IApplicationDbContext db, IStaffAuthorizer authorizer)
        : IQueryHandler<GetPromoCodeQuery, PromoCodeDetails>
    {
        private const int LastUses = 10;

        public async Task<Result<PromoCodeDetails>> Handle(GetPromoCodeQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.ManagePromos, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            string? code = PromoCode.Normalize(query.Code);
            PromoCode? promo = code is null
                ? null
                : await db.PromoCodes.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, cancellationToken);
            if (promo is null)
            {
                return PromoErrors.NotFound;
            }

            IQueryable<Payment> paid = db.Payments.Where(p => p.PromoCodeId == promo.Id && p.Status == PaymentStatus.Succeeded);
            int paidCount = await paid.CountAsync(cancellationToken);
            decimal paidSum = await paid.SumAsync(p => p.Amount, cancellationToken);

            int bonusDays = await db.PromoRedemptions
                .Where(r => r.PromoCodeId == promo.Id)
                .SumAsync(r => r.BonusDays ?? 0, cancellationToken);

            int waiting = await db.Users.CountAsync(u => u.SelectedPromoCodeId == promo.Id, cancellationToken);

            List<PromoUseView> lastUses = await (
                    from r in db.PromoRedemptions.AsNoTracking()
                    where r.PromoCodeId == promo.Id
                    join p in db.Payments.AsNoTracking() on r.PaymentId equals (Guid?)p.Id into payments
                    from p in payments.DefaultIfEmpty()
                    orderby r.CreatedAt descending
                    select new PromoUseView(r.UserId, r.CreatedAt, p != null ? (decimal?)p.Amount : null, r.BonusDays))
                .Take(LastUses)
                .ToListAsync(cancellationToken);

            return new PromoCodeDetails(
                PromoCodeView.From(promo),
                paidCount,
                paidSum,
                bonusDays,
                waiting,
                lastUses);
        }
    }
}
