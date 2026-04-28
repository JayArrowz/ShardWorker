using ShardWorker.Core.Interface;
using System.Collections.Concurrent;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    private sealed class RecordingObserver : IShardEngineObserver
    {
        public ConcurrentBag<(string Event, string Worker, string InstanceId, int Shard)> Events { get; } = new();

        public void OnShardAcquired(string workerName, string instanceId, int shardIndex) =>
            Events.Add(("Acquired", workerName, instanceId, shardIndex));

        public void OnShardReleased(string workerName, string instanceId, int shardIndex) =>
            Events.Add(("Released", workerName, instanceId, shardIndex));

        public void OnShardStolen(string workerName, string instanceId, int shardIndex) =>
            Events.Add(("Stolen", workerName, instanceId, shardIndex));

        public void OnWorkerFaulted(string workerName, string instanceId, int shardIndex, Exception exception) =>
            Events.Add(("Faulted", workerName, instanceId, shardIndex));
    }
}

