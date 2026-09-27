using Mendeleev.Domain.Users;

namespace Mendeleev.Application.Abstractions.Accounts
{
    /// <summary>
    /// HMAC-SHA256 of the normalized key with a secret pepper: the hash finds the account but the key
    /// cannot be recovered from it (ТЗ 21, решение 24.09).
    /// </summary>
    public interface IAccountKeyHasher
    {
        string Hash(AccountKey key);
    }
}
