using Mendeleev.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Mendeleev.Infrastructure.BackgroundJobs
{
    public sealed class JobsOptions
    {
        public const string SectionName = "Jobs";

        /// <summary>Off in tests and in one-off tool runs.</summary>
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// Schedules from ТЗ 22–24, 28. Quartz runs with the in-memory store on purpose: every job derives its
    /// work from the database state and catches up after downtime, so persisting triggers adds a schema
    /// and nothing else. Wall-clock schedules are in Moscow time.
    /// </summary>
    internal static class JobsRegistration
    {
        public static IServiceCollection AddScheduledJobs(this IServiceCollection services)
        {
            services.AddQuartz(q =>
            {
                q.UseInMemoryStore();

                Every<ExpireSubscriptionsJob>(q, "0 * * ? * *");              // every minute (FR-SUB-06)
                Every<SendRemindersJob>(q, "0 */10 * ? * *");                 // every 10 minutes (FR-SUB-07)
                Every<ArchiveExpiredJob>(q, "0 5 * ? * *");                   // hourly (FR-SUB-09)
                Every<OnboardingNudgeJob>(q, "30 */5 * ? * *");               // every 5 minutes (FR-BOT-07)
                Every<CheckPendingPaymentsJob>(q, "15 */5 * ? * *");          // every 5 minutes (FR-PAY-07)
                Every<ReconcilePanelJob>(q, "45 */10 * ? * *");               // every 10 minutes (FR-PNL-05)
                Every<PanelAvailabilityJob>(q, "30 * * ? * *");               // every minute (ТЗ 24, «Панель недоступна дольше 5 минут»)
                Every<InfrastructureCleanupJob>(q, "0 20 * ? * *");           // hourly
                Every<ReconcilePaymentsJob>(q, "0 0 4 ? * *");                // 04:00 МСК (FR-PAY-06)
                Every<RetentionCleanupJob>(q, "0 30 3 ? * *");                // 03:30 МСК
                Every<CollectTrafficJob>(q, "0 10 0 ? * *");                  // 00:10 МСК (FR-PNL-15)
                Every<DailySummaryJob>(q, "0 0 10 ? * *");                    // 10:00 МСК (FR-ADM-09)
            });

            services.AddQuartzHostedService(options =>
            {
                options.WaitForJobsToComplete = true;
                options.AwaitApplicationStarted = true;
            });

            return services;
        }

        private static void Every<TJob>(IQuartzBuilder quartz, string cron)
            where TJob : IJob
        {
            string name = typeof(TJob).Name;
            quartz.ScheduleJob<TJob>(
                trigger => trigger
                    .WithIdentity($"{name}.trigger")
                    .WithCronSchedule(cron, schedule => schedule
                        .InTimeZone(MoscowTime.Zone)
                        .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing)),
                job => job
                    .WithIdentity(name)
                    .DisallowConcurrentExecution());
        }
    }
}
