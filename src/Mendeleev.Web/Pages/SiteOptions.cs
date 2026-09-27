namespace Mendeleev.Web.Pages
{
    /// <summary>
    /// Operator details for the public pages (ТЗ 27, «Публичные страницы»). The legal form and the texts
    /// of the offer and the privacy policy are still with the owner and the lawyer (ТЗ 99).
    /// </summary>
    public sealed class SiteOptions
    {
        public const string SectionName = "Site";

        public string OperatorName { get; init; } = "Оператор сервиса";

        /// <summary>Requisites: legal form, tax number, address — as the aggregator requires.</summary>
        public string OperatorDetails { get; init; } = string.Empty;

        public string SupportEmail { get; init; } = string.Empty;
    }
}
