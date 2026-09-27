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

        public static readonly Error TrialTariffMissing = Error.Problem(
            "Tariffs.TrialTariffMissing",
            "Пробный период сейчас недоступен.");
    }
}
