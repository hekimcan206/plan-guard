using System.Runtime.CompilerServices;
using PlanGuard.Plans;
using PlanGuard.Snapshots;

namespace PlanGuard.UnitTests;

internal static class TestData
{
    public static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative));

    public static ExplainResult Plan(string name) => ExplainJsonParser.Parse(Fixture($"plans/{name}.json"));

    public static PlanNode Scan(string type = "Index Scan", string relation = "orders", string? index = "orders_customer_idx", double cost = 10) =>
        new() { NodeType = type, RelationName = relation, IndexName = index, TotalCost = cost, PlanRows = 5 };

    public static PlanNode Limit(PlanNode child, double cost = 12) =>
        new() { NodeType = "Limit", TotalCost = cost, PlanRows = 5, Children = [child] };

    /// <summary>A measured catalog query with identical runs.</summary>
    public static QuerySnapshot Query(
        string id = "q1",
        double shared = 100,
        double ms = 1.0,
        double temp = 0,
        double cost = 100,
        double rows = 10,
        PlanNode? plan = null,
        int runs = 5,
        QuerySource source = QuerySource.Catalog)
    {
        plan ??= Limit(Scan(cost: cost), cost);
        plan = plan with { TotalCost = cost };
        var fingerprint = PlanShape.Fingerprint(plan);
        var buffers = new BufferCounts((long)shared, 0, 0, 0, 0, 0, (long)(temp / 2), (long)(temp - (long)(temp / 2)));
        return new QuerySnapshot
        {
            Id = id,
            Source = source,
            Sql = "SELECT 1",
            Fingerprint = fingerprint,
            Plan = plan,
            Runs = source == QuerySource.Catalog ? [.. Enumerable.Range(0, runs).Select(_ => new RunSample(ms, 0.1, buffers, rows, fingerprint))] : [],
        };
    }

    public static Snapshot Snapshot(string label, params QuerySnapshot[] queries) => new()
    {
        Meta = new SnapshotMeta
        {
            Label = label,
            CapturedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
            ServerVersion = "17.11",
            Runs = 5,
            Warmups = 1,
            Settings = new Dictionary<string, string> { ["jit"] = "off", ["work_mem"] = "4MB" },
        },
        Queries = queries,
    };
}

/// <summary>
/// Approval testing: the expected output lives in Fixtures/golden. Set PLANGUARD_UPDATE_GOLDEN=1 to rewrite the
/// files after an intentional change, then review the diff.
/// </summary>
internal static class Golden
{
    private static string ThisFile([CallerFilePath] string path = "") => path;

    public static void Verify(string name, string actual)
    {
        actual = actual.ReplaceLineEndings("\n");
        var sourcePath = Path.Combine(Path.GetDirectoryName(ThisFile())!, "Fixtures", "golden", name);
        if (Environment.GetEnvironmentVariable("PLANGUARD_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, actual);
            return;
        }

        var expectedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden", name);
        File.Exists(expectedPath).Should().BeTrue($"golden file {name} must exist (run with PLANGUARD_UPDATE_GOLDEN=1 to create it)");
        actual.Should().Be(File.ReadAllText(expectedPath).ReplaceLineEndings("\n"));
    }
}
