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
                new RuleSetColumnDefinition("segment", 0, RuleFieldRole.Input, "segment", DefaultMatcherKeys.Regex),
                new RuleSetColumnDefinition("age", 1, RuleFieldRole.Input, "age", DefaultMatcherKeys.DecimalInterval),
                new RuleSetColumnDefinition("formula", 2, RuleFieldRole.Output, "formula"),
                new RuleSetColumnDefinition("id", 3, RuleFieldRole.PrimaryKey, "id"),
            ],
            [
                new RuleSetRowData(new Dictionary<string, object?>
                {
                    ["segment"] = ".*Perspektiva.*",
                    ["age"] = "INTERVAL<15;24>",
                    ["formula"] = "C2/240",
                    ["id"] = 1,
                }),
            ]);

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
