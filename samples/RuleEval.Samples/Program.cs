using Microsoft.Extensions.DependencyInjection;
using RuleEval.Abstractions;
using RuleEval.Caching;
using RuleEval.Core;
using RuleEval.Database;
using RuleEval.Database.Abstractions;
using RuleEval.DependencyInjection;

var ruleSet = RuleSetBuilder
    .Create("pricing")
    .AddInput("segment")
    .AddInput("age")
    .AddRule(rule => rule
        .Named("perspective-young")
        .When("segment", ".*Perspektiva.*")
        .When("age", "INTERVAL<15;24>")
        .ThenOutput("formula", "C2/240")
        .WithPrimaryKey("id", 1))
    .AddRule(rule => rule
        .Named("fallback")
        .When("segment", ".*")
        .When("age", "INTERVAL<24;120>")
        .ThenOutput("formula", "Fallback"))
    .Build();

var evaluator = new RuleSetEvaluator();
var result = evaluator.EvaluateFirst(ruleSet, EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m), new EvaluationOptions(CaptureDiagnostics: true));
Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"First output: {result.Match?.Outputs[0].RawValue}");
Console.WriteLine($"Trace captured: {result.Trace is not null}");

var services = new ServiceCollection();
services.AddRuleEval();
var provider = services.BuildServiceProvider();
var resolvedEvaluator = provider.GetRequiredService<RuleSetEvaluator>();
Console.WriteLine($"DI evaluator ready: {resolvedEvaluator is not null}");

var cache = new MemoryRuleSetCache();
await cache.SetAsync(new RuleSetCacheKey("sample", ruleSet.Key), ruleSet, TimeSpan.FromMinutes(5));
Console.WriteLine($"Cached: {await cache.GetAsync(new RuleSetCacheKey("sample", ruleSet.Key)) is not null}");

var repository = new RuleSetRepository(new InMemoryRuleSetSource(ruleSet), cache);
Console.WriteLine($"Repository output: {await repository.GetFirstOutputAsync(ruleSet.Key, EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m), "formula")}");

file sealed class InMemoryRuleSetSource : IRuleSetSource
{
    private readonly RuleSet _ruleSet;

    public InMemoryRuleSetSource(RuleSet ruleSet)
    {
        _ruleSet = ruleSet;
    }

    public ValueTask<DbRuleSetDefinition> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var columns = new List<RuleSetColumnDefinition>
        {
            new("segment", 1, RuleFieldRole.Input, 1),
            new("age", 2, RuleFieldRole.Input, 2),
            new("formula", 3, RuleFieldRole.Output, 3),
        };

        var rows = _ruleSet.Rules.Select(rule => new RuleSetRowData(
            rule.PrimaryKey,
            new Dictionary<string, object?>
            {
                ["Col01"] = rule.Conditions[0].Expected,
                ["Col02"] = rule.Conditions[1].Expected,
                ["Col03"] = rule.Outputs[0].RawValue,
            })).ToArray();

        return ValueTask.FromResult(new DbRuleSetDefinition(key, columns, rows));
    }
}
