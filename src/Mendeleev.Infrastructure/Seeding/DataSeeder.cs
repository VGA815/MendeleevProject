using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Infrastructure.Database;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mendeleev.Infrastructure.Seeding
{
    /// <summary>
    /// Inserts missing tariffs and bootstrap staff at startup. Idempotent: codes and Telegram IDs that
    /// already exist are skipped, so the database stays the source of truth after the first run.
    /// </summary>
    internal sealed class DataSeeder(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IDateTimeProvider clock,
        ILogger<DataSeeder> logger)
        : IHostedService
    {
        public const string SquadsSectionName = "Remnawave:Squads";

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await SeedTariffsAsync(db, cancellationToken);
            await SeedStaffAsync(db, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task SeedTariffsAsync(ApplicationDbContext db, CancellationToken cancellationToken)
        {
            List<TariffSeed> seeds = configuration.GetSection(TariffSeed.SectionName).Get<List<TariffSeed>>() ?? [];
            if (seeds.Count == 0)
            {
                return;
            }

            Dictionary<string, Guid> squads = configuration.GetSection(SquadsSectionName).Get<Dictionary<string, Guid>>()
                ?? new Dictionary<string, Guid>();
            var squadsByName = new Dictionary<string, Guid>(squads, StringComparer.OrdinalIgnoreCase);

            HashSet<string> existing = (await db.Tariffs.Select(t => t.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

            foreach (TariffSeed seed in seeds.Where(s => !existing.Contains(s.Code)))
            {
                List<Guid> tariffSquads = [];
                foreach (string squad in seed.Squads)
                {
                    if (squadsByName.TryGetValue(squad, out Guid uuid))
                    {
                        tariffSquads.Add(uuid);
                    }
                    else
                    {
                        logger.LogWarning("Tariff {Code}: squad '{Squad}' is not configured in {Section}", seed.Code, squad, SquadsSectionName);
                    }
                }

                db.Tariffs.Add(Tariff.Create(
                    seed.Code,
                    seed.Name,
                    seed.Tier,
                    seed.Price,
                    seed.PeriodDays,
                    seed.DeviceLimit,
                    seed.TrafficLimitGb is int gb ? gb * 1024L * 1024 * 1024 : null,
                    tariffSquads,
                    seed.IsActive,
                    seed.SortOrder));

                logger.LogInformation("Seeded tariff {Code}", seed.Code);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        private async Task SeedStaffAsync(ApplicationDbContext db, CancellationToken cancellationToken)
        {
            List<StaffSeed> seeds = configuration.GetSection(StaffSeed.SectionName).Get<List<StaffSeed>>() ?? [];
            if (seeds.Count == 0)
            {
                return;
            }

            HashSet<long> existing = (await db.Staff.Select(s => s.TelegramId).ToListAsync(cancellationToken)).ToHashSet();

            foreach (StaffSeed seed in seeds.Where(s => s.TelegramId > 0 && !existing.Contains(s.TelegramId)))
            {
                db.Staff.Add(StaffMember.Create(seed.TelegramId, seed.Role, seed.DisplayName, createdByStaffId: null, clock.UtcNow));
                logger.LogInformation("Seeded staff member {DisplayName} as {Role}", seed.DisplayName, seed.Role);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
