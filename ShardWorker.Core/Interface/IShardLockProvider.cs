using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShardWorker.Core.Interface;

public interface IShardLockProvider
{
    /// <summary>Create the lock table / schema if it doesn't exist.</summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically attempt to claim all <paramref name="candidates"/> in a single round-trip.
    /// Each shard is claimed only if: (a) no row exists, OR (b) the existing row is expired,
    /// OR (c) the row is already owned by this instanceId.
    /// Returns the subset of candidates that this instance owns after the call.
    /// </summary>
    Task<IReadOnlyList<int>> TryAcquireManyAsync(
        IReadOnlyList<int> candidates, string instanceId, TimeSpan expiry, CancellationToken ct = default);

    /// <summary>
    /// Heartbeat — push expiry forward for all held shards in a single round-trip.
    /// Returns the subset still owned by this instance after renewal.
    /// Any shard absent from the returned list was stolen and its worker should be stopped.
    /// </summary>
    Task<IReadOnlyList<int>> RenewManyAsync(
        IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default);

    /// <summary>
    /// Delete lock rows for the given shards so other instances can claim them immediately.
    /// </summary>
    Task ReleaseManyAsync(IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default);
}

/// <summary>
/// Typed marker interface used to resolve a worker-specific lock provider from DI.
/// Registering separate implementations for each <typeparamref name="TWorker"/> ensures
/// that distinct workers use isolated lock namespaces (e.g. different DB tables).
/// </summary>
public interface IShardLockProvider<TWorker> : IShardLockProvider
    where TWorker : IShardedWorker
{
}
