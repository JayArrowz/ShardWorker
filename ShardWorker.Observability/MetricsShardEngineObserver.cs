using ShardWorker.Core.Interface;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShardWorker.Observability;

/// <summary>
/// <see cref="IShardEngineObserver"/> implementation that publishes metrics via
/// <see cref="System.Diagnostics.Metrics"/> (compatible with OpenTelemetry, Prometheus
/// exporters, dotnet-counters, and any other .NET metrics consumer).
/// </summary>
/// <remarks>
/// Register via <c>AddShardWorkerMetrics()</c> on <c>IServiceCollection</c>.
/// Instruments:
/// <list type="bullet">
///   <item><c>shardworker.shards.acquired</c> — counter, incremented each time a shard is acquired.</item>
///   <item><c>shardworker.shards.released</c> — counter, incremented each time a shard is released.</item>
///   <item><c>shardworker.shards.stolen</c> — counter, incremented each time a heartbeat detects a stolen shard.</item>
///   <item><c>shardworker.worker.faults</c> — counter, incremented each time ExecuteAsync throws.</item>
/// </list>
/// All instruments carry <c>worker</c> and <c>instance_id</c> tags.
/// </remarks>
public sealed class MetricsShardEngineObserver : IShardEngineObserver, IDisposable
{
    /// <summary>The meter name used for all ShardWorker instruments.</summary>
    public const string MeterName = "ShardWorker";

    private readonly Meter _meter;
    private readonly Counter<long> _acquired;
    private readonly Counter<long> _released;
    private readonly Counter<long> _stolen;
    private readonly Counter<long> _faults;

    public MetricsShardEngineObserver()
    {
        _meter = new Meter(MeterName);
        _acquired = _meter.CreateCounter<long>("shardworker.shards.acquired", description: "Number of shard locks acquired.");
        _released = _meter.CreateCounter<long>("shardworker.shards.released", description: "Number of shard locks released.");
        _stolen = _meter.CreateCounter<long>("shardworker.shards.stolen", description: "Number of shard locks detected as stolen during heartbeat.");
        _faults = _meter.CreateCounter<long>("shardworker.worker.faults", description: "Number of unhandled exceptions thrown by ExecuteAsync.");
    }

    public void OnShardAcquired(string workerName, string instanceId, int shardIndex) =>
        _acquired.Add(1, new TagList { { "worker", workerName }, { "instance_id", instanceId } });

    public void OnShardReleased(string workerName, string instanceId, int shardIndex) =>
        _released.Add(1, new TagList { { "worker", workerName }, { "instance_id", instanceId } });

    public void OnShardStolen(string workerName, string instanceId, int shardIndex) =>
        _stolen.Add(1, new TagList { { "worker", workerName }, { "instance_id", instanceId } });

    public void OnWorkerFaulted(string workerName, string instanceId, int shardIndex, Exception exception) =>
        _faults.Add(1, new TagList { { "worker", workerName }, { "instance_id", instanceId } });

    public void Dispose() => _meter.Dispose();
}
