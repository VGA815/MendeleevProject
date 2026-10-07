using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Admin.Audit;
using Mendeleev.Application.Admin.Broadcasts;
using Mendeleev.Application.Admin.Promos;
using Mendeleev.Application.Admin.Staff;
using Mendeleev.Application.Admin.Stats;
using Mendeleev.Application.Admin.Tariffs;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Tariffs;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Infrastructure;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;

namespace Mendeleev.Web.Bot.Handlers
{
    /// <summary>
    /// Staff commands in the same bot (ТЗ 28, «Команды сотрудников в MVP»). Buttons are shown by role, but
    /// every action is authorized again by the Application layer. Irreversible or mass actions ask for
    /// confirmation. Texts are internal and therefore kept in code.
    /// </summary>
    internal sealed class StaffHandler(
        BotResponder responder,
        ConversationStore conversations,
        StaffCommandsPublisher commandsPublisher,
        IUserMessenger messenger,
        ICommandHandler<FindUserCommand, UserCard> findUser,
        ICommandHandler<GetUserCardCommand, UserCard> getCard,
        ICommandHandler<CompensateCommand, DateTime> compensate,
        ICommandHandler<RecordManualPaymentCommand, ManualPaymentRecorded> recordManualPayment,
        ICommandHandler<StaffResetDevicesCommand, int> resetDevices,
        ICommandHandler<StaffDeleteDeviceCommand> deleteDevice,
        ICommandHandler<ReissueLinkCommand> reissueLink,
        ICommandHandler<BlockUserCommand> blockUser,
        ICommandHandler<UnblockUserCommand> unblockUser,
        IQueryHandler<CountRecipientsQuery, IReadOnlyDictionary<BroadcastSegment, int>> countRecipients,
        ICommandHandler<CreateBroadcastCommand, BroadcastDraft> createBroadcast,
        ICommandHandler<StartBroadcastCommand, int> startBroadcast,
        ICommandHandler<CancelBroadcastCommand> cancelBroadcast,
        IQueryHandler<GetStatsQuery, SalesStats> getStats,
        IQueryHandler<ListStaffQuery, IReadOnlyList<StaffIdentityView>> listStaff,
        ICommandHandler<AddStaffCommand, long> addStaff,
        ICommandHandler<ChangeStaffRoleCommand> changeRole,
        ICommandHandler<SetStaffActiveCommand> setActive,
        IQueryHandler<GetAuditQuery, IReadOnlyList<AuditEntryView>> getAudit,
        IQueryHandler<ListTariffsQuery, IReadOnlyList<TariffAdminView>> listTariffs,
        ICommandHandler<CreatePromoCodeCommand, PromoCodeView> createPromo,
        ICommandHandler<DeactivatePromoCodeCommand> deactivatePromo,
        IQueryHandler<ListPromoCodesQuery, IReadOnlyList<PromoCodeView>> listPromos,
        IQueryHandler<GetPromoCodeQuery, PromoCodeDetails> getPromo,
        IOptions<ServiceOptions> serviceOptions,
        StaffPaymentsHandler payments)
    {
        private const string PromoUsage =
            "<code>/promos new КОД 20%</code> — скидка на оплату, от 1 до 99 %\n" +
            "<code>/promos new КОД 7д</code> — бонусные дни, от 1 до 365\n" +
            "После значения можно добавить лимит использований и даты: <code>/promos new AUTUMN 20% 100 31.10.2026</code> — 100 раз до 31.10 включительно; две даты — с и по.\n" +
            "<code>/promos КОД</code> — статистика, <code>/promos off КОД</code> — выключить.\n" +
            "Код — латиница, цифры, «_» и «-», от 3 до 32 символов.";

        private const string IncidentTemplate = "Часть серверов недоступна. Обновите подписку в приложении.";

        private const string ManualPaymentHint = "Сумма целым числом в рублях и комментарий, как и когда получены деньги, одним сообщением: <code>300 перевод на карту 05.10</code>. Или /cancel.";

        private static readonly Dictionary<BroadcastSegment, string> SegmentNames = new()
        {
            [BroadcastSegment.All] = "Все",
            [BroadcastSegment.Active] = "Активные",
            [BroadcastSegment.Trial] = "Триал",
            [BroadcastSegment.Expired] = "Истёкшие (30 дней)",
            [BroadcastSegment.NoSubscription] = "Без подписки",
        };

        // ── /find и карточка ─────────────────────────────────────────────────────────────────────

        public async Task FindAsync(BotContext context, string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                await responder.SendAsync(context.ChatId, "Формат: <code>/find запрос</code> — Telegram ID, ключ из 16 цифр, id платежа (наш или агрегатора) или u123.", null, cancellationToken);
                return;
            }

            Result<UserCard> result = await findUser.Handle(new FindUserCommand(context.Staff!.StaffId, query), cancellationToken);
            await ShowCardAsync(context, result, cancellationToken);
        }

        public async Task CardAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            Result<UserCard> result = await getCard.Handle(new GetUserCardCommand(context.Staff!.StaffId, userId), cancellationToken);
            await ShowCardAsync(context, result, cancellationToken);
        }

        private async Task ShowCardAsync(BotContext context, Result<UserCard> result, CancellationToken cancellationToken)
        {
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            UserCard card = result.Value;
            StaffIdentity staff = context.Staff!;
            var rows = new List<InlineKeyboardButton[]>
            {
                new[] { Keyboards.Callback("Продлить", $"s:x:{card.UserId}"), Keyboards.Callback("Устройства", $"s:d:{card.UserId}") },
                new[] { Keyboards.Callback("Сбросить устройства", $"s:r:{card.UserId}"), Keyboards.Callback("Перевыпустить ссылку", $"s:l:{card.UserId}") },
            };

            var adminRow = new List<InlineKeyboardButton>();
            if (staff.Can(StaffPermission.BlockUsers))
            {
                adminRow.Add(card.Status == Domain.Users.UserStatus.Blocked
                    ? Keyboards.Callback("Разблокировать", $"s:u:{card.UserId}")
                    : Keyboards.Callback("Заблокировать", $"s:b:{card.UserId}"));
            }
            if (staff.Can(StaffPermission.ViewAudit))
            {
                adminRow.Add(Keyboards.Callback("Аудит", $"s:a:{card.UserId}"));
            }
            rows.Add([.. adminRow]);
            var paymentsRow = new List<InlineKeyboardButton>();
            if (staff.Can(StaffPermission.RecordManualPayments))
            {
                paymentsRow.Add(Keyboards.Callback("Оплата вне системы", $"s:m:{card.UserId}"));
            }
            if (staff.Can(StaffPermission.RefundPayments) && card.Payments.Any(p => p.Status == PaymentStatus.Succeeded))
            {
                paymentsRow.Add(Keyboards.Callback("Возврат", $"rf:u:{card.UserId}"));
            }
            rows.Add([.. paymentsRow]);
            rows.Add([Keyboards.Callback("Обновить", $"s:c:{card.UserId}")]);

            await responder.ShowAsync(context, RenderCard(card), Keyboards.Of(rows), cancellationToken);
        }

        private static string RenderCard(UserCard card)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"<b>Пользователь u{card.UserId}</b>\n");
            if (card.TelegramId is long tg)
            {
                text.Append(CultureInfo.InvariantCulture, $"Telegram ID: <a href=\"tg://user?id={tg}\">{tg}</a>\n");
            }
            text.Append($"Статус: {(card.Status == Domain.Users.UserStatus.Blocked ? "🚫 заблокирован" : "активен")} · с {TextRenderer.FormatDate(card.CreatedAt)}\n");
            text.Append($"Триал: {YesNo(card.TrialUsed)} · бот заблокирован: {YesNo(card.BotBlocked)} · ключ сайта: {(card.HasAccountKey ? "есть" : "нет")}\n\n");

            if (card.Subscription is SubscriptionCard s)
            {
                text.Append($"<b>Подписка</b>: {TextRenderer.Encode(s.TariffName)}, {s.Status.ToString().ToLowerInvariant()}\n");
                text.Append(CultureInfo.InvariantCulture, $"До {TextRenderer.FormatDate(s.ExpiresAt)} (МСК), осталось {s.DaysLeft} дн.\n");
                text.Append($"Первое подключение: {(s.FirstConnectedAt is DateTime first ? TextRenderer.FormatDate(first) : "нет")}\n");
                text.Append($"Синхронизация: {s.SyncState.ToString().ToLowerInvariant()} ({TextRenderer.FormatDate(s.UpdatedAt)}), ссылка: {(s.HasLink ? "есть" : "нет")}\n\n");
            }
            else
            {
                text.Append("<b>Подписки нет</b>\n\n");
            }

            text.Append("<b>Платежи</b>");
            if (card.Payments.Count == 0)
            {
                text.Append(": нет\n");
            }
            else
            {
                text.Append('\n');
                foreach (PaymentCard p in card.Payments)
                {
                    string provider = p.Provider == Payment.ManualProvider ? "вне системы" : p.Provider;
                    text.Append($"• {TextRenderer.FormatDate(p.CreatedAt)} — {TextRenderer.Encode(p.Amount)} ₽ — {p.Status.ToString().ToLowerInvariant()}{(p.NeedsReview ? " ⚠️ разбор" : string.Empty)} — {TextRenderer.Encode(provider)}\n  <code>{p.Id}</code>\n");
                }
            }

            if (card.Devices is DevicesCard d)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n<b>Устройства</b>: {(d.Available ? d.Items.Count.ToString(CultureInfo.InvariantCulture) : "?")} из {d.Limit}{(d.Available ? string.Empty : " (панель недоступна)")}\n");
                foreach (var device in d.Items)
                {
                    text.Append($"• {TextRenderer.Encode(device.Platform ?? "—")} {TextRenderer.Encode(device.DeviceModel ?? string.Empty)} {TextRenderer.Encode(device.OsVersion ?? string.Empty)} — {TextRenderer.FormatDate(device.CreatedAt)}\n");
                }
            }

            text.Append(CultureInfo.InvariantCulture, $"\nСбросов устройств за 30 дней: {card.ResetsLast30Days}");
            return text.ToString();
        }

        // ── Компенсация ──────────────────────────────────────────────────────────────────────────

        public Task ExtendMenuAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            int[] options = context.Staff!.Role == StaffRole.Support ? [1, 3, 7] : [1, 3, 7, 14, 30];
            return responder.ShowAsync(
                context,
                $"Продлить u{userId} на сколько дней?",
                Keyboards.Of(
                    options.Select(d => Keyboards.Callback($"+{d}", $"s:x:{userId}:{d}")),
                    [Keyboards.Callback("« Карточка", $"s:c:{userId}")]),
                cancellationToken);
        }

        public Task ExtendChosenAsync(BotContext context, long userId, int days, CancellationToken cancellationToken)
        {
            conversations.Set(context.ChatId, new Conversation(ConversationKinds.ExtendReason, userId, days));
            return responder.SendAsync(context.ChatId, $"Причина продления u{userId} на {days} дн.? Напишите одним сообщением (или /cancel).", null, cancellationToken);
        }

        // ── Оплата вне системы ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// ТЗ 23: without an aggregator the owner takes the money and an admin records it here. Tariff → amount
        /// and comment in one message → confirmation; nothing is written before «Записать».
        /// </summary>
        public async Task ManualPaymentAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<TariffAdminView>> tariffs = await listTariffs.Handle(new ListTariffsQuery(context.Staff!.StaffId), cancellationToken);
            if (tariffs.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(tariffs.Error.Description), null, cancellationToken);
                return;
            }

            IEnumerable<InlineKeyboardButton[]> rows = tariffs.Value
                .Where(t => t.Tier != TariffTier.Trial)
                .Select(t => new[] { Keyboards.Callback($"{t.Name} — {t.PeriodDays} дн.", $"s:m:{userId}:{t.Code}") })
                .Append([Keyboards.Callback("« Карточка", $"s:c:{userId}")]);

            await responder.ShowAsync(context, $"Оплата вне системы от u{userId}. Какой тариф оплачен?", Keyboards.Of(rows), cancellationToken);
        }

        public Task ManualPaymentTariffAsync(BotContext context, long userId, string tariffCode, CancellationToken cancellationToken)
        {
            conversations.Set(context.ChatId, new Conversation(ConversationKinds.ManualPayment, userId, Tariff: tariffCode));
            return responder.SendAsync(context.ChatId, ManualPaymentHint, null, cancellationToken);
        }

        private async Task ManualPaymentInputAsync(BotContext context, long userId, string tariffCode, string text, CancellationToken cancellationToken)
        {
            if (!TryParseManualPayment(text, out decimal amount, out string comment))
            {
                conversations.Set(context.ChatId, new Conversation(ConversationKinds.ManualPayment, userId, Tariff: tariffCode));
                await responder.SendAsync(context.ChatId, "Не понял. " + ManualPaymentHint, null, cancellationToken);
                return;
            }

            Result<IReadOnlyList<TariffAdminView>> tariffs = await listTariffs.Handle(new ListTariffsQuery(context.Staff!.StaffId), cancellationToken);
            if (tariffs.IsFailure || tariffs.Value.FirstOrDefault(t => t.Code == tariffCode) is not TariffAdminView tariff)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(tariffs.IsFailure ? tariffs.Error.Description : TariffErrors.NotFound(tariffCode).Description), null, cancellationToken);
                return;
            }

            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            conversations.SetManualPayment(context.ChatId, new ManualPaymentDraft(token, userId, tariffCode, amount, comment));
            await responder.SendAsync(
                context.ChatId,
                $"Записать оплату вне системы?\nu{userId}: {TextRenderer.Encode(tariff.Name)}, {tariff.PeriodDays} дн. прибавятся к сроку\nСумма: {TextRenderer.Encode(amount)} ₽\nКомментарий: {TextRenderer.Encode(comment)}\n\nПользователь получит сообщение об оплате.",
                Keyboards.Of(
                    [Keyboards.Callback("Записать", $"s:m!:{userId}:{token}")],
                    [Keyboards.Callback("« Карточка", $"s:c:{userId}")]),
                cancellationToken);
        }

        public async Task ManualPaymentConfirmAsync(BotContext context, long userId, string token, CancellationToken cancellationToken)
        {
            InlineKeyboardMarkup back = Keyboards.Of([Keyboards.Callback("« Карточка", $"s:c:{userId}")]);
            if (conversations.TakeManualPayment(context.ChatId, userId, token) is not ManualPaymentDraft draft)
            {
                await responder.SendAsync(context.ChatId, "Эта оплата уже записана или черновик устарел. Начните заново из карточки.", back, cancellationToken);
                return;
            }

            Result<ManualPaymentRecorded> result = await recordManualPayment.Handle(
                new RecordManualPaymentCommand(context.Staff!.StaffId, draft.UserId, draft.TariffCode, draft.Amount, draft.Comment),
                cancellationToken);
            await responder.SendAsync(
                context.ChatId,
                result.IsSuccess
                    ? $"Оплата записана: u{userId} до {TextRenderer.FormatDate(result.Value.ExpiresAt)} (МСК). Пользователь получит сообщение."
                    : TextRenderer.Encode(result.Error.Description),
                back,
                cancellationToken);
        }

        /// <summary>«300 перевод на карту 05.10»: whole rubles first, then a comment of at least 3 characters.</summary>
        internal static bool TryParseManualPayment(string text, out decimal amount, out string comment)
        {
            amount = 0;
            comment = string.Empty;
            string[] parts = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || parts[1].Length < 3
                || !int.TryParse(parts[0].TrimEnd('₽'), NumberStyles.None, CultureInfo.InvariantCulture, out int rubles)
                || rubles <= 0)
            {
                return false;
            }

            amount = rubles;
            comment = parts[1];
            return true;
        }

        // ── Устройства ───────────────────────────────────────────────────────────────────────────

        public async Task DevicesAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            Result<UserCard> result = await getCard.Handle(new GetUserCardCommand(context.Staff!.StaffId, userId), cancellationToken);
            if (result.IsFailure || result.Value.Devices is not DevicesCard devices)
            {
                await responder.SendAsync(context.ChatId, result.IsFailure ? TextRenderer.Encode(result.Error.Description) : "Доступ ещё не выдан — устройств нет.", null, cancellationToken);
                return;
            }

            conversations.SetDevices(context.ChatId, userId, devices.Items.Select(d => d.Hwid).ToList());
            var rows = devices.Items
                .Select((d, i) => new[] { Keyboards.Callback($"✖ {d.Platform ?? "—"} {d.DeviceModel ?? string.Empty} {TextRenderer.FormatDate(d.CreatedAt)}", $"s:dd:{userId}:{i}") })
                .Append([Keyboards.Callback("« Карточка", $"s:c:{userId}")]);

            string text = devices.Items.Count == 0 ? $"У u{userId} нет устройств." : $"Устройства u{userId}. Нажмите, чтобы удалить:";
            await responder.ShowAsync(context, text, Keyboards.Of(rows), cancellationToken);
        }

        public async Task DeleteDeviceAsync(BotContext context, long userId, int index, CancellationToken cancellationToken)
        {
            string? hwid = conversations.GetDevice(context.ChatId, userId, index);
            if (hwid is null)
            {
                await DevicesAsync(context, userId, cancellationToken);
                return;
            }

            Result result = await deleteDevice.Handle(new StaffDeleteDeviceCommand(context.Staff!.StaffId, userId, hwid), cancellationToken);
            await responder.SendAsync(context.ChatId, result.IsSuccess ? "Устройство удалено." : TextRenderer.Encode(result.Error.Description), null, cancellationToken);
            await DevicesAsync(context, userId, cancellationToken);
        }

        public Task ResetDevicesAskAsync(BotContext context, long userId, CancellationToken cancellationToken) =>
            Confirm(context, $"Сбросить все устройства u{userId}?", $"s:r!:{userId}", userId, cancellationToken);

        public async Task ResetDevicesAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            Result<int> result = await resetDevices.Handle(new StaffResetDevicesCommand(context.Staff!.StaffId, userId), cancellationToken);
            await responder.SendAsync(context.ChatId, result.IsSuccess ? $"Сброшено устройств: {result.Value}." : TextRenderer.Encode(result.Error.Description), null, cancellationToken);
            await CardAsync(context, userId, cancellationToken);
        }

        // ── Перевыпуск ссылки ────────────────────────────────────────────────────────────────────

        public Task ReissueAskAsync(BotContext context, long userId, CancellationToken cancellationToken) =>
            Confirm(context, $"Перевыпустить ссылку u{userId}? Старая перестанет работать, устройства придётся добавить заново.", $"s:l!:{userId}", userId, cancellationToken);

        public async Task ReissueAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            Result result = await reissueLink.Handle(new ReissueLinkCommand(context.Staff!.StaffId, userId), cancellationToken);
            await responder.SendAsync(context.ChatId, result.IsSuccess ? "Ссылка перевыпущена, пользователь получит новую." : TextRenderer.Encode(result.Error.Description), null, cancellationToken);
            await CardAsync(context, userId, cancellationToken);
        }

        // ── Блокировка ───────────────────────────────────────────────────────────────────────────

        public Task BlockAskAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            conversations.Set(context.ChatId, new Conversation(ConversationKinds.BlockReason, userId));
            return responder.SendAsync(context.ChatId, $"Причина блокировки u{userId}? Напишите одним сообщением (или /cancel).", null, cancellationToken);
        }

        public async Task BlockConfirmedAsync(BotContext context, long userId, string reason, CancellationToken cancellationToken)
        {
            Result result = await blockUser.Handle(new BlockUserCommand(context.Staff!.StaffId, userId, reason), cancellationToken);
            await responder.SendAsync(context.ChatId, result.IsSuccess ? $"u{userId} заблокирован, доступ отключается." : TextRenderer.Encode(result.Error.Description), null, cancellationToken);
            await CardAsync(context, userId, cancellationToken);
        }

        public Task UnblockAskAsync(BotContext context, long userId, CancellationToken cancellationToken) =>
            Confirm(context, $"Разблокировать u{userId}?", $"s:u!:{userId}", userId, cancellationToken);

        public async Task UnblockAsync(BotContext context, long userId, CancellationToken cancellationToken)
        {
            Result result = await unblockUser.Handle(new UnblockUserCommand(context.Staff!.StaffId, userId), cancellationToken);
            await responder.SendAsync(context.ChatId, result.IsSuccess ? $"u{userId} разблокирован." : TextRenderer.Encode(result.Error.Description), null, cancellationToken);
            await CardAsync(context, userId, cancellationToken);
        }

        /// <summary>A reason, a broadcast text — whatever the pending dialog expects.</summary>
        public async Task<bool> TryHandleInputAsync(BotContext context, string text, CancellationToken cancellationToken)
        {
            Conversation? conversation = conversations.Get(context.ChatId);
            if (conversation is null)
            {
                return false;
            }
            conversations.Clear(context.ChatId);

            switch (conversation.Kind)
            {
                case ConversationKinds.ExtendReason when conversation is { UserId: long userId, Days: int days }:
                    Result<DateTime> extended = await compensate.Handle(new CompensateCommand(context.Staff!.StaffId, userId, days, text), cancellationToken);
                    await responder.SendAsync(
                        context.ChatId,
                        extended.IsSuccess ? $"Готово: u{userId} до {TextRenderer.FormatDate(extended.Value)} (МСК)." : TextRenderer.Encode(extended.Error.Description),
                        Keyboards.Of([Keyboards.Callback("« Карточка", $"s:c:{userId}")]),
                        cancellationToken);
                    return true;

                case ConversationKinds.BlockReason when conversation.UserId is long blockId:
                    await BlockConfirmedAsync(context, blockId, text, cancellationToken);
                    return true;

                case ConversationKinds.ManualPayment when conversation is { UserId: long payerId, Tariff: string tariffCode }:
                    await ManualPaymentInputAsync(context, payerId, tariffCode, text, cancellationToken);
                    return true;

                case ConversationKinds.RefundReason when conversation is { PaymentId: Guid paymentId } && Enum.TryParse(conversation.Option, out RefundKind kind):
                    await payments.RefundReasonAsync(context, paymentId, kind, text, cancellationToken);
                    return true;

                case ConversationKinds.BroadcastText when Enum.TryParse(conversation.Segment, out BroadcastSegment segment):
                    await CreateBroadcastAsync(context, segment, text, conversation.Incident, cancellationToken);
                    return true;
            }

            return false;
        }

        // ── Рассылки ─────────────────────────────────────────────────────────────────────────────

        public async Task BroadcastAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<IReadOnlyDictionary<BroadcastSegment, int>> counts = await countRecipients.Handle(new CountRecipientsQuery(context.Staff!.StaffId), cancellationToken);
            if (counts.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(counts.Error.Description), null, cancellationToken);
                return;
            }

            IEnumerable<InlineKeyboardButton[]> rows = counts.Value.Select(pair => new[]
            {
                Keyboards.Callback($"{SegmentNames[pair.Key]} ({pair.Value})", $"bc:s:{pair.Key}"),
                Keyboards.Callback("Инцидент", $"bc:i:{pair.Key}"),
            });

            await responder.ShowAsync(context, "<b>Рассылка</b>\nВыберите сегмент. «Инцидент» — готовый текст «Часть серверов недоступна, обновите подписку» с кнопкой инструкции.", Keyboards.Of(rows), cancellationToken);
        }

        public async Task BroadcastSegmentAsync(BotContext context, BroadcastSegment segment, bool incident, CancellationToken cancellationToken)
        {
            if (incident)
            {
                await CreateBroadcastAsync(context, segment, IncidentTemplate, withUpdateButton: true, cancellationToken);
                return;
            }

            conversations.Set(context.ChatId, new Conversation(ConversationKinds.BroadcastText, Segment: segment.ToString()));
            await responder.SendAsync(context.ChatId, $"Сегмент: {SegmentNames[segment]}. Пришлите текст рассылки (разметка Telegram HTML) одним сообщением или /cancel.", null, cancellationToken);
        }

        private async Task CreateBroadcastAsync(BotContext context, BroadcastSegment segment, string text, bool withUpdateButton, CancellationToken cancellationToken)
        {
            Result<BroadcastDraft> draft = await createBroadcast.Handle(
                new CreateBroadcastCommand(context.Staff!.StaffId, context.ChatId, segment, text, withUpdateButton),
                cancellationToken);
            if (draft.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(draft.Error.Description), null, cancellationToken);
                return;
            }

            // The preview comes to the author exactly as recipients will see it; broken markup shows up here.
            DeliveryResult preview = await messenger.SendBroadcastAsync(context.ChatId, text, withUpdateButton, cancellationToken);
            if (preview == DeliveryResult.Rejected)
            {
                await cancelBroadcast.Handle(new CancelBroadcastCommand(context.Staff.StaffId, draft.Value.BroadcastId), cancellationToken);
                await responder.SendAsync(context.ChatId, "Telegram не принял разметку текста. Исправьте и запустите /broadcast заново.", null, cancellationToken);
                return;
            }

            await responder.SendAsync(
                context.ChatId,
                $"Выше — предпросмотр. Сегмент: {SegmentNames[segment]}.",
                Keyboards.Of(
                    [Keyboards.Callback($"Отправить {draft.Value.Recipients} получателям", $"bc:go:{draft.Value.BroadcastId}")],
                    [Keyboards.Callback("Отмена", $"bc:x:{draft.Value.BroadcastId}")]),
                cancellationToken);
        }

        public async Task BroadcastGoAsync(BotContext context, long broadcastId, CancellationToken cancellationToken)
        {
            Result<int> result = await startBroadcast.Handle(new StartBroadcastCommand(context.Staff!.StaffId, broadcastId), cancellationToken);
            await responder.ShowAsync(
                context,
                result.IsSuccess ? $"Рассылка #{broadcastId} запущена на {result.Value} получателей. Итог придёт сюда." : TextRenderer.Encode(result.Error.Description),
                result.IsSuccess ? Keyboards.Of([Keyboards.Callback("Остановить", $"bc:x:{broadcastId}")]) : null,
                cancellationToken);
        }

        public async Task BroadcastCancelAsync(BotContext context, long broadcastId, CancellationToken cancellationToken)
        {
            Result result = await cancelBroadcast.Handle(new CancelBroadcastCommand(context.Staff!.StaffId, broadcastId), cancellationToken);
            await responder.ShowAsync(context, result.IsSuccess ? $"Рассылка #{broadcastId} остановлена." : TextRenderer.Encode(result.Error.Description), null, cancellationToken);
        }

        // ── Статистика, сотрудники, аудит, тарифы ────────────────────────────────────────────────

        public async Task StatsAsync(BotContext context, StatsPeriod period, CancellationToken cancellationToken)
        {
            Result<SalesStats> result = await getStats.Handle(new GetStatsQuery(context.Staff!.StaffId, period), cancellationToken);
            string title = period switch
            {
                StatsPeriod.Today => "сегодня",
                StatsPeriod.Yesterday => "вчера",
                StatsPeriod.Last7Days => "7 дней",
                _ => "30 дней",
            };

            await responder.ShowAsync(
                context,
                result.IsSuccess ? $"<b>Статистика: {title}</b>\n{StatsFormatter.Format(result.Value)}" : TextRenderer.Encode(result.Error.Description),
                Keyboards.Of([
                    Keyboards.Callback("Сегодня", "st:Today"),
                    Keyboards.Callback("7 дней", "st:Last7Days"),
                    Keyboards.Callback("30 дней", "st:Last30Days"),
                ]),
                cancellationToken);
        }

        public async Task StaffAsync(BotContext context, string arguments, CancellationToken cancellationToken)
        {
            string[] parts = arguments.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            long actor = context.Staff!.StaffId;

            Result outcome = parts switch
            {
                ["add", var tg, var role, var name] when long.TryParse(tg, out long telegramId) && TryParseRole(role, out StaffRole r)
                    => await AddAsync(actor, telegramId, r, name, cancellationToken),
                ["role", var id, var role] when long.TryParse(id, out long staffId) && TryParseRole(role, out StaffRole r)
                    => await changeRole.Handle(new ChangeStaffRoleCommand(actor, staffId, r), cancellationToken),
                ["off", var id] when long.TryParse(id, out long staffId)
                    => await setActive.Handle(new SetStaffActiveCommand(actor, staffId, false), cancellationToken),
                ["on", var id] when long.TryParse(id, out long staffId)
                    => await setActive.Handle(new SetStaffActiveCommand(actor, staffId, true), cancellationToken),
                [] => Result.Success(),
                _ => Result.Failure(Error.Validation("Staff.Usage", "Не понял команду.")),
            };

            if (outcome.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(outcome.Error.Description), null, cancellationToken);
            }
            else if (parts.Length > 0)
            {
                await commandsPublisher.PublishAllAsync(cancellationToken);
            }

            Result<IReadOnlyList<StaffIdentityView>> list = await listStaff.Handle(new ListStaffQuery(actor), cancellationToken);
            if (list.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(list.Error.Description), null, cancellationToken);
                return;
            }

            var text = new StringBuilder("<b>Сотрудники</b>\n");
            foreach (StaffIdentityView s in list.Value)
            {
                text.Append(CultureInfo.InvariantCulture, $"#{s.Id} {TextRenderer.Encode(s.DisplayName)} — {RoleName(s.Role)}, tg {s.TelegramId}{(s.IsActive ? string.Empty : " (отключён)")}\n");
            }
            text.Append("\nКоманды:\n<code>/staff add &lt;telegram_id&gt; &lt;support|admin|techadmin&gt; &lt;имя&gt;</code>\n<code>/staff role &lt;#id&gt; &lt;роль&gt;</code>\n<code>/staff off &lt;#id&gt;</code>, <code>/staff on &lt;#id&gt;</code>\n\nСотрудникам нужно включить облачный пароль в Telegram.");

            await responder.SendAsync(context.ChatId, text.ToString(), null, cancellationToken);
        }

        public async Task AuditAsync(BotContext context, string arguments, CancellationToken cancellationToken)
        {
            string target = arguments.Trim();
            long? userId = null, staffId = null;
            if (target.StartsWith('s') && long.TryParse(target.AsSpan(1), out long s))
            {
                staffId = s;
            }
            else if (target.StartsWith('u') && long.TryParse(target.AsSpan(1), out long u))
            {
                userId = u;
            }
            else if (target.Length > 0)
            {
                await responder.SendAsync(context.ChatId, "Формат: <code>/audit u123</code> — по пользователю, <code>/audit s5</code> — по сотруднику, без аргумента — последние записи.", null, cancellationToken);
                return;
            }

            await ShowAuditAsync(context, userId, staffId, cancellationToken);
        }

        public async Task ShowAuditAsync(BotContext context, long? userId, long? staffId, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<AuditEntryView>> result = await getAudit.Handle(new GetAuditQuery(context.Staff!.StaffId, userId, staffId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            var text = new StringBuilder("<b>Аудит</b>\n");
            foreach (AuditEntryView e in result.Value)
            {
                string who = e.ActorName is null ? "система" : TextRenderer.Encode(e.ActorName);
                string target = e.TargetUserId is long t ? $" → u{t}" : string.Empty;
                string details = e.Details is { Length: > 0 } d ? $"\n  <code>{TextRenderer.Encode(d.Length > 200 ? d[..200] + "…" : d)}</code>" : string.Empty;
                text.Append($"{TextRenderer.FormatDate(e.CreatedAt)} {who}: {TextRenderer.Encode(e.Action)}{target}{details}\n");
            }

            await responder.SendAsync(context.ChatId, text.ToString(), null, cancellationToken);
        }

        // ── Промокоды ────────────────────────────────────────────────────────────────────────────

        /// <summary><c>/promos</c>: list, <c>new</c>, <c>off</c>, or the statistics of one code (FR-ADM-15).</summary>
        public async Task PromosAsync(BotContext context, string arguments, CancellationToken cancellationToken)
        {
            long actor = context.Staff!.StaffId;
            string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            switch (parts)
            {
                case []:
                    await ListPromosAsync(context, cancellationToken);
                    return;

                case ["new", .. var spec]:
                    if (!TryParsePromo(spec, out PromoDraft? draft, out string problem))
                    {
                        await responder.SendAsync(context.ChatId, $"{TextRenderer.Encode(problem)}\n\n{PromoUsage}", null, cancellationToken);
                        return;
                    }

                    Result<PromoCodeView> created = await createPromo.Handle(
                        new CreatePromoCodeCommand(actor, draft.Code, draft.Type, draft.Value, draft.MaxUses, draft.ValidFrom, draft.ValidTo),
                        cancellationToken);
                    await responder.SendAsync(
                        context.ChatId,
                        created.IsSuccess
                            ? $"Промокод создан:\n{PromoLine(created.Value)}\n\n{PromoLink(created.Value.Code)}"
                            : TextRenderer.Encode(created.Error.Description),
                        null,
                        cancellationToken);
                    return;

                case ["off", var code]:
                    await PromoOffAsync(context, code, cancellationToken);
                    return;

                case [var code]:
                    await PromoDetailsAsync(context, code, cancellationToken);
                    return;

                default:
                    await responder.SendAsync(context.ChatId, "Не понял команду.\n\n" + PromoUsage, null, cancellationToken);
                    return;
            }
        }

        public Task PromoOffAskAsync(BotContext context, string code, CancellationToken cancellationToken) =>
            responder.ShowAsync(
                context,
                $"Выключить промокод {TextRenderer.Encode(code)}? Включить его снова нельзя — только создать новый. Уже созданные платежи со скидкой можно будет оплатить.",
                Keyboards.Of([Keyboards.Callback("Выключить", $"pr:off!:{code}")], [Keyboards.Callback("« Промокоды", "pr:list")]),
                cancellationToken);

        public async Task PromoOffAsync(BotContext context, string code, CancellationToken cancellationToken)
        {
            Result result = await deactivatePromo.Handle(new DeactivatePromoCodeCommand(context.Staff!.StaffId, code), cancellationToken);
            await responder.SendAsync(
                context.ChatId,
                result.IsSuccess ? $"Промокод {TextRenderer.Encode(code.ToUpperInvariant())} выключен." : TextRenderer.Encode(result.Error.Description),
                null,
                cancellationToken);
        }

        public async Task ListPromosAsync(BotContext context, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<PromoCodeView>> result = await listPromos.Handle(new ListPromoCodesQuery(context.Staff!.StaffId), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            var text = new StringBuilder("<b>Промокоды</b>\n");
            if (result.Value.Count == 0)
            {
                text.Append("Пока нет.\n");
            }
            foreach (PromoCodeView promo in result.Value)
            {
                text.Append(PromoLine(promo)).Append('\n');
            }
            text.Append('\n').Append(PromoUsage);

            await responder.ShowAsync(context, text.ToString(), null, cancellationToken);
        }

        public async Task PromoDetailsAsync(BotContext context, string code, CancellationToken cancellationToken)
        {
            Result<PromoCodeDetails> result = await getPromo.Handle(new GetPromoCodeQuery(context.Staff!.StaffId, code), cancellationToken);
            if (result.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(result.Error.Description), null, cancellationToken);
                return;
            }

            PromoCodeDetails details = result.Value;
            PromoCodeView promo = details.Promo;
            var text = new StringBuilder($"<b>Промокод {promo.Code}</b>\n{PromoLine(promo)}\n\n");
            if (promo.Type == PromoType.DiscountPercent)
            {
                text.Append(CultureInfo.InvariantCulture, $"Оплачено со скидкой: {details.PaidPayments} на {TextRenderer.Encode(details.PaidSum)} ₽\n");
                text.Append(CultureInfo.InvariantCulture, $"Ввели и ещё не оплатили: {details.WaitingUsers}\n");
            }
            else
            {
                text.Append(CultureInfo.InvariantCulture, $"Выдано бонусных дней: {details.BonusDaysGiven}\n");
            }

            if (details.LastUses.Count > 0)
            {
                text.Append("\nПоследние использования:\n");
                foreach (PromoUseView use in details.LastUses)
                {
                    string what = use.Amount is decimal amount ? $"{TextRenderer.Encode(amount)} ₽" : $"+{use.BonusDays} дн.";
                    text.Append(CultureInfo.InvariantCulture, $"• {TextRenderer.FormatDate(use.At)} u{use.UserId} — {what}\n");
                }
            }
            text.Append('\n').Append(PromoLink(promo.Code));

            await responder.ShowAsync(
                context,
                text.ToString(),
                promo.IsActive ? Keyboards.Of([Keyboards.Callback("Выключить", $"pr:off:{promo.Code}")]) : null,
                cancellationToken);
        }

        /// <summary><c>AUTUMN 20% 100 31.10.2026</c>: code, value, then optionally a use limit and one or two dates.</summary>
        internal static bool TryParsePromo(string[] spec, [NotNullWhen(true)] out PromoDraft? draft, out string problem)
        {
            draft = null;
            problem = string.Empty;
            if (spec.Length < 2)
            {
                problem = "Укажите код и значение.";
                return false;
            }

            string code = spec[0];
            string value = spec[1].ToLowerInvariant().TrimStart('+');
            PromoType type;
            int amount;
            if (value.EndsWith('%') && int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out amount))
            {
                type = PromoType.DiscountPercent;
            }
            else if (TryParseDays(value, out amount))
            {
                type = PromoType.BonusDays;
            }
            else
            {
                problem = $"Не понял значение «{spec[1]}»: нужно «20%» или «7д».";
                return false;
            }

            int? maxUses = null;
            var dates = new List<DateOnly>();
            foreach (string token in spec[2..])
            {
                if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int limit) && maxUses is null)
                {
                    maxUses = limit;
                }
                else if (DateOnly.TryParseExact(token, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) && dates.Count < 2)
                {
                    dates.Add(day);
                }
                else
                {
                    problem = $"Не понял «{token}»: после значения — лимит (число) и даты ДД.ММ.ГГГГ.";
                    return false;
                }
            }

            dates.Sort();
            // Moscow days, both inclusive: «до 31.10.2026» works through the end of the 31st.
            DateTime? validFrom = dates.Count == 2 ? MoscowTime.StartOfDayUtc(dates[0]) : null;
            DateTime? validTo = dates.Count > 0 ? MoscowTime.StartOfDayUtc(dates[^1].AddDays(1)) : null;

            draft = new PromoDraft(code, type, amount, maxUses, validFrom, validTo);
            return true;
        }

        private static bool TryParseDays(string value, out int days)
        {
            foreach (string suffix in new[] { "дней", "дн", "д", "days", "d" })
            {
                if (value.EndsWith(suffix, StringComparison.Ordinal)
                    && int.TryParse(value.AsSpan(0, value.Length - suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out days))
                {
                    return true;
                }
            }

            days = 0;
            return false;
        }

        private static string PromoLine(PromoCodeView promo)
        {
            string value = promo.Type == PromoType.DiscountPercent ? $"−{promo.Value} %" : $"+{promo.Value} дн.";
            string uses = promo.MaxUses is int max ? $"{promo.UsedCount} из {max}" : $"{promo.UsedCount} исп.";
            string window = (promo.ValidFrom, promo.ValidTo) switch
            {
                (DateTime from, DateTime to) => $"{Day(from)}–{Day(to.AddTicks(-1))}",
                (null, DateTime to) => $"до {Day(to.AddTicks(-1))}",
                (DateTime from, null) => $"с {Day(from)}",
                _ => "без срока",
            };
            return $"<code>{promo.Code}</code> {value} · {uses} · {window}{(promo.IsActive ? string.Empty : " · выключен")}";
        }

        private static string Day(DateTime utc) => MoscowTime.FromUtc(utc).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        private string PromoLink(string code) =>
            string.IsNullOrEmpty(serviceOptions.Value.BotUrl)
                ? $"Ссылка для поста: <code>/start {UserHandler.PromoStartPrefix}{code}</code> (имя бота не задано в Service:BotUsername)"
                : $"Ссылка для поста: {serviceOptions.Value.BotUrl}?start={UserHandler.PromoStartPrefix}{code}";

        private async Task<Result> AddAsync(long actor, long telegramId, StaffRole role, string name, CancellationToken cancellationToken)
        {
            Result<long> added = await addStaff.Handle(new AddStaffCommand(actor, telegramId, role, name), cancellationToken);
            return added.IsSuccess ? Result.Success() : Result.Failure(added.Error);
        }

        private Task Confirm(BotContext context, string question, string confirmData, long userId, CancellationToken cancellationToken) =>
            responder.ShowAsync(
                context,
                question,
                Keyboards.Of(
                    [Keyboards.Callback("Да", confirmData)],
                    [Keyboards.Callback("« Карточка", $"s:c:{userId}")]),
                cancellationToken);

        private static bool TryParseRole(string value, out StaffRole role)
        {
            (bool ok, role) = value.ToLowerInvariant() switch
            {
                "support" => (true, StaffRole.Support),
                "admin" => (true, StaffRole.Admin),
                "techadmin" => (true, StaffRole.TechAdmin),
                _ => (false, default),
            };
            return ok;
        }

        private static string RoleName(StaffRole role) => role switch
        {
            StaffRole.Support => "саппорт",
            StaffRole.Admin => "админ",
            _ => "техадмин",
        };

        private static string YesNo(bool value) => value ? "да" : "нет";
    }

    /// <summary>A parsed <c>/promos new</c>; the Application layer validates the values.</summary>
    internal sealed record PromoDraft(string Code, PromoType Type, int Value, int? MaxUses, DateTime? ValidFrom, DateTime? ValidTo);
}
