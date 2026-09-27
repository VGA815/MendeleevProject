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
    }
}
