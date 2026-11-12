using System.Globalization;
using PlanGuard.Plans;
using PlanGuard.Snapshots;

namespace PlanGuard.Comparison;

/// <summary>Compares a candidate snapshot (the change under review) against a baseline (the target branch).</summary>
public static class SnapshotComparer
{
    /// <summary>Settings whose difference between captures changes plans or measurements.</summary>
    private static readonly string[] SensitiveSettings =
        ["work_mem", "jit", "random_page_cost", "effective_cache_size", "max_parallel_workers_per_gather", "enable_seqscan", "enable_indexscan", "enable_bitmapscan", "enable_hashjoin", "enable_nestloop", "enable_mergejoin", "plan_cache_mode"];

    public static ComparisonReport Compare(Snapshot baseline, Snapshot candidate, Thresholds? thresholds = null, IReadOnlySet<string>? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        thresholds ??= new Thresholds();
        thresholds.Validate();
        ignore ??= new HashSet<string>(StringComparer.Ordinal);

        var results = new List<QueryComparison>();
        var ids = baseline.Queries.Select(q => q.Id)
            .Concat(candidate.Queries.Select(q => q.Id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var b = baseline.Find(id);
            var c = candidate.Find(id);
            results.Add(ignore.Contains(id)
                ? new QueryComparison { Id = id, Verdict = Verdict.Ignored, Findings = [], Baseline = b, Candidate = c }
                : CompareQuery(id, b, c, thresholds));
        }

        return new ComparisonReport
        {
            Baseline = baseline.Meta,
            Candidate = candidate.Meta,
            Thresholds = thresholds,
            Queries = results,
            EnvironmentWarnings = EnvironmentWarnings(baseline.Meta, candidate.Meta),
        };
    }

    public static QueryComparison CompareQuery(string id, QuerySnapshot? b, QuerySnapshot? c, Thresholds t)
    {
        var findings = new List<Finding>();
        if (b is null && c is null)
        {
            throw new ArgumentException("At least one side must be present.", nameof(b));
        }

        if (c is null)
        {
            // A catalog query that vanished is suspicious; a discovered statement simply was not exercised by this test run.
            findings.Add(b!.Source == QuerySource.Catalog
                ? new Finding(Signal.Coverage, FindingLevel.Warn, "Query is missing from the candidate capture.")
                : new Finding(Signal.Coverage, FindingLevel.Info, "Statement was not seen in the candidate workload."));
            return Result(id, b, c, findings, diff: null);
        }

        if (c.Error is not null)
        {
            findings.Add(new Finding(Signal.Error, FindingLevel.Fail, $"Query failed in the candidate: {c.Error}"));
            return Result(id, b, c, findings, diff: null);
        }

        if (b is null || b.Error is not null)
        {
            var reason = b is null ? "New query: no baseline to compare with." : $"Baseline capture failed ({b.Error}); nothing to compare with.";
            findings.Add(new Finding(Signal.Coverage, FindingLevel.Info, reason));
            return new QueryComparison { Id = id, Verdict = Verdict.New, Findings = findings, Baseline = b, Candidate = c };
        }

        var measured = b.Runs.Count > 0 && c.Runs.Count > 0;
        if (measured)
        {
            Metric(findings, Signal.Buffers, "Shared buffers (median blocks)", b.MedianSharedBlocks!.Value, c.MedianSharedBlocks!.Value, t.BufferRatio, t.BufferMinDelta, t.BufferGate);
            Metric(findings, Signal.TempIo, "Temp-file blocks (median)", b.MedianTempBlocks!.Value, c.MedianTempBlocks!.Value, t.TempRatio, t.TempMinDelta, t.TempGate, improvements: false);
            Metric(findings, Signal.Time, "Execution time (median ms)", b.MedianExecutionMs!.Value, c.MedianExecutionMs!.Value, t.TimeRatio, t.TimeMinDeltaMs, t.TimeGate);
            Metric(findings, Signal.Cost, "Planner cost", b.TotalCost, c.TotalCost, t.CostRatio, t.CostMinDelta, t.CostGate, improvements: false);

            if (t.RowsChangedGate != Gate.Off && b.MedianActualRows != c.MedianActualRows)
            {
                findings.Add(new Finding(Signal.Rows, Level(t.RowsChangedGate),
                    "Result row count changed: the query or the data changed, so the numbers compare different work.",
                    b.MedianActualRows, c.MedianActualRows));
            }
        }
        else
        {
            // Discovered statements have no runtime numbers of their own; the planner estimate is the evidence.
            Metric(findings, Signal.Cost, "Planner cost (generic plan)", b.TotalCost, c.TotalCost, t.CostRatio, t.CostMinDelta, t.DiscoveredCostGate);
            if (b.SharedBlocksPerCall is { } bb && c.SharedBlocksPerCall is { } cb)
            {
                // Per-call buffers come from the test suite's own calls, whose parameters may differ between runs.
                Metric(findings, Signal.Buffers, "Shared buffers per call (pg_stat_statements)", bb, cb, t.BufferRatio, t.BufferMinDelta, t.BufferGate == Gate.Off ? Gate.Off : Gate.Warn);
            }
        }

        PlanDiff? diff = null;
        if (b.Fingerprint != c.Fingerprint)
        {
            diff = PlanDiff.Between(b.Plan, c.Plan);
            findings.Add(new Finding(Signal.PlanShape, FindingLevel.Info, DescribeDiff(diff)));
        }

        if (!c.PlanStable)
        {
            findings.Add(new Finding(Signal.PlanStability, FindingLevel.Warn, "The candidate's measured runs did not all use the same plan."));
        }

        return Result(id, b, c, findings, diff);
    }

    private static QueryComparison Result(string id, QuerySnapshot? b, QuerySnapshot? c, List<Finding> findings, PlanDiff? diff)
    {
        Verdict verdict;
        if (findings.Any(f => f.Level == FindingLevel.Fail))
        {
            verdict = Verdict.Fail;
        }
        else if (findings.Any(f => f.Level == FindingLevel.Warn))
        {
            verdict = Verdict.Warn;
        }
        else if (findings.Any(f => f.Level == FindingLevel.Improvement))
        {
            verdict = Verdict.Improved;
        }
        else if (findings.Any(f => f.Signal == Signal.PlanShape))
        {
            verdict = Verdict.Changed;
        }
        else
        {
            verdict = Verdict.Pass;
        }

        return new QueryComparison { Id = id, Verdict = verdict, Findings = findings, Baseline = b, Candidate = c, Diff = diff };
    }

    private static void Metric(List<Finding> findings, Signal signal, string name, double b, double c, double ratio, double minDelta, Gate gate, bool improvements = true)
    {
        if (gate == Gate.Off)
        {
            return;
        }

        if (IsRegression(b, c, ratio, minDelta))
        {
            findings.Add(new Finding(signal, Level(gate), $"{name}: {Format(b)} -> {Format(c)} ({Change(b, c)}).", b, c));
        }
        else if (improvements && IsRegression(c, b, ratio, minDelta))
        {
            findings.Add(new Finding(signal, FindingLevel.Improvement, $"{name}: {Format(b)} -> {Format(c)} ({Change(b, c)}).", b, c));
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> exceeds <paramref name="baseline"/> by more than the ratio AND by at
    /// least the absolute floor. A zero baseline regresses on the floor alone (0 -> 500 temp blocks is a spill).
    /// </summary>
    public static bool IsRegression(double baseline, double candidate, double ratio, double minDelta) =>
        candidate - baseline >= minDelta && candidate - baseline > 0 && candidate > baseline * ratio;

    private static FindingLevel Level(Gate gate) => gate == Gate.Fail ? FindingLevel.Fail : FindingLevel.Warn;

    private static string DescribeDiff(PlanDiff diff)
    {
        if (diff.IsEmpty)
        {
            return "Plan shape changed (same operators, different arrangement).";
        }

        var parts = new List<string>();
        if (diff.Removed.Count > 0)
        {
            parts.Add("removed " + string.Join(", ", diff.Removed.Distinct().Select(x => $"`{x}`")));
        }

        if (diff.Added.Count > 0)
        {
            parts.Add("added " + string.Join(", ", diff.Added.Distinct().Select(x => $"`{x}`")));
        }

        return "Plan changed: " + string.Join("; ", parts) + ".";
    }

    private static List<string> EnvironmentWarnings(SnapshotMeta b, SnapshotMeta c)
    {
        var warnings = new List<string>();
        if (MajorVersion(b.ServerVersion) != MajorVersion(c.ServerVersion))
        {
            warnings.Add($"PostgreSQL major version differs: baseline {b.ServerVersion}, candidate {c.ServerVersion}.");
        }

        foreach (var key in SensitiveSettings)
        {
            b.Settings.TryGetValue(key, out var bv);
            c.Settings.TryGetValue(key, out var cv);
            if (!string.Equals(bv, cv, StringComparison.Ordinal))
            {
                warnings.Add($"Setting `{key}` differs: baseline `{bv ?? "default"}`, candidate `{cv ?? "default"}`.");
            }
        }

        if (b.Runs != c.Runs)
        {
            warnings.Add($"Measured run count differs: baseline {b.Runs}, candidate {c.Runs}.");
        }

        return warnings;
    }

    private static string MajorVersion(string version)
    {
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? version : version[..dot];
    }

    internal static string Format(double v) => v switch
    {
        _ when v == Math.Floor(v) && Math.Abs(v) < 1e15 => v.ToString("#,0", CultureInfo.InvariantCulture),
        >= 100 => v.ToString("#,0", CultureInfo.InvariantCulture),
        >= 10 => v.ToString("0.0", CultureInfo.InvariantCulture),
        _ => v.ToString("0.00", CultureInfo.InvariantCulture),
    };

    internal static string Change(double b, double c)
    {
        if (b == 0)
        {
            return c == 0 ? "no change" : "new";
        }

        var ratio = c / b;
        return ratio >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"x{ratio:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"-{(1 - ratio) * 100:0}%");
    }
}
