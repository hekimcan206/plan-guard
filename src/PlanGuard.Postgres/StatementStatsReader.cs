using Npgsql;
using PlanGuard.Snapshots;

namespace PlanGuard.Postgres;

internal static class StatementStatsReader
{
    /// <summary>Sums the pg_stat_statements rows of one query id in the current database (top-level and nested rows).</summary>
    public static async Task<StatementStats?> ReadAsync(NpgsqlConnection conn, long queryId, CancellationToken ct)
    {
        const string sql = """
            SELECT coalesce(sum(calls), 0)::bigint, coalesce(sum(total_exec_time), 0), coalesce(sum(rows), 0)::bigint,
                   coalesce(sum(shared_blks_hit), 0)::bigint, coalesce(sum(shared_blks_read), 0)::bigint,
                   coalesce(sum(temp_blks_read), 0)::bigint, coalesce(sum(temp_blks_written), 0)::bigint
            FROM pg_stat_statements s
            JOIN pg_database d ON d.oid = s.dbid
            WHERE d.datname = current_database() AND s.queryid = $1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(queryId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var calls = reader.GetInt64(0);
        var total = reader.GetDouble(1);
        return new StatementStats(calls, total, calls == 0 ? 0 : total / calls, reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
    }

    public static StatementStats? Delta(StatementStats? before, StatementStats? after)
    {
        if (after is null)
        {
            return null;
        }

        if (before is null)
        {
            return after;
        }

        var calls = after.Calls - before.Calls;
        var total = after.TotalExecMs - before.TotalExecMs;
        return new StatementStats(calls, total, calls <= 0 ? 0 : total / calls, after.Rows - before.Rows,
            after.SharedBlksHit - before.SharedBlksHit, after.SharedBlksRead - before.SharedBlksRead,
            after.TempBlksRead - before.TempBlksRead, after.TempBlksWritten - before.TempBlksWritten);
    }
}
