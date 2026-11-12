namespace PlanGuard.Snapshots;

public static class Stats
{
    /// <summary>
    /// Median of the values. Regression checks use the median rather than the mean: one run that lands on a busy
    /// moment of a shared CI runner moves the mean arbitrarily far but cannot move the median past the middle run.
    /// </summary>
    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("Median of an empty sequence.", nameof(values));
        }

        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Coefficient of variation (sample standard deviation / mean); 0 for fewer than two values or a zero mean.</summary>
    public static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2)
        {
            return 0;
        }

        var mean = values.Average();
        if (mean == 0)
        {
            return 0;
        }

        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance) / mean;
    }
}
