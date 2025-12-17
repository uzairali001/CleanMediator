using CleanMediator.Abstractions;

using Microsoft.Extensions.Caching.Memory;

using System.Text.Json;

namespace CleanMediator.SampleApi.Behaviors;

[GenerateDecorator("Caching")]
public class CachingDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IMemoryCache cache,
    ILogger<CachingDecorator<TQuery, TResult>> logger,
    int durationInSeconds = 60 * 10
    ) : IQueryHandler<TQuery, TResult>
    where TQuery : IBaseQuery
{
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken ct)
    {
        var key = $"{typeof(TQuery).Name}_{JsonSerializer.Serialize(query)}";

        if (cache.TryGetValue(key, out TResult? cachedResult))
        {
            logger.LogInformation("⚡ Cache Hit for {Query}", key);
            return cachedResult!;
        }

        logger.LogInformation("🐌 Cache Miss for {Query}", key);

        var result = await inner.HandleAsync(query, ct);

        if (result is not null)
        {
            // Use the duration from the attribute
            cache.Set(key, result, TimeSpan.FromSeconds(durationInSeconds));
        }

        return result;
    }
}