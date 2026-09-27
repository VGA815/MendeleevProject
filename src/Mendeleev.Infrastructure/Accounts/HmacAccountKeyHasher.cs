using System.Security.Cryptography;
using System.Text;
using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Domain.Users;
using Microsoft.Extensions.Options;

namespace Mendeleev.Infrastructure.Accounts
{
    public sealed class AccountOptions
    {
        public const string SectionName = "Accounts";

        /// <summary>
        /// Base64 secret "pepper" for the key HMAC. Never rotated without reissuing every key; losing it
        /// means every web user loses sign-in by key, so it must be in the secrets backup (ТЗ 31).
        /// </summary>
        public string KeyPepper { get; init; } = string.Empty;
    }

    internal sealed class HmacAccountKeyHasher(IOptions<AccountOptions> options) : IAccountKeyHasher
    {
        private readonly byte[] _pepper = Decode(options.Value.KeyPepper);

        public string Hash(AccountKey key) =>
            Convert.ToHexStringLower(HMACSHA256.HashData(_pepper, Encoding.ASCII.GetBytes(key.Digits)));

        private static byte[] Decode(string pepper)
        {
            if (string.IsNullOrWhiteSpace(pepper))
            {
                throw new InvalidOperationException("Accounts:KeyPepper is not configured.");
            }

            byte[] bytes = Convert.FromBase64String(pepper);
            if (bytes.Length < 32)
            {
                throw new InvalidOperationException("Accounts:KeyPepper must be at least 32 bytes.");
            }

            return bytes;
        }
    }
}
