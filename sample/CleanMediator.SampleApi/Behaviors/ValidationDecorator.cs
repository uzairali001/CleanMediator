using CleanMediator.Abstractions;

using FluentValidation;

using System.Reflection;

namespace CleanMediator.SampleApi.Behaviors;

// --- Attributes for Opt-In Behavior ---
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class ValidatedAttribute : Attribute { }


public class ValidationDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : IBaseCommand
{
    // Optimize: Check for attribute once per type
    private static readonly bool _isValidationEnabled = typeof(TCommand).GetCustomAttribute<ValidatedAttribute>() != null;


    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct)
    {
        // Skip if attribute is missing OR no validators are registered
        if (!_isValidationEnabled || !validators.Any())
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