using Mendeleev.Domain.Common;
using Mendeleev.Domain.Promos;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Handlers;
using Mendeleev.Web.Bot.Infrastructure;

namespace Mendeleev.UnitTests.Web
{
    public class BotTests
    {
        [Theory]
        [InlineData("/start", "/start", "")]
        [InlineData("/find 123456", "/find", "123456")]
        [InlineData("/find@my_bot  u42 ", "/find", "u42")]
        [InlineData("/STAFF add 1 support Иван", "/staff", "add 1 support Иван")]
        [InlineData("просто текст", "", "просто текст")]
        public void ParseCommand(string text, string command, string arguments)
        {
            UpdateRouter.ParseCommand(text.Trim()).ShouldBe((command, arguments));
        }

        [Theory]
        [InlineData("300 перевод на карту 05.10", true, 300, "перевод на карту 05.10")]
        [InlineData("  1500₽   наличными  ", true, 1500, "наличными")]
        [InlineData("300\nперевод на карту", true, 300, "перевод на карту")]
        [InlineData("300", false, 0, "")]
        [InlineData("300 ок", false, 0, "")]
        [InlineData("перевод 300", false, 0, "")]
        [InlineData("199.50 перевод на карту", false, 0, "")]
        [InlineData("-5 перевод на карту", false, 0, "")]
        [InlineData("0 перевод на карту", false, 0, "")]
        public void ManualPayment_AmountFirst_ThenTheComment(string text, bool parsed, int amount, string comment)
        {
            StaffHandler.TryParseManualPayment(text, out decimal rubles, out string rest).ShouldBe(parsed);
            rubles.ShouldBe(amount);
            rest.ShouldBe(comment);
        }

        [Theory]
        [InlineData("AUTUMN 20%", "AUTUMN", PromoType.DiscountPercent, 20, null, null, null)]
        [InlineData("gift 7д", "gift", PromoType.BonusDays, 7, null, null, null)]
        [InlineData("GIFT +14дн 50", "GIFT", PromoType.BonusDays, 14, 50, null, null)]
        [InlineData("GIFT 3d 31.10.2026", "GIFT", PromoType.BonusDays, 3, null, null, "01.11.2026")]
        [InlineData("SALE 15% 100 31.10.2026 10.10.2026", "SALE", PromoType.DiscountPercent, 15, 100, "10.10.2026", "01.11.2026")]
        public void Promos_New_CodeValueThenLimitAndDates(string spec, string code, PromoType type, int value, int? maxUses, string? from, string? to)
        {
            StaffHandler.TryParsePromo(spec.Split(' '), out PromoDraft? draft, out string problem).ShouldBeTrue(problem);

            draft!.Code.ShouldBe(code);
            draft.Type.ShouldBe(type);
            draft.Value.ShouldBe(value);
            draft.MaxUses.ShouldBe(maxUses);
            // Moscow dates: «до 31.10» runs through the 31st, so the end is midnight of 01.11 in Moscow.
            draft.ValidFrom.ShouldBe(from is null ? null : MoscowMidnight(from));
            draft.ValidTo.ShouldBe(to is null ? null : MoscowMidnight(to));
        }

        [Theory]
        [InlineData("AUTUMN")]
        [InlineData("AUTUMN 20")]
        [InlineData("AUTUMN 20% 1 2")]
        [InlineData("AUTUMN 20% завтра")]
        [InlineData("AUTUMN 7 дней")]
        public void Promos_New_RefusesWhatItCannotRead(string spec)
        {
            StaffHandler.TryParsePromo(spec.Split(' '), out _, out string problem).ShouldBeFalse();
            problem.ShouldNotBeEmpty();
        }

        private static DateTime MoscowMidnight(string date) =>
            MoscowTime.StartOfDayUtc(DateOnly.ParseExact(date, "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture));

        [Fact]
        public void RateLimiter_Allows20PerMinute_WarnsOnce_ThenDrops()
        {
            // FR-BOT-15: 30 clicks a minute are not processed beyond the limit.
            var clock = new MovableClock(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
            var limiter = new BotRateLimiter(clock);

            Enumerable.Range(0, 20).Select(_ => limiter.Check(1)).ShouldAllBe(d => d == RateDecision.Allow);
            limiter.Check(1).ShouldBe(RateDecision.Warn);
            Enumerable.Range(0, 9).Select(_ => limiter.Check(1)).ShouldAllBe(d => d == RateDecision.Drop);
            limiter.Check(2).ShouldBe(RateDecision.Allow);

            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            limiter.Check(1).ShouldBe(RateDecision.Allow);
        }

        [Fact]
        public void TextRenderer_EncodesValues_NotTemplates()
        {
            string text = TextRenderer.Render("<b>{name}</b> до {date}", ("name", "<script>"), ("date", new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc)));

            text.ShouldBe("<b>&lt;script&gt;</b> до 09.11.2026 12:00");
        }

        private sealed class MovableClock(DateTime start) : IDateTimeProvider
        {
            public DateTime UtcNow { get; set; } = start;
        }
    }
}
