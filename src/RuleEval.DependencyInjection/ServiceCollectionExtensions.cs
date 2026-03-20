using Microsoft.Extensions.DependencyInjection;
using RuleEval.Caching;
using RuleEval.Core;

namespace RuleEval.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core RuleEval services: <see cref="MatcherRegistry"/>, <see cref="RuleSetEvaluator"/>,
    /// and a no-op <see cref="IRuleSetCache"/>. Does not register any database-related services.
    /// For database integration use <c>AddRuleEvalDatabase</c> from the
    /// <c>RuleEval.Database.DependencyInjection</c> package.
    /// </summary>
    public static IServiceCollection AddRuleEval(this IServiceCollection services)
    {
        services.AddSingleton(MatcherRegistry.CreateDefault());
        services.AddSingleton<RuleSetEvaluator>();
        services.AddSingleton<IRuleSetCache, NoCacheRuleSetCache>();
        return services;
    }
}
