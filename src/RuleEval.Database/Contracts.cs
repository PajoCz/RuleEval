using RuleEval.Abstractions;

namespace RuleEval.Database.Abstractions;

public sealed record RuleSetColumnDefinition(
    string ColumnName,
    int ColumnOrder,
    RuleFieldRole Role,
    string? FieldName = null,
    string? MatcherKey = null);

public sealed record RuleSetRowData(IReadOnlyDictionary<string, object?> Values);

public sealed record DbRuleSetDefinition(
    string Key,
    IReadOnlyList<RuleSetColumnDefinition> Columns,
    IReadOnlyList<RuleSetRowData> Rows,
    IReadOnlyDictionary<string, string?>? Metadata = null);

public interface IRuleSetSource
{
    ValueTask<DbRuleSetDefinition> LoadAsync(string key, CancellationToken cancellationToken = default);
}

public interface IRuleSetRepository
{
    ValueTask<RuleSet> LoadAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<RuleSet> LoadAsync(string key, TimeSpan cacheTtl, CancellationToken cancellationToken = default);
    ValueTask<EvaluationResult> EvaluateFirstAsync(string key, EvaluationContext context, EvaluationOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<EvaluationResult> EvaluateFirstOrThrowAsync(string key, EvaluationContext context, EvaluationOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<string?> GetFirstOutputAsync(string key, EvaluationContext context, string outputName, EvaluationOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<string> GetFirstOutputOrThrowAsync(string key, EvaluationContext context, string outputName, EvaluationOptions? options = null, CancellationToken cancellationToken = default);
}
