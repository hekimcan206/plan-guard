using System.Text.Json;
using PlanGuard.Comparison;
using PlanGuard.Plans;
using PlanGuard.Reporting;
using PlanGuard.Snapshots;
using static PlanGuard.UnitTests.TestData;

namespace PlanGuard.UnitTests.Reporting;

public sealed class ReportTests
{
    /// <summary>A pull request with one regression (index dropped), one warning (slower), one improvement and an unchanged query.</summary>
    internal static ComparisonReport SampleReport()
    {
        var index = Plan("customer_orders_index").Root;
        var seq = Plan("customer_orders_seqscan").Root;
        var baseline = Snapshot("main",
            Query("customer_recent_orders", shared: 10, ms: 0.07, cost: index.TotalCost, rows: 7, plan: index),
            Query("store_daily_revenue", shared: 163, ms: 2, cost: 514),
            Query("top_customers", shared: 2472, ms: 36, cost: 8119),
            Query("customers_per_city", shared: 569, ms: 8, cost: 1319));
        var candidate = Snapshot("pr-42",
            Query("customer_recent_orders", shared: 2508, ms: 9.24, cost: seq.TotalCost, rows: 7, plan: seq),
            Query("store_daily_revenue", shared: 163, ms: 12, cost: 514),
            Query("top_customers", shared: 169, ms: 12, cost: 2361),
            Query("customers_per_city", shared: 569, ms: 8.1, cost: 1319));
        return SnapshotComparer.Compare(baseline, candidate);
    }

    [Fact]
    public void Markdown_report_matches_the_approved_output()
    {
        Golden.Verify("report.md", MarkdownReport.Render(SampleReport()));
    }

    [Fact]
    public void Markdown_report_starts_with_the_marker_and_states_the_outcome()
    {
        var md = MarkdownReport.Render(SampleReport());

        md.Should().StartWith(MarkdownReport.Marker);
        md.Should().Contain("## plan-guard: FAILED (1 regression, 1 warning, 1 improved)");
    }

    [Fact]
    public void Console_report_matches_the_approved_output()
    {
        Golden.Verify("report.txt", ConsoleReport.Render(SampleReport()));
    }

    [Fact]
    public void Table_cells_cannot_be_broken_by_query_ids()
    {
        var report = SnapshotComparer.Compare(Snapshot("b", Query("a|b<script>", shared: 1)), Snapshot("c", Query("a|b<script>", shared: 5000)));

        var md = MarkdownReport.Render(report);

        md.Should().Contain("a\\|b&lt;script&gt;");
        md.Should().NotContain("<script>");
    }

    [Fact]
    public void Plan_text_with_backticks_cannot_close_the_code_fence()
    {
        var plan = new PlanNode { NodeType = "Seq Scan", RelationName = "t", Filter = "(note = '```')" };
        var report = SnapshotComparer.Compare(Snapshot("b", Query("q", shared: 1, plan: Limit(Scan()))), Snapshot("c", Query("q", shared: 5000, plan: plan)));

        var md = MarkdownReport.Render(report);

        md.Should().Contain("````text");
    }

    [Fact]
    public void Oversized_reports_drop_plan_trees_to_fit_a_github_comment()
    {
        var queries = Enumerable.Range(0, 400).Select(i => (Query($"q{i:000}", shared: 10, plan: Plan("store_top_items_join").Root), Query($"q{i:000}", shared: 9000, plan: Plan("customer_orders_seqscan").Root))).ToList();
        var report = SnapshotComparer.Compare(Snapshot("b", [.. queries.Select(q => q.Item1)]), Snapshot("c", [.. queries.Select(q => q.Item2)]));

        var md = MarkdownReport.Render(report);

        md.Length.Should().BeLessThanOrEqualTo(65_536);
        md.Should().Contain("were left out");
    }

    [Fact]
    public void Annotations_are_escaped_per_the_actions_toolkit()
    {
        GitHubAnnotations.EscapeData("50%\nnext").Should().Be("50%25%0Anext");
        GitHubAnnotations.EscapeProperty("a:b,c").Should().Be("a%3Ab%2Cc");

        var lines = GitHubAnnotations.Render(SampleReport()).ToList();

        lines.Should().Contain(l => l.StartsWith("::error title=plan-guard customer_recent_orders::", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("::warning title=plan-guard store_daily_revenue::", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_report_is_machine_readable()
    {
        using var doc = JsonDocument.Parse(JsonReport.Render(SampleReport()));

        doc.RootElement.GetProperty("overall").GetString().Should().Be("fail");
        var regressed = doc.RootElement.GetProperty("queries").EnumerateArray().Single(q => q.GetProperty("id").GetString() == "customer_recent_orders");
        regressed.GetProperty("planChanged").GetBoolean().Should().BeTrue();
        regressed.GetProperty("addedOperators").EnumerateArray().Select(e => e.GetString()).Should().Contain("Seq Scan on orders");
    }
}

public sealed class SnapshotJsonTests
{
    [Fact]
    public void Round_trips_a_snapshot()
    {
        var original = Snapshot("main", Query("a", plan: Plan("store_top_items_join").Root), Query("b", source: QuerySource.Discovered));

        var copy = SnapshotJson.Deserialize(SnapshotJson.Serialize(original));

        copy.Meta.Should().BeEquivalentTo(original.Meta);
        copy.Queries.Select(q => q.Fingerprint).Should().Equal(original.Queries.Select(q => q.Fingerprint));
        PlanShape.Fingerprint(copy.Queries[0].Plan).Should().Be(original.Queries[0].Fingerprint);
        copy.Queries[0].MedianSharedBlocks.Should().Be(original.Queries[0].MedianSharedBlocks);
        copy.Queries[1].Source.Should().Be(QuerySource.Discovered);
    }

    [Fact]
    public void Rejects_an_unknown_format_version()
    {
        var json = SnapshotJson.Serialize(Snapshot("main") with { FormatVersion = 99 });

        var act = () => SnapshotJson.Deserialize(json);

        act.Should().Throw<FormatException>().WithMessage("*version 99*");
    }

    [Fact]
    public void Rejects_duplicate_query_ids()
    {
        var json = SnapshotJson.Serialize(Snapshot("main", Query("a"), Query("a")));

        var act = () => SnapshotJson.Deserialize(json);

        act.Should().Throw<FormatException>().WithMessage("*'a'*");
    }
}
