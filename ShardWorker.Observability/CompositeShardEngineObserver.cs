using ShardWorker.Core.Interface;

namespace ShardWorker.Observability;

/// <summary>
/// Fans out <see cref="IShardEngineObserver"/> calls to multiple registered observers.
/// This is the implementation resolved from DI when you call <c>AddShardWorkerMetrics()</c>.
/// </summary>
internal sealed class CompositeShardEngineObserver : IShardEngineObserver
{
    private readonly IReadOnlyList<IShardEngineObserver> _observers;

    public CompositeShardEngineObserver(IReadOnlyList<IShardEngineObserver> observers)
        => _observers = observers;

    public void OnShardAcquired(string workerName, string instanceId, int shardIndex)
        => Invoke(o => o.OnShardAcquired(workerName, instanceId, shardIndex));

    public void OnShardReleased(string workerName, string instanceId, int shardIndex)
        => Invoke(o => o.OnShardReleased(workerName, instanceId, shardIndex));

    public void OnShardStolen(string workerName, string instanceId, int shardIndex)
        => Invoke(o => o.OnShardStolen(workerName, instanceId, shardIndex));

    public void OnWorkerFaulted(string workerName, string instanceId, int shardIndex, Exception exception)
        => Invoke(o => o.OnWorkerFaulted(workerName, instanceId, shardIndex, exception));

    private void Invoke(Action<IShardEngineObserver> action)
    {
        foreach (var observer in _observers)
        {
            try { action(observer); }
            catch { /* observers must not crash the engine */ }
        }
    }
}
