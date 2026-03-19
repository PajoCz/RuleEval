using System.Collections.Immutable;

namespace RuleEval.Abstractions;

public enum RuleFieldRole
{
    Input = 0,
    Output = 1,
    PrimaryKey = 2,
}

public enum EvaluationStatus
{
    Matched = 0,
    NoMatch = 1,
    AmbiguousMatch = 2,
    InvalidInput = 3,
}

public enum NoMatchReason
{
    None = 0,
    NoRulesInSet = 1,
    InputCountMismatch = 2,
    NoRuleMatched = 3,
}

public sealed record RuleSet(
    string Key,
    ImmutableArray<string> InputFields,
    ImmutableArray<Rule> Rules,
    ImmutableDictionary<string, string?> Metadata)
{
    public static RuleSet Create(
        string key,
        IEnumerable<string> inputFields,
        IEnumerable<Rule> rules,
        IReadOnlyDictionary<string, string?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var normalizedInputFields = inputFields.ToImmutableArray();
        if (normalizedInputFields.IsDefaultOrEmpty)
        {
            throw new InvalidRuleDefinitionException("A rule set must define at least one input field.");
        }

        var normalizedRules = rules.ToImmutableArray();
        if (normalizedRules.Any(rule => rule.Conditions.Length != normalizedInputFields.Length))
        {
            throw new InvalidRuleDefinitionException("Every rule must define exactly one condition for each input field.");
        }

        return new RuleSet(
            key,
            normalizedInputFields,
            normalizedRules,
            metadata?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) ?? ImmutableDictionary<string, string?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));
    }
}

public sealed record Rule(
    int Order,
    ImmutableArray<Condition> Conditions,
    ImmutableArray<OutputValue> Outputs,
    PrimaryKeyValue? PrimaryKey,
    string? Name = null)
{
    public static Rule Create(
        int order,
        IEnumerable<Condition> conditions,
        IEnumerable<OutputValue> outputs,
        PrimaryKeyValue? primaryKey = null,
        string? name = null)
    {
        var normalizedConditions = conditions.ToImmutableArray();
        if (normalizedConditions.IsDefaultOrEmpty)
        {
            throw new InvalidRuleDefinitionException("A rule must contain at least one condition.");
        }

        return new Rule(order, normalizedConditions, outputs.ToImmutableArray(), primaryKey, name);
    }
}

public sealed record Condition(
    string FieldName,
    object? Expected,
    int Order,
    string MatcherKey = DefaultMatcherKeys.Auto,
    string? Label = null);

public sealed record OutputValue(string Name, object? RawValue, int Order);

public sealed record PrimaryKeyValue(string Name, object? Value);

public sealed record EvaluationContext(
    ImmutableDictionary<string, object?> NamedInputs,
    ImmutableArray<object?> PositionalInputs)
{
    public static EvaluationContext FromNamed(IReadOnlyDictionary<string, object?> values)
        => new(values.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase), ImmutableArray<object?>.Empty);

    public static EvaluationContext FromPositional(params object?[] values)
        => new(ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase), values.ToImmutableArray());

    public static EvaluationContext Empty { get; } = new(
        ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableArray<object?>.Empty);
}

public sealed record EvaluationOptions(
    bool CaptureDiagnostics = false,
    bool DetectAmbiguity = false)
{
    public static EvaluationOptions Default { get; } = new();
}

public sealed record MatchResult(
    Rule Rule,
    ImmutableArray<OutputValue> Outputs,
    PrimaryKeyValue? PrimaryKey,
    int RuleIndex);

public sealed record AmbiguousMatchInfo(
    MatchResult FirstMatch,
    ImmutableArray<MatchResult> AdditionalMatches);

public sealed record EvaluationResult(
    EvaluationStatus Status,
    MatchResult? Match,
    NoMatchReason NoMatchReason,
    AmbiguousMatchInfo? AmbiguousMatch,
    RuleTrace? Trace,
    string? Error)
{
    public static EvaluationResult Matched(MatchResult match, RuleTrace? trace)
        => new(EvaluationStatus.Matched, match, NoMatchReason.None, null, trace, null);

    public static EvaluationResult NoMatch(NoMatchReason reason, RuleTrace? trace, string? error = null)
        => new(EvaluationStatus.NoMatch, null, reason, null, trace, error);

    public static EvaluationResult InvalidInput(NoMatchReason reason, RuleTrace? trace, string error)
        => new(EvaluationStatus.InvalidInput, null, reason, null, trace, error);

    public static EvaluationResult Ambiguous(AmbiguousMatchInfo info, RuleTrace? trace)
        => new(EvaluationStatus.AmbiguousMatch, null, NoMatchReason.None, info, trace, null);
}

public sealed record EvaluationMatchesResult(
    ImmutableArray<MatchResult> Matches,
    RuleTrace? Trace)
{
    public bool HasMatches => !Matches.IsDefaultOrEmpty;
}

public sealed record RuleTrace(
    string RuleSetKey,
    int RulesEvaluated,
    TimeSpan Elapsed,
    ImmutableArray<RuleTraceEntry> Rules,
    string? Summary = null);

public sealed record RuleTraceEntry(
    int RuleIndex,
    string? RuleName,
    bool Matched,
    ImmutableArray<ConditionTraceEntry> Conditions);

public sealed record ConditionTraceEntry(
    string FieldName,
    string MatcherKey,
    object? Expected,
    object? Actual,
    bool Matched,
    string? Detail = null);

public static class DefaultMatcherKeys
{
    public const string Auto = "auto";
    public const string Regex = "regex";
    public const string DecimalInterval = "decimal-interval";
    public const string Equality = "equality";
}

public class RuleEvalException : Exception
{
    public RuleEvalException(string message) : base(message)
    {
    }

    public RuleEvalException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class InvalidRuleDefinitionException : RuleEvalException
{
    public InvalidRuleDefinitionException(string message) : base(message)
    {
    }
}

public sealed class InvalidConditionFormatException : RuleEvalException
{
    public InvalidConditionFormatException(string message) : base(message)
    {
    }
}

public sealed class DatabaseLoadException : RuleEvalException
{
    public DatabaseLoadException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
