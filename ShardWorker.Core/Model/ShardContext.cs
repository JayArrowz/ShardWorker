namespace ShardWorker.Core.Model;

/// <summary>
/// Passed to every IShardedWorker invocation so the worker knows its slice.
/// </summary>
/// <param name="Index">Zero-based shard index owned by this instance.</param>
/// <param name="Total">Total number of shards configured for this worker.</param>
/// <param name="InstanceId">Unique identifier for this running process/host.</param>
/// <param name="WorkerName">Name of the worker type (derived from the class name).</param>
public sealed record ShardContext(int Index, int Total, string InstanceId, string WorkerName);