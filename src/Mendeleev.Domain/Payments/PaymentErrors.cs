using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Payments
{
    public static class PaymentErrors
    {
        public static Error NotFound(Guid paymentId) => Error.NotFound(
            "Payments.NotFound",
            $"Платёж {paymentId} не найден.");

        public static readonly Error NoRecentPayment = Error.NotFound(
            "Payments.NoRecentPayment",
            "За последние сутки платежей нет.");

        public static readonly Error PaymentsDisabled = Error.ServiceUnavailable(
            "Payments.Disabled",
            "Оплата пока недоступна. Напишите в поддержку — продлим подписку вручную.");

        public static readonly Error ProviderUnavailable = Error.ServiceUnavailable(
            "Payments.ProviderUnavailable",
            "Оплата временно недоступна, попробуйте через несколько минут.");

        public static readonly Error TooManyPayments = Error.TooManyRequests(
            "Payments.TooManyPayments",
            "Слишком много попыток оплаты. Попробуйте через час.");

        public static Error UnknownProvider(string provider) => Error.NotFound(
            "Payments.UnknownProvider",
            $"Платёжный провайдер «{provider}» не подключён.");

        public static readonly Error InvalidSignature = Error.Unauthorized(
            "Payments.InvalidSignature",
            "Подпись уведомления не прошла проверку.");

        public static readonly Error ManualForBlockedUser = Error.Conflict(
            "Payments.ManualForBlockedUser",
            "Пользователь заблокирован: сначала разблокируйте его, потом записывайте оплату.");
    }
}
