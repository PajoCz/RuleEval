using Xunit;
using System.Collections.Concurrent;
using RuleEval.Abstractions;
using RuleEval.Caching;
using RuleEval.Core;
using RuleEval.Diagnostics;
using RuleEval.Database;
using RuleEval.Database.Abstractions;

namespace RuleEval.Tests.Unit;

public sealed class RuleEvalUnitTests
{
    [Fact]
    public void Builder_CreatesRuleSet_WithExpectedRolesAndOrdering()
    {
        var ruleSet = CreateSampleRuleSet();

        Assert.Equal(new[] { "segment", "age" }, ruleSet.InputFields.ToArray());
        Assert.Equal(2, ruleSet.Rules.Length);
        Assert.Equal("formula", ruleSet.Rules[0].Outputs[0].Name);
        Assert.Equal("id", ruleSet.Rules[0].PrimaryKey?.Name);
    }

    [Fact]
    public void EvaluateFirst_UsesOrderedFirstMatchSemantics()
    {
        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(CreateSampleRuleSet(), EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("C2/240", result.Match?.Outputs[0].RawValue);
    }

    [Fact]
    public void EvaluateAll_ReturnsAllMatchingRules()
    {
        var evaluator = new RuleSetEvaluator();
        var ruleSet = RuleSetBuilder.Create("all")
            .AddInput("code")
            .AddRule(rule => rule.When("code", ".*").ThenOutput("value", "A"))
            .AddRule(rule => rule.When("code", ".*").ThenOutput("value", "B"))
            .Build();

        var result = evaluator.EvaluateAll(ruleSet, EvaluationContext.FromPositional("x"));

        Assert.Equal(2, result.Matches.Length);
        Assert.Equal("A", result.Matches[0].Outputs[0].RawValue);
        Assert.Equal("B", result.Matches[1].Outputs[0].RawValue);
    }

    [Fact]
    public void RegexMatcher_RespectsFullStringCompatibility()
    {
        var matcher = new RegexConditionMatcher();
        var matched = matcher.Match(new ConditionMatchContext(new Condition("age", "1[5-9]|2[0-4]", 0, DefaultMatcherKeys.Regex), "15"));
        var notMatched = matcher.Match(new ConditionMatchContext(new Condition("age", "1[5-9]|2[0-4]", 0, DefaultMatcherKeys.Regex), "150"));

        Assert.True(matched.Success);
        Assert.False(notMatched.Success);
    }

    [Theory]
    [InlineData("INTERVAL(10;2000)", 10, false)]
    [InlineData("INTERVAL<10;2000)", 10, true)]
    [InlineData("INTERVAL<10;2000>", 2000, true)]
    [InlineData("INTERVAL(10;2000>", 10, false)]
    [InlineData("Interval < 10,5 ; 20.75 >", 20.75, true)]
    public void IntervalParser_AndMatcher_HandleValidSyntax(string text, decimal value, bool expected)
    {
        var parseResult = DecimalIntervalParser.TryParse(text);
        var matcher = new DecimalIntervalConditionMatcher();
        var matchResult = matcher.Match(new ConditionMatchContext(new Condition("amount", text, 0, DefaultMatcherKeys.DecimalInterval), value));

        Assert.True(parseResult.IsSuccess);
        Assert.Equal(expected, matchResult.Success);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("INTER(10;20)")]
    [InlineData("INTERVAL[10;20)")]
    [InlineData("INTERVAL(10,20)")]
    public void IntervalParser_RejectsInvalidSyntax(string? text)
    {
        var result = DecimalIntervalParser.TryParse(text);
        Assert.False(result.IsSuccess);
        Assert.NotEqual(DecimalIntervalParseErrorCode.None, result.ErrorCode);
    }

    [Fact]
    public void EvaluateFirst_ReturnsNoMatch_ForMissingRule()
    {
        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(CreateSampleRuleSet(), EvaluationContext.FromPositional("Nope", 200m));

        Assert.Equal(EvaluationStatus.NoMatch, result.Status);
        Assert.Equal(NoMatchReason.NoRuleMatched, result.NoMatchReason);
    }

    [Fact]
    public void EvaluateFirst_ReturnsAmbiguous_WhenEnabled()
    {
        var evaluator = new RuleSetEvaluator();
        var ruleSet = RuleSetBuilder.Create("ambiguous")
            .AddInput("code")
            .AddRule(rule => rule.When("code", ".*").ThenOutput("value", "A"))
            .AddRule(rule => rule.When("code", ".*").ThenOutput("value", "B"))
            .Build();

        var result = evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("x"), new EvaluationOptions(DetectAmbiguity: true));

        Assert.Equal(EvaluationStatus.AmbiguousMatch, result.Status);
        Assert.NotNull(result.AmbiguousMatch);
        Assert.Single(result.AmbiguousMatch!.AdditionalMatches);
    }

    [Fact]
    public void EvaluateFirst_ReturnsInvalidInput_ForWrongInputCount()
    {
        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(CreateSampleRuleSet(), EvaluationContext.FromPositional("only-one"));

        Assert.Equal(EvaluationStatus.InvalidInput, result.Status);
        Assert.Equal(NoMatchReason.InputCountMismatch, result.NoMatchReason);
    }

    [Fact]
    public void Outputs_AreReturnedAsRawValues()
    {
        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(CreateSampleRuleSet(), EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));

        Assert.Equal("C2/240", result.Match?.Outputs[0].RawValue);
    }

    [Fact]
    public async Task NoCache_NeverReturnsStoredItem()
    {
        var cache = new NoCacheRuleSetCache();
        var key = new RuleSetCacheKey("tests", "sample");
        await cache.SetAsync(key, CreateSampleRuleSet());

        Assert.Null(await cache.GetAsync(key));
    }

    [Fact]
    public async Task MemoryCache_ReturnsStoredItem()
    {
        var cache = new MemoryRuleSetCache();
        var key = new RuleSetCacheKey("tests", "sample");
        var ruleSet = CreateSampleRuleSet();
        await cache.SetAsync(key, ruleSet, TimeSpan.FromMinutes(1));

        Assert.Same(ruleSet, await cache.GetAsync(key));
    }

    [Fact]
    public void DiagnosticsTrace_IsCapturedWhenEnabled()
    {
        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(CreateSampleRuleSet(), EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m), new EvaluationOptions(CaptureDiagnostics: true));

        Assert.NotNull(result.Trace);
        Assert.Equal(1, result.Trace!.RulesEvaluated);
        Assert.True(result.Trace.Rules[0].Matched);
    }

    [Fact]
    public async Task DiagnosticsObserver_CanCaptureEvaluation()
    {
        var observed = new ConcurrentQueue<EvaluationStatus>();
        var observer = new DelegateRuleEvaluationObserver(firstHandler: (key, result, ct) =>
        {
            observed.Enqueue(result.Status);
            return ValueTask.CompletedTask;
        });

        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(CreateSampleRuleSet(), EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));
        await observer.OnEvaluatedAsync("sample", result);

        Assert.Contains(EvaluationStatus.Matched, observed);
    }

    [Fact]
    public async Task Evaluator_IsSafeForParallelReads()
    {
        var evaluator = new RuleSetEvaluator();
        var ruleSet = CreateSampleRuleSet();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m)))));

        Assert.All(results, result => Assert.Equal(EvaluationStatus.Matched, result.Status));
    }

    [Fact]
    public async Task Repository_UsesCacheForLoad()
    {
        var source = new CountingRuleSetSource(CreateDbDefinition());
        var cache = new MemoryRuleSetCache();
        var repository = new RuleSetRepository(source, cache);

        _ = await repository.LoadAsync("pricing", TimeSpan.FromMinutes(5));
        _ = await repository.LoadAsync("pricing", TimeSpan.FromMinutes(5));

        Assert.Equal(1, source.CallCount);
    }

    private static RuleSet CreateSampleRuleSet()
        => RuleSetBuilder
            .Create("pricing")
            .AddInput("segment")
            .AddInput("age")
            .AddRule(rule => rule
                .Named("young")
                .When("segment", ".*Perspektiva.*")
                .When("age", "INTERVAL<15;24>")
                .ThenOutput("formula", "C2/240")
                .WithPrimaryKey("id", 1))
            .AddRule(rule => rule
                .Named("fallback")
                .When("segment", ".*")
                .When("age", "INTERVAL<24;120>")
                .ThenOutput("formula", "Fallback")
                .WithPrimaryKey("id", 2))
            .Build();

    private static DbRuleSetDefinition CreateDbDefinition()
        => new(
            "pricing",
            [
                new RuleSetColumnDefinition("segment", 1, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("age", 2, RuleFieldRole.Input, 2),
                new RuleSetColumnDefinition("formula", 3, RuleFieldRole.Output, 3),
            ],
            [
                new RuleSetRowData(
                    new PrimaryKeyValue("DataId", 1),
                    new Dictionary<string, object?>
                    {
                        ["Col01"] = ".*Perspektiva.*",
                        ["Col02"] = "INTERVAL<15;24>",
                        ["Col03"] = "C2/240",
                    }),
            ]);

    [Fact]
    public void DbMapper_TypeMapping_Input0_Output1()
    {
        var mapper = new DbRuleSetMapper();
        var definition = new DbRuleSetDefinition(
            "type-test",
            [
                new RuleSetColumnDefinition("field1", 1, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("out1", 2, RuleFieldRole.Output, 2),
            ],
            [
                new RuleSetRowData(null, new Dictionary<string, object?> { ["Col01"] = "A", ["Col02"] = "B" }),
            ]);

        var ruleSet = mapper.Map(definition);

        Assert.Equal("field1", ruleSet.InputFields[0]);
        Assert.Equal("out1", ruleSet.Rules[0].Outputs[0].Name);
    }

    [Fact]
    public void DbMapper_InvalidType_ThrowsOnMapRole()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Code"] = "x", ["ColNr"] = 1, ["Order"] = 1, ["Type"] = 99 },
        };

        Assert.Throws<InvalidOperationException>(() => RelationalSourceMappingTestAccessor.MapColumns(rows));
    }

    [Fact]
    public void DbMapper_OrderDiffersFromColNr_PositionalUsesOrder()
    {
        // Order=1 → ColNr=2 (Segment in Col02), Order=2 → ColNr=1 (Age in Col01)
        var mapper = new DbRuleSetMapper();
        var definition = new DbRuleSetDefinition(
            "order-vs-colnr",
            [
                new RuleSetColumnDefinition("Segment", 1, RuleFieldRole.Input, 2),
                new RuleSetColumnDefinition("Age", 2, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("Result", 3, RuleFieldRole.Output, 3),
            ],
            [
                new RuleSetRowData(null, new Dictionary<string, object?>
                {
                    ["Col01"] = "INTERVAL<15;24>",  // Age pattern in Col01
                    ["Col02"] = ".*Perspektiva.*",   // Segment pattern in Col02
                    ["Col03"] = "OK",
                }),
            ]);

        var ruleSet = mapper.Map(definition);

        // InputFields ordered by Order: [Segment(Order=1), Age(Order=2)]
        Assert.Equal(new[] { "Segment", "Age" }, ruleSet.InputFields.ToArray());

        var evaluator = new RuleSetEvaluator();
        // Positional: first arg maps to Segment (Order=1), second to Age (Order=2)
        var result = evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("7BN Perspektiva Důchod", 18m));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("OK", result.Match?.Outputs[0].RawValue);
    }

    [Fact]
    public void DbMapper_NamedEvaluation_UsesCodes()
    {
        var mapper = new DbRuleSetMapper();
        var definition = new DbRuleSetDefinition(
            "named-test",
            [
                new RuleSetColumnDefinition("Segment", 1, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("Result", 2, RuleFieldRole.Output, 2),
            ],
            [
                new RuleSetRowData(null, new Dictionary<string, object?> { ["Col01"] = ".*Foo.*", ["Col02"] = "found" }),
            ]);

        var ruleSet = mapper.Map(definition);

        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(ruleSet,
            EvaluationContext.FromNamed(new Dictionary<string, object?> { ["Segment"] = "FooBar" }));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("found", result.Match?.Outputs[0].RawValue);
    }

    [Fact]
    public void DbMapper_PrimaryKeyFromFirstDataColumn()
    {
        var mapper = new DbRuleSetMapper();
        var definition = new DbRuleSetDefinition(
            "pk-test",
            [
                new RuleSetColumnDefinition("field", 1, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("out", 2, RuleFieldRole.Output, 2),
            ],
            [
                new RuleSetRowData(
                    new PrimaryKeyValue("TranslatorDataId", 42),
                    new Dictionary<string, object?> { ["Col01"] = ".*", ["Col02"] = "val" }),
            ]);

        var ruleSet = mapper.Map(definition);

        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("anything"));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("TranslatorDataId", result.Match?.PrimaryKey?.Name);
        Assert.Equal(42, result.Match?.PrimaryKey?.Value);
    }

    [Fact]
    public void DbMapper_NoSchemaPrimaryKey_EvaluationStillWorks()
    {
        var mapper = new DbRuleSetMapper();
        var definition = new DbRuleSetDefinition(
            "no-pk-test",
            [
                new RuleSetColumnDefinition("field", 1, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("out", 2, RuleFieldRole.Output, 2),
            ],
            [
                new RuleSetRowData(null, new Dictionary<string, object?> { ["Col01"] = ".*", ["Col02"] = "val" }),
            ]);

        var ruleSet = mapper.Map(definition);

        var evaluator = new RuleSetEvaluator();
        var result = evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("anything"));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Null(result.Match?.PrimaryKey);
    }

    [Fact]
    public void DbMapper_PrimaryKeyDoesNotAffectMatching()
    {
        var mapper = new DbRuleSetMapper();
        var definition = new DbRuleSetDefinition(
            "pk-no-match-test",
            [
                new RuleSetColumnDefinition("code", 1, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("out", 2, RuleFieldRole.Output, 2),
            ],
            [
                new RuleSetRowData(new PrimaryKeyValue("RowId", 999), new Dictionary<string, object?> { ["Col01"] = "EXACT", ["Col02"] = "result" }),
            ]);

        var ruleSet = mapper.Map(definition);
        var evaluator = new RuleSetEvaluator();

        // PK value (999) has no effect on match: only Col01 pattern matters
        Assert.Equal(EvaluationStatus.Matched, evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("EXACT")).Status);
        Assert.Equal(EvaluationStatus.NoMatch, evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("OTHER")).Status);
    }

    [Fact]
    public void RelationalSourceMapping_MapColumns_ParsesCodeColNrOrderType()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Code"] = "Segment", ["ColNr"] = 2, ["Order"] = 1, ["Type"] = 0 },
            new Dictionary<string, object?> { ["Code"] = "Age",     ["ColNr"] = 1, ["Order"] = 2, ["Type"] = 0 },
            new Dictionary<string, object?> { ["Code"] = "Result",  ["ColNr"] = 3, ["Order"] = 3, ["Type"] = 1 },
        };

        var columns = RelationalSourceMappingTestAccessor.MapColumns(rows);

        Assert.Equal(3, columns.Count);
        Assert.Equal("Segment", columns[0].Code);
        Assert.Equal(2, columns[0].ColNr);
        Assert.Equal(1, columns[0].Order);
        Assert.Equal(RuleFieldRole.Input, columns[0].Role);
        Assert.Equal(RuleFieldRole.Output, columns[2].Role);
    }

    [Fact]
    public void RelationalSourceMapping_MapRows_DetectsPrimaryKeyColumn()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["TranslatorDataId"] = 7, ["Col01"] = "A", ["Col02"] = "B" },
        };

        var mapped = RelationalSourceMappingTestAccessor.MapRows(rows);

        Assert.Single(mapped);
        Assert.Equal("TranslatorDataId", mapped[0].PrimaryKey?.Name);
        Assert.Equal(7, mapped[0].PrimaryKey?.Value);
        Assert.Equal("A", mapped[0].ColValues["Col01"]);
        Assert.Equal("B", mapped[0].ColValues["Col02"]);
        Assert.DoesNotContain("TranslatorDataId", mapped[0].ColValues.Keys);
    }

    [Fact]
    public void RelationalSourceMapping_MapRows_NoPrimaryKey_WhenOnlyColXX()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Col01"] = "A", ["Col02"] = "B" },
        };

        var mapped = RelationalSourceMappingTestAccessor.MapRows(rows);

        Assert.Single(mapped);
        Assert.Null(mapped[0].PrimaryKey);
    }

    [Fact]
    public void RelationalSourceMapping_MapRows_ThrowsForMultipleNonColXXColumns()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["DataId"] = 1, ["SchemaCode"] = "x", ["Col01"] = "A" },
        };

        Assert.Throws<InvalidOperationException>(() => RelationalSourceMappingTestAccessor.MapRows(rows));
    }
    private sealed class CountingRuleSetSource : IRuleSetSource
    {
        private readonly DbRuleSetDefinition _definition;

        public CountingRuleSetSource(DbRuleSetDefinition definition)
        {
            _definition = definition;
        }

        public int CallCount { get; private set; }

        public ValueTask<DbRuleSetDefinition> LoadAsync(string key, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_definition);
        }
    }
}

internal static class RelationalSourceMappingTestAccessor
{
    public static IReadOnlyList<RuleSetColumnDefinition> MapColumns(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => RelationalSourceMapping.MapColumns(rows);

    public static IReadOnlyList<RuleSetRowData> MapRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => RelationalSourceMapping.MapRows(rows);
}
