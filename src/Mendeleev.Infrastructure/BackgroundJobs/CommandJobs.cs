using System.Diagnostics;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Admin.Stats;
using Mendeleev.Application.Panel.Reconciliation;
using Mendeleev.Application.Panel.Traffic;
using Mendeleev.Application.Payments.Reconciliation;
using Mendeleev.Application.Subscriptions.Maintenance;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Mendeleev.Infrastructure.BackgroundJobs
{
    /// <summary>
    /// A Quartz job that runs one application command. The logic stays in Application; the job only
    /// provides the schedule, a DI scope and logging. Jobs never overlap with themselves.
    /// </summary>
    [DisallowConcurrentExecution]
    internal abstract class CommandJob<TCommand, TResult>(IServiceScopeFactory scopeFactory, ILogger logger) : IJob
        where TCommand : ICommand<TResult>, new()
    {
        public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                ICommandHandler<TCommand, TResult> handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
                Result<TResult> result = await handler.Handle(new TCommand(), cancellationToken);

                if (result.IsFailure)
                {
                    logger.LogWarning("Job {Job} finished with {ErrorCode}: {Error}", typeof(TCommand).Name, result.Error.Code, result.Error.Description);
                }
                else
                {
                    logger.LogDebug("Job {Job} finished in {Elapsed} ms with {Result}", typeof(TCommand).Name, stopwatch.ElapsedMilliseconds, result.Value);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing run is retried by the next trigger; the jobs derive their work from the DB state.
                logger.LogError(ex, "Job {Job} failed", typeof(TCommand).Name);
            }
        }
    }

    internal sealed class ExpireSubscriptionsJob(IServiceScopeFactory f, ILogger<ExpireSubscriptionsJob> l)
        : CommandJob<ExpireSubscriptionsCommand, int>(f, l);

    internal sealed class SendRemindersJob(IServiceScopeFactory f, ILogger<SendRemindersJob> l)
        : CommandJob<SendRemindersCommand, int>(f, l);

    internal sealed class ArchiveExpiredJob(IServiceScopeFactory f, ILogger<ArchiveExpiredJob> l)
        : CommandJob<ArchiveExpiredCommand, int>(f, l);

    internal sealed class OnboardingNudgeJob(IServiceScopeFactory f, ILogger<OnboardingNudgeJob> l)
        : CommandJob<OnboardingNudgeCommand, int>(f, l);

    internal sealed class RetentionCleanupJob(IServiceScopeFactory f, ILogger<RetentionCleanupJob> l)
        : CommandJob<RetentionCleanupCommand, int>(f, l);

    internal sealed class CheckPendingPaymentsJob(IServiceScopeFactory f, ILogger<CheckPendingPaymentsJob> l)
        : CommandJob<CheckPendingPaymentsCommand, int>(f, l);

    internal sealed class ReconcilePaymentsJob(IServiceScopeFactory f, ILogger<ReconcilePaymentsJob> l)
        : CommandJob<ReconcilePaymentsCommand, ReconciliationSummary>(f, l);

    internal sealed class ReconcilePanelJob(IServiceScopeFactory f, ILogger<ReconcilePanelJob> l)
        : CommandJob<ReconcilePanelCommand, PanelReconciliationSummary>(f, l);

    internal sealed class CollectTrafficJob(IServiceScopeFactory f, ILogger<CollectTrafficJob> l)
        : CommandJob<CollectTrafficCommand, int>(f, l);

    internal sealed class DailySummaryJob(IServiceScopeFactory f, ILogger<DailySummaryJob> l)
        : CommandJob<DailySummaryCommand, int>(f, l);
}
