[← README](../README.md)

# Architektura RuleEval

## Přehled vrstev

RuleEval je vrstevnatý rule engine. Každá vrstva je samostatný projekt — vyšší vrstvy závisí na nižších, nikdy naopak. Závislosti tečou striktně jedním směrem.

Veřejné NuGet balíčky jsou označeny **tučně**. Ostatní jsou interní projekty zabalené do příslušných veřejných balíčků.

```
┌──────────────────────────────────────────────────────────────┐
│  RuleEval.Database.DependencyInjection  [veřejný NuGet]      │
│  RuleEval.DependencyInjection           [veřejný NuGet]      │  ← DI integrace (volitelné)
├──────────────────────────────────────────────────────────────┤
│  RuleEval.Database                      [veřejný NuGet]      │  ← DB adaptér (PostgreSQL, SQL Server)
│  RuleEval.Database.Abstractions         [interní projekt]    │
├──────────────────────────────────────────────────────────────┤
│  RuleEval.Diagnostics  [interní]  RuleEval.Caching [interní] │  ← interní doplňky
├──────────────────────────────────────────────────────────────┤
│  RuleEval  (projekt: RuleEval.Core)     [veřejný NuGet]      │  ← evaluační engine + matchery
├──────────────────────────────────────────────────────────────┤
│  RuleEval.Abstractions                  [veřejný NuGet]      │  ← contracts, doménový model
└──────────────────────────────────────────────────────────────┘
```

## Veřejné NuGet balíčky vs. interní projekty

| Projekt | NuGet ID | Typ |
|---|---|---|
| `RuleEval.Abstractions` | `RuleEval.Abstractions` | veřejný NuGet balíček |
| `RuleEval.Core` | `RuleEval` | veřejný NuGet balíček |
| `RuleEval.DependencyInjection` | `RuleEval.DependencyInjection` | veřejný NuGet balíček |
| `RuleEval.Database` | `RuleEval.Database` | veřejný NuGet balíček |
| `RuleEval.Database.DependencyInjection` | `RuleEval.Database.DependencyInjection` | veřejný NuGet balíček |
| `RuleEval.Caching` | — | interní projekt (není publikován) |
| `RuleEval.Diagnostics` | — | interní projekt (není publikován) |
| `RuleEval.Database.Abstractions` | — | interní projekt (není publikován) |

> `RuleEval.Core` je název interního projektu; veřejný NuGet balíček se jmenuje `RuleEval`.
> Interní projekty jsou zahrnuty do příslušných veřejných balíčků přes project reference.

## Doménový model (`RuleEval.Abstractions`)

```
RuleSet
 ├── Key                    (string)
 ├── InputFields            (ImmutableArray<string>)
 ├── Rules                  (ImmutableArray<Rule>)
 │    └── Rule
 │         ├── Index        (int)
 │         ├── Conditions   (ImmutableArray<Condition>)
 │         │    └── Condition: FieldName, Expected, MatcherKey
 │         ├── Outputs      (ImmutableArray<OutputValue>)
 │         │    └── OutputValue: FieldName, RawValue, Index
 │         └── PrimaryKey?  (PrimaryKeyValue)
 └── Metadata               (ImmutableDictionary<string, string?>)
```

Celý model je **immutable** — po sestavení přes `RuleSetBuilder` nelze měnit. To zaručuje thread-safety evaluátoru bez jakéhokoli zamykání.

## Evaluační flow

```
EvaluationContext  ──┐
EvaluationOptions  ──┤──►  RuleSetEvaluator.EvaluateFirst / EvaluateAll
RuleSet            ──┘            │
                                  ▼
                           pro každé Rule (v pořadí):
                             pro každou Condition:
                               MatcherRegistry.Match(condition, actualValue)
                                 └── IConditionMatcher.CanHandle → Match
                                       ├── DecimalIntervalConditionMatcher
                                       ├── RegexConditionMatcher
                                       └── EqualityConditionMatcher  (fallback)
                                  │
                             vše matched? ──► zaznamenat do kandidátů
                                  │
                           EvaluateFirst: vrátit prvního kandidáta
                           EvaluateAll:   vrátit všechny kandidáty
```

### Výsledkové typy

| Typ | Popis |
|---|---|
| `EvaluationResult` | Výsledek `EvaluateFirst`; obsahuje `Status`, `Match?`, `Trace?` |
| `EvaluationMatchesResult` | Výsledek `EvaluateAll`; obsahuje `Status`, `Matches`, `Trace?` |
| `EvaluationStatus.Matched` | Nalezena shoda |
| `EvaluationStatus.NoMatch` | Žádná shoda — běžný stav, ne výjimka |
| `EvaluationStatus.AmbiguousMatch` | Více shod (vyžaduje `EvaluationOptions.DetectAmbiguity: true`) |
| `EvaluationStatus.InvalidInput` | Špatný počet vstupů |

## Matchery (`RuleEval.Core`)

Matchery implementují `IConditionMatcher` a jsou registrovány v `MatcherRegistry`.

```csharp
public interface IConditionMatcher
{
    string Key { get; }
    bool CanHandle(Condition condition);
    ConditionMatchResult Match(ConditionMatchContext context);
}
```

### Vestavěné matchery

| Matcher | `MatcherKey` | Formát hodnoty |
|---|---|---|
| `DecimalIntervalConditionMatcher` | `interval` / auto | `INTERVAL<min;max>`, `INTERVAL(min;max>` apod. |
| `RegexConditionMatcher` | `regex` / auto | Regulární výraz (např. `.*Perspektiva.*`) |
| `EqualityConditionMatcher` | `eq` / auto | Přesná shoda (`string.Equals`, `IComparable`) |

Při `MatcherKey = "auto"` matchery soutěží přes `CanHandle` — první, který hlásí `true`, vyhrává (pořadí: `DecimalInterval` → `Regex` → `Equality`).

### Vlastní matcher

```csharp
public sealed class PrefixMatcher : IConditionMatcher
{
    public string Key => "prefix";

    public bool CanHandle(Condition condition)
        => condition.MatcherKey == Key;

    public ConditionMatchResult Match(ConditionMatchContext context)
        => (context.ActualValue?.ToString() ?? string.Empty)
            .StartsWith(context.Condition.Expected?.ToString() ?? string.Empty, StringComparison.Ordinal)
            ? ConditionMatchResult.Matched(Key)
            : ConditionMatchResult.NotMatched(Key);
}

var registry = MatcherRegistry.CreateDefault().WithMatcher(new PrefixMatcher());
var evaluator = new RuleSetEvaluator(registry);
```

## Cache (interní projekt `RuleEval.Caching`)

```csharp
public interface IRuleSetCache
{
    ValueTask<RuleSet?> GetAsync(RuleSetCacheKey key, CancellationToken cancellationToken = default);
    ValueTask SetAsync(RuleSetCacheKey key, RuleSet ruleSet, TimeSpan ttl, CancellationToken cancellationToken = default);
}
```

| Implementace | Chování |
|---|---|
| `NoCacheRuleSetCache` | Vždy `null` — každé volání načte z DB |
| `MemoryRuleSetCache` | In-process cache s TTL expirací |

`RuleSetCacheKey` obsahuje `Namespace` + `Key`, což umožňuje izolovat cache mezi různými repozitáři.

## Databázová vrstva (`RuleEval.Database`)

`RuleSetRepository` orchestruje celý životní cyklus načtení a vyhodnocení:

```
IRuleSetSource.LoadAsync(key)
       │
       ▼
DbRuleSetDefinition  ──►  DbRuleSetMapper.Map()  ──►  RuleSet
                                                          │
                                              IRuleSetCache.SetAsync()
                                                          │
                                              RuleSetEvaluator.EvaluateFirst()
```

### DB sources

| Třída | Provider |
|---|---|
| `PostgreSqlRuleSetSource` | PostgreSQL přes Npgsql |
| `SqlServerRuleSetSource` | SQL Server — provider-neutral přes `System.Data.Common.DbConnection` |

Core (`RuleEval`) **neví nic o databázi** — závislost teče pouze shora dolů.

## Error model

| Typ | Kdy se používá |
|---|---|
| `EvaluationResult` / `EvaluationMatchesResult` | Běžné runtime výsledky včetně `NoMatch` |
| `InvalidRuleDefinitionException` | Chyba při sestavování `RuleSet` (špatná definice pravidla) |
| `InvalidConditionFormatException` | Žádný matcher nezvládl zpracovat podmínku |
| `DatabaseLoadException` | Selhání načtení z DB |

`NoMatch` **není výjimka** — je to očekávaný business výsledek.

## Kompatibilita vůči původnímu projektu RuleEvaluator

### Zachovaná semantika

- input conditions se vyhodnocují v pořadí,
- všechny conditions v jednom pravidle jsou AND,
- output buňky se při matchování ignorují,
- regex je full-string match,
- `INTERVAL<10;15)` a podobné tvary fungují case-insensitive,
- output string jako `C2/240` zůstává raw.

### Mapování API

| RuleEvaluator | RuleEval |
|---|---|
| `RuleItems` | `RuleSet` |
| `RuleItem` | `Rule` |
| `CellInputOutputType` | `RuleFieldRole` |
| `Find()` | `EvaluateFirst()` |
| `FindAll()` | `EvaluateAll()` |
| `IRuleItemsCall` | `RuleTrace` + `IRuleEvaluationObserver` |
| Castle Windsor factory chain | `MatcherRegistry` + constructor injection |
| `RuleItemsRepository` | `RuleSetRepository` |

## Hlavní design rozhodnutí

| Rozhodnutí | Důvod |
|---|---|
| Immutable doménový model | Thread-safety bez zámků; evaluátor lze sdílet jako singleton |
| `NoMatch` jako stav, ne výjimka | `NoMatch` je v produkci očekávaný výsledek, ne chyba |
| `AmbiguousMatch` opt-in | Ordered first-match je výchozí chování; detekce ambiguity má nenulový overhead |
| Žádný Castle Windsor | Eliminuje povinnou závislost na IoC kontejneru; `MatcherRegistry` řeší discovery explicitně |
| Separátní `RuleEval.Abstractions` | Knihovny třetích stran mohou referovat pouze contracts bez runtime závislosti |
| Diagnostics opt-in | `EvaluationOptions.CaptureDiagnostics: true` — při vypnutém zachytávání je overhead nulový |
