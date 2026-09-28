using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.IssueAccountKey;
using Mendeleev.Application.Accounts.LinkTelegram;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Devices.GetDevices;
using Mendeleev.Application.Devices.ResetDevices;
using Mendeleev.Application.Payments.Check;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Subscriptions.GetSubscription;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Application.Tariffs.GetPurchasableTariffs;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Infrastructure;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;

namespace Mendeleev.Web.Bot.Handlers
{
    /// <summary>The user's side of the bot (ТЗ 26, «Структура меню»). Business rules live in Application.</summary>
    internal sealed class UserHandler(
        BotResponder responder,
        IOptionsMonitor<BotContent> contentMonitor,
        IOptions<ServiceOptions> serviceOptions,
        IQueryHandler<GetSubscriptionQuery, SubscriptionView> getSubscription,
        IQueryHandler<GetPurchasableTariffsQuery, IReadOnlyList<TariffView>> getTariffs,
        ICommandHandler<StartTrialCommand> startTrial,
        ICommandHandler<CreatePaymentCommand, PaymentLink> createPayment,
        ICommandHandler<CheckPaymentCommand, PaymentCheckResult> checkPayment,
        IQueryHandler<GetDevicesQuery, DevicesView> getDevices,
        ICommandHandler<ResetDevicesCommand, DevicesResetResult> resetDevices,
        ICommandHandler<IssueAccountKeyCommand, string> issueKey,
        ICommandHandler<IssueLinkCodeCommand, IssuedLinkCode> issueLinkCode)
    {
        private BotContent C => contentMonitor.CurrentValue;

        public async Task StartAsync(BotContext context, CancellationToken cancellationToken)
        {
            string text = context.User.IsNew
                ? TextRenderer.Render(C.Text("Welcome"), ("service", serviceOptions.Value.Name))
                : C.Text("Menu");
            await responder.SendAsync(context.ChatId, text, Keyboards.MainMenu(C, context.User.TrialAvailable), cancellationToken);
        }

        public Task MenuAsync(BotContext context, CancellationToken cancellationToken) =>
            responder.ShowAsync(context, C.Text("Menu"), Keyboards.MainMenu(C, context.User.TrialAvailable), cancellationToken);

        public async Task SubscriptionAsync(BotContext context, CancellationToken cancellationToken)
        {
            SubscriptionView view = (await getSubscription.Handle(new GetSubscriptionQuery(context.UserId), cancellationToken)).Value;
            if (!view.Exists)
            {
                await responder.ShowAsync(
                    context,
                    C.Text("SubscriptionNone"),
                    Keyboards.Of([Keyboards.Callback(C.Button("Buy"), Cb.Buy)], Keyboards.BackToMenu(C)),
                    cancellationToken);
                return;
            }

            var text = new List<string>
            {
                TextRenderer.Render(C.Text("SubscriptionHeader"),
                    ("tariff", view.TariffName),
                    ("status", StatusText(view.Status)),
                    ("date", view.ExpiresAt),
                    ("days", view.DaysLeft)),
            };

            if (view.TrafficLimitBytes is long limit && view.TrafficLeftBytes is long left)
            {
                text.Add(TextRenderer.Render(C.Text("SubscriptionTraffic"), ("left", TextRenderer.FormatBytes(left)), ("limit", TextRenderer.FormatBytes(limit))));
            }

            switch (view.Status)
            {
                case SubscriptionStatus.Expired:
                    text.Add(C.Text("ExpiredHint"));
                    break;
                case SubscriptionStatus.Disabled:
                    text.Add(C.Text("DisabledHint"));
                    break;
            }

            if (view.AccessPending)
            {
                text.Add(C.Text("AccessPending"));
            }

            var rows = new List<InlineKeyboardButton[]>();
            if (view.SubscriptionUrl is string url)
            {
                text.Add(TextRenderer.Render(C.Text("SubscriptionLink"), ("url", url)));
                rows.Add([Keyboards.Copy(C.Button("CopyLink"), url), Keyboards.Callback(C.Button("Qr"), Cb.Qr)]);
                rows.Add([Keyboards.Callback(C.Button("Connect"), Cb.Connect)]);
            }
            rows.Add([Keyboards.Callback(C.Button("Renew"), Cb.Buy)]);
            rows.Add(Keyboards.BackToMenu(C));

            await responder.ShowAsync(context, string.Join("\n\n", text), Keyboards.Of(rows), cancellationToken);
        }

        public async Task TariffsAsync(BotContext context, CancellationToken cancellationToken)
        {
            IReadOnlyList<TariffView> tariffs = (await getTariffs.Handle(new GetPurchasableTariffsQuery(), cancellationToken)).Value;
            if (tariffs.Count == 0)
            {
                await responder.ShowAsync(context, C.Text("TariffsEmpty"), Keyboards.Of(Keyboards.Support(C), Keyboards.BackToMenu(C)), cancellationToken);
                return;
            }

            IEnumerable<InlineKeyboardButton[]> rows = tariffs.Select(t =>
            {
                string label = $"{t.Name} — {TextRenderer.Encode(t.Price)} ₽";
                if (t.PeriodDays > 31)
                {
                    label += $" (≈ {TextRenderer.Encode(t.MonthlyEquivalent)} ₽/мес)";
                }
                return new[] { Keyboards.Callback(label, Cb.Tariff + t.Code) };
            });

            await responder.ShowAsync(context, C.Text("TariffsHeader"), Keyboards.Of(rows.Append(Keyboards.BackToMenu(C))), cancellationToken);
        }

        public async Task ConfirmTariffAsync(BotContext context, string code, CancellationToken cancellationToken)
        {
            IReadOnlyList<TariffView> tariffs = (await getTariffs.Handle(new GetPurchasableTariffsQuery(), cancellationToken)).Value;
            TariffView? tariff = tariffs.FirstOrDefault(t => t.Code == code);
            if (tariff is null)
            {
                await TariffsAsync(context, cancellationToken);
                return;
            }

            string text = TextRenderer.Render(C.Text("PaymentConfirm"), ("tariff", tariff.Name), ("amount", tariff.Price), ("days", tariff.PeriodDays));
            await responder.ShowAsync(
                context,
                text,
                Keyboards.Of(
                    [Keyboards.Callback(C.Button("Pay"), Cb.Pay + tariff.Code)],
                    [Keyboards.Callback(C.Button("Back"), Cb.Buy)]),
                cancellationToken);
        }

        public async Task PayAsync(BotContext context, string code, CancellationToken cancellationToken)
        {
            Result<PaymentLink> result = await createPayment.Handle(new CreatePaymentCommand(context.UserId, code), cancellationToken);
            if (result.IsFailure)
            {
                await responder.ShowAsync(context, TextRenderer.Encode(result.Error.Description), Keyboards.Of(Keyboards.Support(C), Keyboards.BackToMenu(C)), cancellationToken);
                return;
            }

            PaymentLink link = result.Value;
            string text = TextRenderer.Render(C.Text("PaymentConfirm"), ("tariff", link.TariffName), ("amount", link.Amount), ("days", link.Days));
            if (link.Reused)
            {
                text += "\n\n" + C.Text("PaymentReused");
            }

            await responder.ShowAsync(context, text, PaymentKeyboard(link.ConfirmationUrl, link.PaymentId), cancellationToken);
        }

        public async Task CheckPaymentAsync(BotContext context, Guid paymentId, CancellationToken cancellationToken)
        {
            Result<PaymentCheckResult> result = await checkPayment.Handle(new CheckPaymentCommand(context.UserId, paymentId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            string text = result.Value.Status switch
            {
                PaymentStatus.Succeeded or PaymentStatus.Refunded => C.Text("PaymentSucceeded")
                    + (result.Value.AccessPending ? "\n" + C.Text("AccessPending") : string.Empty),
                PaymentStatus.Canceled or PaymentStatus.Failed => C.Text("PaymentCanceled"),
                _ => C.Text("PaymentPending"),
            };

            await responder.SendAsync(context.ChatId, text, Keyboards.Of(Keyboards.Support(C), Keyboards.BackToMenu(C)), cancellationToken);
        }

        public async Task TrialAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result result = await startTrial.Handle(new StartTrialCommand(context.UserId), cancellationToken);
            string text = result.IsSuccess ? C.Text("TrialStarting") : TextRenderer.Encode(result.Error.Description);
            await responder.ShowAsync(context, text, Keyboards.Of(Keyboards.BackToMenu(C)), cancellationToken);
        }

        public async Task ConnectAsync(BotContext context, CancellationToken cancellationToken)
        {
            SubscriptionView view = (await getSubscription.Handle(new GetSubscriptionQuery(context.UserId), cancellationToken)).Value;
            if (view.SubscriptionUrl is null)
            {
                string text = view.AccessPending ? C.Text("AccessPending") : C.Text("ConnectNoAccess");
                await responder.ShowAsync(context, text, Keyboards.MainMenu(C, context.User.TrialAvailable), cancellationToken);
                return;
            }

            await responder.ShowAsync(
                context,
                C.Text("ConnectChoose") + "\n\n" + C.Text("MobileWarning"),
                Keyboards.Of(Keyboards.Platforms(C).Append(Keyboards.BackToMenu(C))),
                cancellationToken);
        }

        public async Task PlatformAsync(BotContext context, string platformId, CancellationToken cancellationToken)
        {
            PlatformInstruction? platform = C.Platforms.FirstOrDefault(p => p.Id == platformId);
            SubscriptionView view = (await getSubscription.Handle(new GetSubscriptionQuery(context.UserId), cancellationToken)).Value;
            if (platform is null || view.SubscriptionUrl is not string url)
            {
                await ConnectAsync(context, cancellationToken);
                return;
            }

            // Instructions are content (HTML by the owner); only the link is data.
            string text = platform.Instruction.Replace("{url}", TextRenderer.Encode(url), StringComparison.Ordinal)
                + "\n\n" + TextRenderer.Render(C.Text("SubscriptionLink"), ("url", url))
                + "\n\n" + C.Text("MobileWarning");

            await responder.ShowAsync(
                context,
                text,
                Keyboards.Of(
                    [Keyboards.Url(C.Button("AddToHapp"), Keyboards.ImportUrl(C, url))],
                    [Keyboards.Copy(C.Button("CopyLink"), url), Keyboards.Callback(C.Button("Faq"), Cb.Help)],
                    [Keyboards.Callback(C.Button("Back"), Cb.Connect)]),
                cancellationToken);
        }

        public async Task QrAsync(BotContext context, CancellationToken cancellationToken)
        {
            SubscriptionView view = (await getSubscription.Handle(new GetSubscriptionQuery(context.UserId), cancellationToken)).Value;
            if (view.SubscriptionUrl is not string url)
            {
                await ConnectAsync(context, cancellationToken);
                return;
            }

            await responder.SendPhotoAsync(context.ChatId, QrCodes.Png(url), C.Text("QrCaption"), null, cancellationToken);
        }

        public async Task DevicesAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<DevicesView> result = await getDevices.Handle(new GetDevicesQuery(context.UserId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.ShowAsync(context, TextRenderer.Encode(result.Error.Description), Keyboards.Of(Keyboards.BackToMenu(C)), cancellationToken);
                return;
            }

            DevicesView view = result.Value;
            var lines = new List<string> { TextRenderer.Render(C.Text("DevicesHeader"), ("count", view.Devices.Count), ("limit", view.DeviceLimit)) };
            lines.AddRange(view.Devices.Count == 0
                ? [C.Text("DevicesEmpty")]
                : view.Devices.Select(d => TextRenderer.Render(C.Text("DeviceLine"),
                    ("platform", d.Platform ?? "—"), ("model", d.DeviceModel ?? string.Empty), ("date", d.CreatedAt))));

            if (view.NextResetAvailableAtUtc is DateTime next)
            {
                lines.Add(TextRenderer.Render(C.Text("DevicesNextReset"), ("date", next)));
            }

            await responder.ShowAsync(
                context,
                string.Join("\n", lines),
                Keyboards.Of(
                    view.Devices.Count > 0 && view.NextResetAvailableAtUtc is null
                        ? [Keyboards.Callback(C.Button("ResetDevices"), Cb.ResetDevicesAsk)]
                        : [],
                    Keyboards.BackToMenu(C)),
                cancellationToken);
        }

        public Task ResetDevicesAskAsync(BotContext context, CancellationToken cancellationToken) =>
            responder.ShowAsync(
                context,
                C.Text("DevicesResetConfirm"),
                Keyboards.Of(
                    [Keyboards.Callback(C.Button("ConfirmReset"), Cb.ResetDevicesDo)],
                    [Keyboards.Callback(C.Button("Back"), Cb.Devices)]),
                cancellationToken);

        public async Task ResetDevicesAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<DevicesResetResult> result = await resetDevices.Handle(new ResetDevicesCommand(context.UserId), cancellationToken);
            string text;
            if (result.IsFailure)
            {
                text = TextRenderer.Encode(result.Error.Description);
            }
            else
            {
                text = TextRenderer.Render(C.Text("DevicesResetDone"), ("count", result.Value.Removed));
                if (result.Value.NextResetAvailableAtUtc is DateTime next)
                {
                    text += "\n" + TextRenderer.Render(C.Text("DevicesNextReset"), ("date", next));
                }
            }

            await responder.ShowAsync(context, text, Keyboards.Of(Keyboards.Support(C), Keyboards.BackToMenu(C)), cancellationToken);
        }

        public Task HelpAsync(BotContext context, CancellationToken cancellationToken)
        {
            IEnumerable<InlineKeyboardButton[]> rows = C.Faq
                .Select(f => new[] { Keyboards.Callback(f.Question, Cb.Faq + f.Id) })
                .Append(Keyboards.Support(C))
                .Append(Keyboards.BackToMenu(C));

            string text = C.Text("Help");
            if (!string.IsNullOrWhiteSpace(C.SupportEmail))
            {
                text += "\n\n" + TextRenderer.Render(C.Text("SupportContacts"), ("support", C.SupportEmail));
            }

            return responder.ShowAsync(context, text, Keyboards.Of(rows), cancellationToken);
        }

        public Task FaqAsync(BotContext context, string id, CancellationToken cancellationToken)
        {
            FaqItem? item = C.Faq.FirstOrDefault(f => f.Id == id);
            if (item is null)
            {
                return HelpAsync(context, cancellationToken);
            }

            return responder.ShowAsync(
                context,
                $"<b>{TextRenderer.Encode(item.Question)}</b>\n\n{item.Answer}",
                Keyboards.Of(Keyboards.Support(C), [Keyboards.Callback(C.Button("Back"), Cb.Help)]),
                cancellationToken);
        }

        public Task KeyAsync(BotContext context, CancellationToken cancellationToken) =>
            responder.ShowAsync(
                context,
                TextRenderer.Render(C.Text("KeyIntro"), ("site", serviceOptions.Value.SiteBaseUrl)),
                Keyboards.Of(
                    [Keyboards.Callback(context.User.HasAccountKey ? C.Button("ReissueKey") : C.Button("IssueKey"), Cb.KeyIssue)],
                    [Keyboards.Callback(C.Button("LinkCode"), Cb.LinkCode)],
                    Keyboards.BackToMenu(C)),
                cancellationToken);

        public async Task IssueKeyAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<string> result = await issueKey.Handle(new IssueAccountKeyCommand(context.UserId), cancellationToken);
            string text = result.IsSuccess
                ? TextRenderer.Render(C.Text("KeyIssued"), ("key", result.Value), ("site", serviceOptions.Value.SiteBaseUrl))
                : TextRenderer.Encode(result.Error.Description);

            // A new message, not an edit: the key must not disappear from the chat by the next click.
            await responder.SendAsync(context.ChatId, text, Keyboards.Of(Keyboards.BackToMenu(C)), cancellationToken);
        }

        /// <summary>A code to link this Telegram to an account created on the site (FR-ACC-09).</summary>
        public async Task IssueLinkCodeAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<IssuedLinkCode> result = await issueLinkCode.Handle(new IssueLinkCodeCommand(context.UserId), cancellationToken);
            string text = result.IsSuccess
                ? TextRenderer.Render(C.Text("LinkCodeIssued"), ("code", result.Value.Code), ("site", serviceOptions.Value.SiteBaseUrl))
                : TextRenderer.Encode(result.Error.Description);

            // A new message: the code must stay visible while the user types it on the site.
            await responder.SendAsync(context.ChatId, text, Keyboards.Of(Keyboards.BackToMenu(C)), cancellationToken);
        }

        private InlineKeyboardMarkup PaymentKeyboard(string confirmationUrl, Guid paymentId) =>
            Keyboards.Of(
                [Keyboards.Url(C.Button("Pay"), confirmationUrl)],
                [Keyboards.Callback(C.Button("CheckPayment"), Cb.Check + paymentId.ToString("N"))],
                Keyboards.BackToMenu(C));

        private string StatusText(SubscriptionStatus? status) => status switch
        {
            SubscriptionStatus.Trial => C.Text("StatusTrial"),
            SubscriptionStatus.Active => C.Text("StatusActive"),
            SubscriptionStatus.Expired => C.Text("StatusExpired"),
            SubscriptionStatus.Disabled => C.Text("StatusDisabled"),
            _ => string.Empty,
        };
    }
}
