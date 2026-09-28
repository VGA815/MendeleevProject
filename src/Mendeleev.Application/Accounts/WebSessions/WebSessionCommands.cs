using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Accounts.WebSessions
{
    /// <summary>
    /// What the site checks on every request of a signed-in user: the session is still valid (the stamp
    /// has not changed since sign-in, the account was not merged away) and whether the account is blocked.
    /// </summary>
    public sealed record GetWebAccountQuery(long UserId) : IQuery<WebAccountState>;

    public sealed record WebAccountState(
        long UserId,
        Guid SessionStamp,
        bool IsBlocked,
        bool HasTelegram,
        DateTime? AccountKeyIssuedAt);

    /// <summary>«Выйти на всех устройствах» (FR-WEB-12): every cabinet session of the user ends.</summary>
    public sealed record SignOutEverywhereCommand(long UserId) : ICommand;

    internal sealed class GetWebAccountQueryHandler(IApplicationDbContext db)
        : IQueryHandler<GetWebAccountQuery, WebAccountState>
    {
        public async Task<Result<WebAccountState>> Handle(GetWebAccountQuery query, CancellationToken cancellationToken)
        {
            WebAccountState? state = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == query.UserId)
                .Select(u => new WebAccountState(u.Id, u.SessionStamp, u.Status == UserStatus.Blocked, u.TelegramId != null, u.AccountKeyIssuedAt))
                .FirstOrDefaultAsync(cancellationToken);

            return state is null ? UserErrors.NotFound(query.UserId) : state;
        }
    }

    internal sealed class SignOutEverywhereCommandHandler(IApplicationDbContext db, IDateTimeProvider clock)
        : ICommandHandler<SignOutEverywhereCommand>
    {
        public async Task<Result> Handle(SignOutEverywhereCommand command, CancellationToken cancellationToken)
        {
            User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
            if (user is null)
            {
                return Result.Failure(UserErrors.NotFound(command.UserId));
            }

            user.RotateSessionStamp(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
