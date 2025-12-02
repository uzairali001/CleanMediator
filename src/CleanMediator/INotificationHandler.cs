namespace CleanMediator.Abstractions;

public interface INotificationHandler<in TEvent>
{
    Task HandleAsync(TEvent notification, CancellationToken ct = default);
}

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent notification, CancellationToken ct = default);
}