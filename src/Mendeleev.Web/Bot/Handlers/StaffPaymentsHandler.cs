using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Admin.Payments;
using Mendeleev.Application.Admin.Tariffs;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Infrastructure;
using Telegram.Bot.Types.ReplyMarkups;

namespace Mendeleev.Web.Bot.Handlers
{
    /// <summary>
    /// Admin money matters in the bot (ТЗ 23, 28; stage 1.5): refund marks (FR-PAY-16, FR-ADM-18), the active
    /// aggregator (FR-PAY-14) and tariff prices (FR-ADM-14). Each change asks for a confirmation; rights are checked
    /// again by the Application layer. Texts are internal and therefore kept in code.
    /// </summary>
    internal sealed class StaffPaymentsHandler(
        BotResponder responder,
        ConversationStore conversations,
        ICommandHandler<RefundPaymentCommand, RefundResult> refundPayment,
        IQueryHandler<ListRefundablePaymentsQuery, IReadOnlyList<RefundablePayment>> listRefundable,
        IQueryHandler<GetRefundablePaymentQuery, RefundablePayment> getRefundable,
        IQueryHandler<GetPaymentProvidersQuery, PaymentProvidersView> getProviders,
        ICommandHandler<SwitchPaymentProviderCommand> switchProvider,
        IQueryHandler<ListTariffsQuery, IReadOnlyList<TariffAdminView>> listTariffs,
        ICommandHandler<ChangeTariffPriceCommand, TariffAdminView> changePrice,
        ICommandHandler<SetTariffActiveCommand, TariffAdminView> setTariffActive)
    {
        private const string TariffUsage =
            "<code>/tariffs price КОД 249</code> — новая цена в рублях; уже созданные платежи не меняются\n" +
            "<code>/tariffs off КОД</code>, <code>/tariffs on КОД</code> — убрать тариф из продажи или вернуть";

        // ── Возвраты (FR-PAY-16) ─────────────────────────────────────────────────────────────────

        public async Task RefundPaymentsAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<RefundablePayment>> result = await listRefundable.Handle(new ListRefundablePaymentsQuery(context.Staff!.StaffId, userId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            InlineKeyboardButton[] back = [Keyboards.Callback("« Карточка", $"s:c:{userId}")];
            if (result.Value.Count == 0)
            {
                await responder.ShowAsync(context, $"У u{userId} нет оплаченных платежей.", Keyboards.Of(back), cancellationToken);
                return;
            }

            IEnumerable<InlineKeyboardButton[]> rows = result.Value
                .Select(p => new[] { Keyboards.Callback($"{TextRenderer.FormatDate(p.PaidAt)} — {TextRenderer.Encode(p.Amount)} ₽ — {p.TariffName}", $"rf:p:{p.Id:N}") })
                .Append(back);
            await responder.ShowAsync(context, $"Возврат u{userId}: какой платёж вернули?", Keyboards.Of(rows), cancellationToken);
        }

        public async Task RefundPaymentAsync(BotContext context, Guid paymentId, CancellationToken cancellationToken)
        {
            Result<RefundablePayment> result = await getRefundable.Handle(new GetRefundablePaymentQuery(context.Staff!.StaffId, paymentId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            RefundablePayment payment = result.Value;
            InlineKeyboardButton[] back = [Keyboards.Callback("« Карточка", $"s:c:{payment.UserId}")];
            if (payment.Status != PaymentStatus.Succeeded)
            {
                Error error = payment.Status == PaymentStatus.Refunded ? PaymentErrors.AlreadyRefunded : PaymentErrors.NotRefundable;
                await responder.ShowAsync(context, TextRenderer.Encode(error.Description), Keyboards.Of(back), cancellationToken);
                return;
            }

            await responder.ShowAsync(
                context,
                $"{Describe(payment)}\n\nЧто вернули пользователю (оферта, раздел 6)?",
                Keyboards.Of(
                    [Keyboards.Callback("Неиспользованные дни — доступ прекратится", $"rf:k:{payment.Id:N}:u")],
                    [Keyboards.Callback($"Ошибочный или повторный — снять {payment.Days} дн.", $"rf:k:{payment.Id:N}:e")],
                    back),
                cancellationToken);
        }

        public Task RefundKindChosenAsync(BotContext context, Guid paymentId, RefundKind kind, CancellationToken cancellationToken)
        {
            conversations.Set(context.ChatId, new Conversation(ConversationKinds.RefundReason, PaymentId: paymentId, Option: kind.ToString()));
            return responder.SendAsync(context.ChatId, "Причина возврата одним сообщением — например, «заявка от 07.10, вернули 5 USDT» (или /cancel).", null, cancellationToken);
        }

        public async Task RefundReasonAsync(BotContext context, Guid paymentId, RefundKind kind, string reason, CancellationToken cancellationToken)
        {
            reason = reason.Trim();
            if (reason.Length < 3)
            {
                conversations.Set(context.ChatId, new Conversation(ConversationKinds.RefundReason, PaymentId: paymentId, Option: kind.ToString()));
                await responder.SendAsync(context.ChatId, "Причина — не короче 3 символов. Напишите ещё раз (или /cancel).", null, cancellationToken);
                return;
            }

            Result<RefundablePayment> result = await getRefundable.Handle(new GetRefundablePaymentQuery(context.Staff!.StaffId, paymentId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            RefundablePayment payment = result.Value;
            string token = NewToken();
            conversations.SetDraft(context.ChatId, new RefundDraft(token, payment.Id, payment.UserId, kind, reason));

            string effect = kind == RefundKind.UnusedDays
                ? "Доступ прекратится сейчас."
                : $"Срок подписки уменьшится на {payment.Days} дн., но не раньше текущего момента.";
            await responder.SendAsync(
                context.ChatId,
                $"Отметить возврат?\n{Describe(payment)}\n{effect}\nПричина: {TextRenderer.Encode(reason)}\n\nПользователь получит сообщение.",
                Keyboards.Of(
                    [Keyboards.Callback("Отметить возврат", $"rf:ok:{token}")],
                    [Keyboards.Callback("« Карточка", $"s:c:{payment.UserId}")]),
                cancellationToken);
        }

        public async Task RefundConfirmAsync(BotContext context, string token, CancellationToken cancellationToken)
        {
            if (conversations.TakeDraft<RefundDraft>(context.ChatId, token) is not RefundDraft draft)
            {
                await responder.SendAsync(context.ChatId, "Этот возврат уже отмечен или черновик устарел. Начните заново из карточки.", null, cancellationToken);
                return;
            }

            Result<RefundResult> result = await refundPayment.Handle(
                new RefundPaymentCommand(context.Staff!.StaffId, draft.PaymentId, draft.Kind, draft.Reason),
                cancellationToken);

            string text;
            if (result.IsFailure)
            {
                text = TextRenderer.Encode(result.Error.Description);
            }
            else if (result.Value.AccessEnded)
            {
                text = $"Возврат отмечен. Доступ u{draft.UserId} прекращён, пользователь получит сообщение.";
            }
            else
            {
                text = result.Value.ExpiresAt is DateTime until
                    ? $"Возврат отмечен. Подписка u{draft.UserId} до {TextRenderer.FormatDate(until)} (МСК), пользователь получит сообщение."
                    : "Возврат отмечен.";
            }
            await responder.SendAsync(context.ChatId, text, Keyboards.Of([Keyboards.Callback("« Карточка", $"s:c:{draft.UserId}")]), cancellationToken);
        }

        /// <summary><c>/refund &lt;id платежа&gt;</c>: our payment id from the user card.</summary>
        public async Task RefundCommandAsync(BotContext context, string arguments, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(arguments.Trim(), out Guid paymentId))
            {
                await responder.SendAsync(context.ChatId, "Формат: <code>/refund &lt;id платежа&gt;</code> — наш id из карточки пользователя. Или кнопка «Возврат» в карточке.", null, cancellationToken);
                return;
            }

            await RefundPaymentAsync(context, paymentId, cancellationToken);
        }

        private static string Describe(RefundablePayment payment) =>
            $"u{payment.UserId}: {TextRenderer.FormatDate(payment.PaidAt)}, {TextRenderer.Encode(payment.Amount)} ₽ за «{TextRenderer.Encode(payment.TariffName)}» ({payment.Days} дн.), {TextRenderer.Encode(payment.Provider == Payment.ManualProvider ? "вне системы" : payment.Provider)}\n<code>{payment.Id}</code>";

        // ── Агрегатор (FR-PAY-14) ────────────────────────────────────────────────────────────────

        public async Task AggregatorAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<PaymentProvidersView> result = await getProviders.Handle(new GetPaymentProvidersQuery(context.Staff!.StaffId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            PaymentProvidersView view = result.Value;
            string text =
                $"<b>Агрегаторы</b>\nОсновной: <b>{TextRenderer.Encode(view.Active)}</b>{(view.Active == view.ConfiguredDefault ? string.Empty : $" (в настройках — {TextRenderer.Encode(view.ConfiguredDefault)})")}\n" +
                $"Подключены: {TextRenderer.Encode(string.Join(", ", view.SwitchedOn))}\n\n" +
                "Новые платежи идут через основной. Если он не создаёт счёт, платёж автоматически уходит к следующему подключённому. Подключает агрегаторы техадмин.";

            IEnumerable<InlineKeyboardButton[]> rows = view.SwitchedOn
                .Where(code => code != view.Active)
                .Select(code => new[] { Keyboards.Callback($"Сделать основным: {code}", $"ag:set:{code}") });
            await responder.ShowAsync(context, text, Keyboards.Of(rows), cancellationToken);
        }

        public Task AggregatorSwitchAskAsync(BotContext context, string code, CancellationToken cancellationToken) =>
            responder.ShowAsync(
                context,
                $"Сделать основным агрегатор {TextRenderer.Encode(code)}? Новые платежи пойдут через него; созданные остаются у своего агрегатора.",
                Keyboards.Of([Keyboards.Callback("Переключить", $"ag:set!:{code}")], [Keyboards.Callback("« Агрегаторы", "ag:list")]),
                cancellationToken);

        public async Task AggregatorSwitchAsync(BotContext context, string code, CancellationToken cancellationToken)
        {
            Result result = await switchProvider.Handle(new SwitchPaymentProviderCommand(context.Staff!.StaffId, code), cancellationToken);
            await responder.SendAsync(
                context.ChatId,
                result.IsSuccess ? $"Основной агрегатор — {TextRenderer.Encode(code)}." : TextRenderer.Encode(result.Error.Description),
                null,
                cancellationToken);
            await AggregatorAsync(context, cancellationToken);
        }

        // ── Тарифы и цены (FR-ADM-14) ────────────────────────────────────────────────────────────

        /// <summary><c>/tariffs</c>, <c>/tariffs price КОД 249</c>, <c>/tariffs on|off КОД</c>.</summary>
        public async Task TariffsAsync(BotContext context, string arguments, CancellationToken cancellationToken)
        {
            string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (parts)
            {
                case []:
                    await ListTariffsAsync(context, cancellationToken);
                    return;
                case ["price", var code, var price] when TryParseRubles(price, out decimal rubles):
                    await TariffChangeAskAsync(context, code, rubles, null, cancellationToken);
                    return;
                case ["on", var code]:
                    await TariffChangeAskAsync(context, code, null, true, cancellationToken);
                    return;
                case ["off", var code]:
                    await TariffChangeAskAsync(context, code, null, false, cancellationToken);
                    return;
                default:
                    await responder.SendAsync(context.ChatId, "Не понял команду.\n\n" + TariffUsage, null, cancellationToken);
                    return;
            }
        }

        public async Task TariffConfirmAsync(BotContext context, string token, CancellationToken cancellationToken)
        {
            if (conversations.TakeDraft<TariffDraft>(context.ChatId, token) is not TariffDraft draft)
            {
                await responder.SendAsync(context.ChatId, "Это изменение уже применено или черновик устарел. Повторите команду.", null, cancellationToken);
                return;
            }

            long staffId = context.Staff!.StaffId;
            Result<TariffAdminView> result = draft.Price is decimal price
                ? await changePrice.Handle(new ChangeTariffPriceCommand(staffId, draft.Code, price), cancellationToken)
                : await setTariffActive.Handle(new SetTariffActiveCommand(staffId, draft.Code, draft.IsActive!.Value), cancellationToken);

            await responder.SendAsync(
                context.ChatId,
                result.IsSuccess ? $"Готово: {Line(result.Value)}" : TextRenderer.Encode(result.Error.Description),
                null,
                cancellationToken);
        }

        private async Task ListTariffsAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<TariffAdminView>> result = await listTariffs.Handle(new ListTariffsQuery(context.Staff!.StaffId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            var text = new StringBuilder("<b>Тарифы</b>\n");
            foreach (TariffAdminView t in result.Value)
            {
                text.Append(Line(t)).Append('\n');
            }
            text.Append('\n').Append(TariffUsage);

            await responder.SendAsync(context.ChatId, text.ToString(), null, cancellationToken);
        }

        private async Task TariffChangeAskAsync(BotContext context, string code, decimal? price, bool? isActive, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<TariffAdminView>> tariffs = await listTariffs.Handle(new ListTariffsQuery(context.Staff!.StaffId), cancellationToken);
            if (tariffs.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(tariffs.Error.Description), null, cancellationToken);
                return;
            }
            if (tariffs.Value.FirstOrDefault(t => t.Code == code) is not TariffAdminView tariff)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(Domain.Tariffs.TariffErrors.NotFound(code).Description) + "\n\n" + TariffUsage, null, cancellationToken);
                return;
            }

            string question = (price, isActive) switch
            {
                (decimal p, _) => $"Сменить цену «{TextRenderer.Encode(tariff.Name)}»: {TextRenderer.Encode(tariff.Price)} → {TextRenderer.Encode(p)} ₽? Уже созданные платежи сохранят свою сумму.",
                (_, false) => $"Выключить «{TextRenderer.Encode(tariff.Name)}»? Тариф пропадёт из «Купить / Продлить» и с сайта; действующие подписки и созданные платежи останутся.",
                _ => $"Включить «{TextRenderer.Encode(tariff.Name)}»? Он снова появится в продаже, если у него есть цена.",
            };

            string token = NewToken();
            conversations.SetDraft(context.ChatId, new TariffDraft(token, tariff.Code, price, isActive));
            await responder.SendAsync(context.ChatId, question, Keyboards.Of([Keyboards.Callback("Подтвердить", $"tf:ok:{token}")]), cancellationToken);
        }

        private static string Line(TariffAdminView t)
        {
            string traffic = t.TrafficLimitBytes is long bytes ? TextRenderer.FormatBytes(bytes) : "без лимита";
            return string.Create(CultureInfo.InvariantCulture,
                $"<code>{t.Code}</code> {TextRenderer.Encode(t.Name)} — {TextRenderer.Encode(t.Price)} ₽, {t.PeriodDays} дн., {t.DeviceLimit} устр., {traffic}{(t.IsActive ? string.Empty : " (выключен)")}");
        }

        /// <summary>Whole rubles: <c>249</c> or <c>249₽</c>.</summary>
        internal static bool TryParseRubles(string text, out decimal rubles)
        {
            bool parsed = int.TryParse(text.TrimEnd('₽'), NumberStyles.None, CultureInfo.InvariantCulture, out int value);
            rubles = value;
            return parsed && value > 0;
        }

        private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    }

    internal sealed record RefundDraft(string Token, Guid PaymentId, long UserId, RefundKind Kind, string Reason) : IStaffDraft;

    /// <summary>A new price, or a new activity when <see cref="Price"/> is null.</summary>
    internal sealed record TariffDraft(string Token, string Code, decimal? Price, bool? IsActive) : IStaffDraft;
}
