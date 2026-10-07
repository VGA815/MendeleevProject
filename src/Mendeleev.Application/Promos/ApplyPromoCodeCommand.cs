using FluentValidation;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Promos
{
    /// <summary>
    /// A user enters a promo code — in the bot («Ввести промокод», <c>/promo</c>, <c>/start promo_&lt;код&gt;</c>) or
    /// in the cabinet (FR-SUB-15, FR-BOT-20). A discount is remembered for the next payment and counts only once
    /// that payment succeeds; bonus days are added at once by FR-SUB-04, and a user without a subscription gets an
    /// active one on the Basic tariff (ТЗ 22, «Промокоды»). Once per user, within the window and the limit.
    /// </summary>
    public sealed record ApplyPromoCodeCommand(long UserId, string Code) : ICommand<PromoApplied>;

    /// <param name="ExpiresAt">For bonus days — the new end of the subscription.</param>
    public sealed record PromoApplied(string Code, PromoType Type, int Value, DateTime? ExpiresAt);

    internal sealed class ApplyPromoCodeCommandValidator : AbstractValidator<ApplyPromoCodeCommand>
    {
        public ApplyPromoCodeCommandValidator()
        {
            // Anything of a sane length goes on to the lookup: a wrong format reads as «not found» there.
            RuleFor(x => x.Code).Must(c => c?.Trim().Length is > 0 and <= 64).WithMessage(PromoErrors.NotFound.Description);
        }
    }

    internal sealed class ApplyPromoCodeCommandHandler(
        IApplicationDbContext db,
        IDateTimeProvider clock,
        IOptions<SubscriptionOptions> options)
        : ICommandHandler<ApplyPromoCodeCommand, PromoApplied>
    {
        public async Task<Result<PromoApplied>> Handle(ApplyPromoCodeCommand command, CancellationToken cancellationToken)
        {
            if (PromoCode.Normalize(command.Code) is not string code)
            {
                return PromoErrors.NotFound;
            }

            DateTime now = clock.UtcNow;
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            User? user = await db.LockUserAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }
            if (user.IsBlocked)
            {
                return UserErrors.Blocked;
            }

            long? promoCodeId = await db.PromoCodes
                .Where(p => p.Code == code)
                .Select(p => (long?)p.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (promoCodeId is null)
            {
                return PromoErrors.NotFound;
            }

            // Under the code's lock the limit check and the count are one step: the last bonus activation goes to one user.
            PromoCode promo = await db.LockPromoCodeAsync(promoCodeId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Promo code {promoCodeId} disappeared.");

            Result usable = promo.CheckUsable(now);
            if (usable.IsFailure)
            {
                return usable.Error;
            }
            if (await db.PromoRedemptions.AnyAsync(r => r.PromoCodeId == promo.Id && r.UserId == user.Id, cancellationToken))
            {
                return PromoErrors.AlreadyUsed;
            }

            if (promo.Type == PromoType.DiscountPercent)
            {
                user.SelectPromo(promo.Id, now);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PromoApplied(promo.Code, promo.Type, promo.Value, null);
            }

            if (user.TelegramId is null)
            {
                return PromoErrors.BonusNeedsTelegram;
            }

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

            Tariff? paidTariff = subscription is { Tariff.IsTrial: false }
                ? subscription.Tariff
                : await db.Tariffs.FirstOrDefaultAsync(t => t.Code == options.Value.PromoBonusTariffCode, cancellationToken);
            if (paidTariff is null || paidTariff.IsTrial)
            {
                return PromoErrors.BonusTariffMissing;
            }

            // One bonus per user and code: the notice of this redemption is unique.
            string noticeKey = $"promo:{promo.Id}";
            DateTime? before = subscription?.ExpiresAt;

            if (subscription is null)
            {
                subscription = Subscription.CreateFromPromo(user.Id, paidTariff, promo.Value, noticeKey, now);
                db.Subscriptions.Add(subscription);
            }
            else
            {
                subscription.ApplyBonusDays(paidTariff, promo.Value, noticeKey, now);
            }

            promo.RegisterUse(now);
            db.PromoRedemptions.Add(PromoRedemption.ForBonus(promo.Id, user.Id, promo.Value, now));
            db.AuditLog.Add(AuditLogEntry.BySystem(
                AuditActions.PromoBonus,
                user.Id,
                Json.Serialize(new { code = promo.Code, days = promo.Value, before, after = subscription.ExpiresAt }),
                now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PromoApplied(promo.Code, promo.Type, promo.Value, subscription.ExpiresAt);
        }
    }
}
