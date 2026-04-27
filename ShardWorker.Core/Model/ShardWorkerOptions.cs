using System;

namespace ShardWorker.Core.Model;

public sealed class ShardWorkerOptions
{
    /// <summary>
    /// Total number of shards across all instances.
    /// Workers claim as many as they can; unclaimed shards are picked up on AcquireInterval.
    /// </summary>
    public int TotalShards { get; set; } = 10;

    /// <summary>How long a lock row lives before another instance may steal it.</summary>
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often to renew held shards. Must be comfortably less than LockExpiry.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often to scan for unowned/expired shards and try to claim them.</summary>
    public TimeSpan AcquireInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait between worker executions for the same shard.</summary>
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long StopAsync waits for in-flight workers to finish before giving up.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of shards this instance will hold simultaneously.
    /// Defaults to <see cref="int.MaxValue"/> (no limit — claim as many as available).
    /// Set to e.g. 10 to ensure each instance processes at most 10 shards, leaving
    /// the rest for other instances to claim.
    /// </summary>
    public int MaxShardsPerInstance { get; set; } = int.MaxValue;

    /// <summary>
    /// When <c>true</c>, a shard is released immediately after <see cref="IShardedWorker.ExecuteAsync"/>
    /// returns normally (without throwing). The shard goes back into the unclaimed pool so any
    /// instance — including this one — can pick it up on the next acquire cycle.
    /// <para>
    /// Use this for task-queue style workloads where each shard represents a unit of work
    /// that should be processed once and then made available again, rather than held
    /// continuously by the same instance.
    /// </para>
    /// Defaults to <c>false</c> (shards are held until the engine stops or the lock is stolen).
    /// </summary>
    public bool ReleaseOnCompletion { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, a shard is released immediately after <see cref="IShardedWorker.ExecuteAsync"/>
    /// throws an unhandled exception (any exception other than <see cref="OperationCanceledException"/>).
    /// The shard goes back into the unclaimed pool so any instance can retry it on the next acquire cycle.
    /// <para>
    /// Useful when a shard represents a unit of work that may fail transiently and should be
    /// retried by the next available instance rather than re-attempted by the same instance.
    /// </para>
    /// Defaults to <c>false</c> (the engine retries the same shard after <see cref="WorkerInterval"/>).
    /// </summary>
    public bool ReleaseOnThrows { get; set; } = false;

    /// <summary>
    /// Number of concurrent <see cref="IShardedWorker.ExecuteAsync"/> calls to run in parallel
    /// per shard. Each slot runs its own independent execution loop on the same
    /// <see cref="ShardContext"/>.
    /// <para>
    /// Increase this when your worker is I/O-bound and a single shard produces enough work
    /// to saturate multiple concurrent operations (e.g. a per-shard queue drained in parallel).
    /// </para>
    /// Defaults to <c>1</c> (sequential — one call at a time per shard).
    /// </summary>
    public int WorkerConcurrency { get; set; } = 1;
}