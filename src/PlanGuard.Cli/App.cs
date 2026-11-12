using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using PlanGuard.Comparison;
using PlanGuard.Configuration;
using PlanGuard.GitHub;
using PlanGuard.Postgres;
using PlanGuard.Reporting;
using PlanGuard.Snapshots;

namespace PlanGuard.Cli;

/// <summary>Command dispatch. Exit codes: 0 ok, 1 regression found, 2 usage error, 3 runtime error.</summary>
internal sealed partial class App(TextWriter stdout, TextWriter stderr)
{
    public const int Ok = 0;
    public const int Regression = 1;
    public const int Usage = 2;
    public const int RuntimeError = 3;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "github", "help" };

    public const string HelpText = """
        plan-guard: catch PostgreSQL query-plan regressions in CI.

        Commands
          capture   Run the query catalog with EXPLAIN ANALYZE and write a snapshot.
                      --config plan-guard.json  --connection <cs> (or PLANGUARD_CONNECTION)
                      --label <name>  --git-sha <sha> (or GITHUB_SHA)  --out snapshot.json
                      --store-connection <cs> (or PLANGUARD_STORE_CONNECTION) to also save it to the history store
          discover  Plan the statements your test suite ran (pg_stat_statements) with EXPLAIN (GENERIC_PLAN).
                      --connection <cs>  --label <name>  --out snapshot.json  --min-calls 1  --limit 200
          compare   Compare a candidate snapshot with a baseline; exit 1 on a regression.
                      --baseline base.json | --baseline-label main --store-connection <cs>
                      --candidate head.json  --config plan-guard.json  --fail-on fail|warn
                      --markdown report.md  --json report.json  --github (annotations + job summary)
          comment   Create or update the plan-guard comment on a pull request.
                      --report report.md  --pr <number>  --repo owner/name (or GITHUB_REPOSITORY)
                      token from GITHUB_TOKEN; --api-url (or GITHUB_API_URL)  --author-id 41898282
          history   List stored snapshots.  --store-connection <cs>  --label <name>  --limit 20

        Exit codes: 0 ok, 1 regression, 2 usage error, 3 runtime error.
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                await stdout.WriteLineAsync(HelpText);
                return args.Length == 0 ? Usage : Ok;
            }

            if (args[0] is "--version" or "version")
            {
                await stdout.WriteLineAsync(typeof(App).Assembly.GetName().Version?.ToString(3));
                return Ok;
            }

            var cl = CommandLine.Parse(args, Flags);
            return cl.Command switch
            {
                "capture" => await CaptureAsync(cl, ct),
                "discover" => await DiscoverAsync(cl, ct),
                "compare" => await CompareAsync(cl, ct),
                "comment" => await CommentAsync(cl, ct),
                "history" => await HistoryAsync(cl, ct),
                _ => throw new UsageException($"Unknown command '{cl.Command}'."),
            };
        }
        catch (UsageException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            await stderr.WriteLineAsync("Run 'plan-guard --help' for usage.");
            return Usage;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException or NpgsqlException or InvalidOperationException
                                       or NotSupportedException or HttpRequestException or UnauthorizedAccessException or ArgumentException)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            return RuntimeError;
        }
    }

    private async Task<int> CaptureAsync(CommandLine cl, CancellationToken ct)
    {
        var configPath = cl.Get("config") ?? "plan-guard.json";
        var connection = cl.Require("connection", "PLANGUARD_CONNECTION");
        var label = cl.Get("label") ?? "candidate";
        var sha = Sha(cl.Get("git-sha", "GITHUB_SHA"));
        var output = cl.Get("out") ?? "plan-guard-snapshot.json";
        var store = cl.Get("store-connection", "PLANGUARD_STORE_CONNECTION");
        cl.EnsureAllUsed();
        var config = PlanGuardConfig.Load(configPath);

        var queries = config.LoadQueries();
        await using var ds = NpgsqlDataSource.Create(connection);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await new PostgresCapturer(ds).CaptureAsync(queries, CaptureOptions.From(config), label, sha, ct);
        await SnapshotJson.WriteAsync(snapshot, output, ct);
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"captured {snapshot.Queries.Count} queries ({config.Warmups} warm-up + {config.Runs} runs each) in {started.Elapsed.TotalSeconds:0.0} s -> {output}"));
        foreach (var failed in snapshot.Queries.Where(q => q.Error is not null))
        {
            await stderr.WriteLineAsync($"  query {failed.Id} failed: {failed.Error}");
        }

        if (!snapshot.Meta.StatementStatsAvailable)
        {
            await stdout.WriteLineAsync("  note: pg_stat_statements not available to this session; statement counters left empty");
        }

        await SaveToStoreAsync(store, snapshot, ct);
        return Ok;
    }

    private async Task<int> DiscoverAsync(CommandLine cl, CancellationToken ct)
    {
        var connection = cl.Require("connection", "PLANGUARD_CONNECTION");
        var configPath = cl.Get("config");
        var minCalls = cl.Int("min-calls", 1, 1, int.MaxValue);
        var limit = cl.Int("limit", 200, 1, 10_000);
        var label = cl.Get("label") ?? "candidate";
        var sha = Sha(cl.Get("git-sha", "GITHUB_SHA"));
        var output = cl.Get("out") ?? "plan-guard-discovered.json";
        var store = cl.Get("store-connection", "PLANGUARD_STORE_CONNECTION");
        cl.EnsureAllUsed();
        var config = configPath is null ? null : PlanGuardConfig.Load(configPath);
        var options = new DiscoveryOptions
        {
            MinCalls = minCalls,
            Limit = limit,
            StatementTimeoutMs = config?.StatementTimeoutMs ?? 30_000,
            Settings = config?.Settings ?? new DiscoveryOptions().Settings,
        };

        await using var ds = NpgsqlDataSource.Create(connection);
        var result = await new WorkloadDiscovery(ds).DiscoverAsync(options, label, sha, ct);
        await SnapshotJson.WriteAsync(result.Snapshot, output, ct);
        await stdout.WriteLineAsync($"discovered {result.Snapshot.Queries.Count} statements ({result.Skipped.Count} skipped) -> {output}");
        foreach (var (sql, reason) in result.Skipped.Take(20))
        {
            await stdout.WriteLineAsync($"  skipped: {reason}: {Truncate(sql, 100)}");
        }

        await SaveToStoreAsync(store, result.Snapshot, ct);
        return Ok;
    }

    private async Task<int> CompareAsync(CommandLine cl, CancellationToken ct)
    {
        var configPath = cl.Get("config");
        var candidatePath = cl.Require("candidate");
        var baselinePath = cl.Get("baseline");
        var baselineLabel = cl.Get("baseline-label");
        var store = cl.Get("store-connection", "PLANGUARD_STORE_CONNECTION");
        var markdownPath = cl.Get("markdown");
        var jsonPath = cl.Get("json");
        var github = cl.Flag("github");
        FailOn? failOnOption = cl.Get("fail-on") switch
        {
            null => null,
            "fail" => FailOn.Fail,
            "warn" => FailOn.Warn,
            var other => throw new UsageException($"--fail-on must be 'fail' or 'warn', not '{other}'."),
        };
        cl.EnsureAllUsed();

        if ((baselinePath is null) == (baselineLabel is null))
        {
            throw new UsageException("Give exactly one of --baseline <file> or --baseline-label <label>.");
        }

        var config = configPath is null ? new PlanGuardConfig() : PlanGuardConfig.Load(configPath);
        var failOn = failOnOption ?? config.FailOn;

        Snapshot baseline;
        if (baselinePath is not null)
        {
            baseline = await SnapshotJson.ReadAsync(baselinePath, ct);
        }
        else
        {
            if (store is null)
            {
                throw new UsageException("--baseline-label needs --store-connection (or PLANGUARD_STORE_CONNECTION).");
            }

            await using var ds = NpgsqlDataSource.Create(store);
            baseline = await new PostgresSnapshotStore(ds).LatestAsync(baselineLabel!, ct)
                ?? throw new InvalidOperationException($"No stored snapshot with label '{baselineLabel}'.");
        }

        var candidate = await SnapshotJson.ReadAsync(candidatePath, ct);
        var report = SnapshotComparer.Compare(baseline, candidate, config.Thresholds, config.Ignore.ToHashSet(StringComparer.Ordinal));
        var markdown = MarkdownReport.Render(report);

        await stdout.WriteAsync(ConsoleReport.Render(report));
        if (markdownPath is not null)
        {
            await File.WriteAllTextAsync(markdownPath, markdown, ct);
        }

        if (jsonPath is not null)
        {
            await File.WriteAllTextAsync(jsonPath, JsonReport.Render(report), ct);
        }

        if (github)
        {
            foreach (var line in GitHubAnnotations.Render(report))
            {
                await stdout.WriteLineAsync(line);
            }

            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            {
                await File.AppendAllTextAsync(summary, markdown + Environment.NewLine, ct);
            }
        }

        return report.ExitCode(failOn) == 0 ? Ok : Regression;
    }

    private async Task<int> CommentAsync(CommandLine cl, CancellationToken ct)
    {
        var reportPath = cl.Require("report");
        var repo = cl.Require("repo", "GITHUB_REPOSITORY");
        var pr = cl.Int("pr", 0, 1, int.MaxValue);
        var apiUrl = cl.Get("api-url", "GITHUB_API_URL") ?? "https://api.github.com";
        var authorId = cl.Int("author-id", (int)PullRequestCommenter.ActionsTokenUserId, 1, int.MaxValue);
        cl.EnsureAllUsed();
        if (pr == 0)
        {
            throw new UsageException("Option --pr is required.");
        }

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            throw new UsageException("The GITHUB_TOKEN environment variable is required (it is never accepted on the command line).");
        }

        var body = await File.ReadAllTextAsync(reportPath, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var commenter = new PullRequestCommenter(http, new Uri(apiUrl.TrimEnd('/') + "/"), token, authorId);
        var (outcome, id) = await commenter.UpsertAsync(repo, pr, body, ct);
        await stdout.WriteLineAsync($"{outcome.ToString().ToLowerInvariant()} comment {id} on {repo}#{pr}");
        return Ok;
    }

    private async Task<int> HistoryAsync(CommandLine cl, CancellationToken ct)
    {
        var store = cl.Require("store-connection", "PLANGUARD_STORE_CONNECTION");
        var label = cl.Get("label");
        var limit = cl.Int("limit", 20, 1, 1000);
        cl.EnsureAllUsed();
        await using var ds = NpgsqlDataSource.Create(store);
        var s = new PostgresSnapshotStore(ds);
        await s.EnsureSchemaAsync(ct);
        foreach (var row in await s.ListAsync(label, limit, ct))
        {
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{row.Id,6}  {row.CapturedAt:yyyy-MM-dd HH:mm:ss}  {row.Label,-20} {row.GitSha?[..Math.Min(7, row.GitSha.Length)] ?? "-",-8} PG {row.ServerVersion,-8} {row.QueryCount} queries"));
        }

        return Ok;
    }

    private async Task SaveToStoreAsync(string? store, Snapshot snapshot, CancellationToken ct)
    {
        if (store is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(store);
        var s = new PostgresSnapshotStore(ds);
        await s.EnsureSchemaAsync(ct);
        var id = await s.SaveAsync(snapshot, ct);
        await stdout.WriteLineAsync($"  stored as snapshot {id} (label {snapshot.Meta.Label})");
    }

    internal static string? Sha(string? sha)
    {
        if (sha is null)
        {
            return null;
        }

        sha = sha.Trim().ToLowerInvariant();
        return ShaPattern().IsMatch(sha) ? sha : throw new UsageException($"'{sha}' is not a git commit SHA.");
    }

    private static string Truncate(string s, int max)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "...";
    }

    [GeneratedRegex("^[0-9a-f]{7,64}$")]
    private static partial Regex ShaPattern();
}
