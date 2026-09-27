namespace Mendeleev.Domain.Tariffs
{
    /// <summary>
    /// A tariff lives in the database so prices change without a release (FR-SUB-01). The trial is a
    /// tariff too (<see cref="TariffTier.Trial"/>), so every subscription has one.
    /// </summary>
    public sealed class Tariff
    {
        public const string TrialCode = "trial";

        private Tariff() { }

        public int Id { get; private set; }

        public string Code { get; private set; } = string.Empty;

        public string Name { get; private set; } = string.Empty;

        public TariffTier Tier { get; private set; }

        public decimal Price { get; private set; }

        public int PeriodDays { get; private set; }

        public int DeviceLimit { get; private set; }

        /// <summary>Null means unlimited (paid tariffs); the trial has 10 GB.</summary>
        public long? TrafficLimitBytes { get; private set; }

        /// <summary>Remnawave internal squads that define which inbounds the user gets.</summary>
        public List<Guid> PanelSquads { get; private set; } = [];

        public bool IsActive { get; private set; }

        public int SortOrder { get; private set; }

        public bool IsTrial => Tier == TariffTier.Trial;

        /// <summary>Only an active paid tariff with a real price can be bought (ТЗ: «до ответа продажи не открываются»).</summary>
        public bool IsPurchasable => IsActive && !IsTrial && Price > 0;

        public static Tariff Create(
            string code,
            string name,
            TariffTier tier,
            decimal price,
            int periodDays,
            int deviceLimit,
            long? trafficLimitBytes,
            IEnumerable<Guid> panelSquads,
            bool isActive,
            int sortOrder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            ArgumentOutOfRangeException.ThrowIfNegative(price);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodDays);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceLimit);

            return new Tariff
            {
                Code = code,
                Name = name,
                Tier = tier,
                Price = price,
                PeriodDays = periodDays,
                DeviceLimit = deviceLimit,
                TrafficLimitBytes = trafficLimitBytes,
                PanelSquads = [.. panelSquads],
                IsActive = isActive,
                SortOrder = sortOrder,
            };
        }

        /// <summary>Price per 30 days, for the "≈ N ₽/мес" hint on 3- and 12-month tariffs.</summary>
        public decimal MonthlyEquivalent => PeriodDays <= 30 ? Price : Math.Round(Price / PeriodDays * 30m, 0);
    }
}
