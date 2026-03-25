# Built-in Technical Observability in RuleEval

RuleEval ships with lightweight, zero-configuration built-in technical observability.
The library emits signals through standard .NET APIs; the **hosting application** decides
whether (and how) to collect and export them.  No OpenTelemetry SDK, exporter, or logging
provider is configured inside the library itself.

---

## What the library provides

| Signal         | API                                                         | Name / Category               |
|----------------|-------------------------------------------------------------|-------------------------------|
| Structured logs | `Microsoft.Extensions.Logging.ILogger<T>`                  | See [Log categories](#log-categories) |
| Distributed tracing spans | `System.Diagnostics.ActivitySource`          | `"RuleEval"`                  |
| Metrics        | `System.Diagnostics.Metrics.Meter`                          | `"RuleEval"`                  |

All three signals are **safe when no consumer is attached**.  If the hosting application
has not configured a logging provider, tracing listener, or meter listener, the library
behaves exactly as before — no exceptions, no measurable overhead.

The static class `RuleEval.Core.RuleEvalTelemetry` is the single entry point for the
Activity source and metric instruments so that hosting applications can reference
`RuleEvalTelemetry.ServiceName` instead of hard-coding the string `"RuleEval"`.

---

## Logged events

| Class                  | Log level   | Message (abbreviated)                                        |
|------------------------|-------------|--------------------------------------------------------------|
| `RuleSetEvaluator`     | Debug       | Starting evaluation of rule set `'{RuleSetKey}'`             |
| `RuleSetEvaluator`     | Debug       | Evaluation → Matched (rule index, primary key, elapsed)      |
| `RuleSetEvaluator`     | Debug       | Evaluation → NoMatch (reason, elapsed)                       |
| `RuleSetEvaluator`     | Warning     | Evaluation → AmbiguousMatch (match count, elapsed)           |
| `RuleSetEvaluator`     | Warning     | Evaluation → InvalidInput (reason, error, elapsed)           |
| `RuleSetEvaluator`     | Error       | Evaluation failed unexpectedly (exception)                   |
| `RuleSetEvaluator`     | Debug       | Starting EvaluateAll for rule set `'{RuleSetKey}'`           |
| `RuleSetEvaluator`     | Debug       | EvaluateAll completed (match count, elapsed)                 |
| `RuleSetRepository`    | Debug       | Loading rule set `'{RuleSetKey}'`                            |
| `RuleSetRepository`    | Debug       | Rule set served from cache                                   |
| `RuleSetRepository`    | Debug       | Loading rule set from database                               |
| `RuleSetRepository`    | Information | Rule set loaded from database (elapsed, rule count)          |
| `RuleSetRepository`    | Debug       | Rule set stored in cache (TTL)                               |
| `RuleSetRepository`    | Error       | Failed to load rule set from database (exception)            |
| `RuleSetRepository`    | Debug       | Invalidating / Invalidated cache for rule set                |
| `MemoryRuleSetCache`   | Debug       | Cache hit / miss for `'{CacheKey}'`                          |
| `MemoryRuleSetCache`   | Debug       | Cache set / entry removed for `'{CacheKey}'`                 |

### Log categories

Each class uses its own generic `ILogger<T>` so log levels can be configured per category
in the hosting application's `appsettings.json`:

| Category                                | Typical use                                   |
|-----------------------------------------|-----------------------------------------------|
| `RuleEval.Core.RuleSetEvaluator`        | Evaluation flow, match results                |
| `RuleEval.Caching.MemoryRuleSetCache`   | Low-level cache hit/miss detail               |
| `RuleEval.Database.RuleSetRepository`   | Load flow, DB timing, cache hit/miss          |

---

## Activity spans

All spans belong to the `"RuleEval"` activity source.

| Operation name              | Emitted by             | Description                                     |
|-----------------------------|------------------------|-------------------------------------------------|
| `ruleeval.evaluate`         | `RuleSetEvaluator`     | Single evaluation call (`EvaluateFirst` / `EvaluateAll`) |
| `ruleeval.ruleset.load`     | `RuleSetRepository`    | Full load operation (cache lookup + optional DB load + optional cache store) |
| `ruleeval.cache.get`        | `RuleSetRepository`    | Cache lookup within `LoadAsync`                 |
| `ruleeval.db.load`          | `RuleSetRepository`    | Database source call within `LoadAsync`         |
| `ruleeval.cache.set`        | `RuleSetRepository`    | Storing a rule set in the cache                 |
| `ruleeval.cache.invalidate` | `RuleSetRepository`    | Cache invalidation call                         |

### Activity tags

| Tag                 | Type    | Description                                                        |
|---------------------|---------|--------------------------------------------------------------------|
| `rule.key`          | string  | The rule set key (e.g. `"pricing"`)                               |
| `rule.status`       | string  | `Matched` / `NoMatch` / `AmbiguousMatch` / `InvalidInput`         |
| `rule.cache.hit`    | bool    | `true` = served from cache, `false` = loaded from DB              |
| `rule.match.count`  | int     | Number of matched rules (set on `ruleeval.evaluate` for `EvaluateAll`) |
| `rule.primary_key`  | string  | Primary key value of the matched rule (when available)            |
| `error.type`        | string  | Exception type name on failure                                    |

---

## Metrics

All metrics belong to the `"RuleEval"` meter.

| Metric name                    | Type      | Unit | Description                                             |
|--------------------------------|-----------|------|---------------------------------------------------------|
| `ruleeval.cache.hit`           | Counter   | —    | Number of rule set cache hits in `LoadAsync`            |
| `ruleeval.cache.miss`          | Counter   | —    | Number of rule set cache misses in `LoadAsync`          |
| `ruleeval.db.load.duration`    | Histogram | ms   | Duration of rule set load from the database             |
| `ruleeval.evaluate.duration`   | Histogram | ms   | Duration of a rule set evaluation                       |
| `ruleeval.evaluate.total`      | Counter   | —    | Total number of evaluation calls                        |

---

## What the library does NOT do

- It does **not** configure any OpenTelemetry SDK, exporter, or logging provider.
- It does **not** require any additional NuGet package in the hosting application for
  basic operation.
- It does **not** force any specific sampling strategy or export destination.

The hosting application is fully in control of:

- Which log levels to enable per category.
- Whether tracing is enabled and with which sampler.
- Whether metrics collection is enabled.
- Which exporters to use (Console, OTLP, Prometheus, Azure Monitor, etc.).
- Whether to add additional instrumentation such as SQL client, HTTP client, etc.

---

## Enabling observability in the hosting application

### 1. `appsettings.json` — log level configuration

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft": "Warning",
      "RuleEval": "Information",
      "RuleEval.Core.RuleSetEvaluator": "Debug",
      "RuleEval.Caching.MemoryRuleSetCache": "Warning",
      "RuleEval.Database.RuleSetRepository": "Information"
    }
  }
}
```

This configuration:
- Logs significant operational events (`Information`) for the repository (DB load timing,
  etc.).
- Logs detailed flow at `Debug` for the evaluator (useful during development / debugging).
- Suppresses low-level cache chatter unless needed.

### 2. `Program.cs` — OpenTelemetry integration

```csharp
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using RuleEval.Core;                          // RuleEvalTelemetry.ServiceName
using RuleEval.Database.DependencyInjection;  // AddRuleEvalDatabase

var builder = WebApplication.CreateBuilder(args);

// ── RuleEval services ──────────────────────────────────────────────────────
builder.Services.AddRuleEvalDatabase<MyRuleSetSource>(options =>
{
    options.DefaultCacheTtl = TimeSpan.FromMinutes(10);
});

// ── OpenTelemetry ──────────────────────────────────────────────────────────
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("MyApp"))
    .WithTracing(tracing => tracing
        // Subscribe to RuleEval spans
        .AddSource(RuleEvalTelemetry.ServiceName)
        // Optionally add SQL client instrumentation to also capture the actual SQL queries
        // .AddSqlClientInstrumentation()
        .AddOtlpExporter())   // or .AddConsoleExporter() for local development
    .WithMetrics(metrics => metrics
        // Subscribe to RuleEval metrics
        .AddMeter(RuleEvalTelemetry.ServiceName)
        .AddOtlpExporter());  // or .AddPrometheusExporter()

var app = builder.Build();
// ...
app.Run();
```

> **Note:** The library works correctly without any `AddOpenTelemetry()` call.
> If tracing / metrics are not configured, the library simply has no listeners
> and all `ActivitySource.StartActivity(...)` calls return `null` (zero overhead).

---

## Difference between built-in technical observability and a business audit layer

| Aspect               | Built-in technical observability (this feature)   | Business audit layer (future / hosting-app responsibility) |
|----------------------|---------------------------------------------------|------------------------------------------------------------|
| **Purpose**          | Diagnose performance, errors, and data-quality issues in the rule engine | Record business decisions for compliance, audit trail, or reporting |
| **Where**            | Inside the library (RuleEval)                     | In the hosting application or a dedicated service          |
| **Storage**          | Trace backends (Jaeger, Zipkin, Azure Monitor…) and metric backends (Prometheus, etc.) | Business database, audit log, data warehouse               |
| **Contents**         | Timing, status codes, rule key, error type        | Full input/output values, user context, business event identity |
| **Who configures it**| Hosting app (log level, exporter, sampler)        | Hosting app / business domain team                         |
| **Sensitive data**   | No — only structural identifiers and status codes | May contain PII / business-sensitive data (must be handled accordingly) |

The existing `IRuleEvaluationObserver` / `DelegateRuleEvaluationObserver` mechanism in
`RuleEval.Diagnostics` is designed as the extension point for building such a business
audit layer: register a custom observer that writes to your persistent store, decoupled
from the library's internal technical observability.
