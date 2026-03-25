using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RuleEval.Core;

/// <summary>
/// Centralised built-in technical observability for the RuleEval library.
/// Exposes an <see cref="ActivitySource"/> for distributed tracing and a <see cref="Meter"/>
/// for metrics.  Neither OpenTelemetry SDK nor any exporter is configured here;
/// the hosting application decides whether to collect and export these signals.
/// </summary>
/// <remarks>
/// To opt in to tracing and metrics in the hosting application, call:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(RuleEvalTelemetry.ServiceName))
///     .WithMetrics(m => m.AddMeter(RuleEvalTelemetry.ServiceName));
/// </code>
/// </remarks>
public static class RuleEvalTelemetry
{
    /// <summary>
    /// The name used for the <see cref="ActivitySource"/> and <see cref="Meter"/>.
    /// Use this value when configuring OpenTelemetry in the hosting application.
    /// </summary>
    public const string ServiceName = "RuleEval";

    /// <summary>
    /// <see cref="System.Diagnostics.ActivitySource"/> that emits distributed tracing spans
    /// for rule set loading, evaluation, and cache operations.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName);

    private static readonly Meter _meter = new(ServiceName);

    /// <summary>Counter incremented on each cache hit when loading a rule set.</summary>
    public static readonly Counter<long> CacheHitCounter =
        _meter.CreateCounter<long>(
            "ruleeval.cache.hit",
            description: "Number of rule set cache hits.");

    /// <summary>Counter incremented on each cache miss when loading a rule set.</summary>
    public static readonly Counter<long> CacheMissCounter =
        _meter.CreateCounter<long>(
            "ruleeval.cache.miss",
            description: "Number of rule set cache misses.");

    /// <summary>
    /// Histogram of rule set load durations from the database, measured in milliseconds.
    /// </summary>
    public static readonly Histogram<double> DbLoadDurationHistogram =
        _meter.CreateHistogram<double>(
            "ruleeval.db.load.duration",
            unit: "ms",
            description: "Duration of rule set load from the database, in milliseconds.");

    /// <summary>
    /// Histogram of evaluation durations (<see cref="RuleSetEvaluator.EvaluateFirst"/> and
    /// <see cref="RuleSetEvaluator.EvaluateAll"/>), measured in milliseconds.
    /// </summary>
    public static readonly Histogram<double> EvaluateDurationHistogram =
        _meter.CreateHistogram<double>(
            "ruleeval.evaluate.duration",
            unit: "ms",
            description: "Duration of a rule set evaluation, in milliseconds.");

    /// <summary>Counter incremented on each call to evaluate a rule set.</summary>
    public static readonly Counter<long> EvaluateTotalCounter =
        _meter.CreateCounter<long>(
            "ruleeval.evaluate.total",
            description: "Total number of rule set evaluations.");
}
