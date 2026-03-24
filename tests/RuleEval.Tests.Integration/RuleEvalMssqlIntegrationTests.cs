using Xunit;
using Microsoft.Data.SqlClient;
using RuleEval.Abstractions;
using RuleEval.Caching;
using RuleEval.Database;
using RuleEval.Database.Abstractions;

namespace RuleEval.Tests.Integration;

/// <summary>
/// Integrační testy proti skutečnému SQL Serveru.
/// Před spuštěním spusť sql/integration-test-mssql.sql na cílové DB.
///
/// Připojovací řetězec (výchozí nebo env. proměnná):
///   Server=.;Database=RuleEval;Trusted_Connection=True;TrustServerCertificate=True;
/// Přepis: set RULEEVAL_MSSQL=Server=myserver;Database=...
///
/// Testy jsou označeny [Trait("Category","Database")] a přeskočeny,
/// pokud SQL Server není dostupný.
/// </summary>
[Trait("Category", "Database")]
public sealed class RuleEvalMssqlIntegrationTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Server=.;Database=RuleEval;Trusted_Connection=True;TrustServerCertificate=True;";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("RULEEVAL_MSSQL") ?? DefaultConnectionString;

    private bool _skip;
    private RuleSetRepository _repository = null!;

    public async Task InitializeAsync()
    {
        try
        {
            await using var probe = new SqlConnection(ConnectionString);
            await probe.OpenAsync();
        }
        catch
        {
            _skip = true;
            return;
        }

        var executor = new AdoCommandExecutor(cs => new SqlConnection(cs));
        var source = new SqlServerRuleSetSource(
            executor,
            ConnectionString,
            "[RuleEvalTest].p_GetSchemaColBySchemaCode",
            "[RuleEvalTest].p_GetDataBySchemaCode");
        _repository = new RuleSetRepository(source, new NoCacheRuleSetCache());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------
    // Základní schema: Segment (regex) + Age (decimal-interval)
    // ---------------------------------------------------------------

    [SkippableFact]
    public async Task EvaluateFirst_Positional_MatchesFirstRow()
    {
        Skip.If(_skip, $"SQL Server nedostupný. Nastav env RULEEVAL_MSSQL nebo použij: {DefaultConnectionString}");

        var result = await _repository.EvaluateFirstAsync(
            "RuleEvalTest_Pricing",
            EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("C2/240", result.Match?.Outputs.First(o => o.Name == "Formula").RawValue?.ToString());
    }

    [SkippableFact]
    public async Task EvaluateFirst_Positional_MatchesSecondRow()
    {
        Skip.If(_skip, $"SQL Server nedostupný.");

        var result = await _repository.EvaluateFirstAsync(
            "RuleEvalTest_Pricing",
            EvaluationContext.FromPositional("7BN Standard Produkt", 30m));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("D3/120", result.Match?.Outputs.First(o => o.Name == "Formula").RawValue?.ToString());
    }

    [SkippableFact]
    public async Task EvaluateFirst_Named_ReturnsSameResultAsPositional()
    {
        Skip.If(_skip, "SQL Server nedostupný.");

        var result = await _repository.EvaluateFirstAsync(
            "RuleEvalTest_Pricing",
            EvaluationContext.FromNamed(new Dictionary<string, object?>
            {
                ["Segment"] = "7BN Perspektiva Důchod",
                ["Age"]     = 15m,
            }));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("C2/240", result.Match?.Outputs.First(o => o.Name == "Formula").RawValue?.ToString());
    }

    [SkippableFact]
    public async Task GetFirstOutput_ReturnsFormulaValue()
    {
        Skip.If(_skip, "SQL Server nedostupný.");

        var value = await _repository.GetFirstOutputAsync(
            "RuleEvalTest_Pricing",
            EvaluationContext.FromPositional("7BN Standard Produkt", 30m),
            "Formula");

        Assert.Equal("D3/120", value);
    }

    [SkippableFact]
    public async Task EvaluateFirst_UnknownInput_ReturnsNoMatch()
    {
        Skip.If(_skip, "SQL Server nedostupný.");

        var result = await _repository.EvaluateFirstAsync(
            "RuleEvalTest_Pricing",
            EvaluationContext.FromPositional("NOMATCH", 0m));

        Assert.Equal(EvaluationStatus.NoMatch, result.Status);
    }

    // ---------------------------------------------------------------
    // Schema s prohozeným Order vs ColNr
    // Order=1 → ColNr=2 (Col02 = segment pattern)
    // Order=2 → ColNr=1 (Col01 = age pattern)
    // Poziční volání: FromPositional(segmentValue, ageValue)
    // ---------------------------------------------------------------

    [SkippableFact]
    public async Task EvaluateFirst_OrderDiffersFromColNr_PositionalMapsToOrderNotColNr()
    {
        Skip.If(_skip, "SQL Server nedostupný.");

        // Poziční pořadí odpovídá Order (1=Segment, 2=Age),
        // fyzicky jsou ale Col01=Age pattern, Col02=Segment pattern.
        var result = await _repository.EvaluateFirstAsync(
            "RuleEvalTest_OrderVsColNr",
            EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("OK", result.Match?.Outputs.First(o => o.Name == "Result").RawValue?.ToString());
    }

    // ---------------------------------------------------------------
    // Cache: dvě volání LoadAsync → DB voláno jen jednou
    // ---------------------------------------------------------------

    [SkippableFact]
    public async Task LoadAsync_WithMemoryCache_LoadsFromDbOnlyOnce()
    {
        Skip.If(_skip, "SQL Server nedostupný.");

        var executor = new AdoCommandExecutor(cs => new SqlConnection(cs));
        var inner = new SqlServerRuleSetSource(
            executor,
            ConnectionString,
            "[RuleEvalTest].p_GetSchemaColBySchemaCode",
            "[RuleEvalTest].p_GetDataBySchemaCode");
        var counting = new CountingSource(inner);
        var repository = new RuleSetRepository(counting, new MemoryRuleSetCache());
        var ttl = TimeSpan.FromMinutes(5);

        // První volání — cache miss → načte z DB, uloží do cache
        var first = await repository.LoadAsync("RuleEvalTest_Pricing", ttl);
        // Druhé volání — cache hit → vrátí z paměti, DB nevoláno
        var second = await repository.LoadAsync("RuleEvalTest_Pricing", ttl);

        Assert.Equal(1, counting.LoadCount);
        Assert.Same(first, second);
    }

    [SkippableFact]
    public async Task EvaluateFirst_WithCachedRuleSet_DbCalledOnlyOnFirstEval()
    {
        Skip.If(_skip, "SQL Server nedostupný.");

        var executor = new AdoCommandExecutor(cs => new SqlConnection(cs));
        var inner = new SqlServerRuleSetSource(
            executor,
            ConnectionString,
            "[RuleEvalTest].p_GetSchemaColBySchemaCode",
            "[RuleEvalTest].p_GetDataBySchemaCode");
        var counting = new CountingSource(inner);
        var cache = new MemoryRuleSetCache();
        var repository = new RuleSetRepository(counting, cache);
        var ttl = TimeSpan.FromMinutes(5);

        // Zahřeje cache
        await repository.LoadAsync("RuleEvalTest_Pricing", ttl);
        Assert.Equal(1, counting.LoadCount);

        // Obě evaluace používají cache (volaná přes LoadAsync s TTL = 0,
        // ale GetAsync vrátí data uložená předchozím LoadAsync s TTL > 0)
        var result1 = await repository.EvaluateFirstAsync(
            "RuleEvalTest_Pricing",
            EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));
        var result2 = await repository.EvaluateFirstAsync(
            "RuleEvalTest_Pricing",
            EvaluationContext.FromPositional("7BN Standard Produkt", 30m));

        Assert.Equal(EvaluationStatus.Matched, result1.Status);
        Assert.Equal(EvaluationStatus.Matched, result2.Status);
        // DB bylo voláno celkem jen jednou — vše ostatní šlo z cache
        Assert.Equal(1, counting.LoadCount);
    }

    [SkippableFact]
    public async Task LoadAsync_AfterCacheInvalidation_LoadsFromDbAgain()
    {
        Skip.If(_skip, "SQL Server nedostupný.");

        var executor = new AdoCommandExecutor(cs => new SqlConnection(cs));
        var inner = new SqlServerRuleSetSource(
            executor,
            ConnectionString,
            "[RuleEvalTest].p_GetSchemaColBySchemaCode",
            "[RuleEvalTest].p_GetDataBySchemaCode");
        var counting = new CountingSource(inner);
        var repository = new RuleSetRepository(counting, new MemoryRuleSetCache());
        var ttl = TimeSpan.FromMinutes(5);

        // 1. Cache miss → DB voláno, RuleSet uložen do cache
        var first = await repository.LoadAsync("RuleEvalTest_Pricing", ttl);
        Assert.Equal(1, counting.LoadCount);

        // 2. Cache hit → DB nevoláno
        var cached = await repository.LoadAsync("RuleEvalTest_Pricing", ttl);
        Assert.Equal(1, counting.LoadCount);
        Assert.Same(first, cached);

        // 3. Invalidace konkrétního klíče
        await repository.InvalidateCacheAsync("RuleEvalTest_Pricing");

        // 4. Cache miss → DB voláno znovu
        var reloaded = await repository.LoadAsync("RuleEvalTest_Pricing", ttl);
        Assert.Equal(2, counting.LoadCount);
        Assert.NotSame(first, reloaded);
    }

    // ---------------------------------------------------------------

    private sealed class CountingSource : IRuleSetSource
    {
        private readonly IRuleSetSource _inner;
        public int LoadCount { get; private set; }

        public CountingSource(IRuleSetSource inner) => _inner = inner;

        public async ValueTask<DbRuleSetDefinition> LoadAsync(string key, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return await _inner.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }
}
