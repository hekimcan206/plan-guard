using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlanGuard.Plans;

/// <summary>
/// The shape of a plan: which operators run, on which relations and indexes, in which arrangement. Costs, row
/// estimates, timings, buffer counts, aliases and condition text are deliberately excluded, so two captures of the
/// same plan always have the same fingerprint even when statistics were re-sampled or the run was slower.
/// </summary>
public static class PlanShape
{
    /// <summary>Canonical, human-readable shape of the subtree: <c>Node[attrs](child,child)</c>.</summary>
    public static string Canonical(PlanNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var sb = new StringBuilder();
        Append(sb, root);
        return sb.ToString();
    }

    /// <summary>First 16 hex characters of the SHA-256 of <see cref="Canonical"/>.</summary>
    public static string Fingerprint(PlanNode root)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(root)));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// The label PostgreSQL's text format would print for the node, e.g. <c>Index Scan using ix_a on orders</c>.
    /// Aliases are left out so a renamed alias is not reported as a plan change.
    /// </summary>
    public static string Label(PlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sb = new StringBuilder();
        if (node.Strategy is { } strategy && node.NodeType is "Aggregate" or "SetOp")
        {
            sb.Append(strategy switch
            {
                "Hashed" => "HashAggregate",
                "Sorted" => "GroupAggregate",
                "Mixed" => "MixedAggregate",
                _ => node.NodeType,
            });
        }
        else
        {
            sb.Append(node.NodeType);
        }

        if (node.JoinType is { } join && join != "Inner")
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({join})");
        }

        if (node.ScanDirection == "Backward")
        {
            sb.Append(" Backward");
        }

        if (node.IndexName is { } index)
        {
            sb.Append(node.NodeType == "Bitmap Index Scan" ? " on " : " using ").Append(index);
        }

        if (node.RelationName is { } relation)
        {
            sb.Append(" on ").Append(relation);
        }
        else if (node.CteName is { } cte)
        {
            sb.Append(" on ").Append(cte);
        }
        else if (node.FunctionName is { } fn)
        {
            sb.Append(" on ").Append(fn);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, PlanNode node)
    {
        sb.Append(node.NodeType).Append('[');
        // Schema is left out: it only appears with VERBOSE, and the relation name already identifies the table in practice.
        AppendAttr(sb, "rel", node.RelationName);
        AppendAttr(sb, "idx", node.IndexName);
        AppendAttr(sb, "join", node.JoinType);
        AppendAttr(sb, "strategy", node.Strategy);
        AppendAttr(sb, "dir", node.ScanDirection == "Backward" ? "Backward" : null);
        AppendAttr(sb, "parent", node.ParentRelationship);
        AppendAttr(sb, "cte", node.CteName);
        AppendAttr(sb, "fn", node.FunctionName);
        sb.Append(']');
        if (node.Children.Count == 0)
        {
            return;
        }

        sb.Append('(');
        for (var i = 0; i < node.Children.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            Append(sb, node.Children[i]);
        }

        sb.Append(')');
    }

    private static void AppendAttr(StringBuilder sb, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (sb[^1] != '[')
        {
            sb.Append(';');
        }

        sb.Append(key).Append('=').Append(value);
    }
}
