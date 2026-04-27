using Npgsql;
using ShardWorker.Core.Interface;
using ShardWorker.Providers.Postgres;
using Xunit;

namespace ShardWorker.IntegrationTests.Providers;

/// <summary>
/// Runs the provider contract suite against a real PostgreSQL instance.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Provider", "Postgres")]
public sealed class PostgresProviderTests : ProviderContractTests
{
    private static readonly string? ConnStr =
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");

    protected override void CheckAvailability() =>
        Skip.If(ConnStr is null, "Set POSTGRES_CONNECTION_STRING to run Postgres tests.");

    protected override IShardLockProvider CreateProvider(string tableName)
        => new PostgresShardLockProvider(ConnStr!, tableName);

    protected override async Task DropTableAsync(string tableName, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConnStr!);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS {tableName};";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
