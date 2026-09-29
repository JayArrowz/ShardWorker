using ShardWorker.Core.Interface;
using System.Diagnostics;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    private enum RenewBehavior
    {
        /// <summary>Delegate to the inner provider.</summary>
        Succeed,
        /// <summary>Throw, as a provider does when the database is unreachable.</summary>
        Throw,
        /// <summary>Block until the token is cancelled.</summary>
        HangHonoringToken,
        /// <summary>Block until the test calls <see cref="FaultyRenewProvider.Unblock"/>, ignoring the token.</summary>
        HangIgnoringToken,
    }

    /// <summary>
    /// Wraps a provider and chooses how each RenewManyAsync call behaves by its 1-based call
    /// number, simulating an outage or hung connection to the lock database.
    /// </summary>
    private sealed class FaultyRenewProvider : IShardLockProvider
    {
        private readonly IShardLockProvider _inner;
        private readonly Func<int, RenewBehavior> _behavior;
        private readonly TaskCompletionSource _unblock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _renewCount;
        private long _lastSuccessfulRenewStartedAt;
        private int _cancelledRenewCount;

        public FaultyRenewProvider(IShardLockProvider inner, Func<int, RenewBehavior> behavior)
        {
            _inner = inner;
            _behavior = behavior;
        }

        /// <summary>Stopwatch timestamp at which the most recent successful renewal was invoked.</summary>
        public long LastSuccessfulRenewStartedAt => Interlocked.Read(ref _lastSuccessfulRenewStartedAt);

        public int CancelledRenewCount => Volatile.Read(ref _cancelledRenewCount);

        public void Unblock() => _unblock.TrySetResult();

        public Task EnsureSchemaAsync(CancellationToken ct = default)
            => _inner.EnsureSchemaAsync(ct);

        public Task<IReadOnlyList<int>> TryAcquireManyAsync(
            IReadOnlyList<int> candidates, string instanceId, TimeSpan expiry, CancellationToken ct = default)
            => _inner.TryAcquireManyAsync(candidates, instanceId, expiry, ct);

        public async Task<IReadOnlyList<int>> RenewManyAsync(
            IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default)
        {
            var startedAt = Stopwatch.GetTimestamp();
            switch (_behavior(Interlocked.Increment(ref _renewCount)))
            {
                case RenewBehavior.Throw:
                    throw new InvalidOperationException("simulated database outage");

                case RenewBehavior.HangHonoringToken:
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { Interlocked.Increment(ref _cancelledRenewCount); throw; }
                    throw new InvalidOperationException("unreachable");

                case RenewBehavior.HangIgnoringToken:
                    await _unblock.Task;
                    throw new InvalidOperationException("simulated database outage");

                default:
                    var renewed = await _inner.RenewManyAsync(held, instanceId, expiry, ct);
                    Interlocked.Exchange(ref _lastSuccessfulRenewStartedAt, startedAt);
                    return renewed;
            }
        }

        public Task ReleaseManyAsync(
            IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default)
            => _inner.ReleaseManyAsync(shards, instanceId, ct);
    }
}
