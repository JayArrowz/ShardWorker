using Microsoft.Extensions.DependencyInjection;
using ShardWorker.Core.Interface;

namespace ShardWorker.Observability;

/// <summary>
/// Extension methods for registering ShardWorker observability.
/// </summary>
public static class ShardWorkerObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MetricsShardEngineObserver"/> which publishes shard lifecycle
    /// events as <see cref="System.Diagnostics.Metrics"/> counters.
    /// </summary>
    public static IServiceCollection AddShardWorkerMetrics(this IServiceCollection services)
    {
        services.AddSingleton<MetricsShardEngineObserver>();
        services.AddSingleton<IShardEngineObserver>(sp => sp.GetRequiredService<MetricsShardEngineObserver>());
        services.AddSingleton<IShardEngineObserver>(sp =>
        {
            var all = sp.GetServices<IShardEngineObserver>();
            var list = new List<IShardEngineObserver>();
            foreach (var o in all)
                if (o is not CompositeShardEngineObserver)
                    list.Add(o);
            return new CompositeShardEngineObserver(list);
        });
        return services;
    }

    /// <summary>
    /// Registers a custom <see cref="IShardEngineObserver"/> implementation.
    /// Multiple calls are allowed; all registered observers are called for each event.
    /// </summary>
    public static IServiceCollection AddShardEngineObserver<TObserver>(this IServiceCollection services)
        where TObserver : class, IShardEngineObserver
    {
        services.AddSingleton<TObserver>();
        services.AddSingleton<IShardEngineObserver>(sp => sp.GetRequiredService<TObserver>());
        return services;
    }
}
