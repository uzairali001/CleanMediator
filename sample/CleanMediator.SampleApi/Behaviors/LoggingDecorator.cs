using CleanMediator.Abstractions;

using System.Diagnostics;
using System.Reflection;

namespace CleanMediator.SampleApi.Behaviors;

// --- Attributes for Opt-In Behavior ---
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class LoggingAttribute : Attribute { }


// --- 1. Logging Decorator ---
public class LoggingDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<LoggingDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
    where TCommand : IBaseCommand
{

    // Optimize: Check for attribute once per type
    private static readonly bool _isLogEnabled = typeof(TCommand).GetCustomAttribute<LoggingAttribute>() != null;

    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct)
    {
        // Skip logging if attribute is missing
        if (!_isLogEnabled)
        {
            return await inner.HandleAsync(command, ct);
        }

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
