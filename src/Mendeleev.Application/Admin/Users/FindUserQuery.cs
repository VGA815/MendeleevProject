using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Users
{
    /// <summary>
    /// <c>/find</c> (FR-ADM-02): by Telegram ID, account key, our or the aggregator's payment id, or user
    /// id (<c>u123</c>). Every view of a card is written to the audit log (FR-ADM-13).
    /// </summary>
    /// <remarks>A command, not a query: looking at a card leaves a trace.</remarks>
    public sealed record FindUserCommand(long StaffId, string Query) : ICommand<UserCard>;

    /// <summary>Opens the card of a known user (after an action, «Обновить»). Also audited.</summary>
    public sealed record GetUserCardCommand(long StaffId, long UserId) : ICommand<UserCard>;

    internal static class FindUserErrors
    {
        public static readonly Error NotFound = Error.NotFound(
            "Admin.UserNotFound",
            "Не найдено. Форматы: Telegram ID, ключ из 16 цифр, id платежа (наш или агрегатора), u123 — id пользователя.");
    }

    internal sealed class FindUserCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IAccountKeyHasher hasher,
        IUserCardBuilder cards,
        IDateTimeProvider clock)
        : ICommandHandler<FindUserCommand, UserCard>, ICommandHandler<GetUserCardCommand, UserCard>
    {
        public async Task<Result<UserCard>> Handle(FindUserCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.FindUsers, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            long? userId = await ResolveAsync(command.Query.Trim(), cancellationToken);
            if (userId is null)
            {
                return FindUserErrors.NotFound;
            }

            return await OpenAsync(command.StaffId, userId.Value, "find", cancellationToken);
        }

        public async Task<Result<UserCard>> Handle(GetUserCardCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.FindUsers, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            return await OpenAsync(command.StaffId, command.UserId, "card", cancellationToken);
        }

        private async Task<Result<UserCard>> OpenAsync(long staffId, long userId, string via, CancellationToken cancellationToken)
        {
            UserCard? card = await cards.BuildAsync(userId, cancellationToken);
            if (card is null)
            {
                return FindUserErrors.NotFound;
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(staffId, AuditActions.UserView, userId, Json.Serialize(new { via }), clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);
            return card;
        }

        private async Task<long?> ResolveAsync(string query, CancellationToken cancellationToken)
        {
            if (query.Length == 0)
            {
                return null;
            }

            if ((query[0] is 'u' or 'U' or '#') && long.TryParse(query.AsSpan(1), out long explicitUserId))
            {
                return await db.Users.AnyAsync(u => u.Id == explicitUserId, cancellationToken) ? explicitUserId : null;
            }

            if (AccountKey.TryParse(query, out AccountKey key))
            {
                string hash = hasher.Hash(key);
                return await db.Users.Where(u => u.AccountKeyHash == hash).Select(u => (long?)u.Id).FirstOrDefaultAsync(cancellationToken);
            }

            if (Guid.TryParse(query, out Guid paymentId))
            {
                long? byOrder = await db.Payments.Where(p => p.Id == paymentId).Select(p => (long?)p.UserId).FirstOrDefaultAsync(cancellationToken);
                if (byOrder is not null)
                {
                    return byOrder;
                }
            }

            if (long.TryParse(query, out long telegramId))
            {
                long? byTelegram = await db.Users.Where(u => u.TelegramId == telegramId).Select(u => (long?)u.Id).FirstOrDefaultAsync(cancellationToken);
                if (byTelegram is not null)
                {
                    return byTelegram;
                }
            }

            return await db.Payments
                .Where(p => p.ProviderPaymentId == query)
                .Select(p => (long?)p.UserId)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
