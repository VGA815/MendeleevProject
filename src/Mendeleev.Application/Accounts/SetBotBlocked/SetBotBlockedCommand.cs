using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Accounts.SetBotBlocked
{
    /// <summary>From <c>my_chat_member</c>: the user blocked or unblocked the bot (FR-BOT-14).</summary>
    public sealed record SetBotBlockedCommand(long TelegramId, bool Blocked) : ICommand;

    internal sealed class SetBotBlockedCommandHandler(IApplicationDbContext db, IDateTimeProvider clock)
        : ICommandHandler<SetBotBlockedCommand>
    {
        public async Task<Result> Handle(SetBotBlockedCommand command, CancellationToken cancellationToken)
        {
            User? user = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == command.TelegramId, cancellationToken);
            if (user is null)
            {
                return Result.Success();
            }

            if (command.Blocked)
            {
                user.MarkBotBlocked(clock.UtcNow);
            }
            else
            {
                user.MarkBotReachable(clock.UtcNow);
            }

            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
