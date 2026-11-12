using FsCheck;
using FsCheck.Fluent;
using PlanGuard.Comparison;
using PlanGuard.Configuration;
using PlanGuard.Sql;

namespace PlanGuard.UnitTests.Configuration;

public sealed class ConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plan-guard-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_apply_when_the_file_is_minimal()
    {
        var config = PlanGuardConfig.Parse("{}", _dir);

        config.Runs.Should().Be(5);
        config.Warmups.Should().Be(1);
        config.Settings.Should().ContainKey("jit").WhoseValue.Should().Be("off");
        config.Thresholds.Should().Be(new Thresholds());
        config.FailOn.Should().Be(FailOn.Fail);
    }

    [Fact]
    public void Reads_the_sample_config_with_comments()
    {
        var config = PlanGuardConfig.Load(Path.Combine(AppContext.BaseDirectory, "shop", "plan-guard.json"));

        config.Thresholds.BufferRatio.Should().Be(1.3);
        config.Thresholds.TimeGate.Should().Be(Gate.Warn);
        config.LoadQueries().Should().HaveCount(10).And.Contain(q => q.Id == "customer_recent_orders");
    }

    [Theory]
    [InlineData("""{"thresholds": {"bufferRatoi": 2}}""")]
    [InlineData("""{"runs": 0}""")]
    [InlineData("""{"warmups": 50}""")]
    [InlineData("""{"settings": {"work_mem; DROP TABLE x": "1"}}""")]
    [InlineData("""{"thresholds": {"costRatio": 0.5}}""")]
    [InlineData("""{"failOn": "sometimes"}""")]
    public void Rejects_invalid_configs_instead_of_falling_back_to_defaults(string json)
    {
        var act = () => PlanGuardConfig.Parse(json, _dir);

        act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is FormatException || e is ArgumentOutOfRangeException);
    }

    [Fact]
    public void Query_ids_come_from_file_paths_below_the_catalog_folder()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "queries", "reports"));
        File.WriteAllText(Path.Combine(_dir, "queries", "a.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_dir, "queries", "reports", "b.sql"), "-- comment\nSELECT 2");

        var queries = PlanGuardConfig.Parse("""{"queries": ["queries/**/*.sql"]}""", _dir).LoadQueries();

        queries.Select(q => q.Id).Should().Equal("a", "reports/b");
        queries[0].Sql.Should().Be("SELECT 1");
    }

    [Fact]
    public void No_matching_query_files_is_an_error()
    {
        var act = () => PlanGuardConfig.Parse("{}", _dir).LoadQueries();

        act.Should().Throw<FormatException>().WithMessage("No query files match*");
    }

    [Fact]
    public void A_file_with_two_statements_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "queries"));
        File.WriteAllText(Path.Combine(_dir, "queries", "evil.sql"), "SELECT 1; DROP TABLE orders");

        var act = () => PlanGuardConfig.Parse("{}", _dir).LoadQueries();

        act.Should().Throw<FormatException>().WithMessage("*exactly one*");
    }
}

public sealed class SqlTextTests
{
    [Theory]
    [InlineData("SELECT 1", "SELECT 1")]
    [InlineData("SELECT 1;", "SELECT 1")]
    [InlineData("  SELECT 1 ;  \n -- trailing comment\n", "SELECT 1")]
    [InlineData("SELECT ';' AS semi", "SELECT ';' AS semi")]
    [InlineData("SELECT 'it''s; fine'", "SELECT 'it''s; fine'")]
    [InlineData("SELECT E'back\\'slash;'", "SELECT E'back\\'slash;'")]
    [InlineData("SELECT \"we;ird\" FROM t", "SELECT \"we;ird\" FROM t")]
    [InlineData("SELECT $$a;b$$", "SELECT $$a;b$$")]
    [InlineData("SELECT $fn$ x; $$ y; $fn$", "SELECT $fn$ x; $$ y; $fn$")]
    [InlineData("SELECT 1 /* a; /* nested; */ b; */", "SELECT 1 /* a; /* nested; */ b; */")]
    [InlineData("SELECT 1 -- c; d", "SELECT 1 -- c; d")]
    [InlineData("SELECT * FROM t WHERE id = $1", "SELECT * FROM t WHERE id = $1")]
    public void Single_statements_are_accepted(string sql, string expected)
    {
        SqlText.EnsureSingleStatement(sql).Should().Be(expected);
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; DROP TABLE orders;")]
    [InlineData("SELECT 'a'; DELETE FROM t")]
    [InlineData("SELECT $$x$$; COMMIT")]
    [InlineData("SELECT 1 /* c */; VACUUM")]
    public void Multiple_statements_are_refused(string sql)
    {
        var act = () => SqlText.EnsureSingleStatement(sql);

        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ;  ; ")]
    [InlineData("-- only a comment")]
    public void Empty_files_are_refused(string sql)
    {
        var act = () => SqlText.EnsureSingleStatement(sql);

        act.Should().Throw<FormatException>().WithMessage("*no SQL*");
    }

    [Fact]
    public void Normalisation_ignores_whitespace_and_comments_but_not_literals()
    {
        SqlText.Normalize("SELECT  a,\n\tb -- x\nFROM t /* y */ WHERE c = 'two  spaces';")
            .Should().Be("SELECT a, b FROM t WHERE c = 'two  spaces'");
        SqlText.StatementId("select * from t where a = $1").Should().Be(SqlText.StatementId("select *\n  from t\n where a = $1 -- comment"));
        SqlText.StatementId("select 'a'").Should().NotBe(SqlText.StatementId("select 'b'"));
        SqlText.StatementId("select 1").Should().MatchRegex("^q[0-9a-f]{12}$");
    }

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("  with x as (select 1) select * from x", true)]
    [InlineData("/* c */ UPDATE t SET a = $1", true)]
    [InlineData("INSERT INTO t VALUES ($1)", true)]
    [InlineData("SET work_mem = '1MB'", false)]
    [InlineData("BEGIN", false)]
    [InlineData("CREATE TABLE x (a int)", false)]
    [InlineData("EXPLAIN SELECT 1", false)]
    [InlineData("'select'", false)]
    public void Only_plannable_statements_are_explainable(string sql, bool expected)
    {
        SqlText.IsExplainable(sql).Should().Be(expected);
    }

    [FsCheck.Xunit.Property]
    public Property Semicolons_inside_literals_never_split_a_statement() =>
        Prop.ForAll(Gen.Elements("a", ";", " ", "''", "\"", "$", "--", "/*", "*/", "x;y").ListOf().Select(parts => string.Concat(parts)).ToArbitrary(), content =>
        {
            var sql = $"SELECT $body${content.Replace("$body$", string.Empty, StringComparison.Ordinal)}$body$ AS v";
            return SqlText.EnsureSingleStatement(sql) == sql;
        });

    [FsCheck.Xunit.Property]
    public Property Joining_two_statements_is_always_detected() =>
        Prop.ForAll(Gen.Elements("SELECT 1", "SELECT ';'", "SELECT $$;$$", "UPDATE t SET a = 1", "SELECT 1 /* ; */").ToArbitrary(),
            Gen.Elements("SELECT 2", "DROP TABLE t", "DELETE FROM t", "COMMIT").ToArbitrary(),
            (a, b) =>
            {
                try
                {
                    SqlText.EnsureSingleStatement(a + "; " + b);
                    return false;
                }
                catch (FormatException)
                {
                    return true;
                }
            });
}
