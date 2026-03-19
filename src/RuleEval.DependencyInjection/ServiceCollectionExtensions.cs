using Microsoft.Extensions.DependencyInjection;
using RuleEval.Caching;
using RuleEval.Core;
using RuleEval.Database;
using RuleEval.Database.Abstractions;

namespace RuleEval.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRuleEvalCore(this IServiceCollection services)
    {
        services.AddSingleton(MatcherRegistry.CreateDefault());
        services.AddSingleton<RuleSetEvaluator>();
        services.AddSingleton<DbRuleSetMapper>();
        services.AddSingleton<IRuleSetCache, NoCacheRuleSetCache>();
        return services;
    }

    public static IServiceCollection AddRuleEvalRepository<TSource>(this IServiceCollection services)
        where TSource : class, IRuleSetSource
    {
        services.AddRuleEvalCore();
        services.AddSingleton<IRuleSetRepository, RuleSetRepository>();
        services.AddSingleton<IRuleSetSource, TSource>();
        return services;
    }
}
