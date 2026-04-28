using ShardWorker.Core.Interface;
using System;

namespace ShardWorker;

/// <summary>
/// No-op <see cref="IShardEngineObserver"/> used as the default when no observer is
/// registered. Registered automatically via <c>TryAddSingleton</c> so it is only used
/// when the application does not provide its own implementation.
/// </summary>
internal sealed class NullShardEngineObserver : IShardEngineObserver
{
    public static readonly NullShardEngineObserver Instance = new();

    public void OnShardAcquired(string workerName, string instanceId, int shardIndex) { }
    public void OnShardReleased(string workerName, string instanceId, int shardIndex) { }
    public void OnShardStolen(string workerName, string instanceId, int shardIndex) { }
    public void OnWorkerFaulted(string workerName, string instanceId, int shardIndex, Exception exception) { }
}
