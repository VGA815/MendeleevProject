using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mendeleev.Infrastructure.Payments.TryBit
{
    /// <summary>
    /// The token of a TryBit POSTBACK: a JWT signed with the project's SECRET KEY by HS256, valid for 5 minutes after
    /// the notification is sent (docs.trybit.com, «Automatic POSTBACK»; FR-PAY-02). Only HS256 is accepted, never
    /// <c>none</c> or whatever else the token's own header names. <c>exp</c> and <c>nbf</c> are checked when present.
    /// </summary>
    internal static class TryBitToken
    {
        /// <summary>How far our clock and TryBit's may disagree.</summary>
        public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

        public static bool IsValid(string token, string secretKey, DateTime utcNow)
        {
            string[] parts = token.Trim().Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            byte[]? header = Decode(parts[0]);
            byte[]? payload = Decode(parts[1]);
            byte[]? signature = Decode(parts[2]);
            if (header is null || payload is null || signature is null)
            {
                return false;
            }

            byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            return CryptographicOperations.FixedTimeEquals(expected, signature)
                && IsHs256(header)
                && IsCurrent(payload, (utcNow - DateTime.UnixEpoch).TotalSeconds);
        }

        private static bool IsHs256(byte[] header)
        {
            using JsonDocument? document = Parse(header);
            return document is not null
                && document.RootElement.TryGetProperty("alg", out JsonElement alg)
                && alg.ValueKind == JsonValueKind.String
                && alg.GetString() == "HS256";
        }

        private static bool IsCurrent(byte[] payload, double now)
        {
            using JsonDocument? document = Parse(payload);
            if (document is null)
            {
                return false;
            }

            double skew = ClockSkew.TotalSeconds;
            double? expiresAt = Seconds(document.RootElement, "exp");
            double? notBefore = Seconds(document.RootElement, "nbf");

            // NaN, a claim that is not a number, fails both comparisons.
            return (expiresAt is null || now <= expiresAt + skew)
                && (notBefore is null || now >= notBefore - skew);
        }

        /// <summary>Null when the claim is absent, NaN when it is not a number of seconds.</summary>
        private static double? Seconds(JsonElement claims, string name)
        {
            if (!claims.TryGetProperty(name, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) => seconds,
                _ => double.NaN,
            };
        }

        private static JsonDocument? Parse(byte[] json)
        {
            try
            {
                JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return document;
                }

                document.Dispose();
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static byte[]? Decode(string part)
        {
            try
            {
                return Base64Url.DecodeFromChars(part);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
