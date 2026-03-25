using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuleEval.Abstractions;

namespace RuleEval.Caching;

public sealed record RuleSetCacheKey(string Namespace, string RuleSetKey)
{
    public override string ToString() => $"{Namespace}:{RuleSetKey}";
}

public interface IRuleSetCache
{
    ValueTask<RuleSet?> GetAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default);
    ValueTask SetAsync(RuleSetCacheKey key, RuleSet ruleSet, TimeSpan? ttl = null, CancellationToken cancellationToken = default);
    ValueTask RemoveAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default);
}

public sealed class NoCacheRuleSetCache : IRuleSetCache
{
    public ValueTask<RuleSet?> GetAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<RuleSet?>(null);

    public ValueTask SetAsync(RuleSetCacheKey key, RuleSet ruleSet, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask RemoveAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public sealed class MemoryRuleSetCache : IRuleSetCache
{
    private readonly ConcurrentDictionary<RuleSetCacheKey, CacheEntry> _entries = new();
    private readonly ILogger<MemoryRuleSetCache> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="MemoryRuleSetCache"/>.
    /// </summary>
    /// <param name="logger">
    /// Optional logger.  When <c>null</c> a no-op logger is used.
    /// </param>
    public MemoryRuleSetCache(ILogger<MemoryRuleSetCache>? logger = null)
    {
        _logger = logger ?? NullLogger<MemoryRuleSetCache>.Instance;
    }

    public ValueTask<RuleSet?> GetAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc is null || entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("Cache hit for '{CacheKey}'", key);
                return ValueTask.FromResult<RuleSet?>(entry.RuleSet);
            }

            _entries.TryRemove(key, out _);
        }

        _logger.LogDebug("Cache miss for '{CacheKey}'", key);
        return ValueTask.FromResult<RuleSet?>(null);
    }

    public ValueTask SetAsync(RuleSetCacheKey key, RuleSet ruleSet, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        _entries[key] = new CacheEntry(ruleSet, ttl is null ? null : DateTimeOffset.UtcNow.Add(ttl.Value));
        _logger.LogDebug("Cache set for '{CacheKey}' (TTL: {Ttl})", key, ttl?.ToString() ?? "none");
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(key, out _);
        _logger.LogDebug("Cache entry removed for '{CacheKey}'", key);
        return ValueTask.CompletedTask;
    }

    private sealed record CacheEntry(RuleSet RuleSet, DateTimeOffset? ExpiresAtUtc);
}
