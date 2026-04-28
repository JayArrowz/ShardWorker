using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using ShardWorker.Providers.InMemory;
using System.Collections.Concurrent;
using Xunit;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    private static IHost BuildHost<TWorker>(
        TWorker worker,
        IShardLockProvider provider,
        Action<ShardWorkerOptions> configure)
        where TWorker : class, IShardedWorker
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton(worker);
                services.AddShardEngine<TWorker>(configure, provider);
            })
            .Build();
    }

    /// <summary>Polls until <paramref name="condition"/> returns true or the timeout elapses.</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000, int pollMs = 50)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition not met within {timeoutMs} ms.");
            await Task.Delay(pollMs);
        }
    }

    [Fact]
    public async Task Engine_AcquiresShardsAndCallsExecuteAsync()
    {
        var worker = new CountingWorker();
        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 4;
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
        });

        await host.StartAsync();

        // Wait until all 4 shards have been visited at least once
        await WaitForAsync(() => worker.SeenShards.Distinct().Count() == 4);

        await host.StopAsync();
        Assert.Equal(4, worker.SeenShards.Distinct().Count());
    }

    [Fact]
    public async Task Engine_MaxShardsPerInstance_HoldsAtMostN()
    {
        var worker = new CountingWorker();
        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 10;
            opts.MaxShardsPerInstance = 3;
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
        });

        await host.StartAsync();

        // Wait until the engine has had time to try to acquire more than its cap
        await WaitForAsync(() => worker.Count >= 10);

        // The engine must never hold more than MaxShardsPerInstance at once.
        // We verify by inspecting how many distinct shards it ever touched;
        // with cap=3 and total=10 it will eventually cycle through more,
        // but the instantaneous held count must stay ≤ 3.
        // The simplest observable: only 3 shards should be seen at any snapshot.
        // We check the provider state: try to acquire remaining shards from a second instance.
        var otherInstance = "other-instance";
        var remaining = Enumerable.Range(0, 10).ToArray();
        var acquired = await provider.TryAcquireManyAsync(remaining, otherInstance, TimeSpan.FromSeconds(1));

        // At least 7 shards (10 - 3) must be claimable by the other instance
        Assert.True(acquired.Count >= 7,
            $"Expected at least 7 unclaimed shards, got {acquired.Count}");

        await host.StopAsync();
    }

    [Fact]
    public async Task Engine_ReleaseOnCompletion_ReleasesLockAfterExecution()
    {
        // After ExecuteAsync returns with ReleaseOnCompletion=true, the engine must
        // call ReleaseManyAsync so a competing instance can immediately acquire the shard.
        var firstExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new LambdaWorker((ctx, ct) =>
        {
            firstExecuted.TrySetResult();
            return Task.CompletedTask;
        });

        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 1;
            opts.ReleaseOnCompletion = true;
            opts.AcquireInterval = TimeSpan.FromSeconds(5); // long gap — gives time to probe before re-acquire
            opts.WorkerInterval = TimeSpan.FromMilliseconds(0);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync();
        await firstExecuted.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // Give the async finally block time to call ReleaseManyAsync
        await Task.Delay(300);

        // A competing instance must now be able to claim shard 0
        var stolen = await provider.TryAcquireManyAsync([0], "competitor", TimeSpan.FromSeconds(30));
        Assert.Contains(0, stolen);

        await host.StopAsync();
    }

    [Fact]
    public async Task Engine_ReleaseOnThrows_ReleasesLockAfterException()
    {
        // After ExecuteAsync throws with ReleaseOnThrows=true, the engine must
        // call ReleaseManyAsync so a competing instance can immediately acquire the shard.
        var firstThrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new LambdaWorker((ctx, ct) =>
        {
            firstThrown.TrySetResult();
            throw new InvalidOperationException("deliberate failure");
        });

        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 1;
            opts.ReleaseOnThrows = true;
            opts.AcquireInterval = TimeSpan.FromSeconds(5); // long gap — gives time to probe before re-acquire
            opts.WorkerInterval = TimeSpan.FromMilliseconds(0);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync();
        await firstThrown.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // Give the async finally block time to call ReleaseManyAsync
        await Task.Delay(300);

        // A competing instance must now be able to claim shard 0
        var stolen = await provider.TryAcquireManyAsync([0], "competitor", TimeSpan.FromSeconds(30));
        Assert.Contains(0, stolen);

        await host.StopAsync();
    }

    [Fact]
    public async Task Engine_Heartbeat_RenewsLockBeforeExpiry()
    {
        // The heartbeat loop must renew held shards before LockExpiry elapses.
        // If it works, a competitor cannot steal the shard even after LockExpiry has passed.
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new SemaphoreSlim(0);
        var worker = new LambdaWorker(async (ctx, ct) =>
        {
            workerStarted.TrySetResult();
            await releaseGate.WaitAsync(ct);
        });

        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 1;
            opts.LockExpiry = TimeSpan.FromMilliseconds(400);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(100); // renews well within expiry
            opts.AcquireInterval = TimeSpan.FromMilliseconds(200);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(0);
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync();
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // Wait longer than LockExpiry — without heartbeats the lock would have expired
        await Task.Delay(700);

        // The heartbeat should have renewed the lock; competitor must NOT be able to steal it
        var stolen = await provider.TryAcquireManyAsync([0], "competitor", TimeSpan.FromSeconds(30));
        Assert.Empty(stolen);

        releaseGate.Release();
        await host.StopAsync();
    }

    [Fact]
    public async Task Engine_StolenLock_StopsWorker()
    {
        // When RenewManyAsync returns fewer shards than held (simulating a stolen lock),
        // the engine must cancel that shard's worker slot.
        var workerRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;

        var worker = new LambdaWorker(async (ctx, ct) =>
        {
            Interlocked.Increment(ref executionCount);
            workerRunning.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { workerCancelled.TrySetResult(); }
        });

        var innerProvider = new InMemoryShardLockProvider();
        // Allow the first renew to succeed, then start returning empty — simulates a stolen lock
        var spyProvider = new FailAfterNthRenewProvider(innerProvider, allowedRenews: 1);

        using var host = BuildHost(worker, spyProvider, opts =>
        {
            opts.TotalShards = 1;
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
            opts.AcquireInterval = TimeSpan.FromSeconds(60); // prevent re-acquire during test
            opts.WorkerInterval = TimeSpan.FromMilliseconds(0);
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync();
        await workerRunning.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // Wait for the heartbeat to detect the (simulated) theft and cancel the worker
        await workerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Worker must have run exactly once and then been stopped — not retried
        Assert.Equal(1, executionCount);

        await host.StopAsync();
    }

    [Fact]
    public async Task Engine_GracefulShutdown_ReleasesAllLocks()
    {
        var worker = new CountingWorker();
        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 3;
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(1000); // slow loop — shards held during stop
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync();
        await WaitForAsync(() => worker.SeenShards.Distinct().Count() == 3);

        await host.StopAsync();

        // After graceful stop all 3 shards must be releasable by a fresh instance
        var freshAcquired = await provider.TryAcquireManyAsync(
            new[] { 0, 1, 2 }, "fresh-instance", TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { 0, 1, 2 }, freshAcquired.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Engine_WorkerConcurrency_RunsMultipleSlotsPerShard()
    {
        var callCount = 0;
        var maxConcurrent = 0;
        var current = 0;

        var worker = new LambdaWorker(async (ctx, ct) =>
        {
            var c = Interlocked.Increment(ref current);
            var prev = maxConcurrent;
            while (c > prev && Interlocked.CompareExchange(ref maxConcurrent, c, prev) != prev)
                prev = maxConcurrent;
            Interlocked.Increment(ref callCount);
            await Task.Delay(150, ct); // hold for a bit so slots overlap
            Interlocked.Decrement(ref current);
        });

        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 1;
            opts.WorkerConcurrency = 4;
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(0);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
        });

        await host.StartAsync();
        await WaitForAsync(() => callCount >= 4);
        await host.StopAsync();

        Assert.True(maxConcurrent >= 2,
            $"Expected concurrent execution — max observed concurrency was {maxConcurrent}");
    }

    [Fact]
    public async Task Engine_MultipleWorkerTypes_UseIsolatedLockNamespaces()
    {
        var alphaWorker = new WorkerAlpha();
        var betaWorker = new WorkerBeta();
        var alphaProvider = new InMemoryShardLockProvider();
        var betaProvider = new InMemoryShardLockProvider();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

                services.AddSingleton(alphaWorker);
                services.AddShardEngine<WorkerAlpha>(opts =>
                {
                    opts.TotalShards = 3;
                    opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
                    opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
                    opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
                    opts.LockExpiry = TimeSpan.FromSeconds(30);
                }, alphaProvider);

                services.AddSingleton(betaWorker);
                services.AddShardEngine<WorkerBeta>(opts =>
                {
                    opts.TotalShards = 5;
                    opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
                    opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
                    opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
                    opts.LockExpiry = TimeSpan.FromSeconds(30);
                }, betaProvider);
            })
            .Build();

        await host.StartAsync();

        await WaitForAsync(() =>
            alphaWorker.SeenShards.Distinct().Count() == 3 &&
            betaWorker.SeenShards.Distinct().Count() == 5);

        await host.StopAsync();

        // Alpha and Beta use separate providers — their shard indices are completely independent
        Assert.Equal(new[] { 0, 1, 2 }, alphaWorker.SeenShards.Distinct().OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, betaWorker.SeenShards.Distinct().OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Engine_WorkerIntervalOnThrows_UsesLongerDelayAfterException()
    {
        // With WorkerIntervalOnThrows much longer than WorkerInterval, after a throw the shard
        // must NOT be re-executed within WorkerInterval-time but MUST be re-executed within
        // WorkerIntervalOnThrows-time.

        var callTimes = new ConcurrentBag<DateTime>();
        var worker = new LambdaWorker((ctx, ct) =>
        {
            callTimes.Add(DateTime.UtcNow);
            throw new InvalidOperationException("deliberate");
        });

        var provider = new InMemoryShardLockProvider();
        using var host = BuildHost(worker, provider, opts =>
        {
            opts.TotalShards = 1;
            opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
            opts.WorkerIntervalOnThrows = TimeSpan.FromMilliseconds(500);
            opts.AcquireInterval = TimeSpan.FromMilliseconds(50);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
        });

        await host.StartAsync();

        // Wait for at least two calls so we can measure the gap
        await WaitForAsync(() => callTimes.Count >= 2, timeoutMs: 5000);

        await host.StopAsync();

        // Gap between first and second call must be ≥ WorkerIntervalOnThrows (500 ms),
        // not the short WorkerInterval (50 ms).
        var ordered = callTimes.OrderBy(t => t).ToList();
        var gap = ordered[1] - ordered[0];
        Assert.True(gap >= TimeSpan.FromMilliseconds(400),
            $"Expected ≥ 400 ms between calls after throw, but gap was {gap.TotalMilliseconds:F0} ms");
    }

    private static IHost BuildHostWithObserver<TWorker>(
        TWorker worker,
        IShardLockProvider provider,
        RecordingObserver observer,
        Action<ShardWorkerOptions> configure)
        where TWorker : class, IShardedWorker
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton(worker);
                services.AddSingleton<IShardEngineObserver>(observer);
                services.AddShardEngine<TWorker>(configure, provider);
            })
            .Build();
    }

    [Fact]
    public async Task Observer_OnShardAcquired_CalledWhenShardAcquired()
    {
        var worker = new CountingWorker();
        var provider = new InMemoryShardLockProvider();
        var observer = new RecordingObserver();
        using var host = BuildHostWithObserver(worker, provider, observer, opts =>
        {
            opts.TotalShards = 2;
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
        });

        await host.StartAsync();
        await WaitForAsync(() => observer.Events.Count(e => e.Event == "Acquired") >= 2);
        await host.StopAsync();

        var acquired = observer.Events.Where(e => e.Event == "Acquired").ToList();
        Assert.True(acquired.Count >= 2);
        Assert.All(acquired, e => Assert.Equal(nameof(CountingWorker), e.Worker));
        Assert.Contains(acquired, e => e.Shard == 0);
        Assert.Contains(acquired, e => e.Shard == 1);
    }

    [Fact]
    public async Task Observer_OnShardReleased_CalledOnShutdown()
    {
        var worker = new CountingWorker();
        var provider = new InMemoryShardLockProvider();
        var observer = new RecordingObserver();
        using var host = BuildHostWithObserver(worker, provider, observer, opts =>
        {
            opts.TotalShards = 1;
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
        });

        await host.StartAsync();
        await WaitForAsync(() => observer.Events.Any(e => e.Event == "Acquired"));
        await host.StopAsync();

        Assert.Contains(observer.Events, e => e.Event == "Released");
    }

    [Fact]
    public async Task Observer_OnWorkerFaulted_CalledWhenExecuteAsyncThrows()
    {
        var worker = new LambdaWorker((ctx, ct) =>
        {
            throw new InvalidOperationException("deliberate");
        });
        var provider = new InMemoryShardLockProvider();
        var observer = new RecordingObserver();
        using var host = BuildHostWithObserver(worker, provider, observer, opts =>
        {
            opts.TotalShards = 1;
            opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
            opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
            opts.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            opts.LockExpiry = TimeSpan.FromSeconds(30);
        });

        await host.StartAsync();
        await WaitForAsync(() => observer.Events.Any(e => e.Event == "Faulted"));
        await host.StopAsync();

        var faulted = observer.Events.First(e => e.Event == "Faulted");
        Assert.Equal(nameof(LambdaWorker), faulted.Worker);
        Assert.Equal(0, faulted.Shard);
    }

    [Fact]
    public async Task Observer_OnShardStolen_CalledWhenHeartbeatDetectsSteal()
    {
        // Simulate a stolen lock by wrapping the provider so RenewManyAsync returns empty
        // after the first renewal.
        var inner = new InMemoryShardLockProvider();
        var failProvider = new FailAfterNthRenewProvider(inner, allowedRenews: 1);

        var worker = new CountingWorker();
        var observer = new RecordingObserver();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton(worker);
                services.AddSingleton<IShardEngineObserver>(observer);
                services.AddShardEngine<CountingWorker>(opts =>
                {
                    opts.TotalShards = 1;
                    opts.AcquireInterval = TimeSpan.FromMilliseconds(100);
                    opts.WorkerInterval = TimeSpan.FromMilliseconds(50);
                    opts.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
                    opts.LockExpiry = TimeSpan.FromSeconds(30);
                }, failProvider);
            })
            .Build();

        await host.StartAsync();
        await WaitForAsync(() => observer.Events.Any(e => e.Event == "Stolen"), timeoutMs: 5000);
        await host.StopAsync();

        Assert.Contains(observer.Events, e => e.Event == "Stolen");
    }
}

