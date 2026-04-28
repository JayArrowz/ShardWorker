using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System.Collections.Concurrent;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    private sealed class WorkerBeta : IShardedWorker
    {
        public ConcurrentBag<int> SeenShards { get; } = new();
        public Task ExecuteAsync(ShardContext ctx, CancellationToken ct)
        {
            SeenShards.Add(ctx.Index);
            return Task.CompletedTask;
        }
    }
}

