using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Users
{
    public static class UserErrors
    {
        public static Error NotFound(long userId) => Error.NotFound(
            "Users.NotFound",
            $"Пользователь {userId} не найден.");

        /// <summary>Shown to the user as is; no reason is disclosed (ТЗ 21, «Обработка ошибок»).</summary>
        public static readonly Error Blocked = Error.Forbidden(
            "Users.Blocked",
            "Аккаунт заблокирован, обратитесь в поддержку.");

        public static readonly Error AlreadyBlocked = Error.Conflict(
            "Users.AlreadyBlocked",
            "Аккаунт уже заблокирован.");

        public static readonly Error NotBlocked = Error.Conflict(
            "Users.NotBlocked",
            "Аккаунт не заблокирован.");

        public static readonly Error InvalidAccountKey = Error.Validation(
            "Users.InvalidAccountKey",
            "Ключ аккаунта — это 16 цифр.");

        public static readonly Error NoTelegram = Error.Problem(
            "Users.NoTelegram",
            "Действие доступно только пользователям с Telegram.");

        /// <summary>The same text for a wrong and for an unknown key: nothing to learn from it.</summary>
        public static readonly Error WrongAccountKey = Error.Unauthorized(
            "Users.WrongAccountKey",
            "Неверный ключ аккаунта. Проверьте цифры и попробуйте ещё раз.");

        public static readonly Error InvalidLinkCode = Error.Validation(
            "Users.InvalidLinkCode",
            "Код неверный или устарел. Получите новый код в боте: «Вход на сайт» → «Привязать аккаунт с сайта».");

        public static readonly Error TelegramAlreadyLinked = Error.Conflict(
            "Users.TelegramAlreadyLinked",
            "К этому аккаунту уже привязан Telegram.");

        /// <summary>Merge rule 3 (ТЗ 21, решение 24.09): manual merge is stage 2.</summary>
        public static readonly Error MergeNeedsSupport = Error.Conflict(
            "Users.MergeNeedsSupport",
            "Подписка или платежи есть и в Telegram, и на сайте — автоматически объединить аккаунты нельзя. Напишите в поддержку, мы поможем.");
    }
}
