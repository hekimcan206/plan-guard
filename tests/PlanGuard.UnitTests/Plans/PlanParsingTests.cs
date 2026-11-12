using PlanGuard.Plans;

namespace PlanGuard.UnitTests.Plans;

public sealed class ExplainJsonParserTests
{
    [Fact]
    public void Parses_an_analyzed_plan_with_buffers_query_id_and_timings()
    {
        var plan = TestData.Plan("customer_orders_index");

        plan.IsAnalyzed.Should().BeTrue();
        plan.QueryId.Should().NotBeNull();
        plan.ExecutionTimeMs.Should().BeGreaterThan(0);
        plan.Root.NodeType.Should().Be("Limit");
        plan.Root.Buffers.Should().NotBeNull();
        plan.Root.Buffers!.SharedTotal.Should().BeGreaterThan(0);
        plan.Root.Descendants().Should().Contain(n => n.IndexName == "orders_customer_idx");
    }

    [Fact]
    public void Reads_non_default_settings_and_temp_blocks_of_a_spilling_aggregate()
    {
        var plan = TestData.Plan("top_customers_spill");

        plan.Settings.Should().ContainKey("work_mem").WhoseValue.Should().Be("64kB");
        plan.Root.Buffers!.TempTotal.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parses_a_generic_plan_without_runtime_counters()
    {
        var plan = TestData.Plan("generic_plan");

        plan.IsAnalyzed.Should().BeFalse();
        plan.Root.Buffers.Should().BeNull();
        plan.Root.Descendants().Should().Contain(n => n.IndexCond != null && n.IndexCond.Contains("$1"));
    }

    [Fact]
    public void Parses_join_conditions_and_sort_details()
    {
        var plan = TestData.Plan("store_top_items_join");
        var nodes = plan.Root.Descendants().ToList();

        nodes.Should().Contain(n => n.SortKey.Count > 0);
        nodes.Should().Contain(n => n.SortMethod != null);
        nodes.Should().Contain(n => n.RelationName == "order_items");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{\"Plan\": {}}]")]
    [InlineData("[{\"Plan\": {\"Node Type\": \"Seq Scan\"}}, {\"Plan\": {\"Node Type\": \"Seq Scan\"}}]")]
    public void Rejects_documents_that_are_not_a_single_explain_result(string json)
    {
        var act = () => ExplainJsonParser.Parse(json);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Accepts_the_bare_object_form_and_ignores_unknown_keys()
    {
        var plan = ExplainJsonParser.Parse("""{"Plan": {"Node Type": "Result", "Future Key": [1, 2], "Total Cost": "0.01"}, "Something New": true}""");

        plan.Root.NodeType.Should().Be("Result");
        plan.Root.TotalCost.Should().Be(0.01);
    }

    [Fact]
    public void Refuses_absurdly_deep_trees()
    {
        var json = string.Concat(Enumerable.Repeat("{\"Node Type\": \"Materialize\", \"Plans\": [", 300)) + "{\"Node Type\": \"Result\"}" + string.Concat(Enumerable.Repeat("]}", 300));

        var act = () => ExplainJsonParser.Parse("{\"Plan\": " + json + "}");

        act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is FormatException || e is System.Text.Json.JsonException);
    }
}

public sealed class PlanShapeTests
{
    [Fact]
    public void Fingerprint_ignores_costs_rows_timings_buffers_aliases_and_conditions()
    {
        var a = TestData.Plan("customer_orders_index").Root;
        var b = Perturb(a);

        PlanShape.Fingerprint(b).Should().Be(PlanShape.Fingerprint(a));
    }

    [Fact]
    public void Fingerprint_changes_when_an_index_scan_becomes_a_sequential_scan()
    {
        var index = TestData.Plan("customer_orders_index").Root;
        var seq = TestData.Plan("customer_orders_seqscan").Root;

        PlanShape.Fingerprint(seq).Should().NotBe(PlanShape.Fingerprint(index));
    }

    [Fact]
    public void Fingerprint_changes_when_only_the_index_changes()
    {
        var a = TestData.Limit(TestData.Scan(index: "ix_a"));
        var b = TestData.Limit(TestData.Scan(index: "ix_b"));

        PlanShape.Fingerprint(a).Should().NotBe(PlanShape.Fingerprint(b));
    }

    [Fact]
    public void Canonical_form_is_stable()
    {
        var plan = TestData.Plan("customer_orders_seqscan").Root;

        Golden.Verify("customer_orders_seqscan.canonical.txt", PlanShape.Canonical(plan) + "\n");
    }

    [Theory]
    [InlineData("Index Scan", "orders", "ix", null, null, null, "Index Scan using ix on orders")]
    [InlineData("Bitmap Index Scan", null, "ix", null, null, null, "Bitmap Index Scan on ix")]
    [InlineData("Seq Scan", "orders", null, null, null, null, "Seq Scan on orders")]
    [InlineData("Aggregate", null, null, null, "Hashed", null, "HashAggregate")]
    [InlineData("Aggregate", null, null, null, "Sorted", null, "GroupAggregate")]
    [InlineData("Aggregate", null, null, null, "Plain", null, "Aggregate")]
    [InlineData("Hash Join", null, null, "Left", null, null, "Hash Join (Left)")]
    [InlineData("Nested Loop", null, null, "Inner", null, null, "Nested Loop")]
    [InlineData("Index Scan", "orders", "ix", null, null, "Backward", "Index Scan Backward using ix on orders")]
    public void Labels_read_like_text_explain(string type, string? relation, string? index, string? join, string? strategy, string? direction, string expected)
    {
        var node = new PlanNode { NodeType = type, RelationName = relation, IndexName = index, JoinType = join, Strategy = strategy, ScanDirection = direction };

        PlanShape.Label(node).Should().Be(expected);
    }

    internal static PlanNode Perturb(PlanNode n) => n with
    {
        StartupCost = n.StartupCost * 1.7 + 3,
        TotalCost = n.TotalCost * 2.3 + 1,
        PlanRows = n.PlanRows + 17,
        ActualRows = (n.ActualRows ?? 0) + 5,
        ActualTotalTimeMs = 999,
        Alias = "renamed",
        Filter = n.Filter is null ? null : n.Filter + " AND true",
        Buffers = new BufferCounts(1, 2, 3, 4, 5, 6, 7, 8),
        Children = [.. n.Children.Select(Perturb)],
    };
}

public sealed class PlanDiffTests
{
    [Fact]
    public void Reports_the_index_scan_replaced_by_a_sequential_scan()
    {
        var diff = PlanDiff.Between(TestData.Plan("customer_orders_index").Root, TestData.Plan("customer_orders_seqscan").Root);

        diff.Removed.Should().Contain("Bitmap Index Scan on orders_customer_idx");
        diff.Added.Should().Contain("Seq Scan on orders");
        diff.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Identical_plans_have_an_empty_diff()
    {
        var plan = TestData.Plan("store_top_items_join").Root;

        PlanDiff.Between(plan, PlanShapeTests.Perturb(plan)).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Diff_is_a_multiset_difference()
    {
        var two = new PlanNode { NodeType = "Append", Children = [TestData.Scan("Seq Scan", "a", null), TestData.Scan("Seq Scan", "a", null)] };
        var one = new PlanNode { NodeType = "Append", Children = [TestData.Scan("Seq Scan", "a", null)] };

        var diff = PlanDiff.Between(two, one);

        diff.Removed.Should().Equal("Seq Scan on a");
        diff.Added.Should().BeEmpty();
    }

    [Fact]
    public void Renders_plans_like_text_explain()
    {
        Golden.Verify("customer_orders_seqscan.tree.txt", PlanTreeRenderer.Render(StripTimings(TestData.Plan("customer_orders_seqscan").Root)) + "\n");
    }

    // Actual rows and buffers are deterministic for the fixture; timings are not printed by the renderer anyway.
    private static PlanNode StripTimings(PlanNode n) => n with { ActualTotalTimeMs = null, Children = [.. n.Children.Select(StripTimings)] };
}
