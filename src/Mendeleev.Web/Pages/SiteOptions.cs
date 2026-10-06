namespace Mendeleev.Web.Pages
{
    /// <summary>
    /// Support contacts on the public pages (ТЗ 27, «Публичные страницы»). The seller's name, requisites and address
    /// are not published (decision of 05.10.2026); the aggregator's moderation looks for a phone and an email
    /// (lava.ru/site-requirements).
    /// </summary>
    public sealed class SiteOptions
    {
        public const string SectionName = "Site";

        public string Phone { get; init; } = string.Empty;

        public string SupportEmail { get; init; } = string.Empty;

        /// <summary>The phone for a <c>tel:</c> link: «+7 (999) 123-45-67» → «+79991234567».</summary>
        public string PhoneLink => new(Phone.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
    }
}
