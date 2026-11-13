using Npgsql;
using NpgsqlTypes;
using PlanGuard.Snapshots;

namespace PlanGuard.Postgres;

public sealed record StoredSnapshot(long Id, string Label, string? GitSha, DateTimeOffset CapturedAt, string ServerVersion, int QueryCount);

/// <summary>
/// Snapshot history in a PostgreSQL schema (normally a small shared database, not the database under test).
/// It lets a pull request compare against the latest <c>main</c> capture without passing artifacts between
/// workflows, and keeps a trend of plans over time.
/// </summary>
public sealed class PostgresSnapshotStore(NpgsqlDataSource dataSource, string schema = "plan_guard")
{
    private readonly string _schema = ValidIdentifier(schema);

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        // Identifiers cannot be bound; the schema name is validated against a strict pattern in the constructor.
#pragma warning disable CA2100
        await using var cmd = dataSource.CreateCommand($$"""
            CREATE SCHEMA IF NOT EXISTS {{_schema}};
            CREATE TABLE IF NOT EXISTS {{_schema}}.snapshots (
                id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                label          text        NOT NULL CHECK (length(label) BETWEEN 1 AND 200),
                git_sha        text        CHECK (git_sha ~ '^[0-9a-f]{7,64}$'),
                captured_at    timestamptz NOT NULL,
                stored_at      timestamptz NOT NULL DEFAULT now(),
                server_version text        NOT NULL,
                query_count    int         NOT NULL,
                body           jsonb       NOT NULL
            );
            CREATE INDEX IF NOT EXISTS snapshots_label_captured_idx ON {{_schema}}.snapshots (label, captured_at DESC);
            """);
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> SaveAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
#pragma warning disable CA2100
        await using var cmd = dataSource.CreateCommand(
            $"INSERT INTO {_schema}.snapshots (label, git_sha, captured_at, server_version, query_count, body) VALUES ($1, $2, $3, $4, $5, $6) RETURNING id");
#pragma warning restore CA2100
        cmd.Parameters.AddWithValue(snapshot.Meta.Label);
        cmd.Parameters.AddWithValue((object?)snapshot.Meta.GitSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue(snapshot.Meta.CapturedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue(snapshot.Meta.ServerVersion);
        cmd.Parameters.AddWithValue(snapshot.Queries.Count);
        cmd.Parameters.Add(new NpgsqlParameter { Value = SnapshotJson.Serialize(snapshot), NpgsqlDbType = NpgsqlDbType.Jsonb });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The most recent snapshot with this label (by capture time), or null.</summary>
    public async Task<Snapshot?> LatestAsync(string label, CancellationToken ct = default)
    {
#pragma warning disable CA2100
        await using var cmd = dataSource.CreateCommand(
            $"SELECT body::text FROM {_schema}.snapshots WHERE label = $1 ORDER BY captured_at DESC, id DESC LIMIT 1");
#pragma warning restore CA2100
        cmd.Parameters.AddWithValue(label);
        return await cmd.ExecuteScalarAsync(ct) is string json ? SnapshotJson.Deserialize(json) : null;
    }

    public async Task<IReadOnlyList<StoredSnapshot>> ListAsync(string? label = null, int limit = 50, CancellationToken ct = default)
    {
#pragma warning disable CA2100
        await using var cmd = dataSource.CreateCommand($$"""
            SELECT id, label, git_sha, captured_at, server_version, query_count
            FROM {{_schema}}.snapshots
            WHERE $1::text IS NULL OR label = $1
            ORDER BY captured_at DESC, id DESC
            LIMIT $2
            """);
#pragma warning restore CA2100
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)label ?? DBNull.Value);
        cmd.Parameters.AddWithValue(limit);
        var list = new List<StoredSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new StoredSnapshot(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), reader.GetInt32(5)));
        }

        return list;
    }

    private static string ValidIdentifier(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_][a-z0-9_]{0,62}$")
            ? name
            : throw new ArgumentException($"'{name}' is not a plain lower-case schema name.", nameof(name));
}
