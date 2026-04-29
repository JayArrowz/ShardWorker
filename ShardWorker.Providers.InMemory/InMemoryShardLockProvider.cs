using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShardWorker.Providers.InMemory;

public sealed class InMemoryShardLockProvider : IShardLockProvider, IShardLockQueryProvider
{
    private readonly Dictionary<int, LockRow> _rows = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IReadOnlyList<int>> TryAcquireManyAsync(
        IReadOnlyList<int> candidates, string instanceId, TimeSpan expiry, CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

        var acquired = new List<int>(candidates.Count);
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            foreach (var shardIndex in candidates)
            {
                if (_rows.TryGetValue(shardIndex, out var row) &&
                    row.ExpiresAt > now &&
                    row.InstanceId != instanceId)
                    continue;

                _rows[shardIndex] = new LockRow(instanceId, now + expiry);
                acquired.Add(shardIndex);
            }
        }
        return Task.FromResult<IReadOnlyList<int>>(acquired);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<int>> RenewManyAsync(
        IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default)
    {
        if (held.Count == 0)
            return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

        var renewed = new List<int>(held.Count);
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            foreach (var shardIndex in held)
            {
                if (!_rows.TryGetValue(shardIndex, out var row) || row.InstanceId != instanceId)
                    continue;

                _rows[shardIndex] = row with { ExpiresAt = now + expiry };
                renewed.Add(shardIndex);
            }
        }
        return Task.FromResult<IReadOnlyList<int>>(renewed);
    }

    /// <inheritdoc/>
    public Task ReleaseManyAsync(IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var shardIndex in shards)
            {
                if (_rows.TryGetValue(shardIndex, out var row) && row.InstanceId == instanceId)
                    _rows.Remove(shardIndex);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ShardLockRow>> GetAllLocksAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var rows = _rows
                .Where(kvp => kvp.Value.ExpiresAt > now)
                .Select(kvp => new ShardLockRow(kvp.Key, kvp.Value.InstanceId, kvp.Value.ExpiresAt))
                .ToList();
            return Task.FromResult<IReadOnlyList<ShardLockRow>>(rows);
        }
    }
}
