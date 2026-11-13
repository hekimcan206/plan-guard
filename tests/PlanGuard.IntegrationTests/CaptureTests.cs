using Npgsql;
using PlanGuard.Comparison;
using PlanGuard.Configuration;
using PlanGuard.Lab;
using PlanGuard.Postgres;

namespace PlanGuard.IntegrationTests;

[Collection(ShopGroup.Name)]
public sealed class CaptureTests(ShopCluster cluster)
{
    [Fact]
    public void Baseline_captures_every_catalog_query_with_runs_and_statement_counters()
    {
        var baseline = cluster.Baseline;

        baseline.Queries.Should().HaveCount(10);
        baseline.Queries.Should().OnlyContain(q => q.Error == null && q.Runs.Count == 5 && q.PlanStable);
        baseline.Meta.StatementStatsAvailable.Should().BeTrue();
        baseline.Meta.Settings.Should().ContainKey("jit").WhoseValue.Should().Be("off");
        // pg_stat_statements counted exactly the measured runs (the delta excludes the warm-up).
        baseline.Queries.Should().OnlyContain(q => q.QueryId != null && q.Statements != null && q.Statements.Calls == 5);
    }

    [Fact]
    public void Shared_block_counts_are_identical_across_runs_of_an_unchanged_plan()
    {
        foreach (var q in cluster.Baseline.Queries.Where(q => q.Id != "correct_order_total"))
        {
            q.Runs.Select(r => r.Buffers.SharedTotal).Distinct().Should().HaveCount(1, $"{q.Id} reads the same pages every run");
        }
    }

    [Fact]
    public async Task Data_changing_queries_are_measured_and_rolled_back()
    {
        var db = await cluster.CloneAsync();
        var before = await TotalOf777Async(db);

        var snapshot = await cluster.CaptureAsync(db, null, "dml");

        var update = snapshot.Find("correct_order_total")!;
        update.Error.Should().BeNull();
        update.Plan.NodeType.Should().Be("ModifyTable");
        (await TotalOf777Async(db)).Should().Be(before);
        before.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task Capturing_the_same_database_twice_passes()
    {
        var db = await cluster.CloneAsync();

        var again = await cluster.CaptureAsync(db, null, "again");
        var report = SnapshotComparer.Compare(cluster.Baseline, again);

        report.Overall.Should().Be(Verdict.Pass, string.Join("; ", report.Queries.SelectMany(q => q.Findings).Select(f => f.Message)));
        report.EnvironmentWarnings.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Regressions))]
    public async Task Every_regression_scenario_fails_its_target_query(string scenarioId)
    {
        var scenario = ScenarioCatalog.Get(scenarioId);
        var db = await cluster.CloneAsync(scenario);

        var candidate = await cluster.CaptureAsync(db, scenario, scenarioId);
        var report = SnapshotComparer.Compare(cluster.Baseline, candidate, cluster.Config.Thresholds);

        var target = report.Queries.Single(q => q.Id == scenario.Target);
        target.Verdict.Should().Be(Verdict.Fail, string.Join("; ", target.Findings.Select(f => f.Message)));
        report.ExitCode(FailOn.Fail).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Neutral))]
    public async Task Neutral_changes_do_not_fail_the_build(string scenarioId)
    {
        var scenario = ScenarioCatalog.Get(scenarioId);
        var db = await cluster.CloneAsync(scenario);

        var candidate = await cluster.CaptureAsync(db, scenario, scenarioId);
        var report = SnapshotComparer.Compare(cluster.Baseline, candidate, cluster.Config.Thresholds);

        report.Queries.Should().NotContain(q => q.Verdict == Verdict.Fail,
            string.Join("; ", report.Queries.Where(q => q.Verdict == Verdict.Fail).SelectMany(q => q.Findings).Select(f => f.Message)));
    }

    [Fact]
    public async Task A_covering_index_is_reported_as_an_improvement()
    {
        var scenario = ScenarioCatalog.Get("covering-index");
        var db = await cluster.CloneAsync(scenario);

        var candidate = await cluster.CaptureAsync(db, scenario, scenario.Id);
        var report = SnapshotComparer.Compare(cluster.Baseline, candidate, cluster.Config.Thresholds);

        var target = report.Queries.Single(q => q.Id == "top_customers");
        target.Verdict.Should().Be(Verdict.Improved, string.Join("; ", target.Findings.Select(f => f.Message)));
        target.Diff!.Added.Should().Contain(l => l.Contains("orders_created_cover_idx"));
        report.ExitCode(FailOn.Fail).Should().Be(0);
    }

    [Fact]
    public async Task A_query_the_role_may_not_run_is_recorded_as_an_error_and_the_rest_is_still_captured()
    {
        var db = await cluster.CloneAsync();
        await using (var admin = NpgsqlDataSource.Create(cluster.Shop.ConnectionString(db)))
        {
            await using var cmd = admin.CreateCommand(
                "CREATE ROLE ci_reader LOGIN PASSWORD 'reader-test-only'; GRANT SELECT ON ALL TABLES IN SCHEMA public TO ci_reader;");
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var cs = new NpgsqlConnectionStringBuilder(cluster.Shop.ConnectionString(db)) { Username = "ci_reader", Password = "reader-test-only" }.ConnectionString;
        await using var ds = NpgsqlDataSource.Create(cs);
        var snapshot = await new PostgresCapturer(ds).CaptureAsync(ShopDatabase.Catalog(cluster.Config, null), CaptureOptions.From(cluster.Config), "reader", ct: TestContext.Current.CancellationToken);

        snapshot.Meta.StatementStatsAvailable.Should().BeFalse("a non-superuser cannot set pg_stat_statements.track");
        snapshot.Find("correct_order_total")!.Error.Should().StartWith("42501");
        snapshot.Queries.Where(q => q.Id != "correct_order_total").Should().OnlyContain(q => q.Error == null && q.Runs.Count == 5);

        var report = SnapshotComparer.Compare(cluster.Baseline, snapshot);
        report.Queries.Single(q => q.Id == "correct_order_total").Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public async Task Statement_timeout_turns_a_runaway_query_into_an_error()
    {
        var db = await cluster.CloneAsync();
        await using var ds = NpgsqlDataSource.Create(cluster.Shop.ConnectionString(db));
        var slow = QueryDefinition.Create("slow", "SELECT count(*) FROM order_items a JOIN order_items b ON a.qty = b.qty");

        var snapshot = await new PostgresCapturer(ds).CaptureAsync([slow], new CaptureOptions { StatementTimeoutMs = 200, Runs = 1, Warmups = 0 }, "timeout", ct: TestContext.Current.CancellationToken);

        snapshot.Queries.Single().Error.Should().StartWith("57014");
    }

    public static TheoryData<string> Regressions() => [.. ScenarioCatalog.All.Where(s => s.Kind == ScenarioKind.Regression).Select(s => s.Id)];

    public static TheoryData<string> Neutral() => [.. ScenarioCatalog.All.Where(s => s.Kind == ScenarioKind.Neutral).Select(s => s.Id)];

    private async Task<decimal> TotalOf777Async(string db)
    {
        await using var ds = NpgsqlDataSource.Create(cluster.Shop.ConnectionString(db));
        await using var cmd = ds.CreateCommand("SELECT total FROM orders WHERE id = 777");
        return (decimal)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
