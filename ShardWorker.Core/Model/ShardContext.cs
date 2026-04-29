namespace ShardWorker.Core.Model;

/// <summary>
/// Passed to every IShardedWorker invocation so the worker knows its slice.
/// </summary>
/// <param name="Index">Zero-based shard index owned by this instance.</param>
/// <param name="Total">Total number of shards configured for this worker.</param>
/// <param name="InstanceId">Unique identifier for this running process/host.</param>
/// <param name="WorkerName">Name of the worker type (derived from the class name).</param>
public sealed record ShardContext(int Index, int Total, string InstanceId, string WorkerName)
{
    private volatile bool _releaseRequested;

    /// <summary>
    /// Whether <see cref="RequestRelease"/> has been called for this execution.
    /// </summary>
    public bool IsReleaseRequested => _releaseRequested;

    /// <summary>
    /// Signals the engine to release this shard after <c>ExecuteAsync</c> returns.
    /// Call this when no work was found so the shard is freed immediately rather than
    /// being held idle until the next worker interval.
    /// <para>
    /// Unlike <c>ReleaseOnCompletion</c>, which releases after every clean return,
    /// this lets the worker decide per-execution — hold when there is work, release when idle.
    /// </para>
    /// </summary>
    public void RequestRelease() => _releaseRequested = true;
}