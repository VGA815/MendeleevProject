using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Staff
{
    public static class StaffErrors
    {
        /// <summary>Deliberately vague: a non-staff caller must not learn that the command exists.</summary>
        public static readonly Error NotAllowed = Error.Forbidden(
            "Staff.NotAllowed",
            "Команда недоступна.");

        public static readonly Error NotFound = Error.NotFound(
            "Staff.NotFound",
            "Сотрудник не найден.");

        public static readonly Error AlreadyExists = Error.Conflict(
            "Staff.AlreadyExists",
            "Сотрудник с таким Telegram ID уже есть.");

        public static readonly Error CannotManageRole = Error.Forbidden(
            "Staff.CannotManageRole",
            "Эту роль может назначать или менять только техадмин.");

        public static readonly Error CannotChangeSelf = Error.Problem(
            "Staff.CannotChangeSelf",
            "Нельзя менять роль или отключать самого себя.");

        public static readonly Error ReasonRequired = Error.Validation(
            "Staff.ReasonRequired",
            "Укажите причину (не короче 3 символов).");
    }
}
