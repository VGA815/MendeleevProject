namespace Mendeleev.Domain.Audit
{
    public static class AuditActions
    {
        public const string UserView = "user.view";
        public const string UserBlock = "user.block";
        public const string UserUnblock = "user.unblock";
        public const string SubscriptionExtend = "subscription.extend";
        public const string PaymentManual = "payment.manual";
        public const string DevicesReset = "devices.reset";
        public const string DeviceDelete = "devices.delete";
        public const string LinkReissue = "link.reissue";
        public const string BroadcastSend = "broadcast.send";
        public const string BroadcastCancel = "broadcast.cancel";
        public const string StaffAdd = "staff.add";
        public const string StaffRoleChange = "staff.role_change";
        public const string StaffDisable = "staff.disable";
        public const string StaffEnable = "staff.enable";
        public const string PromoCreate = "promo.create";
        public const string PromoDeactivate = "promo.deactivate";

        public const string PanelDriftFixed = "panel.drift_fixed";
        public const string PanelUserDeleted = "panel.user_deleted";
        public const string PanelUserRecreated = "panel.user_recreated";
        public const string PaymentsReconciled = "payments.reconciled";
        public const string PaymentFromBlockedUser = "payment.blocked_user";
        public const string TorrentBlockerReport = "abuse.torrent_blocker";
        public const string AccountsMerged = "account.merged";

        /// <summary>The term grew by a promo code's bonus days (ТЗ 22: the term changes only with an audit trail).</summary>
        public const string PromoBonus = "promo.bonus";
    }
}
