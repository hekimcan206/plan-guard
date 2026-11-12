namespace PlanGuard.Plans;

/// <summary>
/// Block counters reported by <c>EXPLAIN (ANALYZE, BUFFERS)</c> or accumulated by pg_stat_statements.
/// One block is 8 KiB on a default build.
/// </summary>
public sealed record BufferCounts(
    long SharedHit,
    long SharedRead,
    long SharedDirtied,
    long SharedWritten,
    long LocalHit,
    long LocalRead,
    long TempRead,
    long TempWritten)
{
    public static readonly BufferCounts Zero = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Pages the executor touched in shared buffers, cached or not. The hit/read split depends on cache state, so
    /// regressions are judged on the sum, which depends on the plan and the data only.
    /// </summary>
    public long SharedTotal => SharedHit + SharedRead;

    /// <summary>Spill traffic to temporary files (sorts, hashes and materialisation that exceeded work_mem).</summary>
    public long TempTotal => TempRead + TempWritten;
}

/// <summary>One node of a PostgreSQL plan tree, reduced to what regression detection and reporting need.</summary>
public sealed record PlanNode
{
    public required string NodeType { get; init; }
    public string? RelationName { get; init; }
    public string? Schema { get; init; }
    public string? Alias { get; init; }
    public string? IndexName { get; init; }
    public string? JoinType { get; init; }
    public string? Strategy { get; init; }
    public string? ScanDirection { get; init; }
    public string? ParentRelationship { get; init; }
    public string? CteName { get; init; }
    public string? FunctionName { get; init; }
    public string? IndexCond { get; init; }
    public string? RecheckCond { get; init; }
    public string? Filter { get; init; }
    public string? HashCond { get; init; }
    public string? MergeCond { get; init; }
    public string? JoinFilter { get; init; }
    public string? SortMethod { get; init; }
    public string? SortSpaceType { get; init; }
    public IReadOnlyList<string> SortKey { get; init; } = [];
    public double StartupCost { get; init; }
    public double TotalCost { get; init; }
    public double PlanRows { get; init; }
    public double? ActualRows { get; init; }
    public double? ActualLoops { get; init; }
    public double? ActualTotalTimeMs { get; init; }
    public double? RowsRemovedByFilter { get; init; }
    public int? WorkersPlanned { get; init; }
    public BufferCounts? Buffers { get; init; }
    public IReadOnlyList<PlanNode> Children { get; init; } = [];

    /// <summary>All nodes of the subtree in pre-order, this node first.</summary>
    public IEnumerable<PlanNode> Descendants()
    {
        var stack = new Stack<PlanNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }
}

/// <summary>The parsed result of one EXPLAIN statement.</summary>
public sealed record ExplainResult
{
    public required PlanNode Root { get; init; }
    public long? QueryId { get; init; }
    public double? PlanningTimeMs { get; init; }
    public double? ExecutionTimeMs { get; init; }
    public BufferCounts? PlanningBuffers { get; init; }
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>True when the plan carries runtime counters, i.e. it came from EXPLAIN ANALYZE.</summary>
    public bool IsAnalyzed => ExecutionTimeMs is not null;
}
