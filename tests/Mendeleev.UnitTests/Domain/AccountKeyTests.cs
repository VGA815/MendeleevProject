using Mendeleev.Domain.Users;

namespace Mendeleev.UnitTests.Domain
{
    /// <summary>ТЗ 21: 16 digits, entered with or without spaces and dashes.</summary>
    public class AccountKeyTests
    {
        [Theory]
        [InlineData("1234567890123456")]
        [InlineData("1234 5678 9012 3456")]
        [InlineData("1234-5678-9012-3456")]
        [InlineData("  1234 5678-9012 3456 ")]
        public void TryParse_NormalizesSeparators(string input)
        {
            AccountKey.TryParse(input, out AccountKey key).ShouldBeTrue();
            key.Digits.ShouldBe("1234567890123456");
            key.ToDisplayString().ShouldBe("1234 5678 9012 3456");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("123456789012345")]
        [InlineData("12345678901234567")]
        [InlineData("1234 5678 9012 345a")]
        [InlineData("1234_5678_9012_3456")]
        public void TryParse_RejectsAnythingElse(string? input)
        {
            AccountKey.TryParse(input, out _).ShouldBeFalse();
        }

        [Fact]
        public void Generate_Produces16Digits_AndIsRandom()
        {
            HashSet<string> keys = Enumerable.Range(0, 100).Select(_ => AccountKey.Generate().Digits).ToHashSet();

            keys.Count.ShouldBe(100);
            keys.ShouldAllBe(k => k.Length == 16 && k.All(char.IsAsciiDigit));
        }

        [Fact]
        public void ToString_NeverRevealsTheKey()
        {
            AccountKey.TryParse("1234567890123456", out AccountKey key);

            key.ToString().ShouldNotContain("1234");
        }
    }
}
