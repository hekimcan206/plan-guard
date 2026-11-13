using System.Globalization;
using Npgsql;
using PlanGuard.Comparison;
using PlanGuard.Lab;
using PlanGuard.Postgres;
using PlanGuard.Reporting;
using PlanGuard.Snapshots;

// plan-guard-lab: seeds the synthetic shop database, applies scenarios, runs the end-to-end demo and the
// detector experiment. The admin connection must be allowed to CREATE DATABASE.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var command = args.Length > 0 ? args[0] : "help";
string? Opt(string name) => Array.IndexOf(args, "--" + name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var admin = Opt("connection") ?? Environment.GetEnvironmentVariable("PLANGUARD_ADMIN_CONNECTION");

try
{
    if (command is "help" or "--help" || admin is null)
    {
        Console.WriteLine("""
            plan-guard-lab <command> --connection <admin connection string> (or PLANGUARD_ADMIN_CONNECTION)
              seed        --database shop                 create and seed the synthetic shop database
              apply       --database shop --scenario <id> apply a scenario's migration
              queries     --scenario <id> --out <dir>     write the scenario's edited catalog (plan-guard.json + queries/)
              demo        --out <dir>                     base vs a PR with 3 changes; prints the report, exits 0 when it caught them
              experiment  --out <dir> --reps 20 --noise 4 [--only quiet|loaded]
              scenarios                                   list the scenarios
            """);
        return command is "help" or "--help" ? 0 : 2;
    }

    var shop = new ShopDatabase(admin, Opt("samples") ?? ShopDatabase.FindSampleDirectory());
    switch (command)
    {
        case "seed":
            {
                var db = Opt("database") ?? "shop";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await shop.CreateAsync(db, cts.Token);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seeded {db} in {sw.Elapsed.TotalSeconds:0.0} s"));
                return 0;
            }

        case "apply":
            {
                var scenario = ScenarioCatalog.Get(Opt("scenario") ?? throw new ArgumentException("--scenario is required"));
                await shop.ApplyAsync(Opt("database") ?? "shop", scenario, cts.Token);
                Console.WriteLine($"applied {scenario.Id}: {scenario.Description}");
                return 0;
            }

        case "queries":
            {
                var scenario = ScenarioCatalog.Get(Opt("scenario") ?? throw new ArgumentException("--scenario is required"));
                var outDir = Opt("out") ?? throw new ArgumentException("--out is required");
                Directory.CreateDirectory(Path.Combine(outDir, "queries"));
                File.Copy(Path.Combine(shop.SampleDirectory, "plan-guard.json"), Path.Combine(outDir, "plan-guard.json"), overwrite: true);
                foreach (var q in ShopDatabase.Catalog(shop.LoadConfig(), scenario))
                {
                    await File.WriteAllTextAsync(Path.Combine(outDir, "queries", q.Id + ".sql"), q.Sql + "\n", cts.Token);
                }

                Console.WriteLine($"wrote the {scenario.Id} catalog to {outDir}");
                return 0;
            }

        case "scenarios":
            foreach (var s in ScenarioCatalog.All)
            {
                Console.WriteLine($"{s.Id,-24} {s.Kind,-12} {s.Target ?? "-",-24} {s.Description}");
            }

            return 0;

        case "demo":
            return await Demo.RunAsync(shop, Opt("out") ?? "demo-output", cts.Token);

        case "experiment":
            var only = Opt("only");
            return await new Experiment(shop, Console.Out).RunAsync(new ExperimentOptions
            {
                OutputDirectory = Opt("out") ?? "benchmarks/results",
                Repetitions = int.Parse(Opt("reps") ?? "20", CultureInfo.InvariantCulture),
                NoiseWorkers = int.Parse(Opt("noise") ?? "4", CultureInfo.InvariantCulture),
                Quiet = only is null or "quiet",
                Loaded = only is null or "loaded",
            }, cts.Token);

        default:
            Console.Error.WriteLine($"unknown command '{command}'");
            return 2;
    }
}
catch (Exception ex) when (ex is NpgsqlException or ArgumentException or InvalidOperationException or IOException or FormatException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 3;
}

/// <summary>The docker compose demo: one pull request that drops an index, switches to OFFSET pagination and adds a covering index.</summary>
internal static class Demo
{
    private static readonly string[] PullRequest = ["drop-customer-index", "offset-pagination", "covering-index"];

    public static async Task<int> RunAsync(ShopDatabase shop, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var config = shop.LoadConfig();
        if (!await shop.ExistsAsync("shop_base", ct))
        {
            Console.WriteLine("seeding the synthetic shop database (300k orders, 900k order lines) ...");
            await shop.CreateAsync("shop_base", ct);
        }

        await shop.CloneAsync("shop_base", "shop_main", ct);
        await shop.CloneAsync("shop_base", "shop_pr", ct);
        var scenarios = PullRequest.Select(ScenarioCatalog.Get).ToList();
        foreach (var s in scenarios)
        {
            await shop.ApplyAsync("shop_pr", s, ct);
        }

        var edits = scenarios.Where(s => s.QueryEdits is not null).SelectMany(s => s.QueryEdits!).ToDictionary(kv => kv.Key, kv => kv.Value);
        var prScenario = new Scenario("demo-pr", ScenarioKind.Regression, null, "combined", QueryEdits: edits);

        Snapshot baseline, candidate;
        await using (var ds = NpgsqlDataSource.Create(shop.ConnectionString("shop_main")))
        {
            baseline = await new PostgresCapturer(ds).CaptureAsync(ShopDatabase.Catalog(config, null), ShopDatabase.Options(config, null), "main", null, ct);
        }

        await using (var ds = NpgsqlDataSource.Create(shop.ConnectionString("shop_pr")))
        {
            candidate = await new PostgresCapturer(ds).CaptureAsync(ShopDatabase.Catalog(config, prScenario), ShopDatabase.Options(config, null), "pr-42", null, ct);
        }

        await SnapshotJson.WriteAsync(baseline, Path.Combine(outDir, "baseline.json"), ct);
        await SnapshotJson.WriteAsync(candidate, Path.Combine(outDir, "candidate.json"), ct);
        var report = SnapshotComparer.Compare(baseline, candidate, config.Thresholds, config.Ignore.ToHashSet());
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), MarkdownReport.Render(report), ct);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.json"), JsonReport.Render(report), ct);
        Console.WriteLine(ConsoleReport.Render(report));

        var caught = report.Queries.Single(q => q.Id == "customer_recent_orders").Verdict == Verdict.Fail
                     && report.Queries.Single(q => q.Id == "orders_page").Verdict == Verdict.Fail
                     && report.Queries.Single(q => q.Id == "top_customers").Verdict == Verdict.Improved
                     && report.ExitCode(FailOn.Fail) == 1;
        Console.WriteLine(caught
            ? "DEMO_OK plan-guard failed the PR on both regressions and reported the improvement"
            : "DEMO_UNEXPECTED the verdicts differ from the expected ones (see above)");
        await shop.DropAsync("shop_main", ct);
        await shop.DropAsync("shop_pr", ct);
        return caught ? 0 : 1;
    }
}
