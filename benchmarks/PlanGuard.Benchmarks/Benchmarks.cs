using BenchmarkDotNet.Attributes;
using PlanGuard.Comparison;
using PlanGuard.Plans;
using PlanGuard.Reporting;
using PlanGuard.Snapshots;

namespace PlanGuard.Benchmarks;

/// <summary>Per-plan costs: what plan-guard adds on top of the EXPLAIN round trip for every run.</summary>
[MemoryDiagnoser]
[MedianColumn]
public class PlanBenchmarks
{
    private string _json = string.Empty;
    private PlanNode _plan = null!;
    private PlanNode _other = null!;

    [GlobalSetup]
    public void Setup()
    {
        _json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plans", "store_top_items_join.json"));
        _plan = ExplainJsonParser.Parse(_json).Root;
        _other = ExplainJsonParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plans", "customer_orders_seqscan.json"))).Root;
    }

    [Benchmark(Description = "parse EXPLAIN JSON (14 KB, 9 nodes)")]
    public ExplainResult Parse() => ExplainJsonParser.Parse(_json);

    [Benchmark(Description = "fingerprint plan")]
    public string Fingerprint() => PlanShape.Fingerprint(_plan);

    [Benchmark(Description = "diff two plans")]
    public PlanDiff Diff() => PlanDiff.Between(_plan, _other);
}

/// <summary>Whole-snapshot costs for the compare step, at catalog sizes from a small service to a large monolith.</summary>
[MemoryDiagnoser]
[MedianColumn]
public class CompareBenchmarks
{
    private Snapshot _baseline = null!;
    private Snapshot _candidate = null!;
    private ComparisonReport _report = null!;

    [Params(10, 200, 2000)]
    public int Queries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var plan = ExplainJsonParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plans", "store_top_items_join.json"))).Root;
        var seq = ExplainJsonParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plans", "customer_orders_seqscan.json"))).Root;
        _baseline = Build("main", plan, plan, 1.0);
        // Every 10th query regresses with a different plan, the rest are unchanged.
        _candidate = Build("pr", plan, seq, 40.0);
        _report = SnapshotComparer.Compare(_baseline, _candidate);
    }

    private Snapshot Build(string label, PlanNode same, PlanNode changed, double factor) => new()
    {
        Meta = new SnapshotMeta { Label = label, CapturedAt = DateTimeOffset.UnixEpoch, ServerVersion = "17.11", Runs = 5 },
        Queries =
        [
            .. Enumerable.Range(0, Queries).Select(i =>
            {
                var regress = i % 10 == 0;
                var p = regress ? changed : same;
                var shared = regress ? (long)(100 * factor) : 100;
                var fp = PlanShape.Fingerprint(p);
                return new QuerySnapshot
                {
                    Id = $"q{i:0000}",
                    Source = QuerySource.Catalog,
                    Sql = "SELECT 1",
                    Fingerprint = fp,
                    Plan = p,
                    Runs = [.. Enumerable.Range(0, 5).Select(_ => new RunSample(1.0, 0.1, new BufferCounts(shared, 0, 0, 0, 0, 0, 0, 0), 10, fp))],
                };
            }),
        ],
    };

    [Benchmark(Description = "compare snapshots")]
    public ComparisonReport Compare() => SnapshotComparer.Compare(_baseline, _candidate);

    [Benchmark(Description = "render Markdown comment")]
    public string Markdown() => MarkdownReport.Render(_report);

    [Benchmark(Description = "serialize + deserialize snapshot")]
    public Snapshot RoundTrip() => SnapshotJson.Deserialize(SnapshotJson.Serialize(_candidate));
}
