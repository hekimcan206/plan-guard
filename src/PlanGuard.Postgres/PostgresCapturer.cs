using System.Diagnostics;
using Npgsql;
using PlanGuard.Configuration;
using PlanGuard.Plans;
using PlanGuard.Snapshots;

namespace PlanGuard.Postgres;

public sealed record CaptureOptions
{
    public int Runs { get; init; } = 5;
    public int Warmups { get; init; } = 1;
    public int StatementTimeoutMs { get; init; } = 30_000;
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string> { ["jit"] = "off" };

    public static CaptureOptions From(PlanGuardConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new CaptureOptions { Runs = config.Runs, Warmups = config.Warmups, StatementTimeoutMs = config.StatementTimeoutMs, Settings = config.Settings };
    }
}

/// <summary>
/// Runs every catalog query with <c>EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS, FORMAT JSON)</c> inside a
/// transaction that is always rolled back, so INSERT/UPDATE/DELETE queries can be measured without changing data.
/// </summary>
public sealed class PostgresCapturer(NpgsqlDataSource dataSource)
{
    public async Task<Snapshot> CaptureAsync(IReadOnlyList<QueryDefinition> queries, CaptureOptions options, string label, string? gitSha = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(options);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (version, _) = await Session.ServerVersionAsync(conn, ct);
        await Session.ApplyAsync(conn, options.Settings, options.StatementTimeoutMs, ct);
        var statsAvailable = await Session.EnableStatementTrackingAsync(conn, ct);
        var settings = await Session.ReadSettingsAsync(conn, ct);

        var results = new List<QuerySnapshot>(queries.Count);
        foreach (var query in queries)
        {
            results.Add(await CaptureQueryAsync(conn, query, options, statsAvailable, ct));
        }

        return new Snapshot
        {
            Meta = new SnapshotMeta
            {
                Label = label,
                CapturedAt = DateTimeOffset.UtcNow,
                ServerVersion = version,
                GitSha = gitSha,
                Runs = options.Runs,
                Warmups = options.Warmups,
                StatementStatsAvailable = statsAvailable,
                Settings = settings,
            },
            Queries = results,
        };
    }

    private static async Task<QuerySnapshot> CaptureQueryAsync(NpgsqlConnection conn, QueryDefinition query, CaptureOptions options, bool statsAvailable, CancellationToken ct)
    {
        try
        {
            // A plain EXPLAIN first: it yields the query identifier (to read pg_stat_statements before the measured
            // runs) and fails fast on a query that does not plan, without executing anything.
            var plain = await ExplainAsync(conn, "EXPLAIN (VERBOSE, FORMAT JSON) ", query.Sql, ct);
            for (var i = 0; i < options.Warmups; i++)
            {
                await ExplainAsync(conn, AnalyzePrefix, query.Sql, ct);
            }

            var before = statsAvailable && plain.QueryId is { } qid ? await StatementStatsReader.ReadAsync(conn, qid, ct) : null;
            var runs = new List<(ExplainResult Result, RunSample Sample)>(options.Runs);
            for (var i = 0; i < options.Runs; i++)
            {
                var result = await ExplainAsync(conn, AnalyzePrefix, query.Sql, ct);
                runs.Add((result, ToSample(result)));
            }

            var after = statsAvailable && plain.QueryId is { } qid2 ? await StatementStatsReader.ReadAsync(conn, qid2, ct) : null;

            // The plan of the median-time run represents the query; all runs normally share one shape anyway.
            var ordered = runs.OrderBy(r => r.Sample.ExecutionMs).ToList();
            var representative = ordered[ordered.Count / 2].Result;
            return new QuerySnapshot
            {
                Id = query.Id,
                Source = QuerySource.Catalog,
                Sql = query.Sql,
                QueryId = representative.QueryId ?? plain.QueryId,
                Fingerprint = PlanShape.Fingerprint(representative.Root),
                Plan = representative.Root,
                PlanStable = runs.Select(r => r.Sample.Fingerprint).Distinct(StringComparer.Ordinal).Count() == 1,
                Runs = [.. runs.Select(r => r.Sample)],
                Statements = StatementStatsReader.Delta(before, after),
            };
        }
        catch (PostgresException ex)
        {
            return Failed(query, $"{ex.SqlState}: {ex.MessageText}");
        }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException)
        {
            return Failed(query, "client timeout: " + ex.Message);
        }
    }

    private const string AnalyzePrefix = "EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS, FORMAT JSON) ";

    private static QuerySnapshot Failed(QueryDefinition query, string error) => new()
    {
        Id = query.Id,
        Source = QuerySource.Catalog,
        Sql = query.Sql,
        Fingerprint = string.Empty,
        Plan = new PlanNode { NodeType = "Error" },
        Error = error,
    };

    internal static RunSample ToSample(ExplainResult r)
    {
        Debug.Assert(r.IsAnalyzed, "Measured runs come from EXPLAIN ANALYZE.");
        return new RunSample(
            r.ExecutionTimeMs ?? 0,
            r.PlanningTimeMs ?? 0,
            r.Root.Buffers ?? BufferCounts.Zero,
            r.Root.ActualRows ?? 0,
            PlanShape.Fingerprint(r.Root));
    }

    /// <summary>Runs one EXPLAIN in its own transaction and always rolls it back.</summary>
    internal static async Task<ExplainResult> ExplainAsync(NpgsqlConnection conn, string prefix, string sql, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // The statement text is the catalog file itself (validated as a single statement); it cannot be
            // parameterised because it is the thing being explained.
#pragma warning disable CA2100
            await using var cmd = new NpgsqlCommand(prefix + sql, conn, tx);
#pragma warning restore CA2100
            var json = (string?)await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("EXPLAIN returned no rows.");
            return ExplainJsonParser.Parse(json);
        }
        finally
        {
            // After a protocol-level failure the connection is already broken and the transaction gone.
            if (tx.Connection is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
        }
    }
}
