using CleanMediator.Abstractions;

using Microsoft.Extensions.Caching.Memory;

using System.Reflection;
using System.Text.Json;

namespace CleanMediator.SampleApi.Behaviors;

// 1. Define the Metadata Attribute
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class CachedAttribute(int durationMinutes = 1) : Attribute
{
    public int DurationMinutes { get; } = durationMinutes;
}

public class CachingDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IMemoryCache cache,
    ILogger<CachingDecorator<TQuery, TResult>> logger) : IQueryHandler<TQuery, TResult>
    where TQuery : IBaseQuery // Keep broad constraint to avoid DI resolution errors
{

    // 2. Cache the reflection lookup. This runs once per TQuery type.
    private static readonly CachedAttribute? _cachePolicy = typeof(TQuery).GetCustomAttribute<CachedAttribute>();

    public async Task<TResult> HandleAsync(TQuery query, CancellationToken ct)
    {
        // 3. Check if this specific query has the attribute
        if (_cachePolicy is null)
        {
            // Not marked for caching? Pass through immediately.
            return await inner.HandleAsync(query, ct);
        }

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
            cache.Set(key, result, TimeSpan.FromMinutes(_cachePolicy.DurationMinutes));
        }

        return result;
    }
}