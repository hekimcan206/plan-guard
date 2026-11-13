using Npgsql;
using PlanGuard.Cli;
using PlanGuard.Comparison;
using PlanGuard.Postgres;
using PlanGuard.Snapshots;

namespace PlanGuard.IntegrationTests;

[Collection(ShopGroup.Name)]
public sealed class DiscoveryAndStoreTests(ShopCluster cluster)
{
    /// <summary>Stands in for an application's test suite: parameterised statements over the extended protocol.</summary>
    private static async Task RunWorkloadAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            await using var history = ds.CreateCommand("SELECT id, total FROM orders WHERE customer_id = $1 ORDER BY created_at DESC LIMIT 20");
            history.Parameters.AddWithValue(1000 + i);
            await using (var r = await history.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                }
            }

            await using var receipt = ds.CreateCommand("SELECT oi.line_no, m.name FROM order_items oi JOIN menu_items m ON m.id = oi.menu_item_id WHERE oi.order_id = $1");
            receipt.Parameters.AddWithValue((long)(5000 + i));
            await using (var r = await receipt.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                }
            }

            await using var pay = ds.CreateCommand("UPDATE orders SET status = 'paid' WHERE id = $1 AND status = 'open'");
            pay.Parameters.AddWithValue((long)(90_000 + i));
            await pay.ExecuteNonQueryAsync(ct);
        }

        // Plannable only while the temp table exists: discovery must skip it, not fail.
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var temp = new NpgsqlCommand("CREATE TEMP TABLE scratch AS SELECT 1 AS x; SELECT x FROM scratch WHERE x = 1;", conn);
        await temp.ExecuteNonQueryAsync(ct);
    }

    private async Task<DiscoveryResult> DiscoverAsync(string db, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ds = NpgsqlDataSource.Create(cluster.Shop.ConnectionString(db));
        await RunWorkloadAsync(ds, ct);
        return await new WorkloadDiscovery(ds).DiscoverAsync(new DiscoveryOptions { MinCalls = 1 }, label, ct: ct);
    }

    [Fact]
    public async Task Discovery_plans_the_test_suites_statements_with_generic_plans()
    {
        var db = await cluster.CloneAsync();

        var result = await DiscoverAsync(db, "main");

        var history = result.Snapshot.Queries.Should().ContainSingle(q => q.Sql.Contains("customer_id = $1")).Subject;
        history.Source.Should().Be(QuerySource.Discovered);
        history.Statements!.Calls.Should().Be(20);
        history.Plan.Descendants().Should().Contain(n => n.IndexName == "orders_customer_idx");
        history.Id.Should().MatchRegex("^q[0-9a-f]{12}$");
        result.Snapshot.Queries.Should().Contain(q => q.Sql.StartsWith("UPDATE orders", StringComparison.Ordinal));
        result.Snapshot.Queries.Should().NotContain(q => q.Sql.Contains("pg_stat_statements") || q.Sql.Contains("CREATE TEMP"));
        result.Skipped.Should().Contain(s => s.Sql.Contains("scratch"));
    }

    [Fact]
    public async Task Discovered_statement_whose_index_was_dropped_fails_on_cost()
    {
        var baseDb = await cluster.CloneAsync();
        var headDb = await cluster.CloneAsync(PlanGuard.Lab.ScenarioCatalog.Get("drop-customer-index"));

        var baseline = (await DiscoverAsync(baseDb, "main")).Snapshot;
        var candidate = (await DiscoverAsync(headDb, "pr")).Snapshot;
        var report = SnapshotComparer.Compare(baseline, candidate);

        var history = report.Queries.Single(q => q.Baseline?.Sql.Contains("customer_id = $1") == true);
        history.Verdict.Should().Be(Verdict.Fail);
        history.Findings.Should().Contain(f => f.Signal == Signal.Cost && f.Level == FindingLevel.Fail);
        report.Queries.Where(q => q.Id != history.Id).Should().NotContain(q => q.Verdict == Verdict.Fail);
    }

    [Fact]
    public async Task Store_keeps_history_and_returns_the_latest_snapshot_per_label()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ds = NpgsqlDataSource.Create(cluster.Shop.ConnectionString("postgres"));
        var store = new PostgresSnapshotStore(ds, "plan_guard_test");
        await store.EnsureSchemaAsync(ct);
        await store.EnsureSchemaAsync(ct); // idempotent

        var older = cluster.Baseline with { Meta = cluster.Baseline.Meta with { Label = "main", CapturedAt = DateTimeOffset.UtcNow.AddHours(-1), GitSha = "aaaaaaa" } };
        var newer = cluster.Baseline with { Meta = cluster.Baseline.Meta with { Label = "main", CapturedAt = DateTimeOffset.UtcNow, GitSha = "bbbbbbb" } };
        await store.SaveAsync(newer, ct);
        await store.SaveAsync(older, ct);

        var latest = await store.LatestAsync("main", ct);
        latest!.Meta.GitSha.Should().Be("bbbbbbb");
        latest.Queries.Select(q => q.Fingerprint).Should().Equal(cluster.Baseline.Queries.Select(q => q.Fingerprint));
        (await store.LatestAsync("nope", ct)).Should().BeNull();
        (await store.ListAsync("main", 10, ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Cli_capture_and_compare_end_to_end_with_the_store()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("plan-guard-e2e-").FullName;
        try
        {
            var config = Path.Combine(cluster.Shop.SampleDirectory, "plan-guard.json");
            var store = cluster.Shop.ConnectionString("postgres");
            var mainDb = await cluster.CloneAsync();
            var prDb = await cluster.CloneAsync(PlanGuard.Lab.ScenarioCatalog.Get("drop-fk-index"));
            var output = new StringWriter();
            var app = new App(output, output);

            (await app.RunAsync(["capture", "--config", config, "--connection", cluster.Shop.ConnectionString(mainDb), "--label", "main", "--git-sha", "1234567", "--out", Path.Combine(dir, "main.json"), "--store-connection", store], ct))
                .Should().Be(App.Ok, output.ToString());
            (await app.RunAsync(["capture", "--config", config, "--connection", cluster.Shop.ConnectionString(prDb), "--label", "pr", "--out", Path.Combine(dir, "pr.json")], ct))
                .Should().Be(App.Ok, output.ToString());
            var code = await app.RunAsync(["compare", "--baseline-label", "main", "--store-connection", store, "--candidate", Path.Combine(dir, "pr.json"), "--config", config, "--markdown", Path.Combine(dir, "report.md")], ct);

            code.Should().Be(App.Regression, output.ToString());
            var md = await File.ReadAllTextAsync(Path.Combine(dir, "report.md"), ct);
            md.Should().Contain("`main` (1234567) (baseline)");
            md.Should().Contain("| `order_detail` | **FAIL** |");
            (await app.RunAsync(["history", "--store-connection", store, "--label", "main"], ct)).Should().Be(App.Ok);
            output.ToString().Should().Contain("1234567");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
