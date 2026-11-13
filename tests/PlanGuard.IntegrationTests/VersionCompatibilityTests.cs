using Npgsql;
using PlanGuard.Configuration;
using PlanGuard.Postgres;

namespace PlanGuard.IntegrationTests;

/// <summary>
/// The EXPLAIN JSON keys and pg_stat_statements columns plan-guard reads exist on every supported major version.
/// A tiny schema keeps these fast; behaviour on real data is covered on 17 by the shop suite.
/// </summary>
public sealed class VersionCompatibilityTests
{
    [Theory]
    [InlineData("postgres:16-alpine")]
    [InlineData("postgres:18")]
    public async Task Capture_and_discovery_work_on_other_major_versions(string image)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var container = PostgresContainers.Build(image);
        await container.StartAsync(ct);
        await using var ds = NpgsqlDataSource.Create(container.GetConnectionString());
        await using (var setup = ds.CreateCommand("""
            CREATE EXTENSION pg_stat_statements;
            CREATE TABLE t (id int PRIMARY KEY, c int NOT NULL);
            INSERT INTO t SELECT g, g % 100 FROM generate_series(1, 20000) g;
            CREATE INDEX t_c_idx ON t (c);
            ANALYZE t;
            """))
        {
            await setup.ExecuteNonQueryAsync(ct);
        }

        var snapshot = await new PostgresCapturer(ds).CaptureAsync(
            [QueryDefinition.Create("by_c", "SELECT count(*) FROM t WHERE c = 7")],
            new CaptureOptions { Runs = 3 }, "compat", ct: ct);

        var q = snapshot.Queries.Single();
        q.Error.Should().BeNull();
        q.Runs.Should().HaveCount(3).And.OnlyContain(r => r.Buffers.SharedTotal > 0);
        q.Plan.Descendants().Should().Contain(n => n.IndexName == "t_c_idx");
        snapshot.Meta.StatementStatsAvailable.Should().BeTrue();
        q.Statements!.Calls.Should().Be(3);

        await using (var app = ds.CreateCommand("SELECT * FROM t WHERE c = $1"))
        {
            app.Parameters.AddWithValue(3);
            await using var r = await app.ExecuteReaderAsync(ct);
        }

        var discovered = await new WorkloadDiscovery(ds).DiscoverAsync(new DiscoveryOptions(), "compat", ct: ct);
        discovered.Snapshot.Queries.Should().Contain(d => d.Sql.Contains("c = $1") && d.Plan.Descendants().Any(n => n.RelationName == "t"),
            string.Join(" | ", discovered.Skipped.Select(s => s.Reason + ": " + s.Sql)));
    }
}
