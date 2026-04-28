#nullable enable
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShardWorker;

/// <summary>
/// Distributed shard engine for <typeparamref name="TWorker"/>.
/// Each distinct <typeparamref name="TWorker"/> type gets its own engine instance with
/// independent options and a dedicated lock namespace, allowing multiple sharded workers
/// to run in the same host without lock collisions.
/// </summary>
public sealed class ShardEngine<TWorker> : BackgroundService
    where TWorker : class, IShardedWorker
{
    private readonly IShardLockProvider _provider;
    private readonly TWorker _worker;
    private readonly ShardWorkerOptions _opts;
    private readonly ILogger<ShardEngine<TWorker>> _logger;
    private readonly IShardEngineObserver? _observer;
    private readonly string _workerName = typeof(TWorker).Name;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..12];

    // shardIndex → (worker CTS, worker Task)
    private readonly ConcurrentDictionary<int, (CancellationTokenSource Cts, Task Task)> _held = new();

    public ShardEngine(
        IShardLockProvider<TWorker> provider,
        TWorker worker,
        IOptionsMonitor<ShardWorkerOptions> optionsMonitor,
        ILogger<ShardEngine<TWorker>> logger,
        IShardEngineObserver? observer = null)
    {
        _provider = provider;
        _worker = worker;
        _opts = optionsMonitor.Get(typeof(TWorker).Name);
        _logger = logger;
        _observer = observer;

        if (_opts.TotalShards <= 0)
            throw new InvalidOperationException(
                $"[{_workerName}] TotalShards must be greater than zero (got {_opts.TotalShards}).");
        if (_opts.WorkerConcurrency <= 0)
            throw new InvalidOperationException(
                $"[{_workerName}] WorkerConcurrency must be greater than zero (got {_opts.WorkerConcurrency}).");
        if (_opts.HeartbeatInterval >= _opts.LockExpiry)
            throw new InvalidOperationException(
                $"[{_workerName}] HeartbeatInterval ({_opts.HeartbeatInterval}) must be less than LockExpiry ({_opts.LockExpiry}).");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _provider.EnsureSchemaAsync(stoppingToken);
        _logger.LogInformation("[{Worker}:{Id}] Started. TotalShards={N}", _workerName, _instanceId, _opts.TotalShards);

        await Task.WhenAll(
            AcquireLoopAsync(stoppingToken),
            HeartbeatLoopAsync(stoppingToken));
    }

    private async Task AcquireLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var capacity = _opts.MaxShardsPerInstance - _held.Count;
            if (capacity <= 0)
            {
                try { await Task.Delay(_opts.AcquireInterval, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var candidates = new List<int>(_opts.TotalShards);
            for (int i = 0; i < _opts.TotalShards; i++)
            {
                if (!_held.ContainsKey(i))
                    candidates.Add(i);

                if (candidates.Count == capacity)
                    break;
            }

            if (candidates.Count > 0)
            {
                try
                {
                    var acquired = await _provider.TryAcquireManyAsync(candidates, _instanceId, _opts.LockExpiry, ct);
                    foreach (var shardIndex in acquired)
                    {
                        _logger.LogInformation("[{Worker}:{Id}] Acquired shard {Shard}", _workerName, _instanceId, shardIndex);
                        StartWorker(shardIndex, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Worker}:{Id}] Error during bulk acquire", _workerName, _instanceId);
                }
            }

            try { await Task.Delay(_opts.AcquireInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_opts.HeartbeatInterval, ct); }
            catch (OperationCanceledException) { break; }

            var held = _held.Keys.ToList();
            if (held.Count == 0) continue;

            try
            {
                var renewed = await _provider.RenewManyAsync(held, _instanceId, _opts.LockExpiry, ct);
                if (renewed.Count < held.Count)
                {
                    var renewedSet = new HashSet<int>(renewed);
                    foreach (var shardIndex in held)
                    {
                        if (!renewedSet.Contains(shardIndex))
                        {
                            _logger.LogWarning("[{Worker}:{Id}] Shard {Shard} renewal failed — stopping worker",
                                _workerName, _instanceId, shardIndex);
                            TryNotify(() => _observer?.OnShardStolen(_workerName, _instanceId, shardIndex));
                            StopWorker(shardIndex);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Worker}:{Id}] Error during bulk heartbeat", _workerName, _instanceId);
            }
        }
    }

    private void StartWorker(int shardIndex, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var context = new ShardContext(shardIndex, _opts.TotalShards, _instanceId, _workerName);

        var task = Task.Run(async () =>
        {
            try
            {
                var slotCount = _opts.WorkerConcurrency;
                var slots = new Task[slotCount];
                for (int i = 0; i < slotCount; i++)
                    slots[i] = RunWorkerSlotAsync(context, cts, shardIndex);
                await Task.WhenAll(slots);
            }
            finally
            {
                _held.TryRemove(shardIndex, out _);
                try
                {
                    using var releaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _provider.ReleaseManyAsync(new[] { shardIndex }, _instanceId, releaseCts.Token);
                    _logger.LogInformation("[{Worker}:{Id}] Released shard {Shard}", _workerName, _instanceId, shardIndex);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Worker}:{Id}] Release failed for shard {Shard} (will expire naturally)",
                        _workerName, _instanceId, shardIndex);
                }
                TryNotify(() => _observer?.OnShardReleased(_workerName, _instanceId, shardIndex));
            }
        }, CancellationToken.None);

        if (!_held.TryAdd(shardIndex, (cts, task)))
        {
            cts.Cancel();
            cts.Dispose();
        }
        else
        {
            _logger.LogInformation("[{Worker}:{Id}] Acquired shard {Shard}", _workerName, _instanceId, shardIndex);
            TryNotify(() => _observer?.OnShardAcquired(_workerName, _instanceId, shardIndex));
        }
    }

    private async Task RunWorkerSlotAsync(ShardContext context, CancellationTokenSource cts, int shardIndex)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            bool completed = false;
            try
            {
                await _worker.ExecuteAsync(context, cts.Token);
                completed = true;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Worker}:{Id}] Worker threw on shard {Shard}",
                    _workerName, _instanceId, shardIndex);
                TryNotify(() => _observer?.OnWorkerFaulted(_workerName, _instanceId, shardIndex, ex));
            }

            if (completed && _opts.ReleaseOnCompletion)
            {
                _logger.LogDebug("[{Worker}:{Id}] Shard {Shard} completed — releasing for requeue",
                    _workerName, _instanceId, shardIndex);
                cts.Cancel();
                break;
            }

            if (!completed && _opts.ReleaseOnThrows)
            {
                _logger.LogDebug("[{Worker}:{Id}] Shard {Shard} threw — releasing per ReleaseOnThrows",
                    _workerName, _instanceId, shardIndex);
                cts.Cancel();
                break;
            }

            var delay = completed ? _opts.WorkerInterval : (_opts.WorkerIntervalOnThrows ?? _opts.WorkerInterval);
            try { await Task.Delay(delay, cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void TryNotify(Action action)
    {
        try { action(); }
        catch { /* observers must not crash the engine */ }
    }

    private void StopWorker(int shardIndex)
    {
        if (_held.TryRemove(shardIndex, out var entry))
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{Worker}:{Id}] Stopping — releasing {N} shard(s)",
            _workerName, _instanceId, _held.Count);

        await base.StopAsync(cancellationToken);

        var tasks = _held.Values.Select(e => e.Task).ToArray();
        if (tasks.Length == 0) return;

        try
        {
            var allDone = Task.WhenAll(tasks);
            var timeout = Task.Delay(_opts.ShutdownTimeout, cancellationToken);
            if (await Task.WhenAny(allDone, timeout) != allDone)
                _logger.LogWarning("[{Worker}:{Id}] Shutdown timed out; some locks will expire naturally",
                    _workerName, _instanceId);
        }
        catch (OperationCanceledException) { /* host forced abort — exit immediately */ }
    }
}
