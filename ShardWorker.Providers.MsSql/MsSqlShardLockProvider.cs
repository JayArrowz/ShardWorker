using Microsoft.Data.SqlClient;
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System.Text;

namespace ShardWorker.Providers.MsSql;

public sealed class MsSqlShardLockProvider : IShardLockProvider, IShardLockQueryProvider
{
    private readonly string _connectionString;
    private readonly string _table;

    public MsSqlShardLockProvider(string connectionString, string tableName = "__shard_locks")
    {
        _connectionString = connectionString;
        _table = tableName;
    }

    /// <inheritdoc/>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF OBJECT_ID(N'[{_table}]', N'U') IS NULL
                CREATE TABLE [{_table}] (
                    shard_index  INT             NOT NULL PRIMARY KEY,
                    instance_id  NVARCHAR(256)   NOT NULL,
                    expires_at   DATETIMEOFFSET  NOT NULL
                );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> TryAcquireManyAsync(
        IReadOnlyList<int> candidates, string instanceId, TimeSpan expiry, CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return Array.Empty<int>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var sb = new StringBuilder();
        sb.Append($"MERGE [{_table}] WITH (HOLDLOCK) AS target USING (VALUES ");
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"(@s{i})");
            cmd.Parameters.AddWithValue($"@s{i}", candidates[i]);
        }
        sb.Append("""
            ) AS source(shard_index)
            ON target.shard_index = source.shard_index
            WHEN MATCHED AND (target.expires_at < SYSDATETIMEOFFSET() OR target.instance_id = @iid) THEN
                UPDATE SET instance_id = @iid,
                           expires_at  = DATEADD(second, @secs, SYSDATETIMEOFFSET())
            WHEN NOT MATCHED THEN
                INSERT (shard_index, instance_id, expires_at)
                VALUES (source.shard_index, @iid, DATEADD(second, @secs, SYSDATETIMEOFFSET()))
            OUTPUT inserted.shard_index;
            """);

        cmd.CommandText = sb.ToString();
        cmd.Parameters.AddWithValue("@iid", instanceId);
        cmd.Parameters.AddWithValue("@secs", (int)expiry.TotalSeconds);

        var result = new List<int>(candidates.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetInt32(0));
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> RenewManyAsync(
        IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default)
    {
        if (held.Count == 0)
            return Array.Empty<int>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var inClause = BuildInClause(held, cmd);
        cmd.CommandText = $"""
            UPDATE [{_table}]
            SET expires_at = DATEADD(second, @secs, SYSDATETIMEOFFSET())
            OUTPUT inserted.shard_index
            WHERE shard_index IN ({inClause}) AND instance_id = @iid;
            """;
        cmd.Parameters.AddWithValue("@iid", instanceId);
        cmd.Parameters.AddWithValue("@secs", (int)expiry.TotalSeconds);

        var result = new List<int>(held.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetInt32(0));
        return result;
    }

    /// <inheritdoc/>
    public async Task ReleaseManyAsync(
        IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default)
    {
        if (shards.Count == 0)
            return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var inClause = BuildInClause(shards, cmd);
        cmd.CommandText = $"""
            DELETE FROM [{_table}]
            WHERE shard_index IN ({inClause}) AND instance_id = @iid;
            """;
        cmd.Parameters.AddWithValue("@iid", instanceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShardLockRow>> GetAllLocksAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT shard_index, instance_id, expires_at FROM [{_table}] WHERE expires_at > SYSDATETIMEOFFSET() ORDER BY shard_index;";
        var result = new List<ShardLockRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ShardLockRow(reader.GetInt32(0), reader.GetString(1), reader.GetDateTimeOffset(2)));
        return result;
    }

    private static string BuildInClause(IReadOnlyList<int> values, SqlCommand cmd)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var pname = $"@s{i}";
            sb.Append(pname);
            cmd.Parameters.AddWithValue(pname, values[i]);
        }
        return sb.ToString();
    }
}
