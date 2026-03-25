using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuleEval.Abstractions;
using RuleEval.Caching;
using RuleEval.Core;
using RuleEval.Database.Abstractions;

[assembly: InternalsVisibleTo("RuleEval.Tests.Unit")]

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

        var orderedColumns = definition.Columns.OrderBy(static column => column.Order).ToArray();
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

            foreach (var column in orderedColumns)
            {
                var colKey = $"Col{column.ColNr:D2}";
                if (!row.ColValues.TryGetValue(colKey, out var value))
                {
                    throw new InvalidRuleDefinitionException($"Row {rowIndex} is missing value for column '{colKey}'.");
                }

                switch (column.Role)
                {
                    case RuleFieldRole.Input:
                        conditions.Add(new Condition(column.Code, value ?? string.Empty, conditions.Count));
                        break;
                    case RuleFieldRole.Output:
                        outputs.Add(new OutputValue(column.Code, value, outputs.Count));
                        break;
                    default:
                        throw new InvalidRuleDefinitionException($"Unsupported rule field role '{column.Role}'.");
                }
            }

            rules.Add(RuleEval.Abstractions.Rule.Create(rowIndex, conditions.ToImmutable(), outputs.ToImmutable(), row.PrimaryKey));
        }

        return RuleSet.Create(
            definition.Key,
            inputColumns.Select(static column => column.Code),
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
    private readonly ILogger<RuleSetRepository> _logger;

    /// <summary>
    /// Initialises a new <see cref="RuleSetRepository"/>.
    /// </summary>
    /// <param name="source">Database source used to load rule sets.</param>
    /// <param name="cache">Optional cache.  Defaults to a no-op cache.</param>
    /// <param name="mapper">Optional mapper.  Defaults to a new <see cref="DbRuleSetMapper"/>.</param>
    /// <param name="evaluator">Optional evaluator.  Defaults to a new <see cref="RuleSetEvaluator"/>.</param>
    /// <param name="cacheNamespace">Cache namespace prefix.  Defaults to <c>"rulesets"</c>.</param>
    /// <param name="defaultCacheTtl">Default TTL for cached rule sets.  Zero disables caching.</param>
    /// <param name="logger">
    /// Optional logger.  When <c>null</c> a no-op logger is used so that logging never
    /// affects execution when the hosting application has not configured a provider.
    /// </param>
    public RuleSetRepository(
        IRuleSetSource source,
        IRuleSetCache? cache = null,
        DbRuleSetMapper? mapper = null,
        RuleSetEvaluator? evaluator = null,
        string cacheNamespace = "rulesets",
        TimeSpan defaultCacheTtl = default,
        ILogger<RuleSetRepository>? logger = null)
    {
        _source = source;
        _cache = cache ?? new NoCacheRuleSetCache();
        _mapper = mapper ?? new DbRuleSetMapper();
        _evaluator = evaluator ?? new RuleSetEvaluator();
        _cacheNamespace = cacheNamespace;
        _defaultCacheTtl = defaultCacheTtl;
        _logger = logger ?? NullLogger<RuleSetRepository>.Instance;
    }

    public ValueTask<RuleSet> LoadAsync(string key, CancellationToken cancellationToken = default)
        => LoadAsync(key, _defaultCacheTtl, cancellationToken);

    public async ValueTask InvalidateCacheAsync(string key, CancellationToken cancellationToken = default)
    {
        var cacheKey = new RuleSetCacheKey(_cacheNamespace, key);

        using var activity = RuleEvalTelemetry.ActivitySource.StartActivity("ruleeval.cache.invalidate");
        activity?.SetTag("rule.key", key);

        _logger.LogDebug("Invalidating cache for rule set '{RuleSetKey}'", key);
        await _cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Cache invalidated for rule set '{RuleSetKey}'", key);
    }

    public async ValueTask<RuleSet> LoadAsync(string key, TimeSpan cacheTtl, CancellationToken cancellationToken = default)
    {
        var cacheKey = new RuleSetCacheKey(_cacheNamespace, key);

        using var activity = RuleEvalTelemetry.ActivitySource.StartActivity("ruleeval.ruleset.load");
        activity?.SetTag("rule.key", key);

        _logger.LogDebug("Loading rule set '{RuleSetKey}'", key);

        // ── Cache lookup ──────────────────────────────────────────────────────
        using (var cacheActivity = RuleEvalTelemetry.ActivitySource.StartActivity("ruleeval.cache.get"))
        {
            cacheActivity?.SetTag("rule.key", key);
            var cached = await _cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                cacheActivity?.SetTag("rule.cache.hit", true);
                activity?.SetTag("rule.cache.hit", true);
                RuleEvalTelemetry.CacheHitCounter.Add(1);
                _logger.LogDebug("Rule set '{RuleSetKey}' served from cache", key);
                return cached;
            }

            cacheActivity?.SetTag("rule.cache.hit", false);
        }

        activity?.SetTag("rule.cache.hit", false);
        RuleEvalTelemetry.CacheMissCounter.Add(1);

        // ── Database load ─────────────────────────────────────────────────────
        RuleSet ruleSet;
        try
        {
            using var dbActivity = RuleEvalTelemetry.ActivitySource.StartActivity("ruleeval.db.load");
            dbActivity?.SetTag("rule.key", key);

            _logger.LogDebug("Loading rule set '{RuleSetKey}' from database", key);
            var dbLoadStopwatch = Stopwatch.StartNew();

            DbRuleSetDefinition definition;
            try
            {
                definition = await _source.LoadAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                dbActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                dbActivity?.SetTag("error.type", ex.GetType().Name);
                throw;
            }

            dbLoadStopwatch.Stop();
            ruleSet = _mapper.Map(definition);
            RuleEvalTelemetry.DbLoadDurationHistogram.Record(dbLoadStopwatch.Elapsed.TotalMilliseconds);
            _logger.LogInformation(
                "Rule set '{RuleSetKey}' loaded from database in {ElapsedMs:F2}ms ({RuleCount} rule(s))",
                key, dbLoadStopwatch.Elapsed.TotalMilliseconds, ruleSet.Rules.Length);
        }
        catch (RuleEvalException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().Name);
            _logger.LogError(ex, "Failed to load rule set '{RuleSetKey}' from database", key);
            throw new DatabaseLoadException($"Failed to load rule set '{key}'.", ex);
        }

        // ── Cache store ───────────────────────────────────────────────────────
        if (cacheTtl > TimeSpan.Zero)
        {
            using var cacheSetActivity = RuleEvalTelemetry.ActivitySource.StartActivity("ruleeval.cache.set");
            cacheSetActivity?.SetTag("rule.key", key);
            await _cache.SetAsync(cacheKey, ruleSet, cacheTtl, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Rule set '{RuleSetKey}' stored in cache (TTL: {Ttl})", key, cacheTtl);
        }

        return ruleSet;
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
        return new DbRuleSetDefinition(key, columns, RelationalSourceMapping.MapRows(dataRows));
    }

}

internal static class RelationalSourceMapping
{
    public static IReadOnlyList<RuleSetColumnDefinition> MapColumns(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => rows.Select(static row => new RuleSetColumnDefinition(
            Convert.ToString(row["Code"]) ?? throw new InvalidOperationException("Missing Code."),
            Convert.ToInt32(row["Order"]),
            MapRole(Convert.ToInt32(row["Type"])),
            Convert.ToInt32(row["ColNr"])))
            .OrderBy(static column => column.Order)
            .ToArray();

    public static IReadOnlyList<RuleSetRowData> MapRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => rows.Select(static row =>
        {
            var pkColumns = row.Keys.Where(static k => !IsColXxKey(k)).ToArray();
            PrimaryKeyValue? primaryKey = pkColumns.Length switch
            {
                0 => null,
                1 => new PrimaryKeyValue(pkColumns[0], row[pkColumns[0]]),
                _ => throw new InvalidOperationException($"Data row must contain exactly one primary key column (non-ColXX); found {pkColumns.Length}: {string.Join(", ", pkColumns)}.")
            };

            var colValues = row
                .Where(static kvp => IsColXxKey(kvp.Key))
                .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

            return new RuleSetRowData(primaryKey, colValues);
        }).ToArray();

    private static bool IsColXxKey(string key)
        => key.Length == 5
            && key.StartsWith("Col", StringComparison.OrdinalIgnoreCase)
            && char.IsDigit(key[3])
            && char.IsDigit(key[4]);

    private static RuleFieldRole MapRole(int type) => type switch
    {
        0 => RuleFieldRole.Input,
        1 => RuleFieldRole.Output,
        _ => throw new InvalidOperationException($"Unknown column type '{type}'. Supported values: 0 (Input), 1 (Output).")
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
        return new DbRuleSetDefinition(key, columns, RelationalSourceMapping.MapRows(dataRows));
    }
}
