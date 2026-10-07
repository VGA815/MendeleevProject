using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Tariffs
{
    public static class TariffErrors
    {
        public static Error NotFound(string code) => Error.NotFound(
            "Tariffs.NotFound",
            $"Тариф «{code}» не найден.");

        public static readonly Error NotPurchasable = Error.Problem(
            "Tariffs.NotPurchasable",
            "Этот тариф сейчас недоступен для покупки.");

        public static readonly Error TrialNotForSale = Error.Problem(
            "Tariffs.TrialNotForSale",
            "Пробный период не продаётся — выберите платный тариф.");

        public static readonly Error TrialTariffMissing = Error.Problem(
            "Tariffs.TrialTariffMissing",
            "Пробный период сейчас недоступен.");

        public static readonly Error TrialIsFree = Error.Validation(
            "Tariffs.TrialIsFree",
            "Пробный период бесплатный: цену у него не меняют.");

        public static readonly Error InvalidPrice = Error.Validation(
            "Tariffs.InvalidPrice",
            "Цена — целое число рублей от 1 до 1 000 000.");

        public static readonly Error AlreadyActive = Error.Conflict(
            "Tariffs.AlreadyActive",
            "Тариф уже включён.");

        public static readonly Error AlreadyInactive = Error.Conflict(
            "Tariffs.AlreadyInactive",
            "Тариф уже выключен.");
    }
}
