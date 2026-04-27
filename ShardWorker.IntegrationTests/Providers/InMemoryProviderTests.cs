using ShardWorker.Core.Interface;
using ShardWorker.Providers.InMemory;

namespace ShardWorker.IntegrationTests.Providers;

/// <summary>
/// Runs the full provider contract suite against InMemoryShardLockProvider.
/// No environment variable required — always executes.
/// </summary>
public sealed class InMemoryProviderTests : ProviderContractTests
{
    protected override IShardLockProvider CreateProvider(string tableName)
        => new InMemoryShardLockProvider();

    protected override Task DropTableAsync(string tableName, CancellationToken ct = default)
        => Task.CompletedTask;
}
