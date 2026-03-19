using RuleEval.Abstractions;

namespace RuleEval.Diagnostics;

public interface IRuleEvaluationObserver
{
    ValueTask OnEvaluatedAsync(string ruleSetKey, EvaluationResult result, CancellationToken cancellationToken = default);
    ValueTask OnEvaluatedAllAsync(string ruleSetKey, EvaluationMatchesResult result, CancellationToken cancellationToken = default);
}

public sealed class CompositeRuleEvaluationObserver : IRuleEvaluationObserver
{
    private readonly IReadOnlyList<IRuleEvaluationObserver> _observers;

    public CompositeRuleEvaluationObserver(IEnumerable<IRuleEvaluationObserver> observers)
    {
        _observers = observers.ToArray();
    }

    public async ValueTask OnEvaluatedAsync(string ruleSetKey, EvaluationResult result, CancellationToken cancellationToken = default)
    {
        foreach (var observer in _observers)
        {
            await observer.OnEvaluatedAsync(ruleSetKey, result, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask OnEvaluatedAllAsync(string ruleSetKey, EvaluationMatchesResult result, CancellationToken cancellationToken = default)
    {
        foreach (var observer in _observers)
        {
            await observer.OnEvaluatedAllAsync(ruleSetKey, result, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class DelegateRuleEvaluationObserver : IRuleEvaluationObserver
{
    private readonly Func<string, EvaluationResult, CancellationToken, ValueTask>? _firstHandler;
    private readonly Func<string, EvaluationMatchesResult, CancellationToken, ValueTask>? _allHandler;

    public DelegateRuleEvaluationObserver(
        Func<string, EvaluationResult, CancellationToken, ValueTask>? firstHandler = null,
        Func<string, EvaluationMatchesResult, CancellationToken, ValueTask>? allHandler = null)
    {
        _firstHandler = firstHandler;
        _allHandler = allHandler;
    }

    public ValueTask OnEvaluatedAsync(string ruleSetKey, EvaluationResult result, CancellationToken cancellationToken = default)
        => _firstHandler?.Invoke(ruleSetKey, result, cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask OnEvaluatedAllAsync(string ruleSetKey, EvaluationMatchesResult result, CancellationToken cancellationToken = default)
        => _allHandler?.Invoke(ruleSetKey, result, cancellationToken) ?? ValueTask.CompletedTask;
}
