namespace CleanMediator.Abstractions;

public interface IBaseCommand { }

public interface ICommandHandler<in TCommand>
     where TCommand : IBaseCommand
{
    Task HandleAsync(TCommand command, CancellationToken ct = default);
}


public interface ICommandHandler<in TCommand, TResponse>
     where TCommand : IBaseCommand
{
    Task<TResponse> HandleAsync(TCommand command, CancellationToken ct = default);
}
