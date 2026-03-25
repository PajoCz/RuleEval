using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuleEval.Abstractions;

namespace RuleEval.Core;

public sealed record ConditionMatchContext(Condition Condition, object? ActualValue);

public sealed record ConditionMatchResult(bool Success, string MatcherKey, string? Detail = null)
{
    public static ConditionMatchResult Matched(string matcherKey, string? detail = null) => new(true, matcherKey, detail);
    public static ConditionMatchResult NotMatched(string matcherKey, string? detail = null) => new(false, matcherKey, detail);
}

public interface IConditionMatcher
{
    string Key { get; }
    bool CanHandle(Condition condition);
    ConditionMatchResult Match(ConditionMatchContext context);
}

public sealed class MatcherRegistry
{
    private readonly ImmutableArray<IConditionMatcher> _matchers;

    public MatcherRegistry(IEnumerable<IConditionMatcher> matchers)
    {
        _matchers = matchers.ToImmutableArray();
        if (_matchers.IsDefaultOrEmpty)
        {
            throw new InvalidRuleDefinitionException("At least one condition matcher must be registered.");
        }
    }

    public static MatcherRegistry CreateDefault()
        => new([new DecimalIntervalConditionMatcher(), new RegexConditionMatcher(), new EqualityConditionMatcher()]);

    public MatcherRegistry WithMatcher(IConditionMatcher matcher)
        => new(_matchers.Add(matcher));

    public ConditionMatchResult Match(Condition condition, object? actualValue)
    {
        foreach (var matcher in _matchers)
        {
            if (matcher.CanHandle(condition))
            {
                return matcher.Match(new ConditionMatchContext(condition, actualValue));
            }
        }

        throw new InvalidConditionFormatException($"No matcher was able to handle condition '{condition.FieldName}' with matcher key '{condition.MatcherKey}'.");
    }
}

public sealed class EqualityConditionMatcher : IConditionMatcher
{
    public string Key => DefaultMatcherKeys.Equality;

    public bool CanHandle(Condition condition)
        => string.Equals(condition.MatcherKey, DefaultMatcherKeys.Equality, StringComparison.OrdinalIgnoreCase)
           || string.Equals(condition.MatcherKey, DefaultMatcherKeys.Auto, StringComparison.OrdinalIgnoreCase);

    public ConditionMatchResult Match(ConditionMatchContext context)
    {
        if (context.Condition.Expected is null)
        {
            return ConditionMatchResult.NotMatched(Key, "Expected value is null.");
        }

        if (context.ActualValue is null)
        {
            return ConditionMatchResult.NotMatched(Key, "Actual value is null.");
        }

        var success = string.Equals(
            Convert.ToString(context.Condition.Expected, CultureInfo.InvariantCulture),
            Convert.ToString(context.ActualValue, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        return success
            ? ConditionMatchResult.Matched(Key)
            : ConditionMatchResult.NotMatched(Key, "String equality comparison failed.");
    }
}

public sealed class RegexConditionMatcher : IConditionMatcher
{
    private readonly ConcurrentDictionary<string, Regex> _cache = new(StringComparer.Ordinal);

    public string Key => DefaultMatcherKeys.Regex;

    public bool CanHandle(Condition condition)
    {
        if (string.Equals(condition.MatcherKey, DefaultMatcherKeys.Regex, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(condition.MatcherKey, DefaultMatcherKeys.Auto, StringComparison.OrdinalIgnoreCase)
               && condition.Expected is string expected
               && DecimalIntervalParser.TryParse(expected).IsSuccess is false;
    }

    public ConditionMatchResult Match(ConditionMatchContext context)
    {
        var pattern = Convert.ToString(context.Condition.Expected, CultureInfo.InvariantCulture) ?? string.Empty;
        var regex = _cache.GetOrAdd(pattern, static value => new Regex($"^(?:{value})$", RegexOptions.Singleline | RegexOptions.Compiled));
        var actual = Convert.ToString(context.ActualValue, CultureInfo.InvariantCulture) ?? string.Empty;
        return regex.IsMatch(actual)
            ? ConditionMatchResult.Matched(Key)
            : ConditionMatchResult.NotMatched(Key, "Regex full-string match failed.");
    }
}

public sealed class DecimalIntervalConditionMatcher : IConditionMatcher
{
    public string Key => DefaultMatcherKeys.DecimalInterval;

    public bool CanHandle(Condition condition)
    {
        if (string.Equals(condition.MatcherKey, DefaultMatcherKeys.DecimalInterval, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return condition.Expected is DecimalInterval
               || (string.Equals(condition.MatcherKey, DefaultMatcherKeys.Auto, StringComparison.OrdinalIgnoreCase)
                   && condition.Expected is string expected
                   && DecimalIntervalParser.TryParse(expected).IsSuccess);
    }

    public ConditionMatchResult Match(ConditionMatchContext context)
    {
        var intervalResult = context.Condition.Expected switch
        {
            DecimalInterval interval => DecimalIntervalParseResult.Success(interval),
            string text => DecimalIntervalParser.TryParse(text),
            _ => DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.InvalidFormat, "Expected value is not a decimal interval."),
        };

        if (!intervalResult.IsSuccess || intervalResult.Interval is null)
        {
            return ConditionMatchResult.NotMatched(Key, intervalResult.Error ?? "Interval parsing failed.");
        }

        if (!NumericValueConverter.TryConvertToDecimal(context.ActualValue, out var numericValue))
        {
            return ConditionMatchResult.NotMatched(Key, "Actual value cannot be converted to decimal.");
        }

        return intervalResult.Interval.Contains(numericValue)
            ? ConditionMatchResult.Matched(Key)
            : ConditionMatchResult.NotMatched(Key, "Numeric value is outside of the interval.");
    }
}

public static class NumericValueConverter
{
    public static bool TryConvertToDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case null:
                result = default;
                return false;
            case decimal decimalValue:
                result = decimalValue;
                return true;
            default:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return decimal.TryParse(text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
        }
    }
}

public sealed record DecimalInterval(decimal From, bool IncludeFrom, decimal To, bool IncludeTo)
{
    public bool Contains(decimal value)
    {
        var lower = IncludeFrom ? value >= From : value > From;
        var upper = IncludeTo ? value <= To : value < To;
        return lower && upper;
    }
}

public enum DecimalIntervalParseErrorCode
{
    None = 0,
    Empty = 1,
    InvalidPrefix = 2,
    InvalidBoundarySyntax = 3,
    MissingSeparator = 4,
    InvalidNumber = 5,
    InvalidRange = 6,
    InvalidFormat = 7,
}

public sealed record DecimalIntervalParseResult(bool IsSuccess, DecimalInterval? Interval, DecimalIntervalParseErrorCode ErrorCode, string? Error)
{
    public static DecimalIntervalParseResult Success(DecimalInterval interval) => new(true, interval, DecimalIntervalParseErrorCode.None, null);
    public static DecimalIntervalParseResult Failure(DecimalIntervalParseErrorCode errorCode, string error) => new(false, null, errorCode, error);
}

public static class DecimalIntervalParser
{
    public static DecimalIntervalParseResult TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.Empty, "Interval text is empty.");
        }

        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        if (!normalized.StartsWith("INTERVAL", StringComparison.OrdinalIgnoreCase))
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.InvalidPrefix, "Interval must start with INTERVAL.");
        }

        var payload = normalized["INTERVAL".Length..];
        if (payload.Length < 5)
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.InvalidFormat, "Interval payload is too short.");
        }

        var leftBoundary = payload[0];
        var rightBoundary = payload[^1];
        if ((leftBoundary is not '(' and not '<') || (rightBoundary is not ')' and not '>'))
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.InvalidBoundarySyntax, "Interval boundaries must use (, <, ) or >.");
        }

        var separatorIndex = payload.IndexOf(';');
        if (separatorIndex < 0)
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.MissingSeparator, "Interval is missing ';' separator.");
        }

        var fromText = payload[1..separatorIndex];
        var toText = payload[(separatorIndex + 1)..^1];
        if (!decimal.TryParse(fromText, NumberStyles.Number, CultureInfo.InvariantCulture, out var from))
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.InvalidNumber, $"Cannot parse interval start '{fromText}'.");
        }

        if (!decimal.TryParse(toText, NumberStyles.Number, CultureInfo.InvariantCulture, out var to))
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.InvalidNumber, $"Cannot parse interval end '{toText}'.");
        }

        if (to < from)
        {
            return DecimalIntervalParseResult.Failure(DecimalIntervalParseErrorCode.InvalidRange, "Interval end must be greater than or equal to interval start.");
        }

        return DecimalIntervalParseResult.Success(new DecimalInterval(from, leftBoundary == '<', to, rightBoundary == '>'));
    }
}

public sealed class RuleSetEvaluator
{
    private readonly MatcherRegistry _matcherRegistry;
    private readonly ILogger<RuleSetEvaluator> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="RuleSetEvaluator"/>.
    /// </summary>
    /// <param name="matcherRegistry">
    /// The registry of condition matchers to use.  When <c>null</c> the default set of
    /// built-in matchers is used.
    /// </param>
    /// <param name="logger">
    /// Optional logger.  When <c>null</c> a no-op logger is used so that logging never
    /// affects execution when the hosting application has not configured a provider.
    /// </param>
    public RuleSetEvaluator(MatcherRegistry? matcherRegistry = null, ILogger<RuleSetEvaluator>? logger = null)
    {
        _matcherRegistry = matcherRegistry ?? MatcherRegistry.CreateDefault();
        _logger = logger ?? NullLogger<RuleSetEvaluator>.Instance;
    }

    /// <summary>
    /// Evaluates <paramref name="ruleSet"/> against <paramref name="context"/> and returns the
    /// first matching rule, or a non-match / error result.  Emits an <c>ruleeval.evaluate</c>
    /// Activity span and updates the <c>ruleeval.evaluate.total</c> / <c>ruleeval.evaluate.duration</c>
    /// metrics.
    /// </summary>
    public EvaluationResult EvaluateFirst(RuleSet ruleSet, EvaluationContext context, EvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(context);

        using var activity = RuleEvalTelemetry.ActivitySource.StartActivity("ruleeval.evaluate");
        activity?.SetTag("rule.key", ruleSet.Key);

        _logger.LogDebug("Starting evaluation of rule set '{RuleSetKey}'", ruleSet.Key);

        // This Stopwatch measures the total wall-clock time reported to metrics and ILogger.
        // EvaluateFirstCore uses its own internal Stopwatch solely for populating RuleTrace
        // (the optional diagnostics capture), which has a different lifetime and purpose.
        var stopwatch = Stopwatch.StartNew();
        EvaluationResult result;
        try
        {
            result = EvaluateFirstCore(ruleSet, context, options);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().Name);
            _logger.LogError(ex, "Evaluation of rule set '{RuleSetKey}' failed unexpectedly", ruleSet.Key);
            throw;
        }

        stopwatch.Stop();
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

        activity?.SetTag("rule.status", result.Status.ToString());
        if (result.Status == EvaluationStatus.Matched && result.Match?.PrimaryKey is { } pk)
        {
            activity?.SetTag("rule.primary_key", pk.Value?.ToString());
        }

        RuleEvalTelemetry.EvaluateTotalCounter.Add(1);
        RuleEvalTelemetry.EvaluateDurationHistogram.Record(elapsedMs);

        LogEvaluationResult(ruleSet.Key, result, elapsedMs);

        return result;
    }

    /// <summary>
    /// Evaluates <paramref name="ruleSet"/> against <paramref name="context"/> and returns all
    /// matching rules.  Emits an <c>ruleeval.evaluate</c> Activity span and updates the
    /// <c>ruleeval.evaluate.total</c> / <c>ruleeval.evaluate.duration</c> metrics.
    /// </summary>
    public EvaluationMatchesResult EvaluateAll(RuleSet ruleSet, EvaluationContext context, EvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(context);

        using var activity = RuleEvalTelemetry.ActivitySource.StartActivity("ruleeval.evaluate");
        activity?.SetTag("rule.key", ruleSet.Key);

        _logger.LogDebug("Starting EvaluateAll for rule set '{RuleSetKey}'", ruleSet.Key);

        // See EvaluateFirst for explanation of the two-Stopwatch pattern.
        var stopwatch = Stopwatch.StartNew();
        EvaluationMatchesResult result;
        try
        {
            result = EvaluateAllCore(ruleSet, context, options);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().Name);
            _logger.LogError(ex, "EvaluateAll for rule set '{RuleSetKey}' failed unexpectedly", ruleSet.Key);
            throw;
        }

        stopwatch.Stop();
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

        activity?.SetTag("rule.match.count", result.Matches.Length);

        RuleEvalTelemetry.EvaluateTotalCounter.Add(1);
        RuleEvalTelemetry.EvaluateDurationHistogram.Record(elapsedMs);

        _logger.LogDebug(
            "EvaluateAll of '{RuleSetKey}' completed in {ElapsedMs:F2}ms — {MatchCount} match(es)",
            ruleSet.Key, elapsedMs, result.Matches.Length);

        return result;
    }

    public bool TryGetFirstOutput(RuleSet ruleSet, EvaluationContext context, string outputName, out object? rawValue, EvaluationOptions? options = null)
    {
        var result = EvaluateFirst(ruleSet, context, options);
        if (result.Status == EvaluationStatus.Matched && result.Match is not null)
        {
            var output = result.Match.Outputs.FirstOrDefault(output => string.Equals(output.Name, outputName, StringComparison.OrdinalIgnoreCase));
            if (output is not null)
            {
                rawValue = output.RawValue;
                return true;
            }
        }

        rawValue = null;
        return false;
    }

    // ── Logging helper ───────────────────────────────────────────────────────

    private void LogEvaluationResult(string ruleSetKey, EvaluationResult result, double elapsedMs)
    {
        switch (result.Status)
        {
            case EvaluationStatus.Matched:
                _logger.LogDebug(
                    "Evaluation of '{RuleSetKey}' → Matched (rule index {RuleIndex}, primary key {PrimaryKey}) in {ElapsedMs:F2}ms",
                    ruleSetKey,
                    result.Match?.RuleIndex,
                    result.Match?.PrimaryKey?.Value,
                    elapsedMs);
                break;

            case EvaluationStatus.NoMatch:
                _logger.LogDebug(
                    "Evaluation of '{RuleSetKey}' → NoMatch (reason: {Reason}) in {ElapsedMs:F2}ms",
                    ruleSetKey, result.NoMatchReason, elapsedMs);
                break;

            case EvaluationStatus.AmbiguousMatch:
                _logger.LogWarning(
                    "Evaluation of '{RuleSetKey}' → AmbiguousMatch ({MatchCount} rules matched) in {ElapsedMs:F2}ms",
                    ruleSetKey,
                    (result.AmbiguousMatch?.AdditionalMatches.Length ?? 0) + 1,
                    elapsedMs);
                break;

            case EvaluationStatus.InvalidInput:
                _logger.LogWarning(
                    "Evaluation of '{RuleSetKey}' → InvalidInput (reason: {Reason}, error: {Error}) in {ElapsedMs:F2}ms",
                    ruleSetKey, result.NoMatchReason, result.Error, elapsedMs);
                break;
        }
    }

    // ── Core evaluation logic (no telemetry) ─────────────────────────────────

    private EvaluationResult EvaluateFirstCore(RuleSet ruleSet, EvaluationContext context, EvaluationOptions? options)
    {
        var evaluationOptions = options ?? EvaluationOptions.Default;
        var stopwatch = Stopwatch.StartNew();
        var traceEntries = evaluationOptions.CaptureDiagnostics ? new List<RuleTraceEntry>(ruleSet.Rules.Length) : null;
        var matches = new List<MatchResult>();

        if (ruleSet.Rules.IsDefaultOrEmpty)
        {
            return EvaluationResult.NoMatch(NoMatchReason.NoRulesInSet, BuildTrace(ruleSet, stopwatch, traceEntries, "Rule set contains no rules."));
        }

        if (!TryResolveInputs(ruleSet, context, out var actualInputs, out var inputError))
        {
            return EvaluationResult.InvalidInput(NoMatchReason.InputCountMismatch, BuildTrace(ruleSet, stopwatch, traceEntries, inputError), inputError ?? "Invalid input context.");
        }

        for (var index = 0; index < ruleSet.Rules.Length; index++)
        {
            var rule = ruleSet.Rules[index];
            var conditionTrace = traceEntries is null ? null : new List<ConditionTraceEntry>(rule.Conditions.Length);
            var matched = true;

            for (var conditionIndex = 0; conditionIndex < rule.Conditions.Length; conditionIndex++)
            {
                var condition = rule.Conditions[conditionIndex];
                var actual = actualInputs[conditionIndex];
                var result = _matcherRegistry.Match(condition, actual);
                if (conditionTrace is not null)
                {
                    conditionTrace.Add(new ConditionTraceEntry(condition.FieldName, result.MatcherKey, condition.Expected, actual, result.Success, result.Detail));
                }

                if (!result.Success)
                {
                    matched = false;
                    break;
                }
            }

            traceEntries?.Add(new RuleTraceEntry(index, rule.Name, matched, conditionTrace?.ToImmutableArray() ?? ImmutableArray<ConditionTraceEntry>.Empty));
            if (!matched)
            {
                continue;
            }

            var match = new MatchResult(rule, rule.Outputs, rule.PrimaryKey, index);
            matches.Add(match);

            if (!evaluationOptions.DetectAmbiguity)
            {
                return EvaluationResult.Matched(match, BuildTrace(ruleSet, stopwatch, traceEntries, "First matching rule selected."));
            }
        }

        if (matches.Count == 0)
        {
            return EvaluationResult.NoMatch(NoMatchReason.NoRuleMatched, BuildTrace(ruleSet, stopwatch, traceEntries, "No rule matched the provided inputs."));
        }

        if (matches.Count == 1)
        {
            return EvaluationResult.Matched(matches[0], BuildTrace(ruleSet, stopwatch, traceEntries, "Single matching rule selected."));
        }

        return EvaluationResult.Ambiguous(
            new AmbiguousMatchInfo(matches[0], matches.Skip(1).ToImmutableArray()),
            BuildTrace(ruleSet, stopwatch, traceEntries, $"Ambiguous match: {matches.Count} rules matched."));
    }

    private EvaluationMatchesResult EvaluateAllCore(RuleSet ruleSet, EvaluationContext context, EvaluationOptions? options)
    {
        var evaluationOptions = options ?? EvaluationOptions.Default;
        var stopwatch = Stopwatch.StartNew();
        var traceEntries = evaluationOptions.CaptureDiagnostics ? new List<RuleTraceEntry>(ruleSet.Rules.Length) : null;
        var matches = ImmutableArray.CreateBuilder<MatchResult>();

        if (!TryResolveInputs(ruleSet, context, out var actualInputs, out _))
        {
            return new EvaluationMatchesResult(ImmutableArray<MatchResult>.Empty, BuildTrace(ruleSet, stopwatch, traceEntries, "Invalid input context."));
        }

        for (var index = 0; index < ruleSet.Rules.Length; index++)
        {
            var rule = ruleSet.Rules[index];
            var conditionTrace = traceEntries is null ? null : new List<ConditionTraceEntry>(rule.Conditions.Length);
            var matched = true;

            for (var conditionIndex = 0; conditionIndex < rule.Conditions.Length; conditionIndex++)
            {
                var condition = rule.Conditions[conditionIndex];
                var actual = actualInputs[conditionIndex];
                var result = _matcherRegistry.Match(condition, actual);
                if (conditionTrace is not null)
                {
                    conditionTrace.Add(new ConditionTraceEntry(condition.FieldName, result.MatcherKey, condition.Expected, actual, result.Success, result.Detail));
                }

                if (!result.Success)
                {
                    matched = false;
                    break;
                }
            }

            traceEntries?.Add(new RuleTraceEntry(index, rule.Name, matched, conditionTrace?.ToImmutableArray() ?? ImmutableArray<ConditionTraceEntry>.Empty));
            if (matched)
            {
                matches.Add(new MatchResult(rule, rule.Outputs, rule.PrimaryKey, index));
            }
        }

        return new EvaluationMatchesResult(matches.ToImmutable(), BuildTrace(ruleSet, stopwatch, traceEntries, $"Found {matches.Count} matching rules."));
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static bool TryResolveInputs(RuleSet ruleSet, EvaluationContext context, out object?[] actualInputs, out string? error)
    {
        actualInputs = new object?[ruleSet.InputFields.Length];
        error = null;

        if (!context.PositionalInputs.IsDefaultOrEmpty)
        {
            if (context.PositionalInputs.Length != ruleSet.InputFields.Length)
            {
                error = $"Input count mismatch: expected {ruleSet.InputFields.Length}, actual {context.PositionalInputs.Length}.";
                return false;
            }

            for (var index = 0; index < context.PositionalInputs.Length; index++)
            {
                actualInputs[index] = context.PositionalInputs[index];
            }

            return true;
        }

        for (var index = 0; index < ruleSet.InputFields.Length; index++)
        {
            if (!context.NamedInputs.TryGetValue(ruleSet.InputFields[index], out actualInputs[index]))
            {
                error = $"Missing named input '{ruleSet.InputFields[index]}'.";
                return false;
            }
        }

        return true;
    }

    private static RuleTrace? BuildTrace(RuleSet ruleSet, Stopwatch stopwatch, List<RuleTraceEntry>? traceEntries, string? summary)
    {
        if (traceEntries is null)
        {
            return null;
        }

        stopwatch.Stop();
        return new RuleTrace(ruleSet.Key, traceEntries.Count, stopwatch.Elapsed, traceEntries.ToImmutableArray(), summary);
    }
}
