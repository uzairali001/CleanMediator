using CleanMediator.Abstractions;

using FluentValidation;

namespace CleanMediator.SampleApi.Behaviors;

[GenerateDecorator("Validation")]
public class ValidationDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : IBaseCommand
{
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct)
    {
        // Skip if no validators are registered
        if (!validators.Any())
        {
            return await inner.HandleAsync(command, ct);
        }

        var context = new ValidationContext<TCommand>(command);

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