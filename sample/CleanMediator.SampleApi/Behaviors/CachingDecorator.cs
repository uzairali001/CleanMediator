using CleanMediator.Abstractions;

using Microsoft.Extensions.Caching.Memory;

using System.Text.Json;

namespace CleanMediator.SampleApi.Behaviors;

public class CachingDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IMemoryCache cache,
    ILogger<CachingDecorator<TQuery, TResult>> logger
) : IQueryHandler<TQuery, TResult>
    where TQuery : IBaseQuery
{
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken ct)
    {
        // Simple cache key generation
        var key = $"{typeof(TQuery).Name}_{JsonSerializer.Serialize(query)}";

        if (cache.TryGetValue(key, out TResult? cachedResult))
        {
            logger.LogInformation("⚡ Cache Hit for {Query}", key);
            return cachedResult!;
        }

        logger.LogInformation("🐌 Cache Miss for {Query}", key);

        var result = await inner.HandleAsync(query, ct);

        // Cache for 1 minute
        if (result is not null)
        {
            cache.Set(key, result, TimeSpan.FromMinutes(1));
        }

        return result;
    }
}