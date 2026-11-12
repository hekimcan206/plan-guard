using System.Globalization;
using System.Text;
using System.Text.Json;
using PlanGuard.Comparison;
using PlanGuard.Snapshots;

namespace PlanGuard.Reporting;

/// <summary>Plain-text summary for terminals and CI logs.</summary>
public static class ConsoleReport
{
    public static string Render(ComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"plan-guard: {MarkdownReport.Headline(report)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"baseline {report.Baseline.Label}  ->  candidate {report.Candidate.Label}   ({report.Queries.Count} queries)");
        foreach (var w in report.EnvironmentWarnings)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ! {w}");
        }

        var width = report.Queries.Count == 0 ? 10 : Math.Max(10, report.Queries.Max(q => q.Id.Length));
        foreach (var q in report.Queries.OrderByDescending(q => q.Verdict).ThenBy(q => q.Id, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {q.Verdict.ToString().ToUpperInvariant(),-8} {q.Id.PadRight(width)}");
            foreach (var f in q.Findings.Where(f => f.Level != FindingLevel.Info || f.Signal == Signal.PlanShape))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"           {f.Level.ToString().ToLowerInvariant(),-11} {f.Message.Replace("`", string.Empty, StringComparison.Ordinal)}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>GitHub Actions workflow commands, so regressions show up as annotations on the run.</summary>
public static class GitHubAnnotations
{
    public static IEnumerable<string> Render(ComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (var q in report.Queries)
        {
            foreach (var f in q.Findings.Where(f => f.Level is FindingLevel.Fail or FindingLevel.Warn))
            {
                var command = f.Level == FindingLevel.Fail ? "error" : "warning";
                yield return $"::{command} title={EscapeProperty($"plan-guard {q.Id}")}::{EscapeData(f.Message)}";
            }
        }
    }

    // Escaping rules of the Actions toolkit (core/command.ts): data escapes % CR LF; properties also : and ,.
    internal static string EscapeData(string s) =>
        s.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    internal static string EscapeProperty(string s) =>
        EscapeData(s).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);
}

/// <summary>Machine-readable report without plan trees (those live in the snapshots).</summary>
public static class JsonReport
{
    public static string Render(ComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var body = new
        {
            overall = report.Overall,
            baseline = report.Baseline,
            candidate = report.Candidate,
            thresholds = report.Thresholds,
            environmentWarnings = report.EnvironmentWarnings,
            queries = report.Queries.Select(q => new
            {
                id = q.Id,
                verdict = q.Verdict,
                planChanged = q.PlanChanged,
                baselineFingerprint = q.Baseline?.Fingerprint,
                candidateFingerprint = q.Candidate?.Fingerprint,
                removedOperators = q.Diff?.Removed,
                addedOperators = q.Diff?.Added,
                findings = q.Findings,
            }),
        };
        return JsonSerializer.Serialize(body, SnapshotJson.Options);
    }
}
