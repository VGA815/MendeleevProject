using Mendeleev.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Web.Extensions
{
    public static class MigrationExtensions
    {
        /// <summary>
        /// Local runs only. In production migrations are a separate step (EF migration bundle) before the
        /// new version starts (ТЗ 40, «Сборка и поставка»).
        /// </summary>
        public static void ApplyMigrations(this IApplicationBuilder app)
        {
            using IServiceScope scope = app.ApplicationServices.CreateScope();
            using ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.Database.Migrate();
        }
    }
}
