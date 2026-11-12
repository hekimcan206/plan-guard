using PlanGuard.Comparison;
using PlanGuard.Snapshots;

namespace PlanGuard.Lab;

/// <summary>
/// A rule that flags a query as regressed given its baseline and candidate capture. The lab scores several of
/// them on the same captures, so the comparison is apples to apples.
/// </summary>
public sealed record Detector(string Id, string Description, Func<QuerySnapshot, QuerySnapshot, bool> Flags);

public static class Detectors
{
    public static readonly IReadOnlyList<Detector> All =
    [
        new("time-1run-1.2x", "Execution time of a single EXPLAIN ANALYZE run grew > 1.2x (the naive check)",
            (b, c) => First(c) > First(b) * 1.2),
        new("time-median-1.2x", "Median execution time of 5 runs grew > 1.2x",
            (b, c) => c.MedianExecutionMs > b.MedianExecutionMs * 1.2),
        new("time-median-2x+5ms", "Median execution time grew > 2x and by >= 5 ms (plan-guard's time thresholds)",
            (b, c) => SnapshotComparer.IsRegression(b.MedianExecutionMs!.Value, c.MedianExecutionMs!.Value, 2.0, 5)),
        new("cost-1.5x+50", "Planner total cost grew > 1.5x and by >= 50",
            (b, c) => SnapshotComparer.IsRegression(b.TotalCost, c.TotalCost, 1.5, 50)),
        new("plan-shape", "Plan fingerprint changed (any operator, relation or index difference)",
            (b, c) => b.Fingerprint != c.Fingerprint),
        new("buffers-1.3x+64", "Median shared blocks (hit + read) grew > 1.3x and by >= 64 (plan-guard's primary signal)",
            (b, c) => SnapshotComparer.IsRegression(b.MedianSharedBlocks!.Value, c.MedianSharedBlocks!.Value, 1.3, 64)),
        new("plan-guard-default", "plan-guard's default policy returns FAIL (buffers or temp blocks; cost and time only warn)",
            (b, c) => SnapshotComparer.CompareQuery(b.Id, b, c, new Thresholds()).Verdict == Verdict.Fail),
    ];

    private static double First(QuerySnapshot q) => q.Runs[0].ExecutionMs;
}
