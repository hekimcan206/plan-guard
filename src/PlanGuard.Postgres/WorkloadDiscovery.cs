using Npgsql;
using PlanGuard.Plans;
using PlanGuard.Snapshots;
using PlanGuard.Sql;

namespace PlanGuard.Postgres;

public sealed record DiscoveryOptions
{
    /// <summary>Statements called fewer times than this during the workload are ignored.</summary>
    public long MinCalls { get; init; } = 1;

    /// <summary>At most this many statements, by total execution time.</summary>
    public int Limit { get; init; } = 200;

    public int StatementTimeoutMs { get; init; } = 30_000;
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string> { ["jit"] = "off" };
}

public sealed record DiscoveryResult(Snapshot Snapshot, IReadOnlyList<(string Sql, string Reason)> Skipped);

/// <summary>
/// Finds the statements an application's own test suite ran (from pg_stat_statements) and plans each with
/// <c>EXPLAIN (GENERIC_PLAN)</c>, PostgreSQL 16+. The normalised text keeps its <c>$n</c> placeholders, so no
/// parameter values are needed, and nothing is executed.
/// </summary>
public sealed class WorkloadDiscovery(NpgsqlDataSource dataSource)
{
    public const int MinimumServerVersionNum = 160000;

    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryOptions options, string label, string? gitSha = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (version, versionNum) = await Session.ServerVersionAsync(conn, ct);
        if (versionNum < MinimumServerVersionNum)
        {
            throw new NotSupportedException($"Workload discovery needs EXPLAIN (GENERIC_PLAN), PostgreSQL 16 or later; the server is {version}.");
        }

        // Read the workload before planning anything, so plan-guard's own EXPLAINs never show up in it.
        var statements = await ReadWorkloadAsync(conn, options, ct);
        await Session.ApplyAsync(conn, options.Settings, options.StatementTimeoutMs, ct);
        var settings = await Session.ReadSettingsAsync(conn, ct);

        var queries = new List<QuerySnapshot>();
        var skipped = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (queryId, sql, stats) in statements)
        {
            if (!SqlText.IsExplainable(sql))
            {
                skipped.Add((sql, "not a plannable statement"));
                continue;
            }

            var id = SqlText.StatementId(sql);
            if (!seen.Add(id))
            {
                // Same text under two query ids (e.g. two roles, or a dropped and recreated table): keep the first.
                continue;
            }

            try
            {
                var statement = SqlText.EnsureSingleStatement(sql);
                var plan = await GenericPlanAsync(conn, statement, ct);
                if (!TouchesUserTables(plan.Root))
                {
                    // Driver bootstrap (type loading), SELECT version(), current_setting(...): not the application's queries.
                    skipped.Add((sql, "touches no user tables"));
                    continue;
                }

                queries.Add(new QuerySnapshot
                {
                    Id = id,
                    Source = QuerySource.Discovered,
                    Sql = sql,
                    QueryId = queryId,
                    Fingerprint = PlanShape.Fingerprint(plan.Root),
                    Plan = plan.Root,
                    Statements = stats,
                });
            }
            catch (PostgresException ex)
            {
                // Typical causes: temp tables that no longer exist, parameters whose type cannot be inferred.
                skipped.Add((sql, $"{ex.SqlState}: {ex.MessageText}"));
            }
            catch (FormatException ex)
            {
                skipped.Add((sql, ex.Message));
            }
        }

        var snapshot = new Snapshot
        {
            Meta = new SnapshotMeta
            {
                Label = label,
                CapturedAt = DateTimeOffset.UtcNow,
                ServerVersion = version,
                GitSha = gitSha,
                StatementStatsAvailable = true,
                Settings = settings,
            },
            Queries = queries,
        };
        return new DiscoveryResult(snapshot, skipped);
    }

    private static async Task<List<(long QueryId, string Sql, StatementStats Stats)>> ReadWorkloadAsync(NpgsqlConnection conn, DiscoveryOptions options, CancellationToken ct)
    {
        const string sql = """
            SELECT s.queryid, s.query, s.calls, s.total_exec_time, s.rows,
                   s.shared_blks_hit, s.shared_blks_read, s.temp_blks_read, s.temp_blks_written
            FROM pg_stat_statements s
            JOIN pg_database d ON d.oid = s.dbid
            WHERE d.datname = current_database()
              AND s.toplevel
              AND s.calls >= $1
              AND s.query !~* '\m(pg_stat_statements|pg_catalog|information_schema)\M'
              AND s.query !~* '^\s*explain\M'
            ORDER BY s.total_exec_time DESC
            LIMIT $2
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(options.MinCalls);
        cmd.Parameters.AddWithValue(options.Limit);
        var list = new List<(long, string, StatementStats)>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var calls = reader.GetInt64(2);
                var total = reader.GetDouble(3);
                list.Add((reader.GetInt64(0), reader.GetString(1), new StatementStats(
                    calls, total, calls == 0 ? 0 : total / calls, reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8))));
            }
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.ObjectNotInPrerequisiteState)
        {
            throw new InvalidOperationException(
                "pg_stat_statements is not available: add it to shared_preload_libraries and run CREATE EXTENSION pg_stat_statements.", ex);
        }

        return list;
    }

    internal static bool TouchesUserTables(PlanNode root) =>
        root.Descendants().Any(n => n.RelationName is not null && n.Schema is not ("pg_catalog" or "information_schema"));

    /// <summary>
    /// EXPLAIN (GENERIC_PLAN) through a temporary PL/pgSQL function. Sent directly, Npgsql's extended protocol
    /// makes the server treat the statement's $n placeholders as parameters of the EXPLAIN itself: with no values
    /// bound it refuses the statement, and with NULLs bound the planner folds them into constants (a one-time
    /// "false" filter instead of the real plan). Dynamic EXECUTE inside the function has no parameters, which is
    /// exactly the situation GENERIC_PLAN is designed for. The statement travels as a bound text value and the
    /// function lives in pg_temp inside a transaction that is rolled back, so nothing is left behind.
    /// </summary>
    internal static async Task<ExplainResult> GenericPlanAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var create = new NpgsqlCommand("""
                CREATE FUNCTION pg_temp.plan_guard_generic_plan(statement text) RETURNS text
                LANGUAGE plpgsql AS $fn$
                DECLARE result text;
                BEGIN
                    EXECUTE 'EXPLAIN (GENERIC_PLAN, VERBOSE, FORMAT JSON) ' || statement INTO result;
                    RETURN result;
                END
                $fn$
                """, conn, tx))
            {
                await create.ExecuteNonQueryAsync(ct);
            }

            await using var explain = new NpgsqlCommand("SELECT pg_temp.plan_guard_generic_plan($1)", conn, tx);
            explain.Parameters.AddWithValue(sql);
            var json = (string?)await explain.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("EXPLAIN returned no rows.");
            return ExplainJsonParser.Parse(json);
        }
        finally
        {
            if (tx.Connection is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
        }
    }
}
