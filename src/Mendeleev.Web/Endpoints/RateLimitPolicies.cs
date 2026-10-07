namespace Mendeleev.Web.Endpoints
{
    internal static class RateLimitPolicies
    {
        /// <summary>A separate limit for webhooks (ТЗ 31, «Защита вебхуков»).</summary>
        public const string Webhooks = "webhooks";

        /// <summary>Public pages and the payment return page.</summary>
        public const string Pages = "pages";

        /// <summary>Sign-in by key: 5 attempts a minute from an IP (FR-ACC-11, FR-WEB-10).</summary>
        public const string Login = "login";

        /// <summary>Registration: 3 an hour from an IP (ТЗ 27, «Безопасность»).</summary>
        public const string Register = "register";

        /// <summary>Linking code: 5 tries in 10 minutes per account — the lifetime of one code (ТЗ 21).</summary>
        public const string LinkCode = "link-code";

        /// <summary>Promo codes in the cabinet: 10 tries an hour per account, so codes are not guessed by brute force.</summary>
        public const string Promo = "promo";
    }
}
