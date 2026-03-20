# RuleEval

Moderní open-source rule engine pro **.NET 8**, navržený jako čistá a produkčně použitelná náhrada za původní projekt RuleEvaluator. Bez Castle Windsor, bez povinného DI frameworku, s jasně oddělenými vrstvami.

## Obsah

- [Rychlý start](#rychlý-start)
- [NuGet balíčky](#nuget-balíčky)
- [Dokumentace](#dokumentace)
- [Build a test](#build-a-test)
- [Licence](#licence)

## Rychlý start

```bash
dotnet add package RuleEval
```

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

### Volitelná integrace s Microsoft.Extensions.DependencyInjection

```csharp
using Microsoft.Extensions.DependencyInjection;
using RuleEval.DependencyInjection;

services.AddRuleEval();
```

## NuGet balíčky

| Balíček | Popis |
|---|---|
| [`RuleEval.Abstractions`](src/RuleEval.Abstractions) | Contracts a immutable doménový model; referujte, pokud píšete knihovny integrující se s RuleEval |
| [`RuleEval`](src/RuleEval.Core) | Evaluační engine, built-in matchery (`regex`, `INTERVAL`, equality) |
| [`RuleEval.Caching`](src/RuleEval.Caching) | `IRuleSetCache`, `MemoryRuleSetCache`, `NoCacheRuleSetCache` |
| [`RuleEval.Diagnostics`](src/RuleEval.Diagnostics) | `IRuleEvaluationObserver`, observer pattern pro výsledky evaluace |
| [`RuleEval.DependencyInjection`](src/RuleEval.DependencyInjection) | `AddRuleEval()` registrace core služeb do `IServiceCollection` |
| [`RuleEval.Database.Abstractions`](src/RuleEval.Database.Abstractions) | `IRuleSetSource`, `IRuleSetRepository` — provider-neutral DB contracts |
| [`RuleEval.Database`](src/RuleEval.Database) | `DbRuleSetMapper`, `RuleSetRepository`, `PostgreSqlRuleSetSource`, `SqlServerRuleSetSource` |
| [`RuleEval.Database.DependencyInjection`](src/RuleEval.Database.DependencyInjection) | `AddRuleEvalDatabase()` registrace DB služeb do `IServiceCollection` |

## Dokumentace

| Dokument | Obsah |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Architektura, design rozhodnutí, závislosti mezi vrstvami, evaluační flow |
| [docs/nuget-packages.md](docs/nuget-packages.md) | Přehled balíčků, typické instalační scénáře, dependency graph |
| [docs/publishing-nuget.md](docs/publishing-nuget.md) | Jak verzovat, zabalit a publikovat NuGet balíčky |
| [samples/RuleEval.Samples](samples/RuleEval.Samples/Program.cs) | Kompletní ukázky použití včetně DI, cache a DB vrstvy |
| [benchmarks/RuleEval.Benchmarks](benchmarks/RuleEval.Benchmarks/Program.cs) | BenchmarkDotNet výkonnostní testy |

## Build a test

```bash
dotnet restore RuleEval.sln
dotnet build RuleEval.sln -c Release
dotnet test RuleEval.sln -c Release --collect:"XPlat Code Coverage"
```

## Licence

MIT. Viz `LICENSE`.
