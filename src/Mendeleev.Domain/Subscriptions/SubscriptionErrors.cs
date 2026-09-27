using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Subscriptions
{
    public static class SubscriptionErrors
    {
        public static readonly Error NotFound = Error.NotFound(
            "Subscriptions.NotFound",
            "Подписки пока нет.");

        public static readonly Error TrialAlreadyUsed = Error.Conflict(
            "Subscriptions.TrialAlreadyUsed",
            "Пробный период уже активирован.");

        public static readonly Error TrialUnavailable = Error.Problem(
            "Subscriptions.TrialUnavailable",
            "Пробный период доступен только новым пользователям Telegram без оплат.");

        public static readonly Error InvalidDays = Error.Validation(
            "Subscriptions.InvalidDays",
            "Число дней должно быть больше нуля.");

        public static Error CompensationLimitExceeded(int available) => Error.Forbidden(
            "Subscriptions.CompensationLimitExceeded",
            available > 0
                ? $"Превышен лимит компенсации. Доступно ещё: {available} дн."
                : "Лимит компенсации для этого пользователя исчерпан.");
    }
}
