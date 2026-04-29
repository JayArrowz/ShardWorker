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
        var opts = new ShardWorkerOptions();
        configure(opts);

        services.Configure<ShardWorkerOptions>(typeof(TWorker).Name, configure);
        services.TryAddSingleton<TWorker>();
        services.AddSingleton<IShardLockProvider<TWorker>>(sp =>
            new TypedShardLockProvider<TWorker>(lockProviderFactory(sp)));
        services.AddSingleton<WorkerRegistration>(sp => new WorkerRegistration
        {
            WorkerName = typeof(TWorker).Name,
            TotalShards = opts.TotalShards,
            LockProvider = lockProviderFactory(sp)
        });
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
        var opts = new ShardWorkerOptions();
        configure(opts);

        services.Configure<ShardWorkerOptions>(typeof(TWorker).Name, configure);
        services.TryAddSingleton<TWorker>();
        services.AddSingleton<IShardLockProvider<TWorker>>(
            new TypedShardLockProvider<TWorker>(lockProvider));
        services.AddSingleton(new WorkerRegistration
        {
            WorkerName = typeof(TWorker).Name,
            TotalShards = opts.TotalShards,
            LockProvider = lockProvider
        });
        services.AddHostedService<ShardEngine<TWorker>>();
        return services;
    }
}

