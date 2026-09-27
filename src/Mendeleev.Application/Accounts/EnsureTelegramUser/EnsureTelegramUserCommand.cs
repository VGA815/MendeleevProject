using Mendeleev.Application.Abstractions.Messaging;

namespace Mendeleev.Application.Accounts.EnsureTelegramUser
{
    /// <summary>
    /// Runs for every incoming bot update: finds or creates the account by Telegram ID (FR-ACC-01) and
    /// clears the "blocked the bot" flag, because the user is writing to us again (ТЗ 26).
    /// </summary>
    public sealed record EnsureTelegramUserCommand(long TelegramId) : ICommand<TelegramUserState>;

    public sealed record TelegramUserState(
        long UserId,
        bool IsNew,
        bool IsBlocked,
        bool HasSubscription,
        bool TrialAvailable,
        bool HasAccountKey);
}
