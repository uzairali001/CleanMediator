using CleanMediator.Abstractions;

using System.Diagnostics;

namespace CleanMediator.SampleApi.Behaviors;


[GenerateDecorator("Logging")]
public class LoggingDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<LoggingDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
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