using System.Diagnostics;
using RuleEval.Abstractions;
using RuleEval.Auditing;
using RuleEval.Database.Abstractions;

namespace RuleEval.Database;

/// <summary>
/// Decorator around <see cref="IRuleSetRepository"/> that fires a
/// <see cref="RuleEvaluationAuditEvent"/> to all registered <see cref="IRuleEvaluationAuditSink"/>
/// implementations after each evaluation.
/// </summary>
/// <remarks>
/// Activate this decorator by calling
/// <c>services.AddRuleEvalAuditing()</c> from
/// <c>RuleEval.Database.DependencyInjection</c> after <c>AddRuleEvalDatabase()</c>.
/// </remarks>
public sealed class AuditingRuleSetRepository : IRuleSetRepository
{
    private readonly IRuleSetRepository _inner;
    private readonly IRuleSearchContextAccessor _contextAccessor;
    private readonly IReadOnlyList<IRuleEvaluationAuditSink> _sinks;

    /// <summary>
    /// Initialises a new <see cref="AuditingRuleSetRepository"/>.
    /// </summary>
    /// <param name="inner">The inner repository to decorate.</param>
    /// <param name="contextAccessor">Accessor for the current business context.</param>
    /// <param name="sinks">Zero or more audit sinks to notify after each evaluation.</param>
    public AuditingRuleSetRepository(
        IRuleSetRepository inner,
        IRuleSearchContextAccessor contextAccessor,
        IEnumerable<IRuleEvaluationAuditSink> sinks)
    {
        _inner = inner;
        _contextAccessor = contextAccessor;
        _sinks = sinks.ToArray();
    }

    // ── Non-evaluation methods — delegate directly ────────────────────────

    /// <inheritdoc/>
    public ValueTask<RuleSet> LoadAsync(string key, CancellationToken cancellationToken = default)
        => _inner.LoadAsync(key, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<RuleSet> LoadAsync(string key, TimeSpan cacheTtl, CancellationToken cancellationToken = default)
        => _inner.LoadAsync(key, cacheTtl, cancellationToken);

    /// <inheritdoc/>
    public ValueTask InvalidateCacheAsync(string key, CancellationToken cancellationToken = default)
        => _inner.InvalidateCacheAsync(key, cancellationToken);

    // ── Evaluation methods — audited ──────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<EvaluationResult> EvaluateFirstAsync(
        string key,
        EvaluationContext context,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var businessContext = _contextAccessor.Current;

        EvaluationResult result;
        try
        {
            result = await _inner.EvaluateFirstAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await PublishAsync(BuildErrorEvent(key, context, startedAt, sw.Elapsed, ex, businessContext), cancellationToken).ConfigureAwait(false);
            throw;
        }

        sw.Stop();
        await PublishAsync(BuildSuccessEvent(key, context, result, startedAt, sw.Elapsed, businessContext), cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc/>
    public async ValueTask<EvaluationResult> EvaluateFirstOrThrowAsync(
        string key,
        EvaluationContext context,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Route through the audited EvaluateFirstAsync so a single audit event is emitted.
        var result = await EvaluateFirstAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            EvaluationStatus.Matched => result,
            EvaluationStatus.NoMatch => throw new InvalidOperationException($"No match for rule set '{key}'."),
            EvaluationStatus.AmbiguousMatch => throw new InvalidOperationException($"Ambiguous match for rule set '{key}'."),
            EvaluationStatus.InvalidInput => throw new ArgumentException(result.Error ?? "Invalid input."),
            _ => throw new InvalidOperationException("Unknown evaluation state."),
        };
    }

    /// <inheritdoc/>
    public async ValueTask<string?> GetFirstOutputAsync(
        string key,
        EvaluationContext context,
        string outputName,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Route through the audited EvaluateFirstAsync so a single audit event is emitted.
        var result = await EvaluateFirstAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        if (result.Status != EvaluationStatus.Matched || result.Match is null)
            return null;

        return result.Match.Outputs
            .FirstOrDefault(o => string.Equals(o.Name, outputName, StringComparison.OrdinalIgnoreCase))
            ?.RawValue?.ToString();
    }

    /// <inheritdoc/>
    public async ValueTask<string> GetFirstOutputOrThrowAsync(
        string key,
        EvaluationContext context,
        string outputName,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var value = await GetFirstOutputAsync(key, context, outputName, options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException($"Output '{outputName}' was not found for rule set '{key}'.");
    }

    /// <inheritdoc/>
    public async ValueTask<string?> GetFirstOutputAsync(
        string key,
        EvaluationContext context,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Route through the audited EvaluateFirstAsync so a single audit event is emitted.
        var result = await EvaluateFirstAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        if (result.Status != EvaluationStatus.Matched || result.Match is null)
            return null;

        return result.Match.Outputs.FirstOrDefault()?.RawValue?.ToString();
    }

    /// <inheritdoc/>
    public async ValueTask<string> GetFirstOutputOrThrowAsync(
        string key,
        EvaluationContext context,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var value = await GetFirstOutputAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException($"Rule set '{key}' returned no output.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static RuleEvaluationAuditEvent BuildSuccessEvent(
        string key,
        EvaluationContext context,
        EvaluationResult result,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        RuleSearchContext? businessContext)
        => new()
        {
            RuleSetKey = key,
            Inputs = context,
            StartedAt = startedAt,
            Elapsed = elapsed,
            Status = result.Status,
            MatchedRuleName = result.Match?.Rule.Name,
            MatchedRuleIndex = result.Match?.RuleIndex,
            PrimaryKey = result.Match?.PrimaryKey,
            Outputs = result.Match?.Outputs ?? [],
            BusinessContext = businessContext,
        };

    private static RuleEvaluationAuditEvent BuildErrorEvent(
        string key,
        EvaluationContext context,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        Exception ex,
        RuleSearchContext? businessContext)
        => new()
        {
            RuleSetKey = key,
            Inputs = context,
            StartedAt = startedAt,
            Elapsed = elapsed,
            Status = null,
            ErrorType = ex.GetType().FullName,
            ErrorMessage = ex.Message,
            BusinessContext = businessContext,
        };

    private async ValueTask PublishAsync(RuleEvaluationAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            await sink.OnEvaluatedAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
