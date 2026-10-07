using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Admin.Compensation;
using Mendeleev.Domain.Common;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Bot.Content;
using Mendeleev.Web.Bot.Infrastructure;
using Telegram.Bot.Types.ReplyMarkups;

namespace Mendeleev.Web.Bot.Handlers
{
    /// <summary>
    /// <c>/compensate</c>: a mass compensation after an outage (FR-SUB-16, FR-ADM-16; решение 07.10). The admin sees
    /// how many subscriptions it takes before confirming and can switch between «with access now» and «had access
    /// during the outage», with or without trials. A mass action, so it is confirmed by a button (ТЗ 28).
    /// </summary>
    internal sealed partial class StaffCompensationHandler(
        BotResponder responder,
        ConversationStore conversations,
        IDateTimeProvider clock,
        IQueryHandler<CountMassCompensationQuery, int> countRecipients,
        ICommandHandler<MassCompensateCommand, MassCompensationResult> compensate)
    {
        private const string Usage =
            "Формат: <code>/compensate 3 сбой нод вечером 07.10</code> — число дней (от 1 до 365) и причина: её получит каждый пользователь.";

        private const string Stale = "Черновик компенсации устарел. Начните заново: /compensate.";

        public async Task StartAsync(BotContext context, string arguments, CancellationToken cancellationToken)
        {
            string[] parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is not [var daysText, var reason]
                || !int.TryParse(daysText, NumberStyles.None, CultureInfo.InvariantCulture, out int days)
                || days is < 1 or > 365
                || reason.Length is < 3 or > 300)
            {
                await responder.SendAsync(context.ChatId, Usage, null, cancellationToken);
                return;
            }

            var draft = new MassCompensationDraft(NewToken(), days, reason, CompensationSegment.ActiveNow, IncludeTrial: false, From: null, To: null);
            conversations.SetDraft(context.ChatId, draft);
            await PreviewAsync(context, draft, cancellationToken);
        }

        public async Task ActiveNowAsync(BotContext context, string token, CancellationToken cancellationToken)
        {
            if (conversations.GetDraft<MassCompensationDraft>(context.ChatId, token) is not MassCompensationDraft draft)
            {
                await responder.SendAsync(context.ChatId, Stale, null, cancellationToken);
                return;
            }

            draft = draft with { Segment = CompensationSegment.ActiveNow, From = null, To = null };
            conversations.SetDraft(context.ChatId, draft);
            await PreviewAsync(context, draft, cancellationToken);
        }

        public async Task AskWindowAsync(BotContext context, string token, CancellationToken cancellationToken)
        {
            if (conversations.GetDraft<MassCompensationDraft>(context.ChatId, token) is null)
            {
                await responder.SendAsync(context.ChatId, Stale, null, cancellationToken);
                return;
            }

            conversations.Set(context.ChatId, new Conversation(ConversationKinds.CompensationWindow, Option: token));
            await responder.SendAsync(
                context.ChatId,
                "Период сбоя по Москве одним сообщением: <code>07.10 14:00 07.10 18:30</code> (можно с годом: 07.10.2026). Или /cancel.",
                null,
                cancellationToken);
        }

        public async Task WindowAsync(BotContext context, string token, string text, CancellationToken cancellationToken)
        {
            if (conversations.GetDraft<MassCompensationDraft>(context.ChatId, token) is not MassCompensationDraft draft)
            {
                await responder.SendAsync(context.ChatId, Stale, null, cancellationToken);
                return;
            }

            if (!TryParseWindow(text, clock.UtcNow, out DateTime from, out DateTime to))
            {
                conversations.Set(context.ChatId, new Conversation(ConversationKinds.CompensationWindow, Option: token));
                await responder.SendAsync(context.ChatId, "Не понял период. Пример: <code>07.10 14:00 07.10 18:30</code> — начало раньше конца. Или /cancel.", null, cancellationToken);
                return;
            }

            draft = draft with { Segment = CompensationSegment.ActiveDuringOutage, From = from, To = to };
            conversations.SetDraft(context.ChatId, draft);
            await PreviewAsync(context, draft, cancellationToken);
        }

        public async Task ToggleTrialAsync(BotContext context, string token, CancellationToken cancellationToken)
        {
            if (conversations.GetDraft<MassCompensationDraft>(context.ChatId, token) is not MassCompensationDraft draft)
            {
                await responder.SendAsync(context.ChatId, Stale, null, cancellationToken);
                return;
            }

            draft = draft with { IncludeTrial = !draft.IncludeTrial };
            conversations.SetDraft(context.ChatId, draft);
            await PreviewAsync(context, draft, cancellationToken);
        }

        public async Task GoAsync(BotContext context, string token, CancellationToken cancellationToken)
        {
            if (conversations.TakeDraft<MassCompensationDraft>(context.ChatId, token) is not MassCompensationDraft draft)
            {
                await responder.SendAsync(context.ChatId, "Эта компенсация уже выполнена или черновик устарел.", null, cancellationToken);
                return;
            }

            await responder.ShowAsync(context, $"Продлеваю подписки на {draft.Days} дн.… Итог придёт сюда.", null, cancellationToken);
            Result<MassCompensationResult> result = await compensate.Handle(
                new MassCompensateCommand(context.Staff!.StaffId, draft.Spec, draft.Reason),
                cancellationToken);

            await responder.SendAsync(
                context.ChatId,
                result.IsSuccess
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"Готово: продлено подписок — {result.Value.Extended} на {draft.Days} дн. Пропущено (изменились за время работы) — {result.Value.Skipped}. Пользователи получат сообщение с причиной.")
                    : TextRenderer.Encode(result.Error.Description),
                null,
                cancellationToken);
        }

        public Task CancelAsync(BotContext context, string token, CancellationToken cancellationToken)
        {
            conversations.TakeDraft<MassCompensationDraft>(context.ChatId, token);
            return responder.ShowAsync(context, "Массовая компенсация отменена.", null, cancellationToken);
        }

        private async Task PreviewAsync(BotContext context, MassCompensationDraft draft, CancellationToken cancellationToken)
        {
            Result<int> count = await countRecipients.Handle(new CountMassCompensationQuery(context.Staff!.StaffId, draft.Spec), cancellationToken);
            if (count.IsFailure)
            {
                await responder.SendAsync(context.ChatId, TextRenderer.Encode(count.Error.Description), null, cancellationToken);
                return;
            }

            bool window = draft.Segment == CompensationSegment.ActiveDuringOutage;
            string whom = window
                ? $"все, у кого был доступ во время сбоя {TextRenderer.FormatDate(draft.From!.Value)} — {TextRenderer.FormatDate(draft.To!.Value)} (МСК), в том числе те, у кого срок вышел за это время"
                : "все, у кого доступ есть сейчас";
            string text =
                $"<b>Массовая компенсация</b>: +{draft.Days} дн.\n" +
                $"Причина — её получит каждый: {TextRenderer.Encode(draft.Reason)}\n" +
                $"Кому: {whom}, {(draft.IncludeTrial ? "вместе с триалом" : "без триала")} — <b>{count.Value}</b> подписок.\n\n" +
                "Заблокированные, архивные, закончившиеся по трафику триала и после возврата не продлеваются.";

            var rows = new List<InlineKeyboardButton[]>
            {
                new[]
                {
                    Keyboards.Callback((window ? string.Empty : "✓ ") + "С доступом сейчас", $"mc:now:{draft.Token}"),
                    Keyboards.Callback((window ? "✓ " : string.Empty) + "Во время сбоя…", $"mc:win:{draft.Token}"),
                },
                new[] { Keyboards.Callback(draft.IncludeTrial ? "Триал: включён ✓" : "Триал: не включён", $"mc:tr:{draft.Token}") },
            };
            if (count.Value > 0)
            {
                rows.Add([Keyboards.Callback(string.Create(CultureInfo.InvariantCulture, $"Продлить {count.Value}"), $"mc:go:{draft.Token}")]);
            }
            rows.Add([Keyboards.Callback("Отмена", $"mc:x:{draft.Token}")]);

            await responder.ShowAsync(context, text, Keyboards.Of(rows), cancellationToken);
        }

        /// <summary>
        /// <c>07.10 14:00 07.10 18:30</c> (Moscow time; the year may be given: <c>07.10.2026</c>). Without a year a date
        /// later than tomorrow is taken from last year — an outage is in the past.
        /// </summary>
        internal static bool TryParseWindow(string text, DateTime utcNow, out DateTime fromUtc, out DateTime toUtc)
        {
            fromUtc = toUtc = default;
            MatchCollection moments = Moment().Matches(text);
            if (moments.Count != 2
                || !TryParseMoment(moments[0], utcNow, out DateTime from)
                || !TryParseMoment(moments[1], utcNow, out DateTime to)
                || from >= to)
            {
                return false;
            }

            fromUtc = from;
            toUtc = to;
            return true;
        }

        private static bool TryParseMoment(Match match, DateTime utcNow, out DateTime utc)
        {
            utc = default;
            DateTime moscowNow = MoscowTime.FromUtc(utcNow);
            int day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
            int month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
            int year = match.Groups["y"].Success ? int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture) : moscowNow.Year;
            int hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            int minute = int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);

            if (year is < 2020 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
            {
                return false;
            }

            var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            if (!match.Groups["y"].Success && local > moscowNow.AddDays(1))
            {
                local = local.AddYears(-1);
            }

            utc = MoscowTime.ToUtc(local);
            return true;
        }

        private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

        [GeneratedRegex(@"(?<d>\d{1,2})\.(?<m>\d{1,2})(?:\.(?<y>\d{4}))?\s+(?<h>\d{1,2}):(?<min>\d{2})", RegexOptions.CultureInvariant)]
        private static partial Regex Moment();
    }

    internal sealed record MassCompensationDraft(
        string Token,
        int Days,
        string Reason,
        CompensationSegment Segment,
        bool IncludeTrial,
        DateTime? From,
        DateTime? To) : IStaffDraft
    {
        public MassCompensationSpec Spec => new(Days, Segment, IncludeTrial, From, To);
    }
}
