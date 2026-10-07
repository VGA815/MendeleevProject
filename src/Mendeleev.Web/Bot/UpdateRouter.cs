using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Accounts.EnsureTelegramUser;
using Mendeleev.Application.Accounts.SetBotBlocked;
using Mendeleev.Application.Admin.Staff;
using Mendeleev.Application.Admin.Stats;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Handlers;
using Mendeleev.Web.Bot.Infrastructure;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Mendeleev.Web.Bot
{
    /// <summary>
    /// Entry point for every update: rate limit, account, staff check, then the command or button. A
    /// blocked user sees only the blocked message; a non-staff user typing a staff command gets the menu,
    /// as for any unknown command (ТЗ 28: «без подсказок о существовании команд»).
    /// </summary>
    internal sealed class UpdateRouter(
        BotRateLimiter rateLimiter,
        BotResponder responder,
        ConversationStore conversations,
        UserHandler user,
        StaffHandler staff,
        IOptionsMonitor<BotContent> content,
        ICommandHandler<EnsureTelegramUserCommand, TelegramUserState> ensureUser,
        ICommandHandler<SetBotBlockedCommand> setBotBlocked,
        IQueryHandler<GetActiveStaffQuery, StaffIdentity?> getStaff,
        ILogger<UpdateRouter> logger)
    {
        private BotContent C => content.CurrentValue;

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            switch (update)
            {
                case { MyChatMember: { Chat.Type: ChatType.Private } member }:
                    bool blocked = member.NewChatMember.Status is ChatMemberStatus.Kicked or ChatMemberStatus.Left;
                    await setBotBlocked.Handle(new SetBotBlockedCommand(member.From.Id, blocked), cancellationToken);
                    return;

                case { Message: { Chat.Type: ChatType.Private, From: not null, Text: not null } message }:
                    await HandleMessageAsync(message, cancellationToken);
                    return;

                case { CallbackQuery: { Data: not null } callback }:
                    await HandleCallbackAsync(callback, cancellationToken);
                    return;
            }
        }

        private async Task HandleMessageAsync(Message message, CancellationToken cancellationToken)
        {
            long telegramId = message.From!.Id;
            if (!await PassRateLimitAsync(telegramId, message.Chat.Id, callbackId: null, cancellationToken))
            {
                return;
            }

            BotContext? context = await CreateContextAsync(telegramId, message.Chat.Id, callbackMessageId: null, cancellationToken);
            if (context is null)
            {
                return;
            }

            string text = message.Text!.Trim();
            (string command, string arguments) = ParseCommand(text);

            if (command == "/cancel")
            {
                conversations.Clear(context.ChatId);
                conversations.ClearPromoPrompt(context.ChatId);
                await user.MenuAsync(context, cancellationToken);
                return;
            }

            if (context.Staff is not null && command.Length == 0 && await staff.TryHandleInputAsync(context, text, cancellationToken))
            {
                return;
            }

            if (context.User.IsBlocked && context.Staff is null)
            {
                await SendBlockedAsync(context, cancellationToken);
                return;
            }

            if (context.Staff is not null && await TryStaffCommandAsync(context, command, arguments, cancellationToken))
            {
                return;
            }

            // «Ввести промокод» was pressed: this message is the code. Any command cancels the wait.
            if (conversations.TakePromoPrompt(context.ChatId) && command.Length == 0)
            {
                await user.ApplyPromoAsync(context, text, cancellationToken);
                return;
            }

            switch (command)
            {
                case "/start":
                    await user.StartAsync(context, arguments, cancellationToken);
                    break;
                case "/promo" when arguments.Length > 0:
                    await user.ApplyPromoAsync(context, arguments, cancellationToken);
                    break;
                case "/promo":
                    await user.PromoAskAsync(context, cancellationToken);
                    break;
                case "/sub":
                    await user.SubscriptionAsync(context, cancellationToken);
                    break;
                case "/buy":
                    await user.TariffsAsync(context, cancellationToken);
                    break;
                case "/devices":
                    await user.DevicesAsync(context, cancellationToken);
                    break;
                case "/help":
                    await user.HelpAsync(context, cancellationToken);
                    break;
                case "/key":
                    await user.KeyAsync(context, cancellationToken);
                    break;
                case "/link":
                    await user.IssueLinkCodeAsync(context, cancellationToken);
                    break;
                default:
                    await user.MenuAsync(context, cancellationToken);
                    break;
            }
        }

        private async Task<bool> TryStaffCommandAsync(BotContext context, string command, string arguments, CancellationToken cancellationToken)
        {
            StaffIdentity member = context.Staff!;
            switch (command)
            {
                case "/find":
                    await staff.FindAsync(context, arguments, cancellationToken);
                    return true;
                case "/broadcast" when member.Can(StaffPermission.Broadcast):
                    await staff.BroadcastAsync(context, cancellationToken);
                    return true;
                case "/stats" when member.Can(StaffPermission.ViewStats):
                    await staff.StatsAsync(context, StatsPeriod.Today, cancellationToken);
                    return true;
                case "/staff" when member.Can(StaffPermission.ManageStaff):
                    await staff.StaffAsync(context, arguments, cancellationToken);
                    return true;
                case "/audit" when member.Can(StaffPermission.ViewAudit):
                    await staff.AuditAsync(context, arguments, cancellationToken);
                    return true;
                case "/tariffs" when member.Can(StaffPermission.ViewTariffs):
                    await staff.TariffsAsync(context, cancellationToken);
                    return true;
                case "/promos" when member.Can(StaffPermission.ManagePromos):
                    await staff.PromosAsync(context, arguments, cancellationToken);
                    return true;
                default:
                    return false;
            }
        }

        private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken)
        {
            long chatId = callback.Message?.Chat.Id ?? callback.From.Id;
            if (callback.Message?.Chat.Type is not (null or ChatType.Private))
            {
                await responder.AnswerCallbackAsync(callback.Id, null, cancellationToken);
                return;
            }

            if (!await PassRateLimitAsync(callback.From.Id, chatId, callback.Id, cancellationToken))
            {
                return;
            }

            await responder.AnswerCallbackAsync(callback.Id, null, cancellationToken);

            BotContext? context = await CreateContextAsync(callback.From.Id, chatId, callback.Message?.Id, cancellationToken);
            if (context is null)
            {
                return;
            }

            string data = callback.Data!;

            if (data.StartsWith("s:", StringComparison.Ordinal) || data.StartsWith("bc:", StringComparison.Ordinal)
                || data.StartsWith("st:", StringComparison.Ordinal) || data.StartsWith("pr:", StringComparison.Ordinal))
            {
                if (context.Staff is not null)
                {
                    await HandleStaffCallbackAsync(context, data, cancellationToken);
                }
                return;
            }

            if (context.User.IsBlocked)
            {
                await SendBlockedAsync(context, cancellationToken);
                return;
            }

            switch (data)
            {
                case Cb.Menu:
                    await user.MenuAsync(context, cancellationToken);
                    break;
                case Cb.Subscription:
                    await user.SubscriptionAsync(context, cancellationToken);
                    break;
                case Cb.Buy:
                    await user.TariffsAsync(context, cancellationToken);
                    break;
                case Cb.Trial:
                    await user.TrialAsync(context, cancellationToken);
                    break;
                case Cb.Connect:
                    await user.ConnectAsync(context, cancellationToken);
                    break;
                case Cb.Devices:
                    await user.DevicesAsync(context, cancellationToken);
                    break;
                case Cb.ResetDevicesAsk:
                    await user.ResetDevicesAskAsync(context, cancellationToken);
                    break;
                case Cb.ResetDevicesDo:
                    await user.ResetDevicesAsync(context, cancellationToken);
                    break;
                case Cb.Help:
                    await user.HelpAsync(context, cancellationToken);
                    break;
                case Cb.Key:
                    await user.KeyAsync(context, cancellationToken);
                    break;
                case Cb.KeyIssue:
                    await user.IssueKeyAsync(context, cancellationToken);
                    break;
                case Cb.LinkCode:
                    await user.IssueLinkCodeAsync(context, cancellationToken);
                    break;
                case Cb.Qr:
                    await user.QrAsync(context, cancellationToken);
                    break;
                case Cb.Promo:
                    await user.PromoAskAsync(context, cancellationToken);
                    break;
                case var d when d.StartsWith(Cb.Tariff, StringComparison.Ordinal):
                    await user.ConfirmTariffAsync(context, d[Cb.Tariff.Length..], cancellationToken);
                    break;
                case var d when d.StartsWith(Cb.Pay, StringComparison.Ordinal):
                    await user.PayAsync(context, d[Cb.Pay.Length..], cancellationToken);
                    break;
                case var d when d.StartsWith(Cb.Check, StringComparison.Ordinal) && Guid.TryParseExact(d[Cb.Check.Length..], "N", out Guid paymentId):
                    await user.CheckPaymentAsync(context, paymentId, cancellationToken);
                    break;
                case var d when d.StartsWith(Cb.Platform, StringComparison.Ordinal):
                    await user.PlatformAsync(context, d[Cb.Platform.Length..], cancellationToken);
                    break;
                case var d when d.StartsWith(Cb.Faq, StringComparison.Ordinal):
                    await user.FaqAsync(context, d[Cb.Faq.Length..], cancellationToken);
                    break;
                default:
                    await user.MenuAsync(context, cancellationToken);
                    break;
            }
        }

        private async Task HandleStaffCallbackAsync(BotContext context, string data, CancellationToken cancellationToken)
        {
            string[] parts = data.Split(':');
            switch (parts)
            {
                case ["s", "c", var id] when long.TryParse(id, out long userId):
                    await staff.CardAsync(context, userId, cancellationToken);
                    break;
                case ["s", "x", var id] when long.TryParse(id, out long userId):
                    await staff.ExtendMenuAsync(context, userId, cancellationToken);
                    break;
                case ["s", "x", var id, var days] when long.TryParse(id, out long userId) && int.TryParse(days, out int d):
                    await staff.ExtendChosenAsync(context, userId, d, cancellationToken);
                    break;
                case ["s", "d", var id] when long.TryParse(id, out long userId):
                    await staff.DevicesAsync(context, userId, cancellationToken);
                    break;
                case ["s", "dd", var id, var index] when long.TryParse(id, out long userId) && int.TryParse(index, out int i):
                    await staff.DeleteDeviceAsync(context, userId, i, cancellationToken);
                    break;
                case ["s", "r", var id] when long.TryParse(id, out long userId):
                    await staff.ResetDevicesAskAsync(context, userId, cancellationToken);
                    break;
                case ["s", "r!", var id] when long.TryParse(id, out long userId):
                    await staff.ResetDevicesAsync(context, userId, cancellationToken);
                    break;
                case ["s", "l", var id] when long.TryParse(id, out long userId):
                    await staff.ReissueAskAsync(context, userId, cancellationToken);
                    break;
                case ["s", "l!", var id] when long.TryParse(id, out long userId):
                    await staff.ReissueAsync(context, userId, cancellationToken);
                    break;
                case ["s", "b", var id] when long.TryParse(id, out long userId):
                    await staff.BlockAskAsync(context, userId, cancellationToken);
                    break;
                case ["s", "u", var id] when long.TryParse(id, out long userId):
                    await staff.UnblockAskAsync(context, userId, cancellationToken);
                    break;
                case ["s", "u!", var id] when long.TryParse(id, out long userId):
                    await staff.UnblockAsync(context, userId, cancellationToken);
                    break;
                case ["s", "a", var id] when long.TryParse(id, out long userId):
                    await staff.ShowAuditAsync(context, userId, null, cancellationToken);
                    break;
                case ["s", "m", var id] when long.TryParse(id, out long userId):
                    await staff.ManualPaymentAsync(context, userId, cancellationToken);
                    break;
                case ["s", "m", var id, var tariffCode] when long.TryParse(id, out long userId):
                    await staff.ManualPaymentTariffAsync(context, userId, tariffCode, cancellationToken);
                    break;
                case ["s", "m!", var id, var token] when long.TryParse(id, out long userId):
                    await staff.ManualPaymentConfirmAsync(context, userId, token, cancellationToken);
                    break;
                case ["bc", "s", var segment] when Enum.TryParse(segment, out BroadcastSegment s):
                    await staff.BroadcastSegmentAsync(context, s, incident: false, cancellationToken);
                    break;
                case ["bc", "i", var segment] when Enum.TryParse(segment, out BroadcastSegment s):
                    await staff.BroadcastSegmentAsync(context, s, incident: true, cancellationToken);
                    break;
                case ["bc", "go", var id] when long.TryParse(id, out long broadcastId):
                    await staff.BroadcastGoAsync(context, broadcastId, cancellationToken);
                    break;
                case ["bc", "x", var id] when long.TryParse(id, out long broadcastId):
                    await staff.BroadcastCancelAsync(context, broadcastId, cancellationToken);
                    break;
                case ["st", var period] when Enum.TryParse(period, out StatsPeriod p):
                    await staff.StatsAsync(context, p, cancellationToken);
                    break;
                case ["pr", "list"]:
                    await staff.ListPromosAsync(context, cancellationToken);
                    break;
                case ["pr", "off", var code]:
                    await staff.PromoOffAskAsync(context, code, cancellationToken);
                    break;
                case ["pr", "off!", var code]:
                    await staff.PromoOffAsync(context, code, cancellationToken);
                    break;
                default:
                    logger.LogDebug("Unknown staff callback");
                    break;
            }
        }

        private async Task<BotContext?> CreateContextAsync(long telegramId, long chatId, int? callbackMessageId, CancellationToken cancellationToken)
        {
            Result<TelegramUserState> state = await ensureUser.Handle(new EnsureTelegramUserCommand(telegramId), cancellationToken);
            if (state.IsFailure)
            {
                await responder.SendAsync(chatId, C.Text("Error"), null, cancellationToken);
                return null;
            }

            StaffIdentity? member = (await getStaff.Handle(new GetActiveStaffQuery(telegramId), cancellationToken)).Value;

            return new BotContext
            {
                ChatId = chatId,
                TelegramId = telegramId,
                User = state.Value,
                Staff = member,
                CallbackMessageId = callbackMessageId,
            };
        }

        private async Task<bool> PassRateLimitAsync(long telegramId, long chatId, string? callbackId, CancellationToken cancellationToken)
        {
            switch (rateLimiter.Check(telegramId))
            {
                case RateDecision.Allow:
                    return true;
                case RateDecision.Warn:
                    if (callbackId is not null)
                    {
                        await responder.AnswerCallbackAsync(callbackId, C.Text("TooManyRequests"), cancellationToken);
                    }
                    else
                    {
                        await responder.SendAsync(chatId, C.Text("TooManyRequests"), null, cancellationToken);
                    }
                    return false;
                default:
                    return false;
            }
        }

        private Task SendBlockedAsync(BotContext context, CancellationToken cancellationToken) =>
            responder.ShowAsync(context, C.Text("Blocked"), Keyboards.Of(Keyboards.Support(C)), cancellationToken);

        /// <summary><c>/find@bot 123</c> → (<c>/find</c>, <c>123</c>). Plain text has an empty command.</summary>
        internal static (string Command, string Arguments) ParseCommand(string text)
        {
            if (!text.StartsWith('/'))
            {
                return (string.Empty, text);
            }

            int space = text.IndexOf(' ');
            string head = space < 0 ? text : text[..space];
            string arguments = space < 0 ? string.Empty : text[(space + 1)..].Trim();
            int at = head.IndexOf('@');
            return ((at < 0 ? head : head[..at]).ToLowerInvariant(), arguments);
        }
    }
}
