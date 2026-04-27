using MySqlConnector;
using ShardWorker.Core.Interface;
using ShardWorker.Providers.MySql;
using Xunit;

namespace ShardWorker.IntegrationTests.Providers;

/// <summary>
/// Runs the provider contract suite against a real MySQL / MariaDB instance.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Provider", "MySql")]
public sealed class MySqlProviderTests : ProviderContractTests
{
    private static readonly string? ConnStr =
        Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");

    protected override void CheckAvailability() =>
        Skip.If(ConnStr is null, "Set MYSQL_CONNECTION_STRING to run MySQL tests.");

    protected override IShardLockProvider CreateProvider(string tableName)
        => new MySqlShardLockProvider(ConnStr!, tableName);

    protected override async Task DropTableAsync(string tableName, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(ConnStr!);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS `{tableName}`;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
