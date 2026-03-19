# RuleEval

RuleEval je moderní open-source rule engine pro **.NET 10**, navržený jako čistá a produkčně použitelná náhrada za původní projekt RuleEvaluator. Zachovává klíčové use-casy starého projektu — ordered rule scan, `Find`/`FindAll`, regex matching, `INTERVAL...` matching, raw output hodnoty, DB loading a cache — ale bez Castle Windsor, bez povinného DI frameworku a s jasně oddělenými vrstvami.

## Hlavní rozhodnutí návrhu

- **Veřejné API evaluace:** `RuleSetEvaluator` pracuje nad immutable `RuleSet` + `EvaluationContext`.
- **Result model:** `EvaluateFirst` vrací `EvaluationResult`, `EvaluateAll` vrací `EvaluationMatchesResult`.
- **No match:** očekávaný stav, vrací se přes `EvaluationStatus.NoMatch` + `NoMatchReason`.
- **Ambiguous match:** volitelně přes `EvaluationOptions.DetectAmbiguity`; jinak zůstává kompatibilní ordered first-match chování.
- **Trace:** opt-in přes `EvaluationOptions.CaptureDiagnostics`, aby byl overhead nulový při vypnutí.
- **Matchery:** `MatcherRegistry` + `IConditionMatcher`, bez Castle Windsor a bez povinného IoC.
- **Cache:** explicitní `RuleSetCacheKey` + `IRuleSetCache`, implementace `NoCacheRuleSetCache` a `MemoryRuleSetCache`.
- **DB adapter:** `RuleEval.Database.*` mapuje DB DTO do doménového `RuleSet`; Core neví nic o DB.
- **Thread-safety:** runtime model je immutable a evaluátor je read-only.

## Solution struktura

- `src/RuleEval.Abstractions`
- `src/RuleEval.Core`
- `src/RuleEval.Diagnostics`
- `src/RuleEval.Caching`
- `src/RuleEval.Database.Abstractions`
- `src/RuleEval.Database`
- `src/RuleEval.DependencyInjection`
- `tests/RuleEval.Tests.Unit`
- `tests/RuleEval.Tests.Integration`
- `benchmarks/RuleEval.Benchmarks`
- `samples/RuleEval.Samples`

## Mapování RuleEvaluator -> RuleEval

| RuleEvaluator | RuleEval |
|---|---|
| `RuleItems` | `RuleSet` |
| `RuleItem` | `Rule` |
| `CellInputOutputType` | `RuleFieldRole` |
| `Find()` | `EvaluateFirst()` |
| `FindAll()` | `EvaluateAll()` |
| `IRuleItemsCall` | `RuleTrace` + `IRuleEvaluationObserver` |
| Castle Windsor factory chain | `MatcherRegistry` + explicit builder/constructor injection |
| `RuleItemsRepository` | `RuleSetRepository` |

## Použití bez DI

```csharp
using RuleEval.Abstractions;
using RuleEval.Core;

var ruleSet = RuleSetBuilder
    .Create("pricing")
    .AddInput("segment")
    .AddInput("age")
    .AddRule(rule => rule
        .When("segment", ".*Perspektiva.*")
        .When("age", "INTERVAL<15;24>")
        .ThenOutput("formula", "C2/240")
        .WithPrimaryKey("id", 1))
    .Build();

var evaluator = new RuleSetEvaluator();
var result = evaluator.EvaluateFirst(
    ruleSet,
    EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m),
    new EvaluationOptions(CaptureDiagnostics: true));

Console.WriteLine(result.Status);
Console.WriteLine(result.Match?.Outputs[0].RawValue); // C2/240
```

## Data-driven runtime API

```csharp
var context = EvaluationContext.FromNamed(new Dictionary<string, object?>
{
    ["segment"] = "7BN Perspektiva Důchod",
    ["age"] = 15m,
});

var result = evaluator.EvaluateFirst(ruleSet, context);
```

## Custom matcher bez Windsor

```csharp
public sealed class PrefixMatcher : IConditionMatcher
{
    public string Key => "prefix";

    public bool CanHandle(Condition condition)
        => condition.MatcherKey == Key;

    public ConditionMatchResult Match(ConditionMatchContext context)
        => (context.ActualValue?.ToString() ?? string.Empty).StartsWith(context.Condition.Expected?.ToString() ?? string.Empty, StringComparison.Ordinal)
            ? ConditionMatchResult.Matched(Key)
            : ConditionMatchResult.NotMatched(Key);
}

var registry = MatcherRegistry.CreateDefault().WithMatcher(new PrefixMatcher());
var evaluator = new RuleSetEvaluator(registry);
```

## Volitelná integrace s Microsoft.Extensions.DependencyInjection

```csharp
using Microsoft.Extensions.DependencyInjection;
using RuleEval.DependencyInjection;

var services = new ServiceCollection();
services.AddRuleEvalCore();
```

Tato integrace je **volitelná**; Core lze použít bez jakéhokoli containeru.

## NoMatch, AmbiguousMatch a trace

- `NoMatch` je běžný stav, ne výjimka.
- `AmbiguousMatch` se vrací jen pokud to explicitně zapnete přes `DetectAmbiguity`.
- `RuleTrace` obsahuje vyhodnocená pravidla, podmínky, důvody failu a elapsed time.

## Databázová vrstva a cache

`RuleSetRepository` skládá:

- `IRuleSetSource`
- `DbRuleSetMapper`
- `IRuleSetCache`
- `RuleSetEvaluator`

Pro MSSQL je připraven `SqlServerRuleSetSource`, pro PostgreSQL `PostgreSqlRuleSetSource`.

## Dokumentace

- Architektura: `docs/architecture.md`
- Samples: `samples/RuleEval.Samples`
- Benchmarks: `benchmarks/RuleEval.Benchmarks`

## Build a test

```bash
dotnet restore RuleEval.sln
dotnet build RuleEval.sln -c Release
dotnet test RuleEval.sln -c Release --collect:"XPlat Code Coverage"
```

## Licence

MIT. Viz `LICENSE`.
