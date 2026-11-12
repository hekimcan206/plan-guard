using System.Globalization;
using System.Text.Json;

namespace PlanGuard.Plans;

/// <summary>
/// Parses the output of <c>EXPLAIN (FORMAT JSON)</c>. Unknown keys are ignored, so plans from newer PostgreSQL
/// versions (which add keys, never rename them) still parse.
/// </summary>
public static class ExplainJsonParser
{
    public static ExplainResult Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        var top = doc.RootElement;
        // EXPLAIN emits a one-element array; the bare object is accepted too.
        if (top.ValueKind == JsonValueKind.Array)
        {
            if (top.GetArrayLength() != 1)
            {
                throw new FormatException($"Expected one EXPLAIN result, found {top.GetArrayLength()}.");
            }

            top = top[0];
        }

        if (top.ValueKind != JsonValueKind.Object || !top.TryGetProperty("Plan", out var plan))
        {
            throw new FormatException("EXPLAIN JSON has no \"Plan\" object.");
        }

        return new ExplainResult
        {
            Root = ParseNode(plan, depth: 0),
            QueryId = top.TryGetProperty("Query Identifier", out var qid) && qid.ValueKind == JsonValueKind.Number ? qid.GetInt64() : null,
            PlanningTimeMs = Number(top, "Planning Time"),
            ExecutionTimeMs = Number(top, "Execution Time"),
            PlanningBuffers = top.TryGetProperty("Planning", out var planning) && planning.ValueKind == JsonValueKind.Object ? Buffers(planning) : null,
            Settings = Settings(top),
        };
    }

    private static PlanNode ParseNode(JsonElement e, int depth)
    {
        // Real plans are a few dozen levels deep at most; the cap keeps a hostile file from exhausting the stack.
        if (depth > 256)
        {
            throw new FormatException("Plan tree is deeper than 256 levels.");
        }

        var children = new List<PlanNode>();
        if (e.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in plans.EnumerateArray())
            {
                children.Add(ParseNode(child, depth + 1));
            }
        }

        return new PlanNode
        {
            NodeType = Text(e, "Node Type") ?? throw new FormatException("Plan node without \"Node Type\"."),
            RelationName = Text(e, "Relation Name"),
            Schema = Text(e, "Schema"),
            Alias = Text(e, "Alias"),
            IndexName = Text(e, "Index Name"),
            JoinType = Text(e, "Join Type"),
            Strategy = Text(e, "Strategy"),
            ScanDirection = Text(e, "Scan Direction"),
            ParentRelationship = Text(e, "Parent Relationship"),
            CteName = Text(e, "CTE Name"),
            FunctionName = Text(e, "Function Name"),
            IndexCond = Text(e, "Index Cond"),
            RecheckCond = Text(e, "Recheck Cond"),
            Filter = Text(e, "Filter"),
            HashCond = Text(e, "Hash Cond"),
            MergeCond = Text(e, "Merge Cond"),
            JoinFilter = Text(e, "Join Filter"),
            SortMethod = Text(e, "Sort Method"),
            SortSpaceType = Text(e, "Sort Space Type"),
            SortKey = Strings(e, "Sort Key"),
            StartupCost = Number(e, "Startup Cost") ?? 0,
            TotalCost = Number(e, "Total Cost") ?? 0,
            PlanRows = Number(e, "Plan Rows") ?? 0,
            ActualRows = Number(e, "Actual Rows"),
            ActualLoops = Number(e, "Actual Loops"),
            ActualTotalTimeMs = Number(e, "Actual Total Time"),
            RowsRemovedByFilter = Number(e, "Rows Removed by Filter"),
            WorkersPlanned = Number(e, "Workers Planned") is { } w ? (int)w : null,
            Buffers = e.TryGetProperty("Shared Hit Blocks", out _) ? Buffers(e) : null,
            Children = children,
        };
    }

    private static BufferCounts Buffers(JsonElement e) => new(
        Long(e, "Shared Hit Blocks"),
        Long(e, "Shared Read Blocks"),
        Long(e, "Shared Dirtied Blocks"),
        Long(e, "Shared Written Blocks"),
        Long(e, "Local Hit Blocks"),
        Long(e, "Local Read Blocks"),
        Long(e, "Temp Read Blocks"),
        Long(e, "Temp Written Blocks"));

    private static Dictionary<string, string> Settings(JsonElement top)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (top.TryGetProperty("Settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in settings.EnumerateObject())
            {
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
        }

        return result;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];
}
