using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Subscriptions.GetSubscription;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Web.Bot;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Cabinet;
using Mendeleev.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace Mendeleev.Web.Pages.Cabinet
{
    /// <summary>
    /// Subscription status, link, QR code and «Добавить в Happ» (FR-WEB-04). A blocked user sees only
    /// the block message (ТЗ 27, «Бизнес-правила»).
    /// </summary>
    public sealed class IndexModel(
        IQueryHandler<GetSubscriptionQuery, SubscriptionView> getSubscription,
        IOptionsMonitor<BotContent> content)
        : CabinetPageModel
    {
        public SubscriptionView Subscription { get; private set; } = SubscriptionView.None;

        public string? QrDataUri { get; private set; }

        public string? ImportUrl { get; private set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            if (Account.IsBlocked)
            {
                return;
            }

            Subscription = (await getSubscription.Handle(new GetSubscriptionQuery(UserId), cancellationToken)).Value;
            if (Subscription.SubscriptionUrl is string url)
            {
                QrDataUri = QrCodes.PngDataUri(url);
                ImportUrl = Keyboards.ImportUrl(content.CurrentValue, url);
            }
        }

        public string StatusText => Subscription.Status switch
        {
            SubscriptionStatus.Trial => content.CurrentValue.Text("StatusTrial"),
            SubscriptionStatus.Active => content.CurrentValue.Text("StatusActive"),
            SubscriptionStatus.Expired => content.CurrentValue.Text("StatusExpired"),
            SubscriptionStatus.Disabled => content.CurrentValue.Text("StatusDisabled"),
            _ => string.Empty,
        };
    }
}
