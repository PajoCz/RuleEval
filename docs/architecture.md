# RuleEval Architecture

## 1. Cíle

RuleEval odděluje doménu, evaluaci, diagnostiku, cache a databázovou infrastrukturu tak, aby byl engine:

- testovatelný,
- thread-safe,
- rozšiřitelný,
- bez povinného DI containeru,
- připravený pro NuGet publikaci.

## 2. Vrstvy

### RuleEval.Abstractions
- immutable doménový model,
- veřejné result typy,
- doménové výjimky.

### RuleEval.Core
- builder API,
- `RuleSetEvaluator`,
- matcher pipeline,
- built-in regex/equality/decimal interval matchery,
- parser `INTERVAL...` syntaxe.

### RuleEval.Diagnostics
- volitelné observer hooky nad result modely.

### RuleEval.Caching
- explicitní cache key model,
- `NoCacheRuleSetCache`,
- `MemoryRuleSetCache`.

### RuleEval.Database.Abstractions
- kontrakty pro source/repository,
- DTO reprezentace DB metadata + datových řádků.

### RuleEval.Database
- mapování DTO -> `RuleSet`,
- MSSQL / PostgreSQL source adaptery,
- repository skládající source + cache + evaluator.

### RuleEval.DependencyInjection
- pouze volitelný adapter pro `Microsoft.Extensions.DependencyInjection`.

## 3. Kompatibilita vůči RuleEvaluatoru

Zachovaná semantika:

- input conditions se validují v pořadí,
- v jednom pravidle jsou AND,
- output buňky se při matchování ignorují,
- regex je full-string match,
- `INTERVAL<10;15)` a podobné tvary fungují case-insensitive,
- output string jako `C2/240` zůstává raw.

Změny k lepšímu:

- žádný Castle Windsor,
- žádné `params object[]` jako jediné veřejné API,
- očekávané stavy jsou result modely, ne obecné výjimky,
- cache není svázaná s repository implementací,
- mapování DB je explicitní podle metadata definic, ne podle `Dictionary.Values` pořadí.

## 4. Error model

- **Expected runtime states:** `EvaluationResult` / `EvaluationMatchesResult`.
- **Exceptional states:** `InvalidRuleDefinitionException`, `InvalidConditionFormatException`, `DatabaseLoadException`.

Tím se odděluje běžný business výsledek (`NoMatch`) od skutečné chyby návrhu nebo infrastruktury.

## 5. Výkon a budoucí rozvoj

Aktuální evaluace používá ordered lineární scan, ale návrh nechává prostor pro:

- compiled match plans,
- indexing,
- preprocessing rules,
- pokročilejší cache strategiie,
- observability integrations.
