using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using RuleEval.Abstractions;
using RuleEval.Caching;
using RuleEval.Core;
using RuleEval.Database.Abstractions;

namespace RuleEval.Database;

public sealed class DbRuleSetMapper
{
    public RuleSet Map(DbRuleSetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Columns.Count == 0)
        {
            throw new InvalidRuleDefinitionException("Database rule set definition must include column metadata.");
        }

        var orderedColumns = definition.Columns.OrderBy(static column => column.ColumnOrder).ToArray();
        var inputColumns = orderedColumns.Where(static column => column.Role == RuleFieldRole.Input).ToArray();
        if (inputColumns.Length == 0)
        {
            throw new InvalidRuleDefinitionException("Database rule set definition must include at least one input column.");
        }

        var rules = ImmutableArray.CreateBuilder<RuleEval.Abstractions.Rule>(definition.Rows.Count);
        for (var rowIndex = 0; rowIndex < definition.Rows.Count; rowIndex++)
        {
            var row = definition.Rows[rowIndex];
            var conditions = ImmutableArray.CreateBuilder<Condition>(inputColumns.Length);
            var outputs = ImmutableArray.CreateBuilder<OutputValue>();
            PrimaryKeyValue? primaryKey = null;

            foreach (var column in orderedColumns)
            {
                if (!row.Values.TryGetValue(column.ColumnName, out var value))
                {
                    throw new InvalidRuleDefinitionException($"Row {rowIndex} is missing value for column '{column.ColumnName}'.");
                }

                var fieldName = column.FieldName ?? column.ColumnName;
                switch (column.Role)
                {
                    case RuleFieldRole.Input:
                        conditions.Add(new Condition(fieldName, value ?? string.Empty, conditions.Count, column.MatcherKey ?? DefaultMatcherKeys.Auto));
                        break;
                    case RuleFieldRole.Output:
                        outputs.Add(new OutputValue(fieldName, value, outputs.Count));
                        break;
                    case RuleFieldRole.PrimaryKey:
                        primaryKey = new PrimaryKeyValue(fieldName, value);
                        break;
                    default:
                        throw new InvalidRuleDefinitionException($"Unsupported rule field role '{column.Role}'.");
                }
            }

            rules.Add(RuleEval.Abstractions.Rule.Create(rowIndex, conditions.ToImmutable(), outputs.ToImmutable(), primaryKey));
        }

        return RuleSet.Create(
            definition.Key,
            inputColumns.Select(static column => column.FieldName ?? column.ColumnName),
            rules.ToImmutable().ToArray(),
            definition.Metadata);
    }
}

public sealed class RuleSetRepository : IRuleSetRepository
{
    private readonly IRuleSetSource _source;
    private readonly IRuleSetCache _cache;
    private readonly DbRuleSetMapper _mapper;
    private readonly RuleSetEvaluator _evaluator;
    private readonly string _cacheNamespace;
    private readonly TimeSpan _defaultCacheTtl;

    public RuleSetRepository(
        IRuleSetSource source,
        IRuleSetCache? cache = null,
        DbRuleSetMapper? mapper = null,
        RuleSetEvaluator? evaluator = null,
        string cacheNamespace = "rulesets",
        TimeSpan defaultCacheTtl = default)
    {
        _source = source;
        _cache = cache ?? new NoCacheRuleSetCache();
        _mapper = mapper ?? new DbRuleSetMapper();
        _evaluator = evaluator ?? new RuleSetEvaluator();
        _cacheNamespace = cacheNamespace;
        _defaultCacheTtl = defaultCacheTtl;
    }

    public ValueTask<RuleSet> LoadAsync(string key, CancellationToken cancellationToken = default)
        => LoadAsync(key, _defaultCacheTtl, cancellationToken);

    public ValueTask InvalidateCacheAsync(string key, CancellationToken cancellationToken = default)
        => _cache.RemoveAsync(new RuleSetCacheKey(_cacheNamespace, key), cancellationToken);

    public async ValueTask<RuleSet> LoadAsync(string key, TimeSpan cacheTtl, CancellationToken cancellationToken = default)
    {
        var cacheKey = new RuleSetCacheKey(_cacheNamespace, key);
        var cached = await _cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        try
        {
            var definition = await _source.LoadAsync(key, cancellationToken).ConfigureAwait(false);
            var ruleSet = _mapper.Map(definition);
            if (cacheTtl > TimeSpan.Zero)
            {
                await _cache.SetAsync(cacheKey, ruleSet, cacheTtl, cancellationToken).ConfigureAwait(false);
            }

            return ruleSet;
        }
        catch (RuleEvalException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DatabaseLoadException($"Failed to load rule set '{key}'.", ex);
        }
    }

    public async ValueTask<EvaluationResult> EvaluateFirstAsync(string key, EvaluationContext context, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var ruleSet = await LoadAsync(key, cancellationToken).ConfigureAwait(false);
        return _evaluator.EvaluateFirst(ruleSet, context, options);
    }

    public async ValueTask<EvaluationResult> EvaluateFirstOrThrowAsync(string key, EvaluationContext context, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
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

    public async ValueTask<string?> GetFirstOutputAsync(string key, EvaluationContext context, string outputName, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await EvaluateFirstAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        if (result.Status != EvaluationStatus.Matched || result.Match is null)
        {
            return null;
        }

        return result.Match.Outputs.FirstOrDefault(output => string.Equals(output.Name, outputName, StringComparison.OrdinalIgnoreCase))?.RawValue?.ToString();
    }

    public async ValueTask<string> GetFirstOutputOrThrowAsync(string key, EvaluationContext context, string outputName, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var value = await GetFirstOutputAsync(key, context, outputName, options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException($"Output '{outputName}' was not found for rule set '{key}'.");
    }

    public async ValueTask<string?> GetFirstOutputAsync(string key, EvaluationContext context, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await EvaluateFirstAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        if (result.Status != EvaluationStatus.Matched || result.Match is null)
        {
            return null;
        }

        return result.Match.Outputs.FirstOrDefault()?.RawValue?.ToString();
    }

    public async ValueTask<string> GetFirstOutputOrThrowAsync(string key, EvaluationContext context, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var value = await GetFirstOutputAsync(key, context, options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException($"Rule set '{key}' returned no output.");
    }
}

public interface IRelationalCommandExecutor
{
    ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string connectionString, string commandText, IReadOnlyDictionary<string, object?> parameters, CommandType commandType, CancellationToken cancellationToken = default);
}

public sealed class AdoCommandExecutor : IRelationalCommandExecutor
{
    private readonly Func<string, IDbConnection> _connectionFactory;

    public AdoCommandExecutor(Func<string, IDbConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string connectionString, string commandText, IReadOnlyDictionary<string, object?> parameters, CommandType commandType, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory(connectionString);
        if (connection is not DbConnection dbConnection)
        {
            throw new InvalidOperationException("Connection factory must return a DbConnection instance.");
        }

        await dbConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = dbConnection.CreateCommand();
        command.CommandText = commandText;
        command.CommandType = commandType;

        foreach (var parameter in parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Key;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken).ConfigureAwait(false) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }
}

public sealed class SqlServerRuleSetSource : IRuleSetSource
{
    private readonly IRelationalCommandExecutor _executor;
    private readonly string _connectionString;
    private readonly string _columnsStoredProcedure;
    private readonly string _rowsStoredProcedure;

    public SqlServerRuleSetSource(IRelationalCommandExecutor executor, string connectionString, string columnsStoredProcedure, string rowsStoredProcedure)
    {
        _executor = executor;
        _connectionString = connectionString;
        _columnsStoredProcedure = columnsStoredProcedure;
        _rowsStoredProcedure = rowsStoredProcedure;
    }

    public async ValueTask<DbRuleSetDefinition> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["@Code"] = key };
        var columnRows = await _executor.QueryAsync(_connectionString, _columnsStoredProcedure, parameters, CommandType.StoredProcedure, cancellationToken).ConfigureAwait(false);
        var dataRows = await _executor.QueryAsync(_connectionString, _rowsStoredProcedure, parameters, CommandType.StoredProcedure, cancellationToken).ConfigureAwait(false);
        var columns = RelationalSourceMapping.MapColumns(columnRows);
        return new DbRuleSetDefinition(key, columns, RelationalSourceMapping.MapRows(dataRows, columns));
    }

}

internal static class RelationalSourceMapping
{
    public static IReadOnlyList<RuleSetColumnDefinition> MapColumns(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => rows.Select(static row => new RuleSetColumnDefinition(
            Convert.ToString(row["Name"]) ?? throw new InvalidOperationException("Missing Name."),
            Convert.ToInt32(row["Order"]),
            MapRole(Convert.ToInt32(row["Type"])),
            Convert.ToString(row.TryGetValue("FieldName", out var fieldName) ? fieldName : null),
            Convert.ToString(row.TryGetValue("MatcherKey", out var matcherKey) ? matcherKey : null),
            Convert.ToInt32(row["ColNr"])))
            .OrderBy(static column => column.ColumnOrder)
            .ToArray();

    public static IReadOnlyList<RuleSetRowData> MapRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<RuleSetColumnDefinition> columns)
        => rows.Select(row =>
        {
            var mapped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                var colKey = $"Col{(column.SourceColumnNumber ?? column.ColumnOrder):D2}";
                mapped[column.ColumnName] = row.TryGetValue(colKey, out var value) ? value : null;
            }
            return new RuleSetRowData(mapped);
        }).ToArray();

    private static RuleFieldRole MapRole(int type) => type switch
    {
        1 => RuleFieldRole.Input,
        2 => RuleFieldRole.Output,
        3 => RuleFieldRole.PrimaryKey,
        _ => throw new InvalidOperationException($"Unknown column type '{type}'.")
    };
}

public sealed class PostgreSqlRuleSetSource : IRuleSetSource
{
    private readonly IRelationalCommandExecutor _executor;
    private readonly string _connectionString;
    private readonly string _columnsFunction;
    private readonly string _rowsFunction;

    public PostgreSqlRuleSetSource(IRelationalCommandExecutor executor, string connectionString, string columnsFunction, string rowsFunction)
    {
        _executor = executor;
        _connectionString = connectionString;
        _columnsFunction = columnsFunction;
        _rowsFunction = rowsFunction;
    }

    public async ValueTask<DbRuleSetDefinition> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["@code"] = key };
        var columnRows = await _executor.QueryAsync(_connectionString, $"select * from {_columnsFunction}(@code)", parameters, CommandType.Text, cancellationToken).ConfigureAwait(false);
        var dataRows = await _executor.QueryAsync(_connectionString, $"select * from {_rowsFunction}(@code)", parameters, CommandType.Text, cancellationToken).ConfigureAwait(false);
        var columns = RelationalSourceMapping.MapColumns(columnRows);
        return new DbRuleSetDefinition(key, columns, RelationalSourceMapping.MapRows(dataRows, columns));
    }
}
