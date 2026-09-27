using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Subscriptions.Trial
{
    /// <summary>
    /// FR-SUB-02: one trial per Telegram ID, only for a user with Telegram who never paid and never had a
    /// trial. Web-only users get no trial — for them "one per Telegram ID" does not hold.
    /// </summary>
    public interface ITrialEligibility
    {
        Task<TrialEligibilityResult> CheckAsync(User user, CancellationToken cancellationToken);
    }

    public sealed record TrialEligibilityResult(bool IsEligible, bool HasSubscription);

    internal sealed class TrialEligibility(IApplicationDbContext db) : ITrialEligibility
    {
        public async Task<TrialEligibilityResult> CheckAsync(User user, CancellationToken cancellationToken)
        {
            bool hasSubscription = await db.Subscriptions.AnyAsync(s => s.UserId == user.Id, cancellationToken);

            if (user.TrialUsed || user.IsBlocked || user.TelegramId is null || hasSubscription)
            {
                return new TrialEligibilityResult(false, hasSubscription);
            }

            bool hasPaid = await db.Payments.AnyAsync(
                p => p.UserId == user.Id && (p.Status == PaymentStatus.Succeeded || p.Status == PaymentStatus.Refunded),
                cancellationToken);

            bool trialOffered = await db.Tariffs.AnyAsync(
                t => t.Code == Tariff.TrialCode && t.IsActive,
                cancellationToken);

            return new TrialEligibilityResult(!hasPaid && trialOffered, hasSubscription);
        }
    }
}
