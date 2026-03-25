# Business/Audit Observability in RuleEval

This document describes the **business/audit observability** layer of RuleEval — a public
extension point designed for the *hosting application* to capture and store information
about each rule evaluation for business, audit, or reporting purposes.

---

## What is this layer?

The business audit layer lets the hosting application plug in one or more custom
**audit sinks** that receive a structured event payload after every rule evaluation.
The hosting application decides what to do with the event: write it to a database,
publish it to a message bus, emit a business log entry, or display it in an audit UI.

**RuleEval does not store any audit data itself.**  It only calls the registered sinks.

---

## How is it different from the built-in technical observability?

| Aspect               | Technical observability (`docs/observability.md`) | Business audit (this document) |
|----------------------|---------------------------------------------------|-------------------------------|
| **Purpose**          | Diagnose performance, errors, latency             | Record business decisions for compliance, audit trail, reporting |
| **Signals**          | OpenTelemetry spans, metrics, structured logs     | Strongly-typed `RuleEvaluationAuditEvent` payload |
| **Target audience**  | SRE, DevOps, infrastructure team                 | Business/domain developer, audit team |
| **Storage**          | Jaeger, Zipkin, Prometheus, Azure Monitor…       | Application DB, audit log, data warehouse |
| **Sensitive data**   | No — structural identifiers and status codes only | May contain full inputs/outputs and user identity |
| **Who configures**   | Hosting app (exporter, sampler, log level)        | Hosting app (sink implementation) |

---

## What existed in RuleEval before this change

### `IRuleEvaluationObserver` / `DelegateRuleEvaluationObserver` (`RuleEval.Diagnostics`)

The library contained a lightweight observer interface in `Observers.cs`:

```csharp
public interface IRuleEvaluationObserver
{
    ValueTask OnEvaluatedAsync(string ruleSetKey, EvaluationResult result, CancellationToken cancellationToken = default);
    ValueTask OnEvaluatedAllAsync(string ruleSetKey, EvaluationMatchesResult result, CancellationToken cancellationToken = default);
}
```

This interface was **never wired up** inside the library — it was prepared infrastructure
but no hook existed to actually call it.  It also lacked:
- The original evaluation inputs (`EvaluationContext`)
- Timing/duration information
- Business context from the calling application
- Error/exception information

### What was reused and what was added

| What                            | Decision                                                                                   |
|---------------------------------|-------------------------------------------------------------------------------------------|
| `IRuleEvaluationObserver`       | **Kept as-is** for backward compatibility.  The new audit layer supersedes it for business audit purposes. |
| `RuleEvalTelemetry` / OpenTelemetry | **Kept separate** — no business audit concepts mixed into the OTel layer. |
| `IRuleSetRepository`            | **Used as the hook point** via the Decorator pattern (`AuditingRuleSetRepository`).        |
| New: `IRuleEvaluationAuditSink` | **Added** — the stable consumer-facing interface.  Carries a richer, immutable event payload. |
| New: `RuleEvaluationAuditEvent` | **Added** — the event payload with all fields needed for audit/business observability.     |
| New: `IRuleSearchContextAccessor` / `RuleSearchContext` | **Added** — `AsyncLocal`-based mechanism for the hosting app to attach business context. |
| New: `AuditingRuleSetRepository` | **Added** — the decorator that fires audit events around every evaluation.               |
| New: `AddRuleEvalAuditing()`    | **Added** — DI extension to activate the audit layer with a single call.                  |

---

## Public contracts

### `IRuleEvaluationAuditSink`

```csharp
namespace RuleEval.Auditing;

public interface IRuleEvaluationAuditSink
{
    ValueTask OnEvaluatedAsync(
        RuleEvaluationAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}
```

Implement this interface and register it in DI to receive audit events.

### `RuleEvaluationAuditEvent`

Immutable record delivered to every registered sink after each evaluation:

```csharp
public sealed record RuleEvaluationAuditEvent
{
    public required string RuleSetKey { get; init; }
    public required EvaluationContext Inputs { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public EvaluationStatus? Status { get; init; }        // null only on unhandled error
    public string? MatchedRuleName { get; init; }
    public int? MatchedRuleIndex { get; init; }
    public PrimaryKeyValue? PrimaryKey { get; init; }
    public IReadOnlyList<OutputValue> Outputs { get; init; }
    public string? ErrorType { get; init; }               // set on unhandled exception
    public string? ErrorMessage { get; init; }
    public bool IsError { get; }                          // true when ErrorType != null
    public RuleSearchContext? BusinessContext { get; init; }
}
```

### `IRuleSearchContextAccessor` / `RuleSearchContext`

```csharp
public interface IRuleSearchContextAccessor
{
    RuleSearchContext? Current { get; set; }
}

public sealed record RuleSearchContext
{
    public string? OperationName { get; init; }
    public string? CorrelationId { get; init; }
    public string? UserId { get; init; }
    public string? SourceSystem { get; init; }
    public string? CustomerId { get; init; }
    public string? RequestId { get; init; }
    public IReadOnlyDictionary<string, string?> Tags { get; init; }
}
```

`AsyncLocalRuleSearchContextAccessor` is the built-in implementation.
It uses `AsyncLocal<T>` so the context flows automatically through async continuations.

---

## How to register in DI

```csharp
// Program.cs / Startup.cs

builder.Services
    .AddRuleEvalDatabase<MySqlRuleSetSource>(options =>
    {
        options.DefaultCacheTtl = TimeSpan.FromMinutes(10);
    })
    // Activate the business audit layer AFTER AddRuleEvalDatabase:
    .AddRuleEvalAuditing()
    // Register one or more custom sinks:
    .AddSingleton<IRuleEvaluationAuditSink, MyRuleAuditSink>();
```

`AddRuleEvalAuditing()` does **not** require any sink to be registered.
If no sink is registered, the auditing decorator is still active but does nothing — there is
no measurable overhead beyond the cost of iterating an empty list.

### What `AddRuleEvalAuditing()` registers

| Service                             | Implementation                          | Lifetime  |
|-------------------------------------|-----------------------------------------|-----------|
| `IRuleSearchContextAccessor`        | `AsyncLocalRuleSearchContextAccessor`   | Singleton |
| `IRuleSetRepository` (replaced)     | `AuditingRuleSetRepository` (decorator) | Singleton |

---

## How to set business context before a call

Set the context on `IRuleSearchContextAccessor` in the hosting application's service method.
The value is stored in `AsyncLocal<T>` and flows through all `await` continuations within the
same logical call context:

```csharp
public class PricingService
{
    private readonly IRuleSetRepository _repository;
    private readonly IRuleSearchContextAccessor _contextAccessor;

    public PricingService(IRuleSetRepository repository, IRuleSearchContextAccessor contextAccessor)
    {
        _repository = repository;
        _contextAccessor = contextAccessor;
    }

    public async Task<string?> GetFormulaAsync(
        string segment, int age,
        string correlationId, string userId, string customerId,
        CancellationToken ct = default)
    {
        // Set business context — flows to audit sink automatically
        _contextAccessor.Current = new RuleSearchContext
        {
            OperationName = "PricingService.GetFormulaAsync",
            CorrelationId = correlationId,
            UserId        = userId,
            CustomerId    = customerId,
            Tags          = new Dictionary<string, string?> { ["product"] = "pricing-v2" },
        };

        return await _repository.GetFirstOutputAsync(
            "pricing",
            EvaluationContext.FromPositional(segment, age.ToString()),
            "formula",
            cancellationToken: ct);
    }
}
```

---

## Example: full hosting application

### A) Sink implementation

```csharp
using RuleEval.Auditing;

/// <summary>
/// Saves every RuleEval audit event to the application's own audit store.
/// </summary>
public sealed class RuleEvaluationAuditSink : IRuleEvaluationAuditSink
{
    private readonly IRuleAuditStore _store;

    public RuleEvaluationAuditSink(IRuleAuditStore store)
        => _store = store;

    public async ValueTask OnEvaluatedAsync(
        RuleEvaluationAuditEvent ev,
        CancellationToken cancellationToken = default)
    {
        var record = new RuleAuditRecord
        {
            RuleSetKey      = ev.RuleSetKey,
            // Inputs: prefer named; fall back to positional
            Inputs          = ev.Inputs.NamedInputs.Count > 0
                                  ? string.Join(", ", ev.Inputs.NamedInputs.Select(kv => $"{kv.Key}={kv.Value}"))
                                  : string.Join(", ", ev.Inputs.PositionalInputs),
            Status          = ev.Status?.ToString() ?? "Error",
            MatchedRuleName = ev.MatchedRuleName,
            PrimaryKeyName  = ev.PrimaryKey?.Name,
            PrimaryKeyValue = ev.PrimaryKey?.Value?.ToString(),
            Outputs         = string.Join(", ", ev.Outputs.Select(o => $"{o.Name}={o.RawValue}")),
            StartedAt       = ev.StartedAt,
            ElapsedMs       = (long)ev.Elapsed.TotalMilliseconds,
            ErrorType       = ev.ErrorType,
            ErrorMessage    = ev.ErrorMessage,
            // Business context
            CorrelationId   = ev.BusinessContext?.CorrelationId,
            UserId          = ev.BusinessContext?.UserId,
            OperationName   = ev.BusinessContext?.OperationName,
            CustomerId      = ev.BusinessContext?.CustomerId,
        };

        await _store.SaveAsync(record, cancellationToken);
    }
}
```

### B) `Program.cs` registration

```csharp
var builder = WebApplication.CreateBuilder(args);

// RuleEval — database-backed rule loading
builder.Services.AddRuleEvalDatabase<SqlServerRuleSetSource>(options =>
{
    options.DefaultCacheTtl = TimeSpan.FromMinutes(10);
});

// Activate the business audit layer
builder.Services.AddRuleEvalAuditing();

// Register the custom audit sink
builder.Services.AddSingleton<IRuleAuditStore, SqlRuleAuditStore>();
builder.Services.AddSingleton<IRuleEvaluationAuditSink, RuleEvaluationAuditSink>();

// (Optional) OpenTelemetry — completely separate from the audit layer
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(RuleEvalTelemetry.ServiceName).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter(RuleEvalTelemetry.ServiceName).AddOtlpExporter());

var app = builder.Build();
app.MapControllers();
app.Run();
```

### C) Service method in the hosting application

```csharp
public class PricingService
{
    private readonly IRuleSetRepository _repository;
    private readonly IRuleSearchContextAccessor _contextAccessor;

    public PricingService(IRuleSetRepository repository, IRuleSearchContextAccessor contextAccessor)
    {
        _repository = repository;
        _contextAccessor = contextAccessor;
    }

    public async Task<PricingResult> GetPricingAsync(
        PricingRequest request, CancellationToken ct = default)
    {
        // 1. Set business context — the audit sink will receive this
        _contextAccessor.Current = new RuleSearchContext
        {
            OperationName = nameof(GetPricingAsync),
            CorrelationId = request.CorrelationId,
            UserId        = request.UserId,
            CustomerId    = request.CustomerId,
            RequestId     = request.RequestId,
        };

        // 2. Call RuleEval — the AuditingRuleSetRepository intercepts the call,
        //    records start time, calls the inner RuleSetRepository, builds the
        //    RuleEvaluationAuditEvent, and dispatches it to all registered sinks.
        var formula = await _repository.GetFirstOutputAsync(
            "pricing",
            EvaluationContext.FromNamed(new()
            {
                ["segment"] = request.Segment,
                ["age"]     = request.Age.ToString(),
            }),
            "formula",
            cancellationToken: ct);

        // 3. The sink has already received the event — the application's own
        //    persistence logic runs independently of this method's return.
        return new PricingResult(formula);
    }
}
```

### D) Displaying audit data in an application UI

After the sink has persisted the `RuleAuditRecord` rows, the application can query and
display them on a business detail page or admin audit trail:

```csharp
// Example: load audit records for a given request
var records = await _auditStore.GetByCorrelationIdAsync(correlationId);

// Display fields that are always useful in an audit UI:
foreach (var record in records)
{
    Console.WriteLine($"[{record.StartedAt:u}] {record.OperationName}");
    Console.WriteLine($"  Rule set : {record.RuleSetKey}");
    Console.WriteLine($"  Inputs   : {record.Inputs}");
    Console.WriteLine($"  Status   : {record.Status}");
    Console.WriteLine($"  Matched  : {record.MatchedRuleName} (PK {record.PrimaryKeyName}={record.PrimaryKeyValue})");
    Console.WriteLine($"  Outputs  : {record.Outputs}");
    Console.WriteLine($"  Duration : {record.ElapsedMs} ms");
    if (record.ErrorType is not null)
        Console.WriteLine($"  Error    : {record.ErrorType} — {record.ErrorMessage}");
}
```

---

## Architecture: where is the hook?

The audit hook is placed in `AuditingRuleSetRepository`, a **Decorator** over `IRuleSetRepository`.

```
Hosting application
       │
       ▼
AuditingRuleSetRepository   ← captures start time, reads IRuleSearchContextAccessor.Current
       │ calls inner
       ▼
RuleSetRepository            ← loads rule set from cache or DB, runs evaluator
       │ returns EvaluationResult
       ▼
AuditingRuleSetRepository   ← builds RuleEvaluationAuditEvent, calls all IRuleEvaluationAuditSink
       │ returns EvaluationResult (unchanged)
       ▼
Hosting application
       │ forwards events to
       ▼
IRuleAuditStore / message bus / logger / …  (hosting app's own code)
```

The decorator intercepts only the `EvaluateFirst*` and `GetFirstOutput*` methods.
`LoadAsync` and `InvalidateCacheAsync` are passed through without modification.

---

## Changed files

| File | Change |
|------|--------|
| `src/RuleEval.Core/Auditing.cs` | **New** — `RuleSearchContext`, `IRuleSearchContextAccessor`, `AsyncLocalRuleSearchContextAccessor`, `RuleEvaluationAuditEvent`, `IRuleEvaluationAuditSink`, `NullRuleEvaluationAuditSink` |
| `src/RuleEval.Database/AuditingRuleSetRepository.cs` | **New** — `AuditingRuleSetRepository` (decorator) |
| `src/RuleEval.Database.DependencyInjection/ServiceCollectionExtensions.cs` | **Extended** — `AddRuleEvalAuditing()` |
| `tests/RuleEval.Tests.Unit/RuleEvalUnitTests.cs` | **Extended** — 9 new audit layer tests |
| `docs/auditing.md` | **New** — this document |
