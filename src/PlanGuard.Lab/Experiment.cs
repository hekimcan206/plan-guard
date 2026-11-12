using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using PlanGuard.Comparison;
using PlanGuard.Configuration;
using PlanGuard.Postgres;
using PlanGuard.Snapshots;

namespace PlanGuard.Lab;

public sealed record ExperimentOptions
{
    public int Repetitions { get; init; } = 20;
    public int NoiseWorkers { get; init; } = 4;
    public bool Quiet { get; init; } = true;
    public bool Loaded { get; init; } = true;
    public required string OutputDirectory { get; init; }
}

/// <summary>
/// The lab: which signal tells a real plan regression from noise? For each condition (quiet host, host with
/// background load) it captures a baseline, repeated A/A captures of the unchanged database (with and without a
/// fresh ANALYZE), and one capture per scenario, then scores every detector on the same captures.
/// </summary>
public sealed class Experiment(ShopDatabase shop, TextWriter log)
{
    private const string Base = "shop_base";
    private const string Reference = "shop_ref";
    private const string Noise = "shop_noise";

    public async Task<int> RunAsync(ExperimentOptions options, CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var config = shop.LoadConfig();
        var sw = Stopwatch.StartNew();
        await log.WriteLineAsync("seeding shop_base ...");
        await shop.CreateAsync(Base, ct);
        await log.WriteLineAsync($"  seeded in {sw.Elapsed.TotalSeconds:0.0} s");

        await shop.CloneAsync(Base, Reference, ct);
        await shop.CloneAsync(Base, Noise, ct);
        foreach (var s in ScenarioCatalog.All)
        {
            await shop.CloneAsync(Base, Db(s), ct);
            await shop.ApplyAsync(Db(s), s, ct);
        }

        var results = new List<ConditionResult>();
        try
        {
            if (options.Quiet)
            {
                results.Add(await RunConditionAsync("quiet", config, options, noise: 0, ct));
            }

            if (options.Loaded)
            {
                results.Add(await RunConditionAsync("loaded", config, options, options.NoiseWorkers, ct));
            }
        }
        finally
        {
            foreach (var s in ScenarioCatalog.All)
            {
                await shop.DropAsync(Db(s), CancellationToken.None);
            }

            await shop.DropAsync(Noise, CancellationToken.None);
        }

        var json = JsonSerializer.Serialize(results, SnapshotJson.Options);
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "experiment.json"), json, ct);
        var markdown = Render(results, options);
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "experiment.md"), markdown, ct);
        await log.WriteLineAsync(markdown);
        return 0;
    }

    private async Task<ConditionResult> RunConditionAsync(string condition, PlanGuardConfig config, ExperimentOptions options, int noise, CancellationToken ct)
    {
        await log.WriteLineAsync($"== condition {condition} (noise workers: {noise})");
        using var noiseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var noiseTasks = Enumerable.Range(0, noise).Select(i => NoiseLoopAsync(i, noiseCts.Token)).ToList();
        try
        {
            if (noise > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }

            var captureTimes = new List<double>();
            var baseline = await CaptureAsync(Reference, config, null, $"{condition}-baseline", captureTimes, ct);

            var aa = new List<Snapshot>();
            var aaReanalyzed = new List<Snapshot>();
            for (var i = 0; i < options.Repetitions; i++)
            {
                aa.Add(await CaptureAsync(Reference, config, null, $"{condition}-aa-{i}", captureTimes, ct));
            }

            await log.WriteLineAsync($"  A/A: {options.Repetitions} captures done");

            // Re-sampled statistics: ANALYZE before every capture (runs after the plain A/A set, which it would disturb).
            for (var i = 0; i < options.Repetitions; i++)
            {
                await ExecAsync(Reference, "ANALYZE", ct);
                aaReanalyzed.Add(await CaptureAsync(Reference, config, null, $"{condition}-aa-analyzed-{i}", captureTimes, ct));
            }

            await log.WriteLineAsync($"  A/A after ANALYZE: {options.Repetitions} captures done");

            var scenarios = new List<ScenarioResult>();
            foreach (var s in ScenarioCatalog.All)
            {
                var candidate = await CaptureAsync(Db(s), config, s, $"{condition}-{s.Id}", captureTimes, ct);
                scenarios.Add(Score(s, baseline, candidate));
                await log.WriteLineAsync($"  scenario {s.Id}: plan-guard {(scenarios[^1].PlanGuardFailedTarget ? "FAIL" : "pass")}");
            }

            // Restore the reference statistics for the next condition.
            NpgsqlConnection.ClearAllPools();
            await shop.CloneAsync(Base, Reference, ct);

            return new ConditionResult(
                condition, noise, options.Repetitions,
                AaScores(baseline, aa), AaScores(baseline, aaReanalyzed),
                scenarios, NoiseProfile(baseline, aa), Stats.Median(captureTimes),
                baseline.Meta.ServerVersion);
        }
        finally
        {
            await noiseCts.CancelAsync();
            try
            {
                await Task.WhenAll(noiseTasks);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<Snapshot> CaptureAsync(string database, PlanGuardConfig config, Scenario? scenario, string label, List<double> times, CancellationToken ct)
    {
        await using var ds = NpgsqlDataSource.Create(shop.ConnectionString(database));
        var sw = Stopwatch.StartNew();
        var snapshot = await new PostgresCapturer(ds).CaptureAsync(ShopDatabase.Catalog(config, scenario), ShopDatabase.Options(config, scenario), label, null, ct);
        times.Add(sw.Elapsed.TotalSeconds);
        var failed = snapshot.Queries.FirstOrDefault(q => q.Error is not null);
        return failed is null ? snapshot : throw new InvalidOperationException($"{label}: query {failed.Id} failed: {failed.Error}");
    }

    private static ScenarioResult Score(Scenario s, Snapshot baseline, Snapshot candidate)
    {
        var perDetector = new Dictionary<string, DetectorOutcome>(StringComparer.Ordinal);
        foreach (var d in Detectors.All)
        {
            var flagged = baseline.Queries.Where(b => d.Flags(b, candidate.Find(b.Id)!)).Select(b => b.Id).ToList();
            perDetector[d.Id] = new DetectorOutcome(s.Target is not null && flagged.Contains(s.Target), flagged);
        }

        var target = s.Target is null ? null : new TargetNumbers(
            baseline.Find(s.Target)!.MedianSharedBlocks!.Value, candidate.Find(s.Target)!.MedianSharedBlocks!.Value,
            baseline.Find(s.Target)!.MedianTempBlocks!.Value, candidate.Find(s.Target)!.MedianTempBlocks!.Value,
            baseline.Find(s.Target)!.TotalCost, candidate.Find(s.Target)!.TotalCost,
            baseline.Find(s.Target)!.MedianExecutionMs!.Value, candidate.Find(s.Target)!.MedianExecutionMs!.Value);
        var report = SnapshotComparer.Compare(baseline, candidate);
        return new ScenarioResult(s.Id, s.Kind, s.Target, s.Description, perDetector, target,
            s.Target is not null && report.Queries.Single(q => q.Id == s.Target).Verdict == Verdict.Fail,
            report.Overall == Verdict.Fail,
            report.Queries.Where(q => q.Verdict != Verdict.Pass).ToDictionary(q => q.Id, q => q.Verdict.ToString()));
    }

    private static Dictionary<string, AaScore> AaScores(Snapshot baseline, List<Snapshot> reps)
    {
        var result = new Dictionary<string, AaScore>(StringComparer.Ordinal);
        foreach (var d in Detectors.All)
        {
            var pairs = 0;
            var flaggedPairs = 0;
            var failingBuilds = 0;
            foreach (var rep in reps)
            {
                var any = false;
                foreach (var b in baseline.Queries)
                {
                    pairs++;
                    if (d.Flags(b, rep.Find(b.Id)!))
                    {
                        flaggedPairs++;
                        any = true;
                    }
                }

                failingBuilds += any ? 1 : 0;
            }

            result[d.Id] = new AaScore(flaggedPairs, pairs, failingBuilds, reps.Count);
        }

        return result;
    }

    private static List<QueryNoise> NoiseProfile(Snapshot baseline, List<Snapshot> reps) =>
    [
        .. baseline.Queries.Select(b =>
        {
            var all = reps.Select(r => r.Find(b.Id)!).Prepend(b).ToList();
            var single = all.SelectMany(q => q.Runs.Select(r => r.ExecutionMs)).ToList();
            var medians = all.Select(q => q.MedianExecutionMs!.Value).ToList();
            var buffers = all.Select(q => q.MedianSharedBlocks!.Value).ToList();
            return new QueryNoise(b.Id, Stats.Median(medians), Stats.CoefficientOfVariation(single),
                Stats.CoefficientOfVariation(medians), Stats.Median(buffers), Stats.CoefficientOfVariation(buffers),
                single.Max() / single.Min(), all.Select(q => q.Fingerprint).Distinct().Count());
        }),
    ];

    /// <summary>Background load: CPU-heavy aggregates and full scans on a separate copy of the data.</summary>
    private async Task NoiseLoopAsync(int worker, CancellationToken ct)
    {
        await using var ds = NpgsqlDataSource.Create(shop.ConnectionString(Noise));
        var statements = new[]
        {
            "SELECT sum(qty * unit_price) FROM order_items",
            "SELECT count(DISTINCT md5(g::text)) FROM generate_series(1, 400000) AS g",
            "SELECT customer_id, count(*) FROM orders GROUP BY customer_id ORDER BY 2 DESC LIMIT 5",
        };
        var i = worker;
        while (!ct.IsCancellationRequested)
        {
            try
            {
#pragma warning disable CA2100
                await using var cmd = ds.CreateCommand(statements[i++ % statements.Length]);
#pragma warning restore CA2100
                await cmd.ExecuteScalarAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ExecAsync(string database, string sql, CancellationToken ct)
    {
        await using var ds = NpgsqlDataSource.Create(shop.ConnectionString(database));
#pragma warning disable CA2100
        await using var cmd = ds.CreateCommand(sql);
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Db(Scenario s) => "shop_s_" + s.Id.Replace('-', '_');

    internal static string Render(List<ConditionResult> results, ExperimentOptions options)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        foreach (var r in results)
        {
            sb.AppendLine(inv, $"## Condition: {r.Condition} ({r.NoiseWorkers} background load workers, PostgreSQL {r.ServerVersion})");
            sb.AppendLine();
            sb.AppendLine(inv, $"Median capture time for the 10-query catalog (1 warm-up + 5 runs each): {r.MedianCaptureSeconds:0.00} s.");
            sb.AppendLine();
            sb.AppendLine("### Scenario detection (target query flagged?)");
            sb.AppendLine();
            sb.Append("| Scenario | Kind | Target |");
            foreach (var d in Detectors.All)
            {
                sb.Append(inv, $" {d.Id} |");
            }

            sb.AppendLine(" plan-guard verdicts |");
            sb.Append("|---|---|---|");
            sb.Append(string.Concat(Enumerable.Repeat("---|", Detectors.All.Count)));
            sb.AppendLine("---|");
            foreach (var s in r.Scenarios)
            {
                sb.Append(inv, $"| {s.Id} | {s.Kind} | {s.Target ?? "-"} |");
                foreach (var d in Detectors.All)
                {
                    var o = s.Detectors[d.Id];
                    var cell = s.Kind == ScenarioKind.Regression
                        ? (o.TargetFlagged ? "yes" : "**no**")
                        : (o.Flagged.Count == 0 ? "-" : $"**{o.Flagged.Count} FP**");
                    if (s.Kind == ScenarioKind.Improvement && o.Flagged.Count == 0)
                    {
                        cell = "-";
                    }

                    sb.Append(inv, $" {cell} |");
                }

                sb.AppendLine(inv, $" {(s.Verdicts.Count == 0 ? "all pass" : string.Join(", ", s.Verdicts.Select(v => $"{v.Key}={v.Value}")))} |");
            }

            sb.AppendLine();
            sb.AppendLine("### Target query numbers (baseline -> candidate, medians)");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Shared blocks | Temp blocks | Cost | Time (ms) |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var s in r.Scenarios.Where(s => s.Target is not null))
            {
                var t = s.Numbers!;
                sb.AppendLine(inv, $"| {s.Id} | {t.BaselineShared:0} -> {t.CandidateShared:0} | {t.BaselineTemp:0} -> {t.CandidateTemp:0} | {t.BaselineCost:0.0} -> {t.CandidateCost:0.0} | {t.BaselineMs:0.000} -> {t.CandidateMs:0.000} |");
            }

            sb.AppendLine();
            sb.AppendLine(inv, $"### False positives on unchanged code (A/A, {r.Repetitions} repetitions x 10 queries)");
            sb.AppendLine();
            sb.AppendLine("| Detector | Flagged query pairs | Builds that would fail | ... after a fresh ANALYZE: pairs | builds |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var d in Detectors.All)
            {
                var a = r.AaPlain[d.Id];
                var b = r.AaReanalyzed[d.Id];
                sb.AppendLine(inv, $"| {d.Id} | {a.FlaggedPairs}/{a.Pairs} ({100.0 * a.FlaggedPairs / a.Pairs:0.0}%) | {a.FailingBuilds}/{a.Builds} | {b.FlaggedPairs}/{b.Pairs} ({100.0 * b.FlaggedPairs / b.Pairs:0.0}%) | {b.FailingBuilds}/{b.Builds} |");
            }

            sb.AppendLine();
            sb.AppendLine("### Noise profile per query (baseline + A/A captures)");
            sb.AppendLine();
            sb.AppendLine("| Query | Median time (ms) | CV single run | CV of 5-run median | max/min single run | Median shared blocks | CV shared blocks | Distinct plans |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var q in r.Noise)
            {
                sb.AppendLine(inv, $"| {q.Id} | {q.MedianMs:0.000} | {q.CvSingleRun:P1} | {q.CvMedian:P1} | {q.MaxOverMinSingleRun:0.00} | {q.MedianSharedBlocks:0} | {q.CvSharedBlocks:P2} | {q.DistinctPlans} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("Detectors:");
        sb.AppendLine();
        foreach (var d in Detectors.All)
        {
            sb.AppendLine(inv, $"- `{d.Id}`: {d.Description}");
        }

        _ = options;
        return sb.ToString();
    }
}

public sealed record DetectorOutcome(bool TargetFlagged, IReadOnlyList<string> Flagged);

public sealed record TargetNumbers(double BaselineShared, double CandidateShared, double BaselineTemp, double CandidateTemp, double BaselineCost, double CandidateCost, double BaselineMs, double CandidateMs);

public sealed record ScenarioResult(
    string Id, ScenarioKind Kind, string? Target, string Description,
    IReadOnlyDictionary<string, DetectorOutcome> Detectors, TargetNumbers? Numbers,
    bool PlanGuardFailedTarget, bool PlanGuardFailedBuild, IReadOnlyDictionary<string, string> Verdicts);

public sealed record AaScore(int FlaggedPairs, int Pairs, int FailingBuilds, int Builds);

public sealed record QueryNoise(string Id, double MedianMs, double CvSingleRun, double CvMedian, double MedianSharedBlocks, double CvSharedBlocks, double MaxOverMinSingleRun, int DistinctPlans);

public sealed record ConditionResult(
    string Condition, int NoiseWorkers, int Repetitions,
    IReadOnlyDictionary<string, AaScore> AaPlain, IReadOnlyDictionary<string, AaScore> AaReanalyzed,
    IReadOnlyList<ScenarioResult> Scenarios, IReadOnlyList<QueryNoise> Noise, double MedianCaptureSeconds, string ServerVersion);
