using System.Text.Json.Serialization;
using PlanGuard.Plans;
using PlanGuard.Snapshots;

namespace PlanGuard.Comparison;

[JsonConverter(typeof(JsonStringEnumConverter<Signal>))]
public enum Signal
{
    Buffers,
    TempIo,
    Cost,
    Time,
    PlanShape,
    PlanStability,
    Rows,
    Coverage,
    Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingLevel>))]
public enum FindingLevel
{
    Info,
    Improvement,
    Warn,
    Fail,
}

/// <summary>Overall outcome for one query, ordered by how much attention it needs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict
{
    Ignored,
    Pass,
    Improved,
    Changed,
    New,
    Warn,
    Fail,
}

public sealed record Finding(Signal Signal, FindingLevel Level, string Message, double? Baseline = null, double? Candidate = null)
{
    public double? Ratio => Baseline is > 0 && Candidate is { } c ? c / Baseline.Value : null;
}

public sealed record QueryComparison
{
    public required string Id { get; init; }
    public required Verdict Verdict { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public QuerySnapshot? Baseline { get; init; }
    public QuerySnapshot? Candidate { get; init; }
    public PlanDiff? Diff { get; init; }

    public bool PlanChanged => Baseline is not null && Candidate is not null && Baseline.Fingerprint != Candidate.Fingerprint;
}

public sealed record ComparisonReport
{
    public required SnapshotMeta Baseline { get; init; }
    public required SnapshotMeta Candidate { get; init; }
    public required Thresholds Thresholds { get; init; }
    public required IReadOnlyList<QueryComparison> Queries { get; init; }

    /// <summary>Differences between the two capture environments that make the comparison less trustworthy.</summary>
    public IReadOnlyList<string> EnvironmentWarnings { get; init; } = [];

    public int Count(Verdict verdict) => Queries.Count(q => q.Verdict == verdict);

    public Verdict Overall => Queries.Count == 0 ? Verdict.Pass : Queries.Max(q => q.Verdict) switch
    {
        Verdict.Fail => Verdict.Fail,
        Verdict.Warn => Verdict.Warn,
        _ => Verdict.Pass,
    };

    /// <summary>Process exit code: 1 when the build should fail, 0 otherwise.</summary>
    public int ExitCode(FailOn failOn) => Overall switch
    {
        Verdict.Fail => 1,
        Verdict.Warn when failOn == FailOn.Warn => 1,
        _ => 0,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<FailOn>))]
public enum FailOn
{
    Fail,
    Warn,
}
