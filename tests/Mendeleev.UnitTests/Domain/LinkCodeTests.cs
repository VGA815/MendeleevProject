using Mendeleev.Domain.Users;

namespace Mendeleev.UnitTests.Domain
{
    /// <summary>ТЗ 21: the linking code — 8 digits, 10 minutes, one use; the session stamp of the cabinet.</summary>
    public class LinkCodeTests
    {
        private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("12345678")]
        [InlineData("1234 5678")]
        [InlineData("1234-5678")]
        [InlineData(" 12 34 56 78 ")]
        public void TryNormalize_AcceptsSeparators(string input)
        {
            LinkCode.TryNormalizeTelegramCode(input, out string code).ShouldBeTrue();
            code.ShouldBe("12345678");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1234567")]
        [InlineData("123456789")]
        [InlineData("1234567a")]
        public void TryNormalize_RejectsAnythingElse(string? input)
        {
            LinkCode.TryNormalizeTelegramCode(input, out _).ShouldBeFalse();
        }

        [Fact]
        public void Generate_Produces8Digits()
        {
            HashSet<string> codes = Enumerable.Range(0, 50).Select(_ => LinkCode.GenerateTelegramCode()).ToHashSet();

            codes.Count.ShouldBeGreaterThan(45);
            codes.ShouldAllBe(c => c.Length == 8 && c.All(char.IsAsciiDigit));
        }

        [Fact]
        public void Code_WorksFor10Minutes_AndOnce()
        {
            var code = LinkCode.ForTelegram(1, "hash", Now);

            code.IsUsable(Now.AddMinutes(9)).ShouldBeTrue();
            code.IsUsable(Now.AddMinutes(10)).ShouldBeFalse();

            code.MarkUsed(Now.AddMinutes(1));
            code.IsUsable(Now.AddMinutes(2)).ShouldBeFalse();
        }

        [Fact]
        public void NewKey_EndsTheSessions()
        {
            var user = User.CreateForWeb("old", Now);
            Guid stamp = user.SessionStamp;

            user.SetAccountKey("new", Now.AddDays(1));

            user.SessionStamp.ShouldNotBe(stamp);
            user.SessionStamp.ShouldNotBe(Guid.Empty);
            user.AccountKeyIssuedAt.ShouldBe(Now.AddDays(1));
        }

        [Fact]
        public void Merge_TelegramIntoWeb_CarriesTheTrialMark()
        {
            // Rule 1: the trial is tied to the Telegram ID, so its mark moves with it (ТЗ 21).
            var telegram = User.CreateForTelegram(42, Now);
            telegram.MarkTrialUsed(Now.AddHours(1));
            var web = User.CreateForWeb("hash", Now);
            Guid webStamp = web.SessionStamp;

            web.TakeOverTelegram(telegram, Now.AddHours(2));

            web.TelegramId.ShouldBe(42);
            web.TrialUsed.ShouldBeTrue();
            web.TrialStartedAt.ShouldBe(Now.AddHours(1));
            web.SessionStamp.ShouldBe(webStamp);
        }

        [Fact]
        public void Merge_WebIntoTelegram_TakesTheKey_AndEndsOldSessions()
        {
            var telegram = User.CreateForTelegram(42, Now);
            telegram.SetAccountKey("telegram-key", Now);
            Guid stamp = telegram.SessionStamp;
            var web = User.CreateForWeb("web-key", Now.AddHours(1));

            telegram.TakeOverWebCredentials(web, Now.AddHours(2));

            telegram.AccountKeyHash.ShouldBe("web-key");
            telegram.AccountKeyIssuedAt.ShouldBe(Now.AddHours(1));
            telegram.SessionStamp.ShouldNotBe(stamp);
        }
    }

    /// <summary>ТЗ 24: the panel knows the user only as a pseudonym; Remnawave wants at least 3 characters.</summary>
    public class PanelUsernameTests
    {
        [Theory]
        [InlineData(1, "u01")]
        [InlineData(9, "u09")]
        [InlineData(10, "u10")]
        [InlineData(12345, "u12345")]
        public void Pseudonym_HasAtLeastThreeCharacters_AndParsesBack(long userId, string expected)
        {
            User.PanelUsernameFor(userId).ShouldBe(expected);
            User.TryParsePanelUsername(expected, out long parsed).ShouldBeTrue();
            parsed.ShouldBe(userId);
        }
    }
}
