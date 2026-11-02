using System.Text.Json;
using System.Text.Json.Serialization;
using DevCleaner.Core;
using DevCleaner.Core.Engine;

namespace DevCleaner.Infrastructure.Persistence;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<string> ProjectRoots { get; set; } = [];
    public int StaleDays { get; set; } = 90;
    public bool PermanentDelete { get; set; }
    public int MaxParallelism { get; set; } = 4;
    public List<string> DisabledProviders { get; set; } = [];
    public long DuplicateMinSize { get; set; } = 1024 * 1024;
}

public sealed class ExclusionEntry
{
    public string Pattern { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ExclusionList
{
    public int Version { get; set; } = 1;
    public List<ExclusionEntry> Items { get; set; } = [];
}

public sealed class LifetimeStats
{
    public int Version { get; set; } = 1;
    public long TotalBytesFreed { get; set; }
    public int Runs { get; set; }
    public DateTime? FirstCleanUtc { get; set; }
    public DateTime? LastCleanUtc { get; set; }
    public Dictionary<Category, long> ByCategory { get; set; } = [];
}

public sealed record HistoryItem(Category Category, string Name, string Path, CleanStatus Status, long BytesFreed, string? Message);

public sealed record HistoryRun(DateTime StartedUtc, DateTime FinishedUtc, long TotalBytesFreed, List<HistoryItem> Items);

internal static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path)
        where T : new()
    {
        if (!File.Exists(path))
            return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (JsonException)
        {
            // Keep the corrupt file for inspection and start fresh rather than crash.
            File.Copy(path, path + ".corrupt", overwrite: true);
            return new T();
        }
    }

    /// <summary>Atomic write: temp file + rename.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
