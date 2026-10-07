using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.IssueAccountKey;
using Mendeleev.Application.Accounts.LinkTelegram;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Devices.GetDevices;
using Mendeleev.Application.Devices.ResetDevices;
using Mendeleev.Application.Payments.Check;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.History;
using Mendeleev.Application.Promos;
using Mendeleev.Application.Subscriptions.GetSubscription;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Infrastructure;
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
        IQueryHandler<GetOfferQuery, Offer> getOffer,
        ICommandHandler<StartTrialCommand> startTrial,
        ICommandHandler<CreatePaymentCommand, PaymentLink> createPayment,
        ICommandHandler<ApplyPromoCodeCommand, PromoApplied> applyPromo,
        ICommandHandler<CheckPaymentCommand, PaymentCheckResult> checkPayment,
        IQueryHandler<GetDevicesQuery, DevicesView> getDevices,
        ICommandHandler<ResetDevicesCommand, DevicesResetResult> resetDevices,
        ICommandHandler<IssueAccountKeyCommand, string> issueKey,
        ICommandHandler<IssueLinkCodeCommand, IssuedLinkCode> issueLinkCode,
        IQueryHandler<GetPaymentHistoryQuery, IReadOnlyList<PaymentHistoryItem>> getPayments,
        ConversationStore conversations)
    {
        /// <summary><c>t.me/&lt;бот&gt;?start=promo_&lt;код&gt;</c> (FR-BOT-20); <c>ref_</c> comes with stage 2.</summary>
        internal const string PromoStartPrefix = "promo_";

        private BotContent C => contentMonitor.CurrentValue;

        public async Task StartAsync(BotContext context, string arguments, CancellationToken cancellationToken)
        {
            bool withPromo = arguments.StartsWith(PromoStartPrefix, StringComparison.OrdinalIgnoreCase);
            if (withPromo && !context.User.IsNew)
            {
                await ApplyPromoAsync(context, arguments[PromoStartPrefix.Length..], cancellationToken);
                return;
            }

            string text = context.User.IsNew
                ? TextRenderer.Render(C.Text("Welcome"), ("service", serviceOptions.Value.Name))
                : C.Text("Menu");

            // With a code the greeting goes without the menu: what the code gives comes next, with its own buttons.
            await responder.SendAsync(context.ChatId, text, withPromo ? null : Keyboards.MainMenu(C, context.User.TrialAvailable), cancellationToken);
            if (withPromo)
            {
                await ApplyPromoAsync(context, arguments[PromoStartPrefix.Length..], cancellationToken);
            }
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
                    Keyboards.Of([Keyboards.Callback(C.Button("Buy"), Cb.Buy)], [Keyboards.Callback(C.Button("Payments"), Cb.Payments)], Keyboards.BackToMenu(C)),
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
            rows.Add([Keyboards.Callback(C.Button("Renew"), Cb.Buy), Keyboards.Callback(C.Button("Payments"), Cb.Payments)]);
            rows.Add(Keyboards.BackToMenu(C));

            await responder.ShowAsync(context, string.Join("\n\n", text), Keyboards.Of(rows), cancellationToken);
        }

        /// <summary>The user's payments (FR-PAY-17).</summary>
        public async Task PaymentsAsync(BotContext context, CancellationToken cancellationToken)
        {
            IReadOnlyList<PaymentHistoryItem> payments = (await getPayments.Handle(new GetPaymentHistoryQuery(context.UserId), cancellationToken)).Value;

            var lines = new List<string> { C.Text("PaymentsHeader") };
            if (payments.Count == 0)
            {
                lines.Add(C.Text("PaymentsEmpty"));
            }
            foreach (PaymentHistoryItem payment in payments)
            {
                string status = payment.Status switch
                {
                    PaymentStatus.Succeeded => C.Text("PaymentStatusSucceeded"),
                    PaymentStatus.Refunded => C.Text("PaymentStatusRefunded"),
                    _ => C.Text("PaymentStatusPending"),
                };
                if (payment.PromoCode is string promo)
                {
                    status += ", " + TextRenderer.Render(C.Text("PaymentHistoryPromo"), ("code", promo));
                }
                if (payment.Manual)
                {
                    status += ", " + C.Text("PaymentHistoryManual");
                }

                // The status is content with markup, not data: it is added after the values are encoded.
                lines.Add(TextRenderer.Render(C.Text("PaymentHistoryLine"), ("date", payment.At), ("tariff", payment.TariffName), ("amount", payment.Amount))
                    .Replace("{status}", status, StringComparison.Ordinal));
            }

            await responder.ShowAsync(
                context,
                string.Join("\n", lines),
                Keyboards.Of([Keyboards.Callback(C.Button("Back"), Cb.Subscription)], Keyboards.BackToMenu(C)),
                cancellationToken);
        }

        /// <param name="lead">Replaces the usual header, e.g. «промокод принят».</param>
        public async Task TariffsAsync(BotContext context, CancellationToken cancellationToken, string? lead = null)
        {
            Offer offer = (await getOffer.Handle(new GetOfferQuery(context.UserId), cancellationToken)).Value;
            if (offer.Tariffs.Count == 0)
            {
                await responder.ShowAsync(context, C.Text("TariffsEmpty"), Keyboards.Of(Keyboards.Support(C), Keyboards.BackToMenu(C)), cancellationToken);
                return;
            }

            IEnumerable<InlineKeyboardButton[]> rows = offer.Tariffs.Select(t =>
            {
                string label = $"{t.Tariff.Name} — {TextRenderer.Encode(t.FinalPrice)} ₽";
                if (t.Discounted)
                {
                    label += $" вместо {TextRenderer.Encode(t.Tariff.Price)}";
                }
                else if (t.Tariff.PeriodDays > 31)
                {
                    label += $" (≈ {TextRenderer.Encode(t.MonthlyEquivalent)} ₽/мес)";
                }
                return new[] { Keyboards.Callback(label, Cb.Tariff + t.Tariff.Code) };
            });

            string text = lead ?? C.Text("TariffsHeader");
            if (lead is null && offer.Promo is OfferPromo promo)
            {
                text += "\n\n" + TextRenderer.Render(C.Text("TariffsPromo"), ("code", promo.Code), ("percent", promo.DiscountPercent));
            }

            await responder.ShowAsync(
                context,
                text,
                Keyboards.Of(rows
                    .Append([Keyboards.Callback(C.Button("Promo"), Cb.Promo)])
                    .Append(Keyboards.BackToMenu(C))),
                cancellationToken);
        }

        public async Task ConfirmTariffAsync(BotContext context, string code, CancellationToken cancellationToken)
        {
            Offer offer = (await getOffer.Handle(new GetOfferQuery(context.UserId), cancellationToken)).Value;
            if (offer.Find(code) is not OfferedTariff tariff)
            {
                await TariffsAsync(context, cancellationToken);
                return;
            }

            string text = TextRenderer.Render(C.Text("PaymentConfirm"), ("tariff", tariff.Tariff.Name), ("amount", tariff.FinalPrice), ("days", tariff.Tariff.PeriodDays));
            if (tariff.Discounted && offer.Promo is OfferPromo promo)
            {
                text += "\n\n" + TextRenderer.Render(C.Text("PaymentDiscount"), ("code", promo.Code), ("full", tariff.Tariff.Price), ("amount", tariff.FinalPrice));
            }

            await responder.ShowAsync(
                context,
                text,
                Keyboards.Of(
                    [Keyboards.Callback(C.Button("Pay"), Cb.Pay + tariff.Tariff.Code)],
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
            if (link is { PromoCode: string promoCode, FullPrice: decimal full } && full > link.Amount)
            {
                text += "\n\n" + TextRenderer.Render(C.Text("PaymentDiscount"), ("code", promoCode), ("full", full), ("amount", link.Amount));
            }
            if (link.DroppedPromoCode is string dropped)
            {
                text += "\n\n" + TextRenderer.Render(C.Text("PaymentPromoDropped"), ("code", dropped));
            }
            if (link.Reused)
            {
                text += "\n\n" + C.Text("PaymentReused");
            }

            await responder.ShowAsync(context, text, PaymentKeyboard(link.ConfirmationUrl, link.PaymentId), cancellationToken);
        }

        /// <summary>«Ввести промокод»: the next plain message of the user is taken as the code (FR-SUB-15).</summary>
        public Task PromoAskAsync(BotContext context, CancellationToken cancellationToken)
        {
            conversations.SetPromoPrompt(context.ChatId);
            return responder.ShowAsync(context, C.Text("PromoAsk"), Keyboards.Of([Keyboards.Callback(C.Button("Back"), Cb.Buy)]), cancellationToken);
        }

        /// <summary>
        /// A discount shows the tariffs at once with the new prices; bonus days are added at once, and the date comes
        /// with the confirmation once the panel has the new term.
        /// </summary>
        public async Task ApplyPromoAsync(BotContext context, string code, CancellationToken cancellationToken)
        {
            Result<PromoApplied> result = await applyPromo.Handle(new ApplyPromoCodeCommand(context.UserId, code), cancellationToken);
            if (result.IsFailure)
            {
                await responder.ShowAsync(
                    context,
                    TextRenderer.Encode(result.Error.Description),
                    Keyboards.Of([Keyboards.Callback(C.Button("Promo"), Cb.Promo)], [Keyboards.Callback(C.Button("Buy"), Cb.Buy)], Keyboards.BackToMenu(C)),
                    cancellationToken);
                return;
            }

            PromoApplied applied = result.Value;
            if (applied.Type == PromoType.DiscountPercent)
            {
                await TariffsAsync(context, cancellationToken, TextRenderer.Render(C.Text("PromoDiscountApplied"), ("code", applied.Code), ("percent", applied.Value)));
                return;
            }

            await responder.ShowAsync(
                context,
                TextRenderer.Render(C.Text("PromoBonusApplied"), ("code", applied.Code), ("days", applied.Value)),
                Keyboards.Of([Keyboards.Callback(C.Button("MySubscription"), Cb.Subscription)], Keyboards.BackToMenu(C)),
                cancellationToken);
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
