using System.Security.Cryptography;
using System.Text;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.Infrastructure.Accounts;
using Mendeleev.Infrastructure.Database;
using Mendeleev.Infrastructure.Outbox;
using Mendeleev.Infrastructure.Panel;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Options;

namespace Mendeleev.UnitTests.Infrastructure
{
    public class InfrastructureTests
    {
        [Theory]
        [InlineData(SubscriptionStatus.Active, "active")]
        [InlineData(NotificationKind.Expiry3d, "expiry_3d")]
        [InlineData(NotificationKind.TrialTrafficExhausted, "trial_traffic_exhausted")]
        [InlineData(StaffRole.TechAdmin, "tech_admin")]
        public void Enums_AreStoredAsSnakeCase(object value, string expected)
        {
            SnakeCaseEnumConverterProbe.ToSnakeCase(value.ToString()!).ShouldBe(expected);
        }

        [Fact]
        public void EnumConverter_RoundTrips()
        {
            foreach (SubscriptionStatus status in Enum.GetValues<SubscriptionStatus>())
            {
                SnakeCaseEnumConverter<SubscriptionStatus>.FromProvider(SnakeCaseEnumConverter<SubscriptionStatus>.ToProvider(status)).ShouldBe(status);
            }
        }

        [Fact]
        public void OutboxRetries_FollowTheScheduleThenGiveUp()
        {
            // ТЗ 24: 2 s, 5 s, 15 s, 1 min, 5 min, then every 15 min for an hour.
            OutboxRetrySchedule.DelayAfter(1).ShouldBe(TimeSpan.FromSeconds(2));
            OutboxRetrySchedule.DelayAfter(4).ShouldBe(TimeSpan.FromMinutes(1));
            OutboxRetrySchedule.DelayAfter(9).ShouldBe(TimeSpan.FromMinutes(15));
            OutboxRetrySchedule.DelayAfter(10).ShouldBeNull();
        }

        [Fact]
        public void AccountKeyHash_IsDeterministic_AndDependsOnPepper()
        {
            AccountKey.TryParse("1234 5678 9012 3456", out AccountKey key);
            var a = new HmacAccountKeyHasher(Options.Create(new AccountOptions { KeyPepper = Convert.ToBase64String(new byte[32]) }));
            var b = new HmacAccountKeyHasher(Options.Create(new AccountOptions { KeyPepper = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) }));

            a.Hash(key).ShouldBe(a.Hash(key));
            a.Hash(key).ShouldNotBe(b.Hash(key));
            a.Hash(key).ShouldNotContain("1234567890123456");
        }

        [Fact]
        public void AccountKeyHasher_RefusesAShortPepper()
        {
            Should.Throw<InvalidOperationException>(() =>
                new HmacAccountKeyHasher(Options.Create(new AccountOptions { KeyPepper = Convert.ToBase64String(new byte[8]) })));
        }

        [Fact]
        public void RemnawaveWebhook_AcceptsValidSignature_RejectsTamperedAndStale()
        {
            const string secret = "panel-secret";
            var clock = new FixedClock(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
            var parser = new RemnawaveWebhookParser(Options.Create(new RemnawaveOptions { WebhookSecret = secret }), clock);

            byte[] body = Encoding.UTF8.GetBytes("""{"scope":"user","event":"user.first_connected","timestamp":"2026-10-01T12:00:00Z","data":{"id":5,"username":"u42","shortUuid":"abc","vlessUuid":"7a1d4c7e-9f0b-4c7e-9f0b-3a6d1e2f4b5c","status":"ACTIVE","expireAt":"2026-11-01T12:00:00Z","trafficLimitBytes":0,"userTraffic":{"usedTrafficBytes":0,"lifetimeUsedTrafficBytes":0,"firstConnectedAt":"2026-10-01T11:59:00Z"}}}""");

            PanelWebhookEventProbe(parser, body, Sign(body, secret), "2026-10-01T11:58:00Z").User!.Username.ShouldBe("u42");

            Should.Throw<WebhookAuthenticationException>(() => PanelWebhookEventProbe(parser, body, Sign(body, "wrong"), "2026-10-01T11:58:00Z"));
            Should.Throw<WebhookAuthenticationException>(() => PanelWebhookEventProbe(parser, body, Sign(body, secret), "2026-10-01T11:50:00Z"));
        }

        private static Mendeleev.Application.Abstractions.Panel.PanelWebhookEvent PanelWebhookEventProbe(RemnawaveWebhookParser parser, byte[] body, string signature, string timestamp) =>
            parser.Parse(new WebhookRequest(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [RemnawaveWebhookParser.SignatureHeader] = signature,
                    [RemnawaveWebhookParser.TimestampHeader] = timestamp,
                },
                body,
                "10.0.0.1"));

        private static string Sign(byte[] body, string secret) =>
            Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

        private sealed class FixedClock(DateTime utcNow) : IDateTimeProvider
        {
            public DateTime UtcNow { get; } = utcNow;
        }

        private static class SnakeCaseEnumConverterProbe
        {
            public static string ToSnakeCase(string name) => SnakeCaseEnumConverter<SubscriptionStatus>.ToSnakeCase(name);
        }
    }
}
