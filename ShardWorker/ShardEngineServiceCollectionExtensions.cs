using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System;

namespace ShardWorker;

public static class ShardEngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="ShardEngine{TWorker}"/> for the given worker type.
    /// The lock provider factory lets you supply a worker-specific provider instance
    /// (e.g. pointing to a different DB table) so lock rows never collide between workers.
    /// <para>
    /// Multiple calls with different <typeparamref name="TWorker"/> types register
    /// fully independent engines — each with its own options and lock namespace.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShardEngine<TWorker>(
        this IServiceCollection services,
        Action<ShardWorkerOptions> configure,
        Func<IServiceProvider, IShardLockProvider> lockProviderFactory)
        where TWorker : class, IShardedWorker
    {
        services.Configure<ShardWorkerOptions>(typeof(TWorker).Name, configure);
        services.TryAddSingleton<TWorker>();
        services.AddSingleton<IShardLockProvider<TWorker>>(sp =>
            new TypedShardLockProvider<TWorker>(lockProviderFactory(sp)));
        services.TryAddSingleton<IShardEngineObserver, NullShardEngineObserver>();
        services.AddHostedService<ShardEngine<TWorker>>();
        return services;
    }

    /// <summary>
    /// Overload that accepts a pre-built lock provider instance rather than a factory.
    /// </summary>
    public static IServiceCollection AddShardEngine<TWorker>(
        this IServiceCollection services,
        Action<ShardWorkerOptions> configure,
        IShardLockProvider lockProvider)
        where TWorker : class, IShardedWorker
    {
        return services.AddShardEngine<TWorker>(configure, _ => lockProvider);
    }
}

