using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Tariffs;

namespace Mendeleev.Infrastructure.Seeding
{
    /// <summary>
    /// An initial tariff from <c>Seed:Tariffs</c>. Inserted when the code is missing and never
    /// overwritten: after the first start the database is the source of truth and prices are changed
    /// there (ТЗ 28, «Тарифы и цены»).
    /// </summary>
    public sealed class TariffSeed
    {
        public const string SectionName = "Seed:Tariffs";

        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public TariffTier Tier { get; init; }
        public decimal Price { get; init; }
        public int PeriodDays { get; init; }
        public int DeviceLimit { get; init; }
        public int? TrafficLimitGb { get; init; }

        /// <summary>Squad names from <c>Remnawave:Squads</c> (e.g. <c>basic</c>, <c>premium</c>).</summary>
        public List<string> Squads { get; init; } = [];

        public bool IsActive { get; init; }
        public int SortOrder { get; init; }
    }

    /// <summary>
    /// A staff member from <c>Seed:Staff</c>: the first admin and tech admin are set at deployment
    /// (ТЗ 28, «Управление сотрудниками»). Existing members are left as they are.
    /// </summary>
    public sealed class StaffSeed
    {
        public const string SectionName = "Seed:Staff";

        public long TelegramId { get; init; }
        public StaffRole Role { get; init; }
        public string DisplayName { get; init; } = string.Empty;
    }
}
