using System.Globalization;
using Npgsql;

namespace PlanGuard.Postgres;

/// <summary>Session preparation shared by capture and discovery.</summary>
internal static class Session
{
    /// <summary>Settings recorded in every snapshot so that two captures can be checked for comparability.</summary>
    internal static readonly string[] RecordedSettings =
        ["work_mem", "jit", "random_page_cost", "effective_cache_size", "max_parallel_workers_per_gather", "plan_cache_mode", "shared_buffers", "default_statistics_target"];

    public static async Task ApplyAsync(NpgsqlConnection conn, IReadOnlyDictionary<string, string> settings, int statementTimeoutMs, CancellationToken ct)
    {
        // set_config with bound parameters: the names come from a config file, but nothing is spliced into SQL text.
        foreach (var (name, value) in settings.Append(new("statement_timeout", statementTimeoutMs.ToString(CultureInfo.InvariantCulture))))
        {
            await using var cmd = new NpgsqlCommand("SELECT set_config($1, $2, false)", conn);
            cmd.Parameters.AddWithValue(name);
            cmd.Parameters.AddWithValue(value);
            await cmd.ExecuteScalarAsync(ct);
        }
    }

    public static async Task<Dictionary<string, string>> ReadSettingsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT name, current_setting(name) FROM unnest($1::text[]) AS name", conn);
        cmd.Parameters.AddWithValue(RecordedSettings);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public static async Task<(string Version, int VersionNum)> ServerVersionAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_setting('server_version'), current_setting('server_version_num')::int", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetString(0), reader.GetInt32(1));
    }

    /// <summary>
    /// True when pg_stat_statements is installed in this database AND this session may track nested statements.
    /// EXPLAIN ANALYZE runs the query as a nested statement, so without <c>track = all</c> it would not be counted.
    /// </summary>
    public static async Task<bool> EnableStatementTrackingAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements')", conn))
        {
            if (await cmd.ExecuteScalarAsync(ct) is not true)
            {
                return false;
            }
        }

        try
        {
            await using var set = new NpgsqlCommand("SET pg_stat_statements.track = 'all'", conn);
            await set.ExecuteNonQueryAsync(ct);
            await using var probe = new NpgsqlCommand("SELECT count(*) FROM pg_stat_statements LIMIT 1", conn);
            await probe.ExecuteScalarAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ObjectNotInPrerequisiteState or PostgresErrorCodes.UndefinedObject)
        {
            // Not superuser / no SET privilege on the parameter, or the library is not preloaded: capture still works,
            // only the pg_stat_statements columns stay empty.
            return false;
        }
    }
}
