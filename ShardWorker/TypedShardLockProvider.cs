using ShardWorker.Core.Interface;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShardWorker;

/// <summary>
/// Internal wrapper that adapts a plain <see cref="IShardLockProvider"/> to the typed
/// <see cref="IShardLockProvider{TWorker}"/> marker interface. This allows each worker
/// to receive a distinct provider instance (e.g. pointing to a different DB table) while
/// the underlying implementation class can be shared.
/// </summary>
internal sealed class TypedShardLockProvider<TWorker> : IShardLockProvider<TWorker>
    where TWorker : IShardedWorker
{
    private readonly IShardLockProvider _inner;

    public TypedShardLockProvider(IShardLockProvider inner) => _inner = inner;

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        _inner.EnsureSchemaAsync(ct);

    public Task<IReadOnlyList<int>> TryAcquireManyAsync(
        IReadOnlyList<int> candidates, string instanceId, TimeSpan expiry, CancellationToken ct = default) =>
        _inner.TryAcquireManyAsync(candidates, instanceId, expiry, ct);

    public Task<IReadOnlyList<int>> RenewManyAsync(
        IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default) =>
        _inner.RenewManyAsync(held, instanceId, expiry, ct);

    public Task ReleaseManyAsync(IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default) =>
        _inner.ReleaseManyAsync(shards, instanceId, ct);
}