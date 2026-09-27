using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace Mendeleev.Application.Abstractions.Behaviors
{
    /// <summary>
    /// Logs the name and the outcome of each use case. Commands are never logged with their payload:
    /// they can carry account keys, codes and texts (ТЗ 31, «Логи и приватность»).
    /// </summary>
    internal static class LoggingDecorator
    {
        internal sealed class CommandHandler<TCommand, TResponse>(
            ICommandHandler<TCommand, TResponse> innerHandler,
            ILogger<CommandHandler<TCommand, TResponse>> logger)
            : ICommandHandler<TCommand, TResponse>
            where TCommand : ICommand<TResponse>
        {
            public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
            {
                Result<TResponse> result = await innerHandler.Handle(command, cancellationToken);
                Log(logger, typeof(TCommand).Name, result);
                return result;
            }
        }

        internal sealed class CommandBaseHandler<TCommand>(
            ICommandHandler<TCommand> innerHandler,
            ILogger<CommandBaseHandler<TCommand>> logger)
            : ICommandHandler<TCommand>
            where TCommand : ICommand
        {
            public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
            {
                Result result = await innerHandler.Handle(command, cancellationToken);
                Log(logger, typeof(TCommand).Name, result);
                return result;
            }
        }

        internal sealed class QueryHandler<TQuery, TResponse>(
            IQueryHandler<TQuery, TResponse> innerHandler,
            ILogger<QueryHandler<TQuery, TResponse>> logger)
            : IQueryHandler<TQuery, TResponse>
            where TQuery : IQuery<TResponse>
        {
            public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
            {
                Result<TResponse> result = await innerHandler.Handle(query, cancellationToken);
                if (result.IsFailure)
                {
                    Log(logger, typeof(TQuery).Name, result);
                }
                return result;
            }
        }

        private static void Log(ILogger logger, string name, Result result)
        {
            if (result.IsSuccess)
            {
                logger.LogInformation("Completed {UseCase}", name);
                return;
            }

            using (LogContext.PushProperty("ErrorCode", result.Error.Code))
            {
                // Expected failures (validation, limits, not found) are business outcomes, not faults.
                if (result.Error.Type is ErrorType.Failure)
                {
                    logger.LogError("Completed {UseCase} with error {ErrorCode}", name, result.Error.Code);
                }
                else
                {
                    logger.LogInformation("Completed {UseCase} with {ErrorCode}", name, result.Error.Code);
                }
            }
        }
    }
}
