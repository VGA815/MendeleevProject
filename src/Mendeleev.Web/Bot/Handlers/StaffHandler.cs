using System.Globalization;
using System.Text;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Admin.Audit;
using Mendeleev.Application.Admin.Broadcasts;
using Mendeleev.Application.Admin.Staff;
using Mendeleev.Application.Admin.Stats;
using Mendeleev.Application.Admin.Tariffs;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Infrastructure;
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
        IQueryHandler<ListTariffsQuery, IReadOnlyList<TariffAdminView>> listTariffs)
    {
        private const string IncidentTemplate = "Часть серверов недоступна. Обновите подписку в приложении.";

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
                    text.Append($"• {TextRenderer.FormatDate(p.CreatedAt)} — {TextRenderer.Encode(p.Amount)} ₽ — {p.Status.ToString().ToLowerInvariant()}{(p.NeedsReview ? " ⚠️ разбор" : string.Empty)} — {TextRenderer.Encode(p.Provider)}\n  <code>{p.Id}</code>\n");
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

        public async Task TariffsAsync(BotContext context, CancellationToken cancellationToken)
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
                string traffic = t.TrafficLimitBytes is long bytes ? TextRenderer.FormatBytes(bytes) : "без лимита";
                text.Append(CultureInfo.InvariantCulture, $"<code>{t.Code}</code> {TextRenderer.Encode(t.Name)} — {TextRenderer.Encode(t.Price)} ₽, {t.PeriodDays} дн., {t.DeviceLimit} устр., {traffic}{(t.IsActive ? string.Empty : " (выключен)")}\n");
            }
            text.Append("\nЦены меняет техадмин в БД по заявке админа; команда для этого — на этапе 1.5.");

            await responder.SendAsync(context.ChatId, text.ToString(), null, cancellationToken);
        }

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
}
