# RuleEval

[![RuleEval.Abstractions](https://img.shields.io/nuget/v/RuleEval.Abstractions.svg?label=RuleEval.Abstractions)](https://www.nuget.org/packages/RuleEval.Abstractions)
[![RuleEval](https://img.shields.io/nuget/v/RuleEval.svg?label=RuleEval)](https://www.nuget.org/packages/RuleEval)
[![RuleEval.DependencyInjection](https://img.shields.io/nuget/v/RuleEval.DependencyInjection.svg?label=RuleEval.DependencyInjection)](https://www.nuget.org/packages/RuleEval.DependencyInjection)
[![RuleEval.Database](https://img.shields.io/nuget/v/RuleEval.Database.svg?label=RuleEval.Database)](https://www.nuget.org/packages/RuleEval.Database)
[![RuleEval.Database.DependencyInjection](https://img.shields.io/nuget/v/RuleEval.Database.DependencyInjection.svg?label=RuleEval.Database.DependencyInjection)](https://www.nuget.org/packages/RuleEval.Database.DependencyInjection)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

📚 Dokumentace: [architecture.md](docs/architecture.md) · [nuget-packages.md](docs/nuget-packages.md) · [publishing-nuget.md](docs/publishing-nuget.md)

Moderní open-source rule engine pro **.NET 8**

## Obsah

- [Rychlý start](#rychlý-start)
- [EvaluationContext — pozičně nebo dle názvů](#evaluationcontext--pozičně-nebo-dle-názvů)
- [Vyhodnocení z databáze](#vyhodnocení-z-databáze)
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

## EvaluationContext — pozičně nebo dle názvů

Vstupní hodnoty lze předat dvěma způsoby. Pořadí pozičních hodnot odpovídá sloupci `Order` definice pravidel.

```csharp
// Pozičně — hodnoty ve stejném pořadí jako vstupní pole v RuleSet
var result = evaluator.EvaluateFirst(
    ruleSet,
    EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));

// Dle názvů — nezávislé na pořadí
var result = evaluator.EvaluateFirst(
    ruleSet,
    EvaluationContext.FromNamed(new Dictionary<string, object?>
    {
        ["segment"] = "7BN Perspektiva Důchod",
        ["age"]     = 15m,
    }));

Console.WriteLine(result.Status);                        // Matched
Console.WriteLine(result.Match?.Outputs[0].RawValue);    // C2/240
```

## Vyhodnocení z databáze

```bash
dotnet add package RuleEval.Database
dotnet add package RuleEval.Database.DependencyInjection
```

DB metadata stored procedure vrací sloupce (`Code`, `ColNr`, `Order`, `Type`) a datová stored procedure vrací první technický identifikátor řádku (např. `TranslatorDataId`) a za ním `Col01`, `Col02`, …

| Metadata sloupec | Popis |
|---|---|
| `Code` | Logický/business název pole (vstup nebo výstup) |
| `ColNr` | Fyzické číslo sloupce dat (`Col01`..`Col20`) |
| `Order` | Pořadí pro poziční evaluaci (`EvaluationContext.FromPositional`) |
| `Type` | `0` = Input, `1` = Output |

**První datový sloupec** (např. `TranslatorDataId`) je diagnostický identifikátor – automaticky se stane `MatchResult.PrimaryKey`. Neovlivňuje výsledek evaluace.

### Inicializace v `Program.cs`

```csharp
using Microsoft.Data.SqlClient;
using RuleEval.Database;
using RuleEval.Database.DependencyInjection;

builder.Services.AddRuleEvalDatabase(
    sp => new SqlServerRuleSetSource(
        new AdoCommandExecutor(cs => new SqlConnection(cs)),
        builder.Configuration.GetConnectionString("RuleEval")!,
        "[dbo].[p_GetSchemaColBySchemaCode]",
        "[dbo].[p_GetDataBySchemaCode]"),
    opt => opt.DefaultCacheTtl = TimeSpan.FromMinutes(30));
```

Výsledkem je, že `IRuleSetRepository` je singleton s 30minutovou cache. Každé volání `LoadAsync` / `EvaluateFirstAsync` / `GetFirstOutputAsync` automaticky čte z cache; do DB se jde jen při prvním načtení nebo po invalidaci.

### SQL kontrakt – metadata stored procedure

```sql
-- Vrací: Code, ColNr, Order, Type
-- Type: 0 = Input, 1 = Output
SELECT Code, ColNr, [Order], [Type]
FROM   SchemaCol
WHERE  SchemaCode = @Code
ORDER  BY [Order];
```

### SQL kontrakt – datová stored procedure

```sql
-- První sloupec = diagnostický identifikátor (stane se PrimaryKey)
-- Následují Col01..Col20
SELECT TranslatorDataId,
       Col01, Col02, Col03, ...
FROM   Data
WHERE  SchemaCode = @Code;
```

### Použití v business kódu

Do třídy stačí injektovat jen `IRuleSetRepository` — o připojení k DB, cache i TTL se stará DI konfigurace z `Program.cs`.

```csharp
public class PricingService(IRuleSetRepository rules)
{
    // Vrátí hodnotu prvního výstupního sloupce — název sloupce nás nezajímá
    public Task<string?> GetFormulaAsync(string segment, decimal age) =>
        rules.GetFirstOutputAsync("Pricing",
            EvaluationContext.FromPositional(segment, age));

    // Vrátí hodnotu konkrétního výstupního sloupce dle názvu
    public Task<string?> GetFormulaByNameAsync(string segment, decimal age) =>
        rules.GetFirstOutputAsync("Pricing",
            EvaluationContext.FromPositional(segment, age),
            outputName: "Formula");
}
```

### Invalidace cache

```csharp
// Vyhodí konkrétní pravidlo z cache — příští volání načte z DB a uloží znovu
await repository.InvalidateCacheAsync("Pricing");
```

### Přehled metod `IRuleSetRepository`

```csharp
// Načte RuleSet (z cache nebo DB)
RuleSet ruleSet = await repository.LoadAsync(key);

// Výsledek evaluace
EvaluationResult result = await repository.EvaluateFirstAsync(key, context);

// Hodí výjimku při NoMatch / AmbiguousMatch / InvalidInput
EvaluationResult result = await repository.EvaluateFirstOrThrowAsync(key, context);

// Hodnota prvního výstupního sloupce (název nás nezajímá), nebo null
string? value = await repository.GetFirstOutputAsync(key, context);

// Totéž, hodí výjimku pokud pravidlo neodpovídá nebo nemá výstupy
string value = await repository.GetFirstOutputOrThrowAsync(key, context);

// Hodnota konkrétního výstupního sloupce dle názvu, nebo null
string? value = await repository.GetFirstOutputAsync(key, context, outputName: "Formula");

// Totéž, hodí výjimku pokud výstup chybí
string value = await repository.GetFirstOutputOrThrowAsync(key, context, outputName: "Formula");

// Invalidace cache pro konkrétní pravidlo
await repository.InvalidateCacheAsync(key);
```

## NuGet balíčky

| Projekt | NuGet | Popis |
|---|---|---|
| [`RuleEval.Abstractions`](src/RuleEval.Abstractions) | [📦](https://www.nuget.org/packages/RuleEval.Abstractions) | Contracts a immutable doménový model; referujte, pokud píšete knihovny integrující se s RuleEval |
| [`RuleEval`](src/RuleEval.Core) | [📦](https://www.nuget.org/packages/RuleEval) | Evaluační engine, built-in matchery (`regex`, `INTERVAL`, equality) |
| [`RuleEval.Caching`](src/RuleEval.Caching) | — | Interní projekt bez vlastního NuGet; zkompilován do `RuleEval.DependencyInjection` a `RuleEval.Database`. Obsahuje `IRuleSetCache`, `MemoryRuleSetCache`, `NoCacheRuleSetCache` |
| [`RuleEval.Diagnostics`](src/RuleEval.Diagnostics) | — | Interní projekt bez vlastního NuGet. Obsahuje `IRuleEvaluationObserver`, observer pattern pro výsledky evaluace |
| [`RuleEval.DependencyInjection`](src/RuleEval.DependencyInjection) | [📦](https://www.nuget.org/packages/RuleEval.DependencyInjection) | `AddRuleEval()` registrace core služeb do `IServiceCollection` |
| [`RuleEval.Database.Abstractions`](src/RuleEval.Database.Abstractions) | — | Interní projekt bez vlastního NuGet; zkompilován do `RuleEval.Database`. Obsahuje `IRuleSetSource`, `IRuleSetRepository` — provider-neutral DB contracts |
| [`RuleEval.Database`](src/RuleEval.Database) | [📦](https://www.nuget.org/packages/RuleEval.Database) | `DbRuleSetMapper`, `RuleSetRepository`, `PostgreSqlRuleSetSource`, `SqlServerRuleSetSource` |
| [`RuleEval.Database.DependencyInjection`](src/RuleEval.Database.DependencyInjection) | [📦](https://www.nuget.org/packages/RuleEval.Database.DependencyInjection) | `AddRuleEvalDatabase()` registrace DB služeb do `IServiceCollection` |

## Dokumentace

| Dokument | Obsah |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Architektura, design rozhodnutí, závislosti mezi vrstvami, evaluační flow |
| [docs/observability.md](docs/observability.md) | Technická observability: `ILogger`, `ActivitySource` (OpenTelemetry), `Meter` (metriky) |
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
