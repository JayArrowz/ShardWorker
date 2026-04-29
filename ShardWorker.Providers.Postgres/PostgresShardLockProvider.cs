using Npgsql;
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;

namespace ShardWorker.Providers.Postgres;

public sealed class PostgresShardLockProvider : IShardLockProvider, IShardLockQueryProvider
{
    private readonly string _connectionString;
    private readonly string _table;

    public PostgresShardLockProvider(string connectionString, string tableName = "__shard_locks")
    {
        _connectionString = connectionString;
        _table = tableName;
    }

    /// <inheritdoc/>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_table} (
                shard_index  INTEGER      NOT NULL PRIMARY KEY,
                instance_id  TEXT         NOT NULL,
                expires_at   TIMESTAMPTZ  NOT NULL
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

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            WITH upserted AS (
                INSERT INTO {_table} (shard_index, instance_id, expires_at)
                SELECT unnest(@candidates), @instance_id, NOW() + @expiry
                ON CONFLICT (shard_index) DO UPDATE
                    SET instance_id = EXCLUDED.instance_id,
                        expires_at  = EXCLUDED.expires_at
                    WHERE {_table}.expires_at < NOW()
                       OR {_table}.instance_id = EXCLUDED.instance_id
                RETURNING shard_index
            )
            SELECT shard_index FROM upserted;
            """;

        cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "@candidates", Value = ToIntArray(candidates) });
        cmd.Parameters.AddWithValue("@instance_id", instanceId);
        cmd.Parameters.AddWithValue("@expiry", expiry);

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

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            UPDATE {_table}
            SET expires_at = NOW() + @expiry
            WHERE shard_index = ANY(@shards) AND instance_id = @instance_id
            RETURNING shard_index;
            """;

        cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "@shards", Value = ToIntArray(held) });
        cmd.Parameters.AddWithValue("@instance_id", instanceId);
        cmd.Parameters.AddWithValue("@expiry", expiry);

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

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            DELETE FROM {_table}
            WHERE shard_index = ANY(@shards) AND instance_id = @instance_id;
            """;

        cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "@shards", Value = ToIntArray(shards) });
        cmd.Parameters.AddWithValue("@instance_id", instanceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShardLockRow>> GetAllLocksAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT shard_index, instance_id, expires_at FROM {_table} WHERE expires_at > NOW() ORDER BY shard_index;";
        var result = new List<ShardLockRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ShardLockRow(reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return result;
    }

    private static int[] ToIntArray(IReadOnlyList<int> list)
    {
        if (list is int[] arr) return arr;
        var result = new int[list.Count];
        for (int i = 0; i < list.Count; i++) result[i] = list[i];
        return result;
    }
}