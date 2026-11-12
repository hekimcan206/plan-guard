using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using PlanGuard.Comparison;
using PlanGuard.Snapshots;
using PlanGuard.Sql;

namespace PlanGuard.Configuration;

/// <summary>Contents of <c>plan-guard.json</c>. Paths are relative to the file's directory.</summary>
public sealed partial record PlanGuardConfig
{
    public IReadOnlyList<string> Queries { get; init; } = ["queries/**/*.sql"];
    public int Runs { get; init; } = 5;
    public int Warmups { get; init; } = 1;
    public int StatementTimeoutMs { get; init; } = 30_000;

    /// <summary>
    /// Session settings applied before every capture. JIT is off by default: whether it kicks in depends on cost
    /// thresholds, and its compile time adds tens of milliseconds of noise that has nothing to do with the plan.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string> { ["jit"] = "off" };

    public Thresholds Thresholds { get; init; } = new();
    public IReadOnlyList<string> Ignore { get; init; } = [];
    public FailOn FailOn { get; init; } = FailOn.Fail;

    /// <summary>Directory the config file lives in; query globs resolve against it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BaseDirectory { get; init; } = Directory.GetCurrentDirectory();

    private static readonly JsonSerializerOptions JsonOptions = new(SnapshotJson.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static PlanGuardConfig Parse(string json, string baseDirectory)
    {
        PlanGuardConfig config;
        try
        {
            config = JsonSerializer.Deserialize<PlanGuardConfig>(json, JsonOptions) ?? new PlanGuardConfig();
        }
        catch (JsonException ex)
        {
            // A typo in a threshold name must not silently fall back to the default.
            throw new FormatException($"Invalid plan-guard config: {ex.Message}", ex);
        }

        config = config with { BaseDirectory = baseDirectory };
        config.Validate();
        return config;
    }

    public static PlanGuardConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        return Parse(File.ReadAllText(full), Path.GetDirectoryName(full)!);
    }

    public void Validate()
    {
        if (Runs is < 1 or > 100)
        {
            throw new FormatException("runs must be between 1 and 100.");
        }

        if (Warmups is < 0 or > 20)
        {
            throw new FormatException("warmups must be between 0 and 20.");
        }

        if (StatementTimeoutMs < 0)
        {
            throw new FormatException("statementTimeoutMs must not be negative.");
        }

        foreach (var key in Settings.Keys)
        {
            if (!SettingName().IsMatch(key))
            {
                throw new FormatException($"'{key}' is not a valid PostgreSQL setting name.");
            }
        }

        Thresholds.Validate();
    }

    /// <summary>Loads the catalog: one query per .sql file, named after the file (path relative to the base, without extension).</summary>
    public IReadOnlyList<QueryDefinition> LoadQueries()
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        foreach (var pattern in Queries)
        {
            matcher.AddInclude(pattern);
        }

        var files = matcher.GetResultsInFullPath(BaseDirectory).Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            throw new FormatException($"No query files match {string.Join(", ", Queries)} under {BaseDirectory}.");
        }

        var result = new List<QueryDefinition>();
        foreach (var file in files)
        {
            var id = QueryId(Path.GetRelativePath(BaseDirectory, file));
            result.Add(QueryDefinition.Create(id, File.ReadAllText(file)));
        }

        var dup = result.GroupBy(q => q.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return dup is null ? result : throw new FormatException($"Two query files map to the id '{dup.Key}'.");
    }

    internal static string QueryId(string relativePath)
    {
        var noExt = Path.ChangeExtension(relativePath, null)!.Replace('\\', '/');
        // The first path segment is the catalog folder itself ("queries/a.sql" -> "a"); deeper folders stay in the id.
        var slash = noExt.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? noExt[(slash + 1)..] : noExt;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_.]{0,62}$")]
    private static partial Regex SettingName();
}

/// <summary>A named catalog query.</summary>
public sealed record QueryDefinition(string Id, string Sql)
{
    public static QueryDefinition Create(string id, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new QueryDefinition(id, SqlText.EnsureSingleStatement(sql));
    }
}
