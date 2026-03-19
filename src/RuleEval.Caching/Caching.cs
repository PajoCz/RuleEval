using System.Collections.Concurrent;
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

    public ValueTask<RuleSet?> GetAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc is null || entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return ValueTask.FromResult<RuleSet?>(entry.RuleSet);
            }

            _entries.TryRemove(key, out _);
        }

        return ValueTask.FromResult<RuleSet?>(null);
    }

    public ValueTask SetAsync(RuleSetCacheKey key, RuleSet ruleSet, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        _entries[key] = new CacheEntry(ruleSet, ttl is null ? null : DateTimeOffset.UtcNow.Add(ttl.Value));
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    private sealed record CacheEntry(RuleSet RuleSet, DateTimeOffset? ExpiresAtUtc);
}
