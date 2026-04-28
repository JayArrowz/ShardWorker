using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;

namespace ShardWorker.IntegrationTests.Engine;

public sealed partial class ShardEngineTests
{
    private sealed class LambdaWorker : IShardedWorker
    {
        private readonly Func<ShardContext, CancellationToken, Task> _execute;

        public LambdaWorker(Func<ShardContext, CancellationToken, Task> execute)
            => _execute = execute;

        public Task ExecuteAsync(ShardContext ctx, CancellationToken ct)
            => _execute(ctx, ct);
    }
}

