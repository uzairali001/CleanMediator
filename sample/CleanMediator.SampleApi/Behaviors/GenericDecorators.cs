using CleanMediator.Abstractions;

using FluentValidation;

using System.Diagnostics;

namespace CleanMediator.SampleApi.Behaviors;

// --- 1. Logging Decorator ---
// Wraps the inner handler, logs timing, and catches errors.
public class LoggingDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<LoggingDecorator<TCommand, TResult>> logger
) : ICommandHandler<TCommand, TResult>
    where TCommand : IBaseCommand
{
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct)
    {
        var commandName = typeof(TCommand).Name;
        logger.LogInformation("➡️ Starting {Command}", commandName);

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await inner.HandleAsync(command, ct);

            sw.Stop();
            logger.LogInformation("✅ Completed {Command} in {Elapsed}ms", commandName, sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception)
        {
            logger.LogError("❌ Failed {Command} after {Elapsed}ms", commandName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}

// --- 2. Validation Decorator ---
// Runs strictly BEFORE the handler. If validation fails, the handler is never called.
public class ValidationDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : IBaseCommand
{
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct)
    {
        if (!validators.Any())
        {
            return await inner.HandleAsync(command, ct);
        }

        var context = new ValidationContext<TCommand>(command);

        // Run all validators in parallel
        var validationResults = await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(context, ct))
        );

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f != null)
            .ToList();

        if (failures.Count != 0)
        {
            throw new ValidationException(failures);
        }

        return await inner.HandleAsync(command, ct);
    }
}