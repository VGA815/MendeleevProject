using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Admin.Staff;

namespace Mendeleev.Web.Bot
{
    /// <summary>Who is talking to the bot and where to answer.</summary>
    internal sealed class BotContext
    {
        public required long ChatId { get; init; }

        public required long TelegramId { get; init; }

        public required TelegramUserState User { get; init; }

        /// <summary>Active staff member, re-read for every update.</summary>
        public StaffIdentity? Staff { get; init; }

        /// <summary>The message with the pressed button: screens edit it instead of adding new ones.</summary>
        public int? CallbackMessageId { get; init; }

        public long UserId => User.UserId;
    }
}
