using PlanGuard.Cli;
using PlanGuard.Snapshots;
using static PlanGuard.UnitTests.TestData;

namespace PlanGuard.UnitTests.Cli;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plan-guard-cli-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Task<int> Run(params string[] args) => new App(_out, _err).RunAsync(args, TestContext.Current.CancellationToken);

    private string Write(string name, Snapshot snapshot)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, SnapshotJson.Serialize(snapshot));
        return path;
    }

    [Fact]
    public async Task Compare_exits_1_on_a_regression_and_writes_reports()
    {
        var b = Write("base.json", Snapshot("main", Query("q", shared: 100)));
        var c = Write("head.json", Snapshot("pr", Query("q", shared: 5000)));
        var md = Path.Combine(_dir, "report.md");
        var json = Path.Combine(_dir, "report.json");

        var code = await Run("compare", "--baseline", b, "--candidate", c, "--markdown", md, "--json", json);

        code.Should().Be(App.Regression);
        File.ReadAllText(md).Should().Contain("FAILED");
        File.ReadAllText(json).Should().Contain("\"overall\": \"fail\"");
        _out.ToString().Should().Contain("FAIL     q");
    }

    [Fact]
    public async Task Compare_exits_0_when_nothing_regressed()
    {
        var b = Write("base.json", Snapshot("main", Query("q")));
        var c = Write("head.json", Snapshot("pr", Query("q")));

        (await Run("compare", "--baseline", b, "--candidate", c)).Should().Be(App.Ok);
    }

    [Fact]
    public async Task Fail_on_warn_turns_warnings_into_a_failing_exit_code()
    {
        var b = Write("base.json", Snapshot("main", Query("q", ms: 1)));
        var c = Write("head.json", Snapshot("pr", Query("q", ms: 50)));

        (await Run("compare", "--baseline", b, "--candidate", c)).Should().Be(App.Ok);
        (await Run("compare", "--baseline", b, "--candidate", c, "--fail-on", "warn")).Should().Be(App.Regression);
    }

    [Fact]
    public async Task Github_flag_prints_annotations_and_appends_the_job_summary()
    {
        var b = Write("base.json", Snapshot("main", Query("q", shared: 100)));
        var c = Write("head.json", Snapshot("pr", Query("q", shared: 5000)));
        var summary = Path.Combine(_dir, "summary.md");
        Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", summary);
        try
        {
            await Run("compare", "--baseline", b, "--candidate", c, "--github");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
        }

        _out.ToString().Should().Contain("::error title=plan-guard q::");
        File.ReadAllText(summary).Should().Contain("<!-- plan-guard:report -->");
    }

    [Theory]
    [InlineData("")]
    [InlineData("frobnicate")]
    [InlineData("compare --candidate")]
    [InlineData("compare --candidate x.json")]
    [InlineData("compare --baseline a --baseline-label main --candidate b")]
    [InlineData("compare --baseline a --candidate b --fail-on maybe")]
    [InlineData("compare --baseline a --candidate b --colour on")]
    [InlineData("compare --baseline a --baseline b")]
    [InlineData("capture --git-sha not-a-sha --connection Host=x --config missing.json")]
    public async Task Usage_errors_exit_2(string commandLine)
    {
        (await Run(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))).Should().Be(App.Usage);
    }

    [Fact]
    public async Task Unreadable_snapshot_is_a_runtime_error()
    {
        var b = Path.Combine(_dir, "broken.json");
        File.WriteAllText(b, "{ not json");
        var c = Write("head.json", Snapshot("pr", Query("q")));

        (await Run("compare", "--baseline", b, "--candidate", c)).Should().Be(App.RuntimeError);
        _err.ToString().Should().StartWith("error:");
    }

    [Fact]
    public async Task Comment_requires_the_token_from_the_environment_only()
    {
        var md = Path.Combine(_dir, "r.md");
        File.WriteAllText(md, "x");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        (await Run("comment", "--report", md, "--repo", "acme/shop", "--pr", "1")).Should().Be(App.Usage);
        _err.ToString().Should().Contain("GITHUB_TOKEN");
    }

    [Fact]
    public async Task Help_and_version()
    {
        (await Run("--help")).Should().Be(App.Ok);
        _out.ToString().Should().Contain("Commands");
        (await Run("--version")).Should().Be(App.Ok);
        _out.ToString().Should().Contain("1.0.0");
    }

    [Theory]
    [InlineData("ABCDEF1234567", "abcdef1234567")]
    [InlineData(" 0123456789abcdef0123456789abcdef01234567 ", "0123456789abcdef0123456789abcdef01234567")]
    public void Git_shas_are_normalised(string input, string expected)
    {
        App.Sha(input).Should().Be(expected);
    }
}
