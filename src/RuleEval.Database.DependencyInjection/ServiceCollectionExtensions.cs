using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RuleEval.Auditing;
using RuleEval.Caching;
using RuleEval.Core;
using RuleEval.Database;
using RuleEval.Database.Abstractions;
using RuleEval.DependencyInjection;

namespace RuleEval.Database.DependencyInjection;

/// <summary>
/// Configuration options for <see cref="ServiceCollectionExtensions.AddRuleEvalDatabase{TSource}"/>.
/// </summary>
public sealed class RuleEvalDatabaseOptions
{
    /// <summary>
    /// Default TTL for cached rule sets loaded via <see cref="IRuleSetRepository.LoadAsync(string, System.Threading.CancellationToken)"/>.
    /// Set to <see cref="TimeSpan.Zero"/> (default) to disable caching.
    /// </summary>
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.Zero;
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all RuleEval services with a type-registered <see cref="IRuleSetSource"/>.
    /// </summary>
    /// <typeparam name="TSource">
    /// The concrete <see cref="IRuleSetSource"/> implementation to use
    /// (e.g. <c>SqlServerRuleSetSource</c>, <c>PostgreSqlRuleSetSource</c>).
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="RuleEvalDatabaseOptions"/>.</param>
    public static IServiceCollection AddRuleEvalDatabase<TSource>(
        this IServiceCollection services,
        Action<RuleEvalDatabaseOptions>? configure = null)
        where TSource : class, IRuleSetSource
    {
        services.AddSingleton<IRuleSetSource, TSource>();
        return services.AddRuleEvalDatabase(configure);
    }

    /// <summary>
    /// Registers all RuleEval services with a factory-provided <see cref="IRuleSetSource"/>.
    /// Use this overload when the source requires runtime configuration (e.g. connection string from
    /// <c>IConfiguration</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="sourceFactory">Factory that creates the <see cref="IRuleSetSource"/>.</param>
    /// <param name="configure">Optional callback to configure <see cref="RuleEvalDatabaseOptions"/>.</param>
    public static IServiceCollection AddRuleEvalDatabase(
        this IServiceCollection services,
        Func<IServiceProvider, IRuleSetSource> sourceFactory,
        Action<RuleEvalDatabaseOptions>? configure = null)
    {
        services.AddSingleton<IRuleSetSource>(sourceFactory);
        return services.AddRuleEvalDatabase(configure);
    }

    private static IServiceCollection AddRuleEvalDatabase(
        this IServiceCollection services,
        Action<RuleEvalDatabaseOptions>? configure)
    {
        var options = new RuleEvalDatabaseOptions();
        configure?.Invoke(options);

        services.AddRuleEval();

        if (options.DefaultCacheTtl > TimeSpan.Zero)
        {
            // Override the NoCacheRuleSetCache registered by AddRuleEval()
            services.AddSingleton<IRuleSetCache, MemoryRuleSetCache>();
        }

        services.AddSingleton<DbRuleSetMapper>();
        services.AddSingleton<IRuleSetRepository>(sp => new RuleSetRepository(
            sp.GetRequiredService<IRuleSetSource>(),
            sp.GetRequiredService<IRuleSetCache>(),
            sp.GetRequiredService<DbRuleSetMapper>(),
            sp.GetRequiredService<RuleSetEvaluator>(),
            defaultCacheTtl: options.DefaultCacheTtl,
            logger: sp.GetService<ILogger<RuleSetRepository>>()));

        return services;
    }

    /// <summary>
    /// Activates the business/audit observability layer for RuleEval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this method <b>after</b> <see cref="AddRuleEvalDatabase{TSource}"/> so that the
    /// <see cref="IRuleSetRepository"/> registration already exists to be decorated.
    /// </para>
    /// <para>
    /// This method:
    /// <list type="bullet">
    ///   <item>Registers <see cref="IRuleSearchContextAccessor"/> as <see cref="AsyncLocalRuleSearchContextAccessor"/> (singleton, <c>TryAdd</c> — replaceable).</item>
    ///   <item>Wraps the existing <see cref="IRuleSetRepository"/> in an <see cref="AuditingRuleSetRepository"/> decorator that notifies all registered <see cref="IRuleEvaluationAuditSink"/> implementations.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Register custom sinks before or after this call:
    /// <code>
    /// services.AddSingleton&lt;IRuleEvaluationAuditSink, MyAuditSink&gt;();
    /// </code>
    /// If no sink is registered, evaluation behaves identically to a non-audited repository.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddRuleEvalAuditing(this IServiceCollection services)
    {
        // Register the AsyncLocal-based context accessor.
        // TryAdd allows the hosting app to substitute a custom implementation.
        services.TryAddSingleton<IRuleSearchContextAccessor, AsyncLocalRuleSearchContextAccessor>();

        // Decorate the registered IRuleSetRepository with the auditing wrapper.
        var innerDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IRuleSetRepository));
        if (innerDescriptor is null)
            return services;

        services.Remove(innerDescriptor);

        services.Add(ServiceDescriptor.Describe(
            typeof(IRuleSetRepository),
            sp =>
            {
                var inner = innerDescriptor switch
                {
                    { ImplementationInstance: not null } => (IRuleSetRepository)innerDescriptor.ImplementationInstance,
                    { ImplementationFactory: not null } => (IRuleSetRepository)innerDescriptor.ImplementationFactory(sp),
                    { ImplementationType: not null } => (IRuleSetRepository)ActivatorUtilities.CreateInstance(sp, innerDescriptor.ImplementationType),
                    _ => throw new InvalidOperationException("Cannot determine how to create the inner IRuleSetRepository."),
                };

                return new AuditingRuleSetRepository(
                    inner,
                    sp.GetRequiredService<IRuleSearchContextAccessor>(),
                    sp.GetServices<IRuleEvaluationAuditSink>());
            },
            innerDescriptor.Lifetime));

        return services;
    }
}
