using System.Globalization;
using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Devices
{
    public static class DeviceErrors
    {
        public static Error ResetLimitReached(DateTime availableAtMoscow) => Error.TooManyRequests(
            "Devices.ResetLimitReached",
            $"Сброс доступен с {availableAtMoscow.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} (МСК). Если срочно — напишите в поддержку.");

        public static readonly Error NoAccess = Error.Problem(
            "Devices.NoAccess",
            "Устройств пока нет: доступ ещё не выдан.");

        public static readonly Error PanelUnavailable = Error.ServiceUnavailable(
            "Devices.PanelUnavailable",
            "Панель недоступна, повторите позже.");

        public static readonly Error DeviceNotFound = Error.NotFound(
            "Devices.NotFound",
            "Устройство не найдено — возможно, его уже удалили.");
    }
}
