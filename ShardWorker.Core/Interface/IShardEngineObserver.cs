using System;

namespace ShardWorker.Core.Interface;

/// <summary>
/// Receives lifecycle notifications from <c>ShardEngine</c>.
/// Implement this to plug in metrics, tracing, or alerting without coupling to a specific
/// observability library. Register your implementation via DI and the engine will call it
/// automatically.
/// <para>
/// All methods are called on background threads. Implementations must be thread-safe and
/// must not throw — any exception will be swallowed to protect the engine loop.
/// </para>
/// </summary>
public interface IShardEngineObserver
{
    /// <summary>Called when this instance successfully acquires a shard lock.</summary>
    void OnShardAcquired(string workerName, string instanceId, int shardIndex);

    /// <summary>
    /// Called when this instance voluntarily releases a shard lock (on completion, on throw,
    /// or on graceful shutdown). Not called when a lock expires naturally after a crash.
    /// </summary>
    void OnShardReleased(string workerName, string instanceId, int shardIndex);

    /// <summary>
    /// Called when the heartbeat detects that a shard lock was stolen — i.e. the lock row
    /// was claimed by another instance between heartbeat intervals.
    /// </summary>
    void OnShardStolen(string workerName, string instanceId, int shardIndex);

    /// <summary>
    /// Called each time <c>ExecuteAsync</c> throws an unhandled exception on a shard.
    /// </summary>
    void OnWorkerFaulted(string workerName, string instanceId, int shardIndex, Exception exception);
}
