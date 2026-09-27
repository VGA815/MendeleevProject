namespace Mendeleev.Web.Endpoints
{
    internal static class RateLimitPolicies
    {
        /// <summary>A separate limit for webhooks (ТЗ 31, «Защита вебхуков»).</summary>
        public const string Webhooks = "webhooks";

        /// <summary>Public pages and the payment return page.</summary>
        public const string Pages = "pages";
    }
}
