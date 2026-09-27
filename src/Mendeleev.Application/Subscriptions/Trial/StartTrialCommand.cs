using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Subscriptions.Trial
{
    /// <summary>
    /// «Попробовать бесплатно». The trial flag and the subscription are written in one transaction
    /// under the user's lock, so a double or parallel click cannot produce a second trial (FR-SUB-02).
    /// The link arrives with the «Доступ выдан» message once the panel user exists.
    /// </summary>
    public sealed record StartTrialCommand(long UserId) : ICommand;

    internal sealed class StartTrialCommandHandler(
        IApplicationDbContext db,
        ITrialEligibility eligibility,
        IDateTimeProvider clock)
        : ICommandHandler<StartTrialCommand>
    {
        public async Task<Result> Handle(StartTrialCommand command, CancellationToken cancellationToken)
        {
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            User? user = await db.LockUserAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return Result.Failure(UserErrors.NotFound(command.UserId));
            }
            if (user.IsBlocked)
            {
                return Result.Failure(UserErrors.Blocked);
            }
            if (user.TrialUsed)
            {
                return Result.Failure(SubscriptionErrors.TrialAlreadyUsed);
            }

            TrialEligibilityResult result = await eligibility.CheckAsync(user, cancellationToken);
            if (!result.IsEligible)
            {
                return Result.Failure(SubscriptionErrors.TrialUnavailable);
            }

            Tariff? trial = await db.Tariffs.FirstOrDefaultAsync(t => t.Code == Tariff.TrialCode && t.IsActive, cancellationToken);
            if (trial is null)
            {
                return Result.Failure(TariffErrors.TrialTariffMissing);
            }

            DateTime now = clock.UtcNow;
            db.Subscriptions.Add(Subscription.StartTrial(user.Id, trial, now));
            user.MarkTrialUsed(now);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
    }
}
