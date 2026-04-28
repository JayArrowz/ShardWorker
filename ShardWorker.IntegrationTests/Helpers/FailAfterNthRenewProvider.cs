using ShardWorker.Core.Interface;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    /// <summary>
    /// Wraps a provider and returns empty from RenewManyAsync after
    /// <paramref name="allowedRenews"/> successful renewals, simulating a stolen lock.
    /// </summary>
    private sealed class FailAfterNthRenewProvider : IShardLockProvider
    {
        private readonly IShardLockProvider _inner;
        private readonly int _allowedRenews;
        private int _renewCount;

        public FailAfterNthRenewProvider(IShardLockProvider inner, int allowedRenews)
        {
            _inner = inner;
            _allowedRenews = allowedRenews;
        }

        public Task EnsureSchemaAsync(CancellationToken ct = default)
            => _inner.EnsureSchemaAsync(ct);

        public Task<IReadOnlyList<int>> TryAcquireManyAsync(
            IReadOnlyList<int> candidates, string instanceId, TimeSpan expiry, CancellationToken ct = default)
            => _inner.TryAcquireManyAsync(candidates, instanceId, expiry, ct);

        public Task<IReadOnlyList<int>> RenewManyAsync(
            IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _renewCount) > _allowedRenews)
                return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            return _inner.RenewManyAsync(held, instanceId, expiry, ct);
        }

        public Task ReleaseManyAsync(
            IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default)
            => _inner.ReleaseManyAsync(shards, instanceId, ct);
    }
}

