using ShardWorker.Core.Interface;

namespace ShardWorker.Core.Model;

/// <summary>
/// Metadata registered for each worker type. Used by the dashboard to associate
/// a worker name and total shard count with its lock provider.
/// </summary>
public sealed class WorkerRegistration
{
    public required string WorkerName { get; init; }
    public required int TotalShards { get; init; }
    public required IShardLockProvider LockProvider { get; init; }
}
