namespace PlanGuard.Lab;

public enum ScenarioKind
{
    /// <summary>A change that makes a query do more work; plan-guard must fail it.</summary>
    Regression,

    /// <summary>A change that must not fail the build (false-positive check).</summary>
    Neutral,

    /// <summary>A change that makes a query cheaper; must pass and be reported as improved.</summary>
    Improvement,
}

/// <summary>
/// One change a pull request could make to the shop sample: a migration, an edited query, or a different session
/// setting. <see cref="Target"/> is the catalog query the change is about (null for neutral changes).
/// </summary>
public sealed record Scenario(
    string Id,
    ScenarioKind Kind,
    string? Target,
    string Description,
    string? Migration = null,
    IReadOnlyDictionary<string, string>? QueryEdits = null,
    IReadOnlyDictionary<string, string>? Settings = null);

public static class ScenarioCatalog
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        new("drop-customer-index", ScenarioKind.Regression, "customer_recent_orders",
            "Migration drops orders_customer_idx (\"unused\" according to a stale report).",
            Migration: "DROP INDEX orders_customer_idx;"),

        new("drop-fk-index", ScenarioKind.Regression, "order_detail",
            "Migration drops the index behind the order_items -> orders foreign key.",
            Migration: "DROP INDEX order_items_order_idx;"),

        new("non-sargable-date", ScenarioKind.Regression, "store_daily_revenue",
            "Query rewritten to filter on created_at::date, so the (store_id, created_at) range scan becomes store_id only.",
            QueryEdits: new Dictionary<string, string>
            {
                ["store_daily_revenue"] = """
                    SELECT date_trunc('day', created_at, 'UTC') AS day, count(*) AS orders, sum(total) AS revenue
                    FROM orders
                    WHERE store_id = 17
                      AND (created_at AT TIME ZONE 'UTC')::date BETWEEN DATE '2026-03-01' AND DATE '2026-03-31'
                    GROUP BY 1
                    ORDER BY 1
                    """,
            }),

        new("lost-functional-index", ScenarioKind.Regression, "customer_by_email",
            "Query switched from lower(email) = lower($1) to ILIKE, which the expression index cannot serve.",
            QueryEdits: new Dictionary<string, string>
            {
                ["customer_by_email"] = "SELECT id, city FROM customers WHERE email ILIKE 'Customer4242@example.test'",
            }),

        new("partial-index-mismatch", ScenarioKind.Regression, "open_orders_queue",
            "Status list widened to IN ('open','cancelled'), so the partial index WHERE status = 'open' no longer applies.",
            QueryEdits: new Dictionary<string, string>
            {
                ["open_orders_queue"] = "SELECT id, store_id, created_at FROM orders WHERE status IN ('open', 'cancelled') ORDER BY created_at LIMIT 50",
            }),

        new("offset-pagination", ScenarioKind.Regression, "orders_page",
            "Keyset pagination replaced by OFFSET 150000.",
            QueryEdits: new Dictionary<string, string>
            {
                ["orders_page"] = "SELECT id, total FROM orders ORDER BY id OFFSET 150000 LIMIT 50",
            }),

        new("work-mem-spill", ScenarioKind.Regression, "top_customers",
            "Deployment config lowers work_mem to 64kB; the aggregate and sort spill to temp files.",
            Settings: new Dictionary<string, string> { ["work_mem"] = "64kB" }),

        new("join-without-index", ScenarioKind.Regression, "store_top_items",
            "Migration drops the order_items(order_id) index that the weekly item report joins through.",
            Migration: "DROP INDEX order_items_order_idx;"),

        new("add-unrelated-index", ScenarioKind.Neutral, null,
            "Migration adds an index on menu_items(category) that no catalog query uses.",
            Migration: "CREATE INDEX menu_items_category_idx ON menu_items (category);"),

        new("add-nullable-column", ScenarioKind.Neutral, null,
            "Migration adds a nullable column to orders (no table rewrite).",
            Migration: "ALTER TABLE orders ADD COLUMN note text;"),

        new("reanalyze", ScenarioKind.Neutral, null,
            "ANALYZE re-samples statistics: estimates move a little, plans should not.",
            Migration: "ANALYZE;"),

        new("equivalent-rewrite", ScenarioKind.Neutral, "customer_recent_orders",
            "Query rewritten with IN (...) and explicit column order: same plan, same work.",
            QueryEdits: new Dictionary<string, string>
            {
                ["customer_recent_orders"] = "SELECT o.id, o.status, o.total, o.created_at FROM orders AS o WHERE o.customer_id IN (4242) ORDER BY o.created_at DESC LIMIT 20",
            }),

        new("covering-index", ScenarioKind.Improvement, "top_customers",
            "Migration adds an index on orders(created_at) INCLUDE (customer_id, total) for the monthly export.",
            Migration: "CREATE INDEX orders_created_cover_idx ON orders (created_at) INCLUDE (customer_id, total);"),
    ];

    public static Scenario Get(string id) =>
        All.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException($"Unknown scenario '{id}'. Known: {string.Join(", ", All.Select(s => s.Id))}.");
}
