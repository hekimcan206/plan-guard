using Npgsql;
using PlanGuard.Configuration;
using PlanGuard.Lab;
using PlanGuard.Postgres;
using PlanGuard.Snapshots;
using Testcontainers.PostgreSql;

namespace PlanGuard.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container with pg_stat_statements preloaded, the seeded shop database as a clone template,
/// and a baseline capture of the unchanged catalog that the scenario tests compare against.
/// </summary>
public sealed class ShopCluster : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";
    private const string Base = "shop_base";

    public PostgreSqlContainer Container { get; } = PostgresContainers.Build(Image);

    public ShopDatabase Shop { get; private set; } = null!;
    public PlanGuardConfig Config { get; private set; } = null!;
    public Snapshot Baseline { get; private set; } = null!;

    private int _counter;

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();
        Shop = new ShopDatabase(AdminConnectionString, ShopDatabase.FindSampleDirectory());
        Config = Shop.LoadConfig();
        await Shop.CreateAsync(Base);
        var db = await CloneAsync();
        Baseline = await CaptureAsync(db, null, "main");
    }

    public string AdminConnectionString =>
        new NpgsqlConnectionStringBuilder(Container.GetConnectionString()) { Timeout = 60, CommandTimeout = 600 }.ConnectionString;

    /// <summary>A fresh copy of the seeded database, as a pull request's CI job would build it.</summary>
    public async Task<string> CloneAsync(Scenario? scenario = null)
    {
        var name = $"shop_t{Interlocked.Increment(ref _counter)}";
        await Shop.CloneAsync(Base, name);
        if (scenario is not null)
        {
            await Shop.ApplyAsync(name, scenario);
        }

        return name;
    }

    public async Task<Snapshot> CaptureAsync(string database, Scenario? scenario, string label)
    {
        await using var ds = NpgsqlDataSource.Create(Shop.ConnectionString(database));
        return await new PostgresCapturer(ds).CaptureAsync(ShopDatabase.Catalog(Config, scenario), ShopDatabase.Options(Config, scenario), label);
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class ShopGroup : ICollectionFixture<ShopCluster>
{
    public const string Name = "shop";
}

internal static class PostgresContainers
{
    /// <summary>pg_stat_statements must be preloaded at server start; compute_query_id links EXPLAIN to its rows.</summary>
    public static PostgreSqlContainer Build(string image) => new PostgreSqlBuilder(image)
        .WithCommand("-c", "shared_preload_libraries=pg_stat_statements", "-c", "compute_query_id=on")
        .Build();
}
