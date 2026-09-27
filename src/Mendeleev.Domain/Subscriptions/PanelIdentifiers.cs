using System.Security.Cryptography;

namespace Mendeleev.Domain.Subscriptions
{
    /// <summary>
    /// Identifiers that the service generates for the panel user. The short UUID is the secret part of
    /// the subscription link; Remnawave accepts 16–64 characters.
    /// </summary>
    public static class PanelIdentifiers
    {
        public const int ShortUuidLength = 24;

        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        public static string NewShortUuid() =>
            RandomNumberGenerator.GetString(Alphabet, ShortUuidLength);
    }
}
