using ShardWorker.Core.Interface;
using Xunit;

namespace ShardWorker.IntegrationTests.Providers;

/// <summary>
/// Abstract contract suite run against every IShardLockProvider implementation.
/// Concrete subclasses supply the provider factory and (optionally) override
/// <see cref="CheckAvailability"/> to skip when the target DB is not configured.
/// </summary>
public abstract class ProviderContractTests : IAsyncLifetime
{
    private readonly List<(IShardLockProvider Provider, string TableName)> _created = new();
        
    /// <summary>
    /// Called at the start of every test. Override to call
    /// <c>Skip.If(connectionString is null, reason)</c> for DB-backed providers.
    /// </summary>
    protected virtual void CheckAvailability() { }

    protected abstract IShardLockProvider CreateProvider(string tableName);
    protected abstract Task DropTableAsync(string tableName, CancellationToken ct = default);

    protected async Task<IShardLockProvider> MakeProviderAsync()
    {
        CheckAvailability();
        var table = "t_" + Guid.NewGuid().ToString("N")[..16];
        var provider = CreateProvider(table);
        await provider.EnsureSchemaAsync();
        _created.Add((provider, table));
        return provider;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (_, table) in _created)
        {
            try { await DropTableAsync(table); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task EnsureSchema_IsIdempotent()
    {
        CheckAvailability();
        var table = "t_" + Guid.NewGuid().ToString("N")[..16];
        var provider = CreateProvider(table);
        _created.Add((provider, table));

        await provider.EnsureSchemaAsync();
        await provider.EnsureSchemaAsync(); // must not throw
    }

    [SkippableFact]
    public async Task TryAcquireMany_ClaimsUnownedShards()
    {
        var p = await MakeProviderAsync();

        var acquired = await p.TryAcquireManyAsync(new[] { 0, 1, 2 }, "inst-a", TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { 0, 1, 2 }, acquired.OrderBy(x => x).ToArray());
    }

    [SkippableFact]
    public async Task TryAcquireMany_EmptyCandidates_ReturnsEmpty()
    {
        var p = await MakeProviderAsync();

        var acquired = await p.TryAcquireManyAsync(Array.Empty<int>(), "inst-a", TimeSpan.FromMinutes(5));

        Assert.Empty(acquired);
    }

    [SkippableFact]
    public async Task TryAcquireMany_DoesNotStealActiveLock()
    {
        var p = await MakeProviderAsync();
        await p.TryAcquireManyAsync(new[] { 0 }, "inst-a", TimeSpan.FromMinutes(5));

        var acquired = await p.TryAcquireManyAsync(new[] { 0 }, "inst-b", TimeSpan.FromMinutes(5));

        Assert.Empty(acquired);
    }

    [SkippableFact]
    public async Task TryAcquireMany_ClaimsExpiredLock()
    {
        var p = await MakeProviderAsync();
        await p.TryAcquireManyAsync(new[] { 0 }, "inst-a", TimeSpan.FromMilliseconds(100));

        await Task.Delay(500); // wait for expiry

        var acquired = await p.TryAcquireManyAsync(new[] { 0 }, "inst-b", TimeSpan.FromMinutes(5));

        Assert.Contains(0, acquired);
    }

    [SkippableFact]
    public async Task TryAcquireMany_ReacquiresOwnLock()
    {
        var p = await MakeProviderAsync();
        await p.TryAcquireManyAsync(new[] { 0 }, "inst-a", TimeSpan.FromMinutes(5));

        // same instance re-acquiring should succeed (idempotent)
        var acquired = await p.TryAcquireManyAsync(new[] { 0 }, "inst-a", TimeSpan.FromMinutes(5));

        Assert.Contains(0, acquired);
    }

    [SkippableFact]
    public async Task TryAcquireMany_PartialAcquire_SkipsLockedShards()
    {
        var p = await MakeProviderAsync();
        await p.TryAcquireManyAsync(new[] { 1 }, "inst-a", TimeSpan.FromMinutes(5));

        // inst-b tries for 0, 1, 2 — should only get 0 and 2
        var acquired = await p.TryAcquireManyAsync(new[] { 0, 1, 2 }, "inst-b", TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { 0, 2 }, acquired.OrderBy(x => x).ToArray());
    }

    [SkippableFact]
    public async Task RenewMany_ReturnsSameShards()
    {
        var p = await MakeProviderAsync();
        await p.TryAcquireManyAsync(new[] { 0, 1, 2 }, "inst-a", TimeSpan.FromMinutes(5));

        var renewed = await p.RenewManyAsync(new[] { 0, 1, 2 }, "inst-a", TimeSpan.FromMinutes(10));

        Assert.Equal(new[] { 0, 1, 2 }, renewed.OrderBy(x => x).ToArray());
    }

    [SkippableFact]
    public async Task RenewMany_StolenShard_ExcludedFromResult()
    {
        var p = await MakeProviderAsync();
        // inst-a acquires shards 0 and 2; shard 1 expires and is stolen by inst-b
        await p.TryAcquireManyAsync(new[] { 0, 2 }, "inst-a", TimeSpan.FromMinutes(5));
        await p.TryAcquireManyAsync(new[] { 1 }, "inst-a", TimeSpan.FromMilliseconds(100));

        await Task.Delay(500); // let shard 1 expire

        await p.TryAcquireManyAsync(new[] { 1 }, "inst-b", TimeSpan.FromMinutes(5));

        // inst-a renews all three — only 0 and 2 should succeed
        var renewed = await p.RenewManyAsync(new[] { 0, 1, 2 }, "inst-a", TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { 0, 2 }, renewed.OrderBy(x => x).ToArray());
    }

    [SkippableFact]
    public async Task RenewMany_EmptyHeld_ReturnsEmpty()
    {
        var p = await MakeProviderAsync();

        var renewed = await p.RenewManyAsync(Array.Empty<int>(), "inst-a", TimeSpan.FromMinutes(5));

        Assert.Empty(renewed);
    }

    [SkippableFact]
    public async Task ReleaseMany_AllowsReacquisitionByAnotherInstance()
    {
        var p = await MakeProviderAsync();
        await p.TryAcquireManyAsync(new[] { 0, 1 }, "inst-a", TimeSpan.FromMinutes(5));

        await p.ReleaseManyAsync(new[] { 0, 1 }, "inst-a");

        var acquired = await p.TryAcquireManyAsync(new[] { 0, 1 }, "inst-b", TimeSpan.FromMinutes(5));
        Assert.Equal(new[] { 0, 1 }, acquired.OrderBy(x => x).ToArray());
    }

    [SkippableFact]
    public async Task ReleaseMany_DoesNotReleaseAnotherInstancesLock()
    {
        var p = await MakeProviderAsync();
        await p.TryAcquireManyAsync(new[] { 0 }, "inst-a", TimeSpan.FromMinutes(5));

        // inst-b should not be able to release inst-a's lock
        await p.ReleaseManyAsync(new[] { 0 }, "inst-b");

        var acquired = await p.TryAcquireManyAsync(new[] { 0 }, "inst-b", TimeSpan.FromMinutes(5));
        Assert.Empty(acquired);
    }

    [SkippableFact]
    public async Task ReleaseMany_EmptyShards_IsNoOp()
    {
        var p = await MakeProviderAsync();

        await p.ReleaseManyAsync(Array.Empty<int>(), "inst-a"); // must not throw
    }

    [SkippableFact]
    public async Task TwoInstances_ConcurrentAcquire_EachOwnDistinctShards()
    {
        var p = await MakeProviderAsync();
        const int total = 10;
        var all = Enumerable.Range(0, total).ToArray();

        // Both instances race to claim all shards simultaneously
        var aTask = p.TryAcquireManyAsync(all, "inst-a", TimeSpan.FromMinutes(5));
        var bTask = p.TryAcquireManyAsync(all, "inst-b", TimeSpan.FromMinutes(5));
        await Task.WhenAll(aTask, bTask);

        var aShards = aTask.Result;
        var bShards = bTask.Result;

        // No shard owned by both
        Assert.Empty(aShards.Intersect(bShards));
        // All shards owned by someone
        Assert.Equal(total, aShards.Count + bShards.Count);
    }
}
