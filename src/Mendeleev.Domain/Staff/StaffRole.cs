namespace Mendeleev.Domain.Staff
{
    public enum StaffRole
    {
        /// <summary>The owner's support people: search, compensation within the limit, devices, link.</summary>
        Support = 0,

        /// <summary>The owner: sales, broadcasts, blocking, staff.</summary>
        Admin = 1,

        /// <summary>The developer: everything, including infrastructure.</summary>
        TechAdmin = 2,
    }
}
