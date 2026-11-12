using PlanGuard.Plans;

namespace PlanGuard.Snapshots;

/// <summary>Where a query in a snapshot came from.</summary>
public enum QuerySource
{
    /// <summary>A named query from the catalog, executed with EXPLAIN ANALYZE.</summary>
    Catalog,

    /// <summary>A normalised statement found in pg_stat_statements, planned with EXPLAIN (GENERIC_PLAN).</summary>
    Discovered,
}

/// <summary>Everything plan-guard recorded about a workload at one point in time (one commit, one database).</summary>
public sealed record Snapshot
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required SnapshotMeta Meta { get; init; }
    public required IReadOnlyList<QuerySnapshot> Queries { get; init; }

    public QuerySnapshot? Find(string id) => Queries.FirstOrDefault(q => q.Id == id);
}

public sealed record SnapshotMeta
{
    public required string Label { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required string ServerVersion { get; init; }
    public string? GitSha { get; init; }
    public int Runs { get; init; }
    public int Warmups { get; init; }
    public bool StatementStatsAvailable { get; init; }

    /// <summary>Session settings applied before capturing (jit, work_mem, ...), recorded so a diff can be explained.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

/// <summary>One query's plan and measurements.</summary>
public sealed record QuerySnapshot
{
    /// <summary>Stable identity across captures: the catalog name, or a hash of the normalised text for discovered statements.</summary>
    public required string Id { get; init; }
    public required QuerySource Source { get; init; }
    public required string Sql { get; init; }

    /// <summary>
    /// PostgreSQL's query identifier. Informational only: it is computed from object OIDs, so it differs between
    /// two databases built from the same migrations and cannot be used to match queries across captures.
    /// </summary>
    public long? QueryId { get; init; }

    public required string Fingerprint { get; init; }
    public required PlanNode Plan { get; init; }

    /// <summary>False when the measured runs did not all produce the same plan shape.</summary>
    public bool PlanStable { get; init; } = true;

    public IReadOnlyList<RunSample> Runs { get; init; } = [];
    public StatementStats? Statements { get; init; }

    /// <summary>Set when the query could not be planned or executed; such entries carry an empty plan.</summary>
    public string? Error { get; init; }

    public double TotalCost => Plan.TotalCost;
    public double? MedianExecutionMs => Runs.Count == 0 ? null : Stats.Median(Runs.Select(r => r.ExecutionMs));
    public double? MedianSharedBlocks => Runs.Count == 0 ? null : Stats.Median(Runs.Select(r => (double)r.Buffers.SharedTotal));
    public double? MedianTempBlocks => Runs.Count == 0 ? null : Stats.Median(Runs.Select(r => (double)r.Buffers.TempTotal));
    public double? MedianActualRows => Runs.Count == 0 ? null : Stats.Median(Runs.Select(r => r.ActualRows));

    /// <summary>Shared blocks per call from pg_stat_statements, for discovered statements that have no measured runs.</summary>
    public double? SharedBlocksPerCall => Statements is { Calls: > 0 } s ? (double)(s.SharedBlksHit + s.SharedBlksRead) / s.Calls : null;
}

/// <summary>One measured EXPLAIN ANALYZE execution.</summary>
public sealed record RunSample(double ExecutionMs, double PlanningMs, BufferCounts Buffers, double ActualRows, string Fingerprint);

/// <summary>pg_stat_statements counters for the statement (deltas over the capture for catalog queries).</summary>
public sealed record StatementStats(
    long Calls,
    double TotalExecMs,
    double MeanExecMs,
    long Rows,
    long SharedBlksHit,
    long SharedBlksRead,
    long TempBlksRead,
    long TempBlksWritten);
