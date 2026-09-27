using Mendeleev.Infrastructure.Outbox;
using Mendeleev.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mendeleev.Infrastructure.Database
{
    /// <summary>
    /// For <c>dotnet ef</c> only. The connection string comes from <c>ConnectionStrings__Database</c> or a
    /// local default; migrations are generated without touching the database.
    /// </summary>
    public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext(string[] args)
        {
            string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
                ?? "Host=localhost;Port=5432;Database=mendeleev;Username=mendeleev;Password=mendeleev";

            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schemas.Default))
                .UseSnakeCaseNamingConvention()
                .Options;

            return new ApplicationDbContext(options, new DateTimeProvider(), new OutboxSignal());
        }
    }
}
