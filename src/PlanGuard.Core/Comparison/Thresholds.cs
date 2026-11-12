using System.Text.Json.Serialization;

namespace PlanGuard.Comparison;

/// <summary>What a signal that crosses its threshold does to the build.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Gate>))]
public enum Gate
{
    Off,
    Warn,
    Fail,
}

/// <summary>
/// Regression thresholds. A metric regresses only when it crosses BOTH the ratio and the absolute floor: the ratio
/// alone would fail a 0.1 ms query that became 0.3 ms, the floor alone would ignore a 10x blow-up of a small query.
/// The defaults come from the noise measurements in docs/benchmarks.md.
/// </summary>
public sealed record Thresholds
{
    /// <summary>Median shared blocks (hit + read) of the measured runs. Primary signal: deterministic for a given plan and data.</summary>
    public double BufferRatio { get; init; } = 1.3;
    public long BufferMinDelta { get; init; } = 64;
    public Gate BufferGate { get; init; } = Gate.Fail;

    /// <summary>Temporary-file blocks (sort/hash spills).</summary>
    public double TempRatio { get; init; } = 1.5;
    public long TempMinDelta { get; init; } = 8;
    public Gate TempGate { get; init; } = Gate.Fail;

    /// <summary>Planner total cost. Fails discovered statements (no runtime numbers); warns for measured catalog queries.</summary>
    public double CostRatio { get; init; } = 1.5;
    public double CostMinDelta { get; init; } = 50;
    public Gate CostGate { get; init; } = Gate.Warn;
    public Gate DiscoveredCostGate { get; init; } = Gate.Fail;

    /// <summary>Median execution time. Wall-clock time on a shared runner is noisy, so by default it only warns.</summary>
    public double TimeRatio { get; init; } = 2.0;
    public double TimeMinDeltaMs { get; init; } = 5;
    public Gate TimeGate { get; init; } = Gate.Warn;

    /// <summary>What a different median row count does. Rows changing means the comparison may reflect data, not the plan.</summary>
    public Gate RowsChangedGate { get; init; } = Gate.Warn;

    public void Validate()
    {
        static void Ratio(double v, string name)
        {
            if (!(v >= 1.0) || double.IsInfinity(v))
            {
                throw new ArgumentOutOfRangeException(name, v, "A ratio threshold must be a finite number >= 1.0.");
            }
        }

        Ratio(BufferRatio, nameof(BufferRatio));
        Ratio(TempRatio, nameof(TempRatio));
        Ratio(CostRatio, nameof(CostRatio));
        Ratio(TimeRatio, nameof(TimeRatio));
        if (BufferMinDelta < 0 || TempMinDelta < 0 || CostMinDelta < 0 || TimeMinDeltaMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferMinDelta), "Absolute floors must not be negative.");
        }
    }
}
