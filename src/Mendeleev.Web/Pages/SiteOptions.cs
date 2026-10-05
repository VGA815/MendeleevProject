namespace Mendeleev.Web.Pages
{
    /// <summary>
    /// The operator on the public pages (ТЗ 27, «Публичные страницы»). The aggregator's moderation wants the
    /// requisites, an email, a phone and an address on the site (lava.ru/site-requirements). The texts of the
    /// offer and the policy are still with the owner and the lawyer (ТЗ 99).
    /// </summary>
    public sealed class SiteOptions
    {
        public const string SectionName = "Site";

        public const string DefaultOperatorName = "Оператор сервиса";

        /// <summary>Full name of the self-employed owner or the name of the company, as registered with the tax office.</summary>
        public string OperatorName { get; init; } = DefaultOperatorName;

        /// <summary>Legal form and numbers: «Самозанятый (плательщик НПД), ИНН …» or «ИП, ИНН …, ОГРНИП …».</summary>
        public string OperatorDetails { get; init; } = string.Empty;

        /// <summary>Legal or actual address.</summary>
        public string Address { get; init; } = string.Empty;

        public string Phone { get; init; } = string.Empty;

        public string SupportEmail { get; init; } = string.Empty;

        /// <summary>Everything the moderation looks for on the contacts page is filled in.</summary>
        public bool HasRequisites =>
            OperatorName != DefaultOperatorName
            && new[] { OperatorName, OperatorDetails, Address, Phone, SupportEmail }.All(value => !string.IsNullOrWhiteSpace(value));

        /// <summary>The phone for a <c>tel:</c> link: «+7 (999) 123-45-67» → «+79991234567».</summary>
        public string PhoneLink => new(Phone.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
    }
}
