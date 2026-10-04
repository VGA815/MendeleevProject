namespace Mendeleev.Domain.Staff
{
    /// <summary>Permissions from the role matrix (ТЗ 31, «Матрица ролей и прав»).</summary>
    public enum StaffPermission
    {
        FindUsers,
        Compensate,
        ManageDevices,
        ReissueLink,
        BlockUsers,
        Broadcast,
        ViewStats,
        ManageStaff,
        ViewAudit,
        ViewTariffs,
        RecordManualPayments,
    }
}
