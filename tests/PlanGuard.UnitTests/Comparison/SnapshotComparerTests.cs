using FsCheck;
using FsCheck.Fluent;
using PlanGuard.Comparison;
using PlanGuard.Snapshots;
using static PlanGuard.UnitTests.TestData;

namespace PlanGuard.UnitTests.Comparison;

public sealed class SnapshotComparerTests
{
    private static QueryComparison Compare(QuerySnapshot b, QuerySnapshot c, Thresholds? t = null) =>
        SnapshotComparer.Compare(Snapshot("base", b), Snapshot("head", c), t).Queries.Single();

    [Fact]
    public void Unchanged_query_passes()
    {
        var r = Compare(Query(), Query());

        r.Verdict.Should().Be(Verdict.Pass);
        r.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Buffer_growth_beyond_ratio_and_floor_fails()
    {
        var r = Compare(Query(shared: 100), Query(shared: 400));

        r.Verdict.Should().Be(Verdict.Fail);
        r.Findings.Should().ContainSingle(f => f.Signal == Signal.Buffers && f.Level == FindingLevel.Fail)
            .Which.Ratio.Should().Be(4.0);
    }

    [Theory]
    [InlineData(10, 60, Verdict.Pass)]    // x6 but only +50 blocks: below the absolute floor
    [InlineData(1000, 1250, Verdict.Pass)] // +250 blocks but only x1.25: below the ratio
    [InlineData(1000, 1400, Verdict.Fail)]
    [InlineData(0, 64, Verdict.Fail)]      // from nothing to the floor
    public void Buffer_regression_needs_both_the_ratio_and_the_floor(double before, double after, Verdict expected)
    {
        Compare(Query(shared: before), Query(shared: after)).Verdict.Should().Be(expected);
    }

    [Fact]
    public void Slower_execution_alone_only_warns_by_default()
    {
        var r = Compare(Query(ms: 2), Query(ms: 20));

        r.Verdict.Should().Be(Verdict.Warn);
        r.Findings.Should().ContainSingle(f => f.Signal == Signal.Time && f.Level == FindingLevel.Warn);
    }

    [Fact]
    public void Time_gate_can_be_raised_to_fail()
    {
        var r = Compare(Query(ms: 2), Query(ms: 20), new Thresholds { TimeGate = Gate.Fail });

        r.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Small_absolute_time_changes_are_ignored()
    {
        Compare(Query(ms: 0.1), Query(ms: 2)).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void A_new_spill_to_temp_files_fails()
    {
        var r = Compare(Query(temp: 0), Query(temp: 900));

        r.Verdict.Should().Be(Verdict.Fail);
        r.Findings.Should().Contain(f => f.Signal == Signal.TempIo && f.Level == FindingLevel.Fail);
    }

    [Fact]
    public void Higher_cost_with_the_same_work_only_warns_for_measured_queries()
    {
        var r = Compare(Query(cost: 100), Query(cost: 1000));

        r.Verdict.Should().Be(Verdict.Warn);
        r.Findings.Should().ContainSingle(f => f.Signal == Signal.Cost);
    }

    [Fact]
    public void Higher_cost_fails_a_discovered_statement()
    {
        var r = Compare(Query(cost: 100, source: QuerySource.Discovered), Query(cost: 1000, source: QuerySource.Discovered));

        r.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Less_work_is_reported_as_an_improvement()
    {
        var r = Compare(Query(shared: 2000), Query(shared: 100));

        r.Verdict.Should().Be(Verdict.Improved);
        r.Findings.Should().ContainSingle(f => f.Level == FindingLevel.Improvement);
    }

    [Fact]
    public void A_plan_change_within_thresholds_is_reported_but_passes()
    {
        var r = Compare(Query(plan: Limit(Scan(index: "ix_a"))), Query(plan: Limit(Scan(index: "ix_b"))));

        r.Verdict.Should().Be(Verdict.Changed);
        r.PlanChanged.Should().BeTrue();
        r.Diff!.Removed.Should().Equal("Index Scan using ix_a on orders");
        r.Diff.Added.Should().Equal("Index Scan using ix_b on orders");
    }

    [Fact]
    public void Changed_row_count_warns_because_the_work_is_not_comparable()
    {
        var r = Compare(Query(rows: 10), Query(rows: 11));

        r.Verdict.Should().Be(Verdict.Warn);
        r.Findings.Should().ContainSingle(f => f.Signal == Signal.Rows);
    }

    [Fact]
    public void Unstable_candidate_plan_warns()
    {
        var c = Query() with { PlanStable = false };

        Compare(Query(), c).Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    public void Missing_catalog_query_warns_but_a_missing_discovered_statement_does_not()
    {
        var catalog = SnapshotComparer.Compare(Snapshot("b", Query("a")), Snapshot("c")).Queries.Single();
        var discovered = SnapshotComparer.Compare(Snapshot("b", Query("a", source: QuerySource.Discovered)), Snapshot("c")).Queries.Single();

        catalog.Verdict.Should().Be(Verdict.Warn);
        discovered.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void New_query_has_no_baseline()
    {
        var r = SnapshotComparer.Compare(Snapshot("b"), Snapshot("c", Query("a"))).Queries.Single();

        r.Verdict.Should().Be(Verdict.New);
    }

    [Fact]
    public void Query_that_fails_in_the_candidate_fails_the_build()
    {
        var failed = Query() with { Error = "42P01: relation \"orders\" does not exist" };

        Compare(Query(), failed).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Ignored_queries_are_not_judged()
    {
        var report = SnapshotComparer.Compare(Snapshot("b", Query("a", shared: 1)), Snapshot("c", Query("a", shared: 10_000)), ignore: new HashSet<string> { "a" });

        report.Queries.Single().Verdict.Should().Be(Verdict.Ignored);
        report.Overall.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Different_settings_and_versions_are_reported_as_environment_warnings()
    {
        var b = Snapshot("b", Query());
        var c = Snapshot("c", Query()) with
        {
            Meta = Snapshot("c").Meta with { ServerVersion = "16.4", Settings = new Dictionary<string, string> { ["jit"] = "off", ["work_mem"] = "64kB" } },
        };

        var report = SnapshotComparer.Compare(b, c);

        report.EnvironmentWarnings.Should().HaveCount(2);
        report.EnvironmentWarnings.Should().Contain(w => w.Contains("work_mem"));
        report.EnvironmentWarnings.Should().Contain(w => w.Contains("major version"));
    }

    [Theory]
    [InlineData(FailOn.Fail, 1)]
    [InlineData(FailOn.Warn, 1)]
    public void Exit_code_is_one_on_a_regression(FailOn failOn, int expected)
    {
        var report = SnapshotComparer.Compare(Snapshot("b", Query(shared: 100)), Snapshot("c", Query(shared: 1000)));

        report.ExitCode(failOn).Should().Be(expected);
    }

    [Theory]
    [InlineData(FailOn.Fail, 0)]
    [InlineData(FailOn.Warn, 1)]
    public void Warnings_fail_the_build_only_when_asked(FailOn failOn, int expected)
    {
        var report = SnapshotComparer.Compare(Snapshot("b", Query(ms: 2)), Snapshot("c", Query(ms: 50)));

        report.ExitCode(failOn).Should().Be(expected);
    }

    [Theory]
    [InlineData(0.99)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_ratios_are_rejected(double ratio)
    {
        var act = () => SnapshotComparer.Compare(Snapshot("b"), Snapshot("c"), new Thresholds { BufferRatio = ratio });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class ComparisonProperties
{
    private static Arbitrary<(double Shared, double Ms, double Temp, double Cost)> Metrics() =>
        (from shared in Gen.Choose(0, 1_000_000)
         from ms in Gen.Choose(0, 100_000)
         from temp in Gen.Choose(0, 10_000)
         from cost in Gen.Choose(1, 1_000_000)
         select ((double)shared, ms / 100.0, (double)temp, (double)cost))
        .ToArbitrary();

    [FsCheck.Xunit.Property]
    public Property Comparing_a_capture_with_itself_always_passes() =>
        Prop.ForAll(Metrics(), m =>
        {
            var q = Query(shared: m.Shared, ms: m.Ms, temp: m.Temp, cost: m.Cost);
            return SnapshotComparer.Compare(Snapshot("a", q), Snapshot("b", q)).Queries.Single().Verdict == Verdict.Pass;
        });

    [FsCheck.Xunit.Property]
    public Property More_buffers_never_give_a_better_verdict() =>
        Prop.ForAll(Metrics(), Gen.Choose(0, 1_000_000).ToArbitrary(), Gen.Choose(0, 1_000_000).ToArbitrary(), (m, x, extra) =>
        {
            var b = Query(shared: m.Shared, ms: m.Ms, cost: m.Cost);
            var c1 = Query(shared: x, ms: m.Ms, cost: m.Cost);
            var c2 = Query(shared: x + extra, ms: m.Ms, cost: m.Cost);
            static int Rank(Verdict v) => v switch { Verdict.Improved => 0, Verdict.Pass => 1, Verdict.Warn => 2, Verdict.Fail => 3, _ => 1 };
            var v1 = SnapshotComparer.Compare(Snapshot("a", b), Snapshot("b", c1)).Queries.Single().Verdict;
            var v2 = SnapshotComparer.Compare(Snapshot("a", b), Snapshot("b", c2)).Queries.Single().Verdict;
            return Rank(v2) >= Rank(v1);
        });

    [FsCheck.Xunit.Property]
    public Property A_value_never_regresses_against_itself_or_in_both_directions() =>
        Prop.ForAll(
            (from a in Gen.Choose(0, 100_000) from b in Gen.Choose(0, 100_000) from ratioPct in Gen.Choose(100, 400) from floor in Gen.Choose(0, 500) select (a, b, ratioPct, floor)).ToArbitrary(),
            t =>
            {
                var (a, b, ratioPct, floor) = t;
                var ratio = ratioPct / 100.0;
                return !SnapshotComparer.IsRegression(a, a, ratio, floor)
                       && !(SnapshotComparer.IsRegression(a, b, ratio, floor) && SnapshotComparer.IsRegression(b, a, ratio, floor));
            });

    [FsCheck.Xunit.Property]
    public Property Median_lies_between_min_and_max_and_ignores_order() =>
        Prop.ForAll(Gen.Choose(-1000, 1000).Select(i => (double)i).NonEmptyListOf().ToArbitrary(), values =>
        {
            var list = values.ToList();
            var median = Stats.Median(list);
            var reversed = Stats.Median(Enumerable.Reverse(list));
            return median >= list.Min() && median <= list.Max() && median == reversed;
        });
}
