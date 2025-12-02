namespace CleanMediator.Abstractions;

public interface IBaseQuery { }

public interface IQueryHandler<in TQuery, TResult>
     where TQuery : IBaseQuery
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}