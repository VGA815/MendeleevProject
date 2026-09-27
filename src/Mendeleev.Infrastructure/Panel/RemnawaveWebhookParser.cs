using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Options;

namespace Mendeleev.Infrastructure.Panel
{
    /// <summary>
    /// Remnawave signs the raw body: <c>X-Remnawave-Signature</c> = hex HMAC-SHA256 with the
    /// <c>WEBHOOK_SECRET_HEADER</c> secret; <c>X-Remnawave-Timestamp</c> is ISO 8601 (FR-PNL-13).
    /// </summary>
    internal sealed class RemnawaveWebhookParser(IOptions<RemnawaveOptions> options, IDateTimeProvider clock) : IPanelWebhookParser
    {
        public const string SignatureHeader = "X-Remnawave-Signature";
        public const string TimestampHeader = "X-Remnawave-Timestamp";

        public PanelWebhookEvent Parse(WebhookRequest request)
        {
            RemnawaveOptions settings = options.Value;
            if (string.IsNullOrEmpty(settings.WebhookSecret))
            {
                throw new WebhookAuthenticationException("Panel webhook secret is not configured.");
            }

            string? signature = request.Header(SignatureHeader);
            if (string.IsNullOrEmpty(signature) || !IsValidSignature(request.Body, signature, settings.WebhookSecret))
            {
                throw new WebhookAuthenticationException("Invalid panel webhook signature.");
            }

            string? timestampHeader = request.Header(TimestampHeader);
            if (!DateTime.TryParse(timestampHeader, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime timestamp)
                && !TryParseUnix(timestampHeader, out timestamp))
            {
                throw new WebhookAuthenticationException("Missing or malformed panel webhook timestamp.");
            }

            if ((clock.UtcNow - timestamp).Duration() > settings.WebhookMaxAge)
            {
                throw new WebhookAuthenticationException("Panel webhook is too old.");
            }

            RemnawaveWebhookDto envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<RemnawaveWebhookDto>(request.Body, RemnawaveClient.JsonOptions)
                    ?? throw new WebhookAuthenticationException("Empty panel webhook.");
            }
            catch (JsonException ex)
            {
                throw new WebhookAuthenticationException($"Unreadable panel webhook: {ex.Message}");
            }

            PanelUser? user = null;
            string? subject = null;

            switch (envelope.Scope)
            {
                case "user":
                    user = envelope.Data.Deserialize<RemnawaveUserDto>(RemnawaveClient.JsonOptions)?.ToPanelUser();
                    subject = user?.Username;
                    break;
                case "user_hwid_devices":
                case "torrent_blocker":
                    if (envelope.Data.TryGetProperty("user", out JsonElement nested))
                    {
                        user = nested.Deserialize<RemnawaveUserDto>(RemnawaveClient.JsonOptions)?.ToPanelUser();
                    }
                    subject = TryGetString(envelope.Data, "node", "name") ?? user?.Username;
                    break;
                case "node":
                    subject = TryGetString(envelope.Data, "name") ?? TryGetString(envelope.Data, "address");
                    break;
                default:
                    subject = TryGetString(envelope.Data, "ip") ?? TryGetString(envelope.Data, "loginAttempt", "ip");
                    break;
            }

            return new PanelWebhookEvent(
                envelope.Scope,
                envelope.Event,
                DateTime.SpecifyKind(envelope.Timestamp, DateTimeKind.Utc),
                user,
                subject);
        }

        internal static bool IsValidSignature(byte[] body, string signatureHex, string secret)
        {
            byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
            byte[] actual;
            try
            {
                actual = Convert.FromHexString(signatureHex.Trim());
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        private static bool TryParseUnix(string? value, out DateTime timestamp)
        {
            timestamp = default;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            {
                return false;
            }

            timestamp = (seconds > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(seconds) : DateTimeOffset.FromUnixTimeSeconds(seconds)).UtcDateTime;
            return true;
        }

        private static string? TryGetString(JsonElement element, params string[] path)
        {
            JsonElement current = element;
            foreach (string segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
    }
}
