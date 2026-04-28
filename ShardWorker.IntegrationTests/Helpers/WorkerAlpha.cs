using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System.Collections.Concurrent;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    // Two distinct types needed for the multi-worker isolation test.
    private sealed class WorkerAlpha : IShardedWorker
    {
        public ConcurrentBag<int> SeenShards { get; } = new();
        public Task ExecuteAsync(ShardContext ctx, CancellationToken ct)
        {
            SeenShards.Add(ctx.Index);
            return Task.CompletedTask;
        }
    }
}

