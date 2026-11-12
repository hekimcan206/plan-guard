using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanGuard.Snapshots;

/// <summary>Snapshot (de)serialisation. Snapshots are the contract between the capture and compare steps, which
/// often run in different jobs, so the format is versioned and read strictly.</summary>
public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Plans are tree-shaped; this bounds a hostile file without limiting real plans.
        MaxDepth = 512,
    };

    public static string Serialize(Snapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static Snapshot Deserialize(string json)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, Options)
            ?? throw new FormatException("Snapshot file is empty.");
        if (snapshot.FormatVersion != Snapshot.CurrentFormatVersion)
        {
            throw new FormatException(
                $"Snapshot format version {snapshot.FormatVersion} is not supported (expected {Snapshot.CurrentFormatVersion}). Re-capture the baseline with this plan-guard version.");
        }

        var duplicate = snapshot.Queries.GroupBy(q => q.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new FormatException($"Snapshot contains query id '{duplicate.Key}' more than once.");
        }

        return snapshot;
    }

    public static async Task WriteAsync(Snapshot snapshot, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, Serialize(snapshot), ct);
    }

    public static async Task<Snapshot> ReadAsync(string path, CancellationToken ct = default) =>
        Deserialize(await File.ReadAllTextAsync(path, ct));
}
