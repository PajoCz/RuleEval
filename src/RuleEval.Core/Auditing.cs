using RuleEval.Abstractions;

namespace RuleEval.Auditing;

/// <summary>
/// Business/application context set by the hosting application before calling RuleEval.
/// Attach correlation IDs, user identity, and custom tags so they are available in
/// <see cref="RuleEvaluationAuditEvent.BusinessContext"/> when audit sinks are notified.
/// </summary>
public sealed record RuleSearchContext
{
    /// <summary>Name of the calling operation or service method (e.g. "PricingService.GetFormulaAsync").</summary>
    public string? OperationName { get; init; }

    /// <summary>Correlation ID that links this evaluation to the originating request.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>ID of the user who triggered the evaluation.</summary>
    public string? UserId { get; init; }

    /// <summary>Name of the source system or component that initiated the evaluation.</summary>
    public string? SourceSystem { get; init; }

    /// <summary>Customer identifier relevant to the evaluation.</summary>
    public string? CustomerId { get; init; }

    /// <summary>Request identifier (e.g. HTTP request ID, message ID).</summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Arbitrary key/value tags that the hosting application wants to attach to the audit event.
    /// Keys are case-insensitive.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Tags { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Provides access to the ambient <see cref="RuleSearchContext"/> for the current
/// asynchronous execution context.
/// </summary>
public interface IRuleSearchContextAccessor
{
    /// <summary>
    /// Gets or sets the current <see cref="RuleSearchContext"/>.
    /// Set this in the hosting application before calling a RuleEval evaluation method;
    /// the value flows automatically through async continuations via <see cref="AsyncLocal{T}"/>.
    /// </summary>
    RuleSearchContext? Current { get; set; }
}

/// <summary>
/// Default <see cref="AsyncLocal{T}"/>-based implementation of <see cref="IRuleSearchContextAccessor"/>.
/// Register as a singleton.  The context value is stored per-logical-call-context and flows
/// through <see langword="async"/>/<see langword="await"/> continuations automatically.
/// </summary>
public sealed class AsyncLocalRuleSearchContextAccessor : IRuleSearchContextAccessor
{
    private static readonly AsyncLocal<RuleSearchContext?> _current = new();

    /// <inheritdoc/>
    public RuleSearchContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>
/// Immutable payload delivered to <see cref="IRuleEvaluationAuditSink"/> after each rule evaluation.
/// Contains everything the hosting application needs to build an audit trail, business log,
/// or observability record — without coupling to any specific storage or transport.
/// </summary>
/// <remarks>
/// Designed to be:
/// <list type="bullet">
///   <item>Serializable to JSON via <c>System.Text.Json</c>.</item>
///   <item>Mappable to a relational or document DB row.</item>
///   <item>Publishable to a message bus or event stream.</item>
///   <item>Displayable in an audit UI.</item>
/// </list>
/// </remarks>
public sealed record RuleEvaluationAuditEvent
{
    /// <summary>The rule set key that was evaluated (e.g. <c>"pricing"</c>).</summary>
    public required string RuleSetKey { get; init; }

    /// <summary>The inputs used during evaluation.</summary>
    public required EvaluationContext Inputs { get; init; }

    /// <summary>UTC timestamp when the evaluation started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock duration of the evaluation (including rule set load from cache or DB).</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Evaluation status.  <see langword="null"/> only when the evaluation could not start at all
    /// (e.g. the rule set could not be loaded from the database).
    /// </summary>
    public EvaluationStatus? Status { get; init; }

    /// <summary>Name of the matched rule, if any.</summary>
    public string? MatchedRuleName { get; init; }

    /// <summary>Zero-based index of the matched rule within the rule set, if any.</summary>
    public int? MatchedRuleIndex { get; init; }

    /// <summary>Primary key of the matched database row, if the rule set was loaded from a database source.</summary>
    public PrimaryKeyValue? PrimaryKey { get; init; }

    /// <summary>Output values returned by the matched rule.  Empty when there is no match.</summary>
    public IReadOnlyList<OutputValue> Outputs { get; init; } = [];

    /// <summary>The fully-qualified type name of the exception that caused the evaluation to fail, if any.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The exception message when the evaluation failed, if any.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Returns <see langword="true"/> when the evaluation failed with an unhandled exception.</summary>
    public bool IsError => ErrorType is not null;

    /// <summary>
    /// The business context captured from <see cref="IRuleSearchContextAccessor"/> at the time the
    /// evaluation was invoked.  <see langword="null"/> when no context was set.
    /// </summary>
    public RuleSearchContext? BusinessContext { get; init; }
}

/// <summary>
/// Extension point for business/audit observability in the hosting application.
/// Implement this interface to receive a structured <see cref="RuleEvaluationAuditEvent"/>
/// after every rule evaluation performed via <c>IRuleSetRepository</c>.
/// </summary>
/// <remarks>
/// <para>This interface is NOT OpenTelemetry.  It is NOT a technical logging sink.</para>
/// <para>
/// The hosting application is fully in control of what to do with the event:
/// store it in a database, publish it to a message bus, write to a business log,
/// display it in an audit UI, or simply ignore it.
/// </para>
/// <para>
/// Register one or more implementations in DI via
/// <c>services.AddSingleton&lt;IRuleEvaluationAuditSink, YourSink&gt;()</c>
/// and call <c>services.AddRuleEvalAuditing()</c> to activate the hook.
/// </para>
/// </remarks>
public interface IRuleEvaluationAuditSink
{
    /// <summary>
    /// Called after a rule evaluation completes (whether matched, not matched, or failed).
    /// </summary>
    /// <param name="auditEvent">Immutable payload describing the evaluation.</param>
    /// <param name="cancellationToken">Cancellation token from the originating call.</param>
    ValueTask OnEvaluatedAsync(RuleEvaluationAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op implementation of <see cref="IRuleEvaluationAuditSink"/> used as the default
/// when no sink is registered.
/// </summary>
public sealed class NullRuleEvaluationAuditSink : IRuleEvaluationAuditSink
{
    /// <summary>Singleton instance.</summary>
    public static readonly NullRuleEvaluationAuditSink Instance = new();

    /// <inheritdoc/>
    public ValueTask OnEvaluatedAsync(RuleEvaluationAuditEvent auditEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
