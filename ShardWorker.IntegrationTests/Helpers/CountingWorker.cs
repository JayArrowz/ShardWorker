using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System.Collections.Concurrent;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    private sealed class CountingWorker : IShardedWorker
    {
        private int _count;
        public int Count => _count;
        public ConcurrentBag<int> SeenShards { get; } = new();

        public Task ExecuteAsync(ShardContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            SeenShards.Add(ctx.Index);
            return Task.CompletedTask;
        }
    }
}

