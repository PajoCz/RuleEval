using Microsoft.Extensions.DependencyInjection;
using RuleEval.Database;
using RuleEval.Database.Abstractions;
using RuleEval.DependencyInjection;

namespace RuleEval.Database.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all RuleEval services including database-backed rule loading.
    /// Calls <see cref="RuleEval.DependencyInjection.ServiceCollectionExtensions.AddRuleEval"/> and
    /// additionally registers <see cref="DbRuleSetMapper"/>, <see cref="IRuleSetRepository"/>,
    /// and the specified <typeparamref name="TSource"/> as <see cref="IRuleSetSource"/>.
    /// </summary>
    /// <typeparam name="TSource">
    /// The concrete <see cref="IRuleSetSource"/> implementation to use
    /// (e.g. <c>PostgreSqlRuleSetSource</c>, <c>SqlServerRuleSetSource</c>).
    /// </typeparam>
    public static IServiceCollection AddRuleEvalDatabase<TSource>(this IServiceCollection services)
        where TSource : class, IRuleSetSource
    {
        services.AddRuleEval();
        services.AddSingleton<DbRuleSetMapper>();
        services.AddSingleton<IRuleSetRepository, RuleSetRepository>();
        services.AddSingleton<IRuleSetSource, TSource>();
        return services;
    }
}
