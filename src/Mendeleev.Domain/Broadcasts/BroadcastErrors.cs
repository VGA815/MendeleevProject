using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Broadcasts
{
    public static class BroadcastErrors
    {
        public static readonly Error NotFound = Error.NotFound(
            "Broadcasts.NotFound",
            "Рассылка не найдена.");

        public static readonly Error NotDraft = Error.Conflict(
            "Broadcasts.NotDraft",
            "Рассылка уже запущена или отменена.");

        public static readonly Error AlreadyFinished = Error.Conflict(
            "Broadcasts.AlreadyFinished",
            "Рассылка уже завершена.");

        public static readonly Error TextTooLong = Error.Validation(
            "Broadcasts.TextTooLong",
            $"Текст длиннее {Broadcast.MaxTextLength} символов.");

        public static readonly Error NoRecipients = Error.Problem(
            "Broadcasts.NoRecipients",
            "В сегменте нет получателей.");
    }
}
