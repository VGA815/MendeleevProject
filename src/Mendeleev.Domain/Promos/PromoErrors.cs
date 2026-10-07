using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Promos
{
    public static class PromoErrors
    {
        /// <summary>The same text for a mistyped and for an unknown code: nothing to learn from it.</summary>
        public static readonly Error NotFound = Error.NotFound(
            "Promos.NotFound",
            "Промокод не найден. Проверьте, как он написан.");

        public static readonly Error Inactive = Error.Problem(
            "Promos.Inactive",
            "Промокод больше не действует.");

        public static readonly Error NotStarted = Error.Problem(
            "Promos.NotStarted",
            "Промокод ещё не действует.");

        public static readonly Error Expired = Error.Problem(
            "Promos.Expired",
            "Срок действия промокода закончился.");

        public static readonly Error Exhausted = Error.Problem(
            "Promos.Exhausted",
            "Промокод закончился: все активации уже использованы.");

        public static readonly Error AlreadyUsed = Error.Conflict(
            "Promos.AlreadyUsed",
            "Вы уже использовали этот промокод.");

        /// <summary>
        /// Bonus days are free access, and «one per user» means nothing for web accounts anyone can create in
        /// any number — the same reason the trial is for Telegram only (FR-SUB-02).
        /// </summary>
        public static readonly Error BonusNeedsTelegram = Error.Problem(
            "Promos.BonusNeedsTelegram",
            "Бонусные дни по промокоду выдаются только аккаунтам с Telegram. Привяжите Telegram в кабинете и введите промокод ещё раз.");

        public static Error NoLongerValid(string code) => Error.Problem(
            "Promos.NoLongerValid",
            $"Промокод {code} больше не действует, скидка снята. Выберите тариф ещё раз — цена будет без скидки.");

        public static readonly Error BonusTariffMissing = Error.Problem(
            "Promos.BonusTariffMissing",
            "Бонусные дни сейчас недоступны. Напишите в поддержку.");

        public static readonly Error InvalidCode = Error.Validation(
            "Promos.InvalidCode",
            "Код промокода — от 3 до 32 латинских букв, цифр, «_» или «-».");

        public static readonly Error InvalidDiscount = Error.Validation(
            "Promos.InvalidDiscount",
            "Скидка — от 1 до 99 %.");

        public static readonly Error InvalidBonusDays = Error.Validation(
            "Promos.InvalidBonusDays",
            "Бонусных дней — от 1 до 365.");

        public static readonly Error InvalidMaxUses = Error.Validation(
            "Promos.InvalidMaxUses",
            "Лимит использований — целое число от 1.");

        public static readonly Error InvalidWindow = Error.Validation(
            "Promos.InvalidWindow",
            "Окончание действия должно быть позже начала и позже текущего момента.");

        public static Error CodeExists(string code) => Error.Conflict(
            "Promos.CodeExists",
            $"Промокод {code} уже есть. Регистр букв не различается.");

        public static readonly Error AlreadyDeactivated = Error.Conflict(
            "Promos.AlreadyDeactivated",
            "Промокод уже выключен.");
    }
}
