using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using RuleEval.Abstractions;
using RuleEval.Core;

BenchmarkRunner.Run<EvaluatorBenchmarks>();

[MemoryDiagnoser]
public class EvaluatorBenchmarks
{
    private readonly RuleSet _ruleSet;
    private readonly RuleSetEvaluator _evaluator = new();
    private readonly EvaluationContext _context = EvaluationContext.FromPositional("A", 15m);

    public EvaluatorBenchmarks()
    {
        var builder = RuleSetBuilder.Create("bench").AddInput("code").AddInput("age");
        for (var index = 0; index < 100; index++)
        {
            var localIndex = index;
            builder.AddRule(rule => rule
                .Named($"rule-{localIndex}")
                .When("code", localIndex == 99 ? "A" : $"X{localIndex}")
                .When("age", localIndex == 99 ? "INTERVAL<10;20>" : "INTERVAL<20;30>")
                .ThenOutput("result", localIndex));
        }

        _ruleSet = builder.Build();
    }

    [Benchmark]
    public EvaluationResult EvaluateFirst()
        => _evaluator.EvaluateFirst(_ruleSet, _context);
}
