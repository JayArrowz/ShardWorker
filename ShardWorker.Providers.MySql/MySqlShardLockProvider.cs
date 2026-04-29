using MySqlConnector;
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;
using System.Text;

namespace ShardWorker.Providers.MySql;

public sealed class MySqlShardLockProvider : IShardLockProvider, IShardLockQueryProvider
{
    private readonly string _connectionString;
    private readonly string _table;

    public MySqlShardLockProvider(string connectionString, string tableName = "__shard_locks")
    {
        _connectionString = connectionString;
        _table = tableName;
    }

    /// <inheritdoc/>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS `{_table}` (
                shard_index  INT          NOT NULL PRIMARY KEY,
                instance_id  VARCHAR(256) NOT NULL,
                expires_at   DATETIME(6)  NOT NULL
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

        var expiresAt = DateTime.UtcNow.Add(expiry);

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var insertCmd = conn.CreateCommand())
        {
            var sb = new StringBuilder();
            sb.Append($"INSERT INTO `{_table}` (shard_index, instance_id, expires_at) VALUES ");
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"(@s{i}, @iid, @exp)");
                insertCmd.Parameters.AddWithValue($"@s{i}", candidates[i]);
            }
            sb.Append($"""
                 ON DUPLICATE KEY UPDATE
                     instance_id = IF(expires_at < UTC_TIMESTAMP(6) OR instance_id = VALUES(instance_id), VALUES(instance_id), instance_id),
                     expires_at  = IF(expires_at < UTC_TIMESTAMP(6) OR instance_id = VALUES(instance_id), VALUES(expires_at),  expires_at);
                """);
            insertCmd.CommandText = sb.ToString();
            insertCmd.Parameters.AddWithValue("@iid", instanceId);
            insertCmd.Parameters.AddWithValue("@exp", expiresAt);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        return await SelectOwnedAsync(conn, candidates, instanceId, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> RenewManyAsync(
        IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default)
    {
        if (held.Count == 0)
            return Array.Empty<int>();

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var updateCmd = conn.CreateCommand())
        {
            var inClause = BuildInClause(held, updateCmd);
            updateCmd.CommandText = $"""
                UPDATE `{_table}`
                SET expires_at = @exp
                WHERE shard_index IN ({inClause}) AND instance_id = @iid;
                """;
            updateCmd.Parameters.AddWithValue("@iid", instanceId);
            updateCmd.Parameters.AddWithValue("@exp", DateTime.UtcNow.Add(expiry));
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        return await SelectOwnedAsync(conn, held, instanceId, ct);
    }

    /// <inheritdoc/>
    public async Task ReleaseManyAsync(
        IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default)
    {
        if (shards.Count == 0)
            return;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var inClause = BuildInClause(shards, cmd);
        cmd.CommandText = $"""
            DELETE FROM `{_table}`
            WHERE shard_index IN ({inClause}) AND instance_id = @iid;
            """;
        cmd.Parameters.AddWithValue("@iid", instanceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<int>> SelectOwnedAsync(
        MySqlConnection conn, IReadOnlyList<int> shards, string instanceId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var inClause = BuildInClause(shards, cmd);
        cmd.CommandText = $"SELECT shard_index FROM `{_table}` WHERE shard_index IN ({inClause}) AND instance_id = @iid;";
        cmd.Parameters.AddWithValue("@iid", instanceId);
        var result = new List<int>(shards.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetInt32(0));
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShardLockRow>> GetAllLocksAsync(CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT shard_index, instance_id, expires_at FROM `{_table}` WHERE expires_at > UTC_TIMESTAMP(6) ORDER BY shard_index;";
        var result = new List<ShardLockRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ShardLockRow(reader.GetInt32(0), reader.GetString(1), new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero)));
        return result;
    }

    private static string BuildInClause(IReadOnlyList<int> values, MySqlCommand cmd)
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
