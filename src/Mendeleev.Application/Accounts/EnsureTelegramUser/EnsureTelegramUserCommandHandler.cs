using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;

namespace Mendeleev.Application.Accounts.EnsureTelegramUser
{
    internal sealed class EnsureTelegramUserCommandHandler(
        IApplicationDbContext db,
        ITrialEligibility trialEligibility,
        IDateTimeProvider clock)
        : ICommandHandler<EnsureTelegramUserCommand, TelegramUserState>
    {
        public async Task<Result<TelegramUserState>> Handle(EnsureTelegramUserCommand command, CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            (User user, bool created) = await db.GetOrCreateTelegramUserAsync(command.TelegramId, now, cancellationToken);

            if (user.BotBlocked)
            {
                user.MarkBotReachable(now);
                await db.SaveChangesAsync(cancellationToken);
            }

            TrialEligibilityResult eligibility = await trialEligibility.CheckAsync(user, cancellationToken);

            return new TelegramUserState(
                user.Id,
                created,
                user.IsBlocked,
                eligibility.HasSubscription,
                eligibility.IsEligible,
                user.AccountKeyHash is not null);
        }
    }
}
