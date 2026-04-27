using Microsoft.Data.SqlClient;
using ShardWorker.Core.Interface;
using ShardWorker.Providers.MsSql;
using Xunit;

namespace ShardWorker.IntegrationTests.Providers;

/// <summary>
/// Runs the provider contract suite against a real SQL Server instance.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Provider", "MsSql")]
public sealed class MsSqlProviderTests : ProviderContractTests
{
    private static readonly string? ConnStr =
        Environment.GetEnvironmentVariable("MSSQL_CONNECTION_STRING");

    protected override void CheckAvailability() =>
        Skip.If(ConnStr is null, "Set MSSQL_CONNECTION_STRING to run SQL Server tests.");

    protected override IShardLockProvider CreateProvider(string tableName)
        => new MsSqlShardLockProvider(ConnStr!, tableName);

    protected override async Task DropTableAsync(string tableName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(ConnStr!);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF OBJECT_ID(N'[{tableName}]', N'U') IS NOT NULL DROP TABLE [{tableName}];";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
