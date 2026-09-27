using System.Collections.Concurrent;
using System.Text.Json;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Infrastructure.Database;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Infrastructure.Outbox
{
    public sealed class OutboxOptions
    {
        public const string SectionName = "Outbox";

        public int BatchSize { get; init; } = 20;

        public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>How long a claimed message is reserved for this processor.</summary>
        public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// Delivers outbox messages to their domain event handlers with retries (ТЗ 10, 24). Messages are
    /// claimed with a lease under <c>FOR UPDATE SKIP LOCKED</c>, so an overlapping instance during a
    /// deploy does not double-process; handlers are idempotent anyway.
    /// </summary>
    internal sealed class OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        OutboxSignal signal,
        IDateTimeProvider clock,
        IOptions<OutboxOptions> options,
        ILogger<OutboxProcessor> logger)
        : BackgroundService
    {
        private static readonly ConcurrentDictionary<Type, Type> HandlerTypes = new();
        private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(15);

        private DateTime _metricsUpdatedAt = DateTime.MinValue;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int processed = 0;
                try
                {
                    processed = await ProcessBatchAsync(stoppingToken);
                    await UpdateMetricsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Outbox processing loop failed");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }

                if (processed == 0)
                {
                    try
                    {
                        await signal.WaitAsync(options.Value.PollInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>Claims and processes one batch. Exposed for tests.</summary>
        internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
        {
            List<OutboxMessage> batch;
            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                DateTime now = clock.UtcNow;
                DateTime leaseUntil = now + options.Value.Lease;
                int batchSize = options.Value.BatchSize;

                batch = await db.OutboxMessages
                    .FromSql(
                        $"""
                        UPDATE outbox_messages SET locked_until = {leaseUntil}
                        WHERE id IN (
                            SELECT id FROM outbox_messages
                            WHERE status = 'pending'
                              AND next_attempt_at <= {now}
                              AND (locked_until IS NULL OR locked_until < {now})
                            ORDER BY id
                            LIMIT {batchSize}
                            FOR UPDATE SKIP LOCKED)
                        RETURNING *
                        """)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
            }

            foreach (OutboxMessage message in batch.OrderBy(m => m.Id))
            {
                await ProcessOneAsync(message, cancellationToken);
            }

            return batch.Count;
        }

        private async Task ProcessOneAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Exception? failure = null;
            try
            {
                await DispatchAsync(message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            OutboxMessage? tracked = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);
            if (tracked is null)
            {
                return;
            }

            DateTime now = clock.UtcNow;
            if (failure is null)
            {
                tracked.MarkDone(now);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            bool permanent = failure is INonRetryableException;
            bool gaveUp = tracked.MarkFailedAttempt($"{failure.GetType().Name}: {failure.Message}", permanent, now);
            await db.SaveChangesAsync(cancellationToken);

            if (gaveUp)
            {
                logger.LogError(failure, "Outbox message {MessageId} ({Type}) failed permanently after {Attempts} attempts", message.Id, message.Type, tracked.Attempts);
                IAlertSink alerts = scope.ServiceProvider.GetRequiredService<IAlertSink>();
                await alerts.RaiseAsync(new Alert(
                    AlertSeverity.Critical,
                    $"outbox-failed:{message.Id}",
                    $"Задача {message.Type} #{message.Id} не выполнена после {tracked.Attempts} попыток: {failure.Message}"),
                    cancellationToken);
            }
            else
            {
                logger.LogWarning("Outbox message {MessageId} ({Type}) attempt {Attempts} failed: {Error}", message.Id, message.Type, tracked.Attempts, failure.Message);
            }
        }

        private async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Type eventType = DomainEventTypes.Resolve(message.Type)
                ?? throw new UnknownEventTypeException(message.Type);

            var domainEvent = (IDomainEvent)(JsonSerializer.Deserialize(message.Payload, eventType, DomainEventTypes.SerializerOptions)
                ?? throw new UnknownEventTypeException(message.Type));

            Type handlerType = HandlerTypes.GetOrAdd(eventType, t => typeof(IDomainEventHandler<>).MakeGenericType(t));

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            foreach (object? handler in scope.ServiceProvider.GetServices(handlerType))
            {
                if (handler is null)
                {
                    continue;
                }

                var task = (Task)handlerType
                    .GetMethod(nameof(IDomainEventHandler<IDomainEvent>.Handle))!
                    .Invoke(handler, [domainEvent, cancellationToken])!;
                await task;
            }
        }

        private async Task UpdateMetricsAsync(CancellationToken cancellationToken)
        {
            DateTime now = clock.UtcNow;
            if (now - _metricsUpdatedAt < MetricsInterval)
            {
                return;
            }
            _metricsUpdatedAt = now;

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            IQueryable<OutboxMessage> pending = db.OutboxMessages.Where(m => m.Status == OutboxStatus.Pending);
            int count = await pending.CountAsync(cancellationToken);
            DateTime? oldest = count == 0 ? null : await pending.MinAsync(m => (DateTime?)m.CreatedAt, cancellationToken);

            AppMetrics.SetOutboxState(count, oldest is DateTime at ? (now - at).TotalSeconds : 0);
        }

        private sealed class UnknownEventTypeException(string type)
            : Exception($"Unknown outbox message type '{type}'."), INonRetryableException;
    }
}
