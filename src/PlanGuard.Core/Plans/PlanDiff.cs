using System.Globalization;
using System.Text;

namespace PlanGuard.Plans;

/// <summary>Operators that disappeared from or appeared in a plan, by <see cref="PlanShape.Label"/>.</summary>
public sealed record PlanDiff(IReadOnlyList<string> Removed, IReadOnlyList<string> Added)
{
    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0;

    /// <summary>
    /// Multiset difference of node labels. A tree edit distance would also catch pure re-orderings (join order
    /// swaps), but those already change the fingerprint; for a PR comment "Index Scan using ix on orders was
    /// replaced by Seq Scan on orders" is what a reviewer needs.
    /// </summary>
    public static PlanDiff Between(PlanNode baseline, PlanNode candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var before = baseline.Descendants().Select(PlanShape.Label).ToList();
        var after = candidate.Descendants().Select(PlanShape.Label).ToList();
        return new PlanDiff(Subtract(before, after), Subtract(after, before));
    }

    private static List<string> Subtract(List<string> from, List<string> what)
    {
        var remaining = what.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var label in from)
        {
            if (remaining.TryGetValue(label, out var n) && n > 0)
            {
                remaining[label] = n - 1;
            }
            else
            {
                result.Add(label);
            }
        }

        return result;
    }
}

/// <summary>Renders a plan tree in a compact form close to PostgreSQL's text EXPLAIN output.</summary>
public static class PlanTreeRenderer
{
    public static string Render(PlanNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var sb = new StringBuilder();
        Render(sb, root, 0);
        return sb.ToString().TrimEnd('\n');
    }

    private static void Render(StringBuilder sb, PlanNode node, int depth)
    {
        // Same layout as PostgreSQL's text format: children start "  ->  " six columns right of their parent.
        if (depth > 0)
        {
            sb.Append(' ', 2 + ((depth - 1) * 6)).Append("->  ");
        }

        sb.Append(PlanShape.Label(node))
          .Append(CultureInfo.InvariantCulture, $"  (cost={node.StartupCost:0.00}..{node.TotalCost:0.00} rows={node.PlanRows:0})");
        if (node.ActualRows is { } rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (actual rows={rows:0} loops={node.ActualLoops ?? 1:0})");
        }

        if (node.Buffers is { } b && (b.SharedTotal > 0 || b.TempTotal > 0))
        {
            sb.Append(CultureInfo.InvariantCulture, $" [shared={b.SharedTotal}");
            if (b.TempTotal > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" temp={b.TempTotal}");
            }

            sb.Append(']');
        }

        sb.Append('\n');
        var pad = new string(' ', (depth * 6) + 2);
        AppendDetail(sb, pad, "Index Cond", node.IndexCond);
        AppendDetail(sb, pad, "Recheck Cond", node.RecheckCond);
        AppendDetail(sb, pad, "Hash Cond", node.HashCond);
        AppendDetail(sb, pad, "Merge Cond", node.MergeCond);
        AppendDetail(sb, pad, "Join Filter", node.JoinFilter);
        AppendDetail(sb, pad, "Filter", node.Filter);
        if (node.RowsRemovedByFilter is > 0 and var removed)
        {
            AppendDetail(sb, pad, "Rows Removed by Filter", removed.ToString("0", CultureInfo.InvariantCulture));
        }

        if (node.SortMethod is { } method)
        {
            AppendDetail(sb, pad, "Sort Method", node.SortSpaceType is null ? method : $"{method} ({node.SortSpaceType})");
        }

        foreach (var child in node.Children)
        {
            Render(sb, child, depth + 1);
        }
    }

    private static void AppendDetail(StringBuilder sb, string pad, string name, string? value)
    {
        if (value is not null)
        {
            sb.Append(pad).Append(name).Append(": ").Append(value).Append('\n');
        }
    }
}
