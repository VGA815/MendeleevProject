using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Accounts.SignIn
{
    /// <summary>
    /// Sign-in on the site by the account key (FR-ACC-06, FR-WEB-03). The key is normalized, hashed and
    /// looked up; the attempt limits are enforced by the site (FR-ACC-11). A blocked user still signs in
    /// and sees only the block message (ТЗ 27, «Бизнес-правила»).
    /// </summary>
    public sealed record SignInWithKeyCommand(string AccountKey) : ICommand<WebSession>;

    public sealed record WebSession(long UserId, Guid SessionStamp);

    internal sealed class SignInWithKeyCommandHandler(IApplicationDbContext db, IAccountKeyHasher hasher)
        : ICommandHandler<SignInWithKeyCommand, WebSession>
    {
        public async Task<Result<WebSession>> Handle(SignInWithKeyCommand command, CancellationToken cancellationToken)
        {
            if (!AccountKey.TryParse(command.AccountKey, out AccountKey key))
            {
                return UserErrors.InvalidAccountKey;
            }

            string hash = hasher.Hash(key);
            WebSession? session = await db.Users
                .AsNoTracking()
                .Where(u => u.AccountKeyHash == hash)
                .Select(u => new WebSession(u.Id, u.SessionStamp))
                .FirstOrDefaultAsync(cancellationToken);

            return session is null ? UserErrors.WrongAccountKey : session;
        }
    }
}
