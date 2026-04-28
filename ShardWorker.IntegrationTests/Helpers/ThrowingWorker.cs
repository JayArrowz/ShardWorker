using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    private sealed class ThrowingWorker : IShardedWorker
    {
        private int _calls;
        public int Calls => _calls;

        public Task ExecuteAsync(ShardContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("deliberate failure");
        }
    }
}

