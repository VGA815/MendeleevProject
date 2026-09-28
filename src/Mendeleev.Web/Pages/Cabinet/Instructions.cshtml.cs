using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Subscriptions.GetSubscription;
using Mendeleev.Web.Bot;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Cabinet;
using Microsoft.Extensions.Options;

namespace Mendeleev.Web.Pages.Cabinet
{
    /// <summary>
    /// Instructions for the platforms, only after sign-in (FR-WEB-07, опросник 3.3). The texts are the
    /// bot's (<c>content/bot.yaml</c>), so the owner edits them in one place and both channels agree.
    /// </summary>
    public sealed class InstructionsModel(
        IQueryHandler<GetSubscriptionQuery, SubscriptionView> getSubscription,
        IOptionsMonitor<BotContent> content)
        : CabinetPageModel
    {
        public BotContent Texts => content.CurrentValue;

        public string? SubscriptionUrl { get; private set; }

        public string? ImportUrl { get; private set; }

        public bool AccessPending { get; private set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            SubscriptionView view = (await getSubscription.Handle(new GetSubscriptionQuery(UserId), cancellationToken)).Value;
            SubscriptionUrl = view.SubscriptionUrl;
            AccessPending = view.AccessPending;
            if (SubscriptionUrl is string url)
            {
                ImportUrl = Keyboards.ImportUrl(Texts, url);
            }
        }

        /// <summary>Owner's Telegram HTML (b, i, code, a) is valid HTML too; only the link is data and gets encoded.</summary>
        public string Render(PlatformInstruction platform) =>
            platform.Instruction.Replace("{url}", TextRenderer.Encode(SubscriptionUrl), StringComparison.Ordinal);
    }
}
