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

        /// <summary>
        /// HMAC of a one-time code (ТЗ 21: «в БД — только хеш»). With the pepper, a database dump does not
        /// let anyone enumerate the 10⁸ possible linking codes offline.
        /// </summary>
        string HashLinkCode(string code);
    }
}
