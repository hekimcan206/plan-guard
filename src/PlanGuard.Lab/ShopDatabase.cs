using System.Text.RegularExpressions;
using Npgsql;
using PlanGuard.Configuration;
using PlanGuard.Postgres;

namespace PlanGuard.Lab;

/// <summary>Creates, clones and changes copies of the synthetic shop database.</summary>
public sealed partial class ShopDatabase(string adminConnectionString, string sampleDirectory)
{
    public string SampleDirectory { get; } = sampleDirectory;

    /// <summary>Locates samples/shop next to the binary (copied there by the build) or up the source tree.</summary>
    public static string FindSampleDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "shop"), Path.Combine(dir.FullName, "samples", "shop") })
            {
                if (File.Exists(Path.Combine(candidate, "plan-guard.json")))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("samples/shop not found next to the binary or in any parent directory.");
    }

    public string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;

    /// <summary>A connection string that bypasses the pool, for databases that will be used as clone templates.</summary>
    private string Unpooled(string database) =>
        new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database, Pooling = false }.ConnectionString;

    public async Task<bool> ExistsAsync(string database, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(Unpooled("postgres"));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)", conn);
        cmd.Parameters.AddWithValue(database);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Creates the database with the shop schema and the deterministic seed.</summary>
    public async Task CreateAsync(string database, CancellationToken ct = default)
    {
        await AdminAsync($"DROP DATABASE IF EXISTS {Ident(database)} WITH (FORCE)", ct);
        await AdminAsync($"CREATE DATABASE {Ident(database)}", ct);
        await using var conn = new NpgsqlConnection(Unpooled(database));
        await conn.OpenAsync(ct);
        foreach (var file in new[] { "schema.sql", "seed.sql" })
        {
#pragma warning disable CA2100 // Trusted sample scripts shipped with the tool.
            await using var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(SampleDirectory, file), ct), conn) { CommandTimeout = 600 };
#pragma warning restore CA2100
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // VACUUM sets the visibility map (without it index-only scans are never chosen) and ANALYZE gathers
        // statistics once. Clones copy pg_statistic, so every copy plans from the same statistics.
        foreach (var sql in new[] { "VACUUM (ANALYZE)" })
        {
#pragma warning disable CA2100
            await using var vacuum = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
#pragma warning restore CA2100
            await vacuum.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>CREATE DATABASE ... TEMPLATE: a file-level copy, much faster than re-seeding.</summary>
    public async Task CloneAsync(string template, string database, CancellationToken ct = default)
    {
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"DROP DATABASE IF EXISTS {Ident(database)} WITH (FORCE)", ct);
        await AdminAsync($"CREATE DATABASE {Ident(database)} TEMPLATE {Ident(template)}", ct);
    }

    public async Task DropAsync(string database, CancellationToken ct = default)
    {
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"DROP DATABASE IF EXISTS {Ident(database)} WITH (FORCE)", ct);
    }

    /// <summary>Applies the scenario's migration, if it has one.</summary>
    public async Task ApplyAsync(string database, Scenario scenario, CancellationToken ct = default)
    {
        if (scenario.Migration is null)
        {
            return;
        }

        await using var conn = new NpgsqlConnection(Unpooled(database));
        await conn.OpenAsync(ct);
#pragma warning disable CA2100 // Migrations are compiled-in scenario definitions.
        await using var cmd = new NpgsqlCommand(scenario.Migration, conn) { CommandTimeout = 600 };
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public PlanGuardConfig LoadConfig() => PlanGuardConfig.Load(Path.Combine(SampleDirectory, "plan-guard.json"));

    /// <summary>The catalog as the pull request would have it: base queries with the scenario's edits applied.</summary>
    public static IReadOnlyList<QueryDefinition> Catalog(PlanGuardConfig config, Scenario? scenario)
    {
        var queries = config.LoadQueries();
        if (scenario?.QueryEdits is not { } edits)
        {
            return queries;
        }

        foreach (var id in edits.Keys.Where(id => queries.All(q => q.Id != id)))
        {
            throw new InvalidOperationException($"Scenario {scenario.Id} edits unknown query '{id}'.");
        }

        return [.. queries.Select(q => edits.TryGetValue(q.Id, out var sql) ? QueryDefinition.Create(q.Id, sql) : q)];
    }

    public static CaptureOptions Options(PlanGuardConfig config, Scenario? scenario)
    {
        var options = CaptureOptions.From(config);
        if (scenario?.Settings is not { } overrides)
        {
            return options;
        }

        var merged = new Dictionary<string, string>(options.Settings, StringComparer.Ordinal);
        foreach (var (k, v) in overrides)
        {
            merged[k] = v;
        }

        return options with { Settings = merged };
    }

    private async Task AdminAsync(string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Unpooled("postgres"));
        await conn.OpenAsync(ct);
#pragma warning disable CA2100 // Identifiers validated by Ident().
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Ident(string name) =>
        DatabaseName().IsMatch(name) ? name : throw new ArgumentException($"'{name}' is not a plain lower-case database name.", nameof(name));

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex DatabaseName();
}
