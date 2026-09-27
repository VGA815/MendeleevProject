using Mendeleev.Domain.Tariffs;

namespace Mendeleev.UnitTests
{
    internal static class TestData
    {
        public static readonly Guid BasicSquad = Guid.Parse("11111111-1111-1111-1111-111111111111");

        public static Tariff Trial() =>
            Tariff.Create("trial", "Пробный", TariffTier.Trial, 0, 2, 3, 10L * 1024 * 1024 * 1024, [BasicSquad], true, 0);

        public static Tariff Basic1M() =>
            Tariff.Create("basic_1m", "Базовый, 1 месяц", TariffTier.Basic, 199, 30, 3, null, [BasicSquad], true, 10);

        /// <summary>Moscow wall-clock time → UTC, to write the ТЗ examples as they are written there.</summary>
        public static DateTime Msk(int month, int day, int hour, int minute = 0) =>
            new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-3);
    }
}
