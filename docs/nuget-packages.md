[← README](../README.md)

# RuleEval NuGet Packages

This document describes all public NuGet packages produced by the RuleEval project,
their purpose, and the recommended ways to use them in an application.

## Public packages

| Package | Description |
|---|---|
| `RuleEval` | **Main runtime package.** Evaluation engine + built-in matchers. Start here. Internal project: `RuleEval.Core`. |
| `RuleEval.Abstractions` | Public contracts and immutable domain model. For library authors. |
| `RuleEval.DependencyInjection` | `AddRuleEval()` extension for `IServiceCollection`. Core only, no DB pull-in. |
| `RuleEval.Database` | `DbRuleSetMapper`, `RuleSetRepository`, `PostgreSqlRuleSetSource`, `SqlServerRuleSetSource`. |
| `RuleEval.Database.DependencyInjection` | `AddRuleEvalDatabase<TSource>()` extension. Registers mapper + repository + your source. |

> **Note:** `RuleEval.Core` is the internal project name for the main evaluation engine.
> The public NuGet package ID is `RuleEval` (not `RuleEval.Core`).
> Caching (`IRuleSetCache`, `MemoryRuleSetCache`) and diagnostics observer types (`IRuleEvaluationObserver`)
> are compiled directly into the `RuleEval` package (`src/RuleEval.Core`).
> Database contracts (`IRuleSetSource`, `IRuleSetRepository`, `DbRuleSetDefinition`) are compiled directly
> into the `RuleEval.Database` package (`src/RuleEval.Database`).

## Typical installation scenarios

### Scenario 1 – Standalone use (no DI, no database)

Install only the main package:

```xml
<PackageReference Include="RuleEval" Version="0.1.0" />
```

### Scenario 2 – With Microsoft.Extensions.DependencyInjection (no database)

```xml
<PackageReference Include="RuleEval" Version="0.1.0" />
<PackageReference Include="RuleEval.DependencyInjection" Version="0.1.0" />
```

Register in `Program.cs` / `Startup.cs`:

```csharp
services.AddRuleEval();
```

This registers `MatcherRegistry`, `RuleSetEvaluator`, and a no-op `IRuleSetCache`.

### Scenario 3 – With database-backed rule loading (PostgreSQL example)

```xml
<PackageReference Include="RuleEval" Version="0.1.0" />
<PackageReference Include="RuleEval.Database" Version="0.1.0" />
<PackageReference Include="RuleEval.Database.DependencyInjection" Version="0.1.0" />
```

```csharp
services.AddRuleEvalDatabase<PostgreSqlRuleSetSource>();
// Also register your NpgsqlDataSource / connection string separately
```

### Scenario 4 – Library that integrates with RuleEval (no runtime dependency)

```xml
<PackageReference Include="RuleEval.Abstractions" Version="0.1.0" />
```

Use only `RuleEval.Abstractions` to avoid pulling in the full evaluation engine.

## Dependency graph (public packages)

```
RuleEval.Abstractions                   (no dependencies)
RuleEval                                → RuleEval.Abstractions
                                          (caching + diagnostics observer types compiled in from src/RuleEval.Core)
RuleEval.DependencyInjection            → RuleEval + Microsoft.Extensions.DependencyInjection.Abstractions
RuleEval.Database                       → RuleEval + Npgsql
                                          (DB contracts compiled in from src/RuleEval.Database)
RuleEval.Database.DependencyInjection   → RuleEval.DependencyInjection + RuleEval.Database
```

## Publishing a new version

1. Push a Git tag following the `v{MAJOR}.{MINOR}.{PATCH}` convention:

   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

2. The [publish workflow](../.github/workflows/publish.yml) triggers automatically,
   builds, tests, packs all public packages and pushes them to NuGet.org using the
   `NUGET_API_KEY` repository secret.

3. To publish manually, trigger the `publish` workflow via GitHub Actions
   **workflow_dispatch** and supply the version number.

### Setting up the NUGET_API_KEY secret

1. Create an API key at <https://www.nuget.org/account/apikeys>.
2. In the GitHub repository go to **Settings → Secrets and variables → Actions**.
3. Add a new repository secret named `NUGET_API_KEY` with the key value.

## Database providers

`RuleEval.Database` bundles both the provider-neutral mapper/repository and two
concrete providers:

| Class | Provider |
|---|---|
| `PostgreSqlRuleSetSource` | PostgreSQL via **Npgsql** |
| `SqlServerRuleSetSource` | SQL Server via `System.Data.Common.DbConnection` (no extra NuGet dependency) |

Additional providers (SQLite, Oracle, …) can be added by implementing
`IRuleSetSource` from the `RuleEval.Database` package.
