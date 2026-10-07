namespace Mendeleev.Domain.Staff
{
    /// <summary>
    /// The role matrix in one place. It is checked on the server in every staff use case, not only in
    /// the bot's menus (ТЗ 28, «API и контракты»).
    /// </summary>
    public static class StaffPolicy
    {
        public static bool Allows(StaffRole role, StaffPermission permission) => permission switch
        {
            StaffPermission.FindUsers => true,
            StaffPermission.Compensate => true,
            StaffPermission.ManageDevices => true,
            StaffPermission.ReissueLink => true,
            StaffPermission.BlockUsers => role is StaffRole.Admin or StaffRole.TechAdmin,
            StaffPermission.Broadcast => role is StaffRole.Admin or StaffRole.TechAdmin,
            StaffPermission.ViewStats => role is StaffRole.Admin or StaffRole.TechAdmin,
            StaffPermission.ManageStaff => role is StaffRole.Admin or StaffRole.TechAdmin,
            StaffPermission.ViewAudit => role is StaffRole.Admin or StaffRole.TechAdmin,
            StaffPermission.ViewTariffs => role is StaffRole.Admin or StaffRole.TechAdmin,
            StaffPermission.RecordManualPayments => role is StaffRole.Admin or StaffRole.TechAdmin,
            StaffPermission.ManagePromos => role is StaffRole.Admin or StaffRole.TechAdmin,
            _ => false,
        };

        /// <summary>
        /// Admins add and disable support and admins; only a tech admin hands out or touches the tech
        /// admin role (ТЗ 28, «Управление сотрудниками»).
        /// </summary>
        public static bool CanManage(StaffRole actor, StaffRole target) => actor switch
        {
            StaffRole.TechAdmin => true,
            StaffRole.Admin => target is StaffRole.Support or StaffRole.Admin,
            _ => false,
        };

        /// <summary>Support is capped per action and per user; admins and tech admins are not (FR-SUB-12).</summary>
        public static bool HasCompensationLimit(StaffRole role) => role == StaffRole.Support;
    }
}
