using ShardWorker.Providers.InMemory;
using System.Diagnostics;
using Xunit;

namespace ShardWorker.IntegrationTests.Engine;

/// <summary>
/// Lease-deadline behaviour: when renewals keep failing, the engine must stop a shard's
/// worker before its lock row can expire and be claimed by another instance.
/// </summary>
public sealed partial class ShardEngineTests
{
    private static TimeSpan ElapsedBetween(long from, long to) =>
        TimeSpan.FromSeconds((to - from) / (double)Stopwatch.Frequency);

    /// <summary>A worker that blocks until cancelled and records when that happened.</summary>
    private sealed class BlockingWorker
    {
        private long _cancelledAt;
        private int _executionCount;

        public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long CancelledAt => Interlocked.Read(ref _cancelledAt);
        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public LambdaWorker ToWorker() => new(async (ctx, ct) =>
        {
            Interlocked.Increment(ref _executionCount);
            Running.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _cancelledAt, Stopwatch.GetTimestamp());
                Cancelled.TrySetResult();
                throw;
            }
        });
    }

    [Fact]
    public async Task Engine_RenewalsThrowing_StopsWorkerBeforeLeaseExpires()
    {
        var lockExpiry = TimeSpan.FromSeconds(2);
        var margin = TimeSpan.FromSeconds(1);
        var blocking = new BlockingWorker();
        var observer = new RecordingObserver();
        var inner = new InMemoryShardLockProvider();
        // First renewal succeeds, then the database "goes away" for renewals only.
        var provider = new FaultyRenewProvider(inner, n => n == 1 ? RenewBehavior.Succeed : RenewBehavior.Throw);

        using var host = BuildHostWithObserver(blocking.ToWorker(), provider, observer, opts =>
        {
            opts.TotalShards = 1;
            opts.LockExpiry = lockExpiry;
            opts.LeaseSafetyMargin = margin;
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
            opts.AcquireInterval = TimeSpan.FromSeconds(60); // prevent re-acquire during test
            opts.WorkerInterval = TimeSpan.Zero;
        });

        await host.StartAsync();
        await blocking.Running.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await blocking.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sinceLastRenewal = ElapsedBetween(provider.LastSuccessfulRenewStartedAt, blocking.CancelledAt);
        // Several failed heartbeats are tolerated while the lease is still good...
        Assert.True(sinceLastRenewal >= lockExpiry - margin - TimeSpan.FromMilliseconds(100),
            $"Worker stopped too early: {sinceLastRenewal.TotalMilliseconds} ms after last renewal");
        // ...but the worker is cancelled before the lock row can expire.
        Assert.True(sinceLastRenewal < lockExpiry,
            $"Worker outlived its lease: {sinceLastRenewal.TotalMilliseconds} ms after last renewal");

        // Release still reaches the database, so the shard is freed before the row would expire.
        IReadOnlyList<int> claimed = Array.Empty<int>();
        await WaitForAsync(() =>
        {
            claimed = inner.TryAcquireManyAsync([0], "competitor", TimeSpan.FromSeconds(30)).Result;
            return claimed.Count == 1;
        }, timeoutMs: 800);
        Assert.True(ElapsedBetween(provider.LastSuccessfulRenewStartedAt, Stopwatch.GetTimestamp()) < lockExpiry,
            "Shard was only claimable after natural expiry, so the engine did not release it");

        Assert.Equal(1, blocking.ExecutionCount);
        Assert.Contains(observer.Events, e => e.Event == "LeaseLost" && e.Shard == 0);
        Assert.DoesNotContain(observer.Events, e => e.Event == "Stolen");
        Assert.DoesNotContain(observer.Events, e => e.Event == "Released");

        await host.StopAsync();
    }

    [Fact]
    public async Task Engine_RenewalHangingAndIgnoringToken_StillStopsWorkerBeforeLeaseExpires()
    {
        // Worst case: the provider never returns and ignores cancellation, so the heartbeat
        // loop is stuck. The lease deadline must be enforced independently of it.
        var lockExpiry = TimeSpan.FromSeconds(1);
        var blocking = new BlockingWorker();
        var observer = new RecordingObserver();
        var provider = new FaultyRenewProvider(new InMemoryShardLockProvider(),
            n => n == 1 ? RenewBehavior.Succeed : RenewBehavior.HangIgnoringToken);

        using var host = BuildHostWithObserver(blocking.ToWorker(), provider, observer, opts =>
        {
            opts.TotalShards = 1;
            opts.LockExpiry = lockExpiry;
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
            opts.AcquireInterval = TimeSpan.FromSeconds(60);
            opts.WorkerInterval = TimeSpan.Zero;
        });

        try
        {
            await host.StartAsync();
            await blocking.Running.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await blocking.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var sinceLastRenewal = ElapsedBetween(provider.LastSuccessfulRenewStartedAt, blocking.CancelledAt);
            Assert.True(sinceLastRenewal < lockExpiry,
                $"Worker outlived its lease: {sinceLastRenewal.TotalMilliseconds} ms after last renewal");
            Assert.Contains(observer.Events, e => e.Event == "LeaseLost" && e.Shard == 0);
        }
        finally
        {
            provider.Unblock();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Engine_RenewalHangingHonoringToken_IsCancelledAndHeartbeatRecovers()
    {
        // A hung renewal is abandoned at the lease cutoff so the heartbeat loop keeps going,
        // and a later re-acquired shard is renewed normally.
        var observer = new RecordingObserver();
        var worker = new CountingWorker();
        var provider = new FaultyRenewProvider(new InMemoryShardLockProvider(),
            n => n == 2 ? RenewBehavior.HangHonoringToken : RenewBehavior.Succeed);

        using var host = BuildHostWithObserver(worker, provider, observer, opts =>
        {
            opts.TotalShards = 1;
            opts.LockExpiry = TimeSpan.FromSeconds(1);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
        });

        await host.StartAsync();
        await WaitForAsync(() => provider.CancelledRenewCount == 1, timeoutMs: 5000);
        await WaitForAsync(() => provider.LastSuccessfulRenewStartedAt != 0
            && observer.Events.Count(e => e.Event == "Acquired") >= 2
            && ElapsedBetween(provider.LastSuccessfulRenewStartedAt, Stopwatch.GetTimestamp()) < TimeSpan.FromMilliseconds(400),
            timeoutMs: 5000);
        await host.StopAsync();

        Assert.Contains(observer.Events, e => e.Event == "LeaseLost");
    }

    [Fact]
    public async Task Engine_SingleFailedRenewal_DoesNotStopWorker()
    {
        var blocking = new BlockingWorker();
        var observer = new RecordingObserver();
        var provider = new FaultyRenewProvider(new InMemoryShardLockProvider(),
            n => n == 2 ? RenewBehavior.Throw : RenewBehavior.Succeed);

        using var host = BuildHostWithObserver(blocking.ToWorker(), provider, observer, opts =>
        {
            opts.TotalShards = 1;
            opts.LockExpiry = TimeSpan.FromSeconds(1);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
            opts.AcquireInterval = TimeSpan.FromSeconds(60);
            opts.WorkerInterval = TimeSpan.Zero;
        });

        await host.StartAsync();
        await blocking.Running.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // Run well past the point where the failed renewal's lease would have lapsed.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.False(blocking.Cancelled.Task.IsCompleted, "Worker was stopped after a single failed renewal");
        Assert.DoesNotContain(observer.Events, e => e.Event == "LeaseLost");

        await host.StopAsync();
    }

    [Fact]
    public async Task Engine_HeartbeatPlusMarginNotBelowLockExpiry_ThrowsAtStartup()
    {
        using var host = BuildHost(new CountingWorker(), new InMemoryShardLockProvider(), opts =>
        {
            opts.LockExpiry = TimeSpan.FromSeconds(1);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LeaseSafetyMargin = TimeSpan.FromMilliseconds(500);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("LeaseSafetyMargin", ex.Message);
    }
}
