namespace Mendeleev.Application.Configuration
{
    public sealed class PaymentOptions
    {
        public const string SectionName = "Payments";

        /// <summary>
        /// Off until there is a contract with an aggregator: the launch then runs on trials and manual
        /// extensions by the admin (ТЗ 23, «Если договор с агрегатором не готов к запуску»).
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>Code of the provider new payments are created with.</summary>
        public string ActiveProvider { get; init; } = "fake";

        /// <summary>An unpaid payment younger than this is shown again instead of a new one (FR-PAY-10).</summary>
        public int ReuseWithinMinutes { get; init; } = 60;

        public int MaxCreatesPerHour { get; init; } = 5;

        /// <summary>Unpaid payments are canceled after this (FR-PAY-11).</summary>
        public int CancelAfterMinutes { get; init; } = 60;

        /// <summary>«Проверить оплату» asks the aggregator not more often than this (FR-PAY-09).</summary>
        public int CheckThrottleSeconds { get; init; } = 10;

        /// <summary>The 5-minute check looks at pending payments this old (FR-PAY-07).</summary>
        public int PendingCheckWindowHours { get; init; } = 2;

        /// <summary>The daily reconciliation window (ТЗ 23, «Сверка с агрегатором»).</summary>
        public int ReconciliationWindowHours { get; init; } = 48;

        /// <summary>Description on the payment page and in the receipt; <c>{tariff}</c> is replaced.</summary>
        public string DescriptionTemplate { get; init; } = "Подписка «{tariff}»";
    }
}
