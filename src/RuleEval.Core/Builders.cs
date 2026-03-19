using System.Collections.Immutable;
using RuleEval.Abstractions;

namespace RuleEval.Core;

public sealed class RuleSetBuilder
{
    private readonly string _key;
    private readonly List<string> _inputs = [];
    private readonly List<Rule> _rules = [];
    private readonly Dictionary<string, string?> _metadata = new(StringComparer.OrdinalIgnoreCase);

    private RuleSetBuilder(string key)
    {
        _key = key;
    }

    public static RuleSetBuilder Create(string key) => new(key);

    public RuleSetBuilder AddInput(string fieldName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        _inputs.Add(fieldName);
        return this;
    }

    public RuleSetBuilder AddMetadata(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _metadata[key] = value;
        return this;
    }

    public RuleSetBuilder AddRule(Func<RuleBuilder, RuleBuilder> configure)
    {
        var builder = configure(new RuleBuilder(_inputs, _rules.Count));
        _rules.Add(builder.Build());
        return this;
    }

    public RuleSet Build() => RuleSet.Create(_key, _inputs, _rules, _metadata);
}

public sealed class RuleBuilder
{
    private readonly IReadOnlyList<string> _expectedInputs;
    private readonly List<Condition> _conditions = [];
    private readonly List<OutputValue> _outputs = [];
    private readonly int _order;
    private PrimaryKeyValue? _primaryKey;
    private string? _name;

    internal RuleBuilder(IReadOnlyList<string> expectedInputs, int order)
    {
        _expectedInputs = expectedInputs;
        _order = order;
    }

    public RuleBuilder Named(string name)
    {
        _name = name;
        return this;
    }

    public RuleBuilder When(string fieldName, object? expected, string matcherKey = DefaultMatcherKeys.Auto, string? label = null)
    {
        _conditions.Add(new Condition(fieldName, expected, _conditions.Count, matcherKey, label));
        return this;
    }

    public RuleBuilder ThenOutput(string name, object? rawValue)
    {
        _outputs.Add(new OutputValue(name, rawValue, _outputs.Count));
        return this;
    }

    public RuleBuilder WithPrimaryKey(string name, object? value)
    {
        _primaryKey = new PrimaryKeyValue(name, value);
        return this;
    }

    internal Rule Build()
    {
        if (_conditions.Count != _expectedInputs.Count)
        {
            throw new InvalidRuleDefinitionException($"Rule '{_name ?? _order.ToString()}' must define exactly {_expectedInputs.Count} conditions.");
        }

        var expectedFieldSet = _expectedInputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualFieldSet = _conditions.Select(static condition => condition.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedFieldSet.SetEquals(actualFieldSet))
        {
            throw new InvalidRuleDefinitionException("Rule condition fields must exactly match the rule set input fields.");
        }

        return Rule.Create(_order, _conditions.OrderBy(static condition => condition.Order), _outputs.OrderBy(static output => output.Order), _primaryKey, _name);
    }
}
