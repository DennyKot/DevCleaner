using DevCleaner.Core;
using DevCleaner.Core.Abstractions;
using DevCleaner.Core.Engine;

namespace DevCleaner.Infrastructure.Persistence;

/// <summary>JSON-backed settings, exclusions, lifetime stats and run history under the app-data folder.</summary>
public sealed class DataStore : ICleanRecorder
{
    private readonly Lock _lock = new();

    public DataStore(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("DEVCLEANER_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevCleaner");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RulesDirectory);
    }

    public string Root { get; }
    public string RulesDirectory => Path.Combine(Root, "rules");
    public string BackupsDirectory => Path.Combine(Root, "backups");
    private string HistoryDirectory => Path.Combine(Root, "history");
    private string SettingsFile => Path.Combine(Root, "settings.json");
    private string ExclusionsFile => Path.Combine(Root, "exclusions.json");
    private string StatsFile => Path.Combine(Root, "stats.json");

    public AppSettings LoadSettings() => JsonStore.Load<AppSettings>(SettingsFile);

    public void SaveSettings(AppSettings s) => JsonStore.Save(SettingsFile, s);

    public ExclusionList LoadExclusions() => JsonStore.Load<ExclusionList>(ExclusionsFile);

    public PathMatcher ExclusionMatcher() => new(LoadExclusions().Items.Select(i => i.Pattern));

    public bool AddExclusion(string pattern, string? reason = null)
    {
        lock (_lock)
        {
            var list = LoadExclusions();
            var normalized = pattern.Contains('*') || pattern.Contains('?') ? pattern : PathMatcher.Normalize(pattern);
            if (list.Items.Any(i => i.Pattern.Equals(normalized, OsInfo.PathComparison)))
                return false;
            list.Items.Add(new ExclusionEntry { Pattern = normalized, Reason = reason });
            JsonStore.Save(ExclusionsFile, list);
            return true;
        }
    }

    public bool RemoveExclusion(string pattern)
    {
        lock (_lock)
        {
            var list = LoadExclusions();
            var normalized = pattern.Contains('*') || pattern.Contains('?') ? pattern : PathMatcher.Normalize(pattern);
            var removed = list.Items.RemoveAll(i => i.Pattern.Equals(normalized, OsInfo.PathComparison)) > 0;
            if (removed)
                JsonStore.Save(ExclusionsFile, list);
            return removed;
        }
    }

    public LifetimeStats LoadStats() => JsonStore.Load<LifetimeStats>(StatsFile);

    public void Record(CleanReport report)
    {
        if (report.DryRun)
            return;
        lock (_lock)
        {
            var stats = LoadStats();
            stats.TotalBytesFreed += report.TotalBytesFreed;
            stats.Runs++;
            stats.FirstCleanUtc ??= report.StartedUtc;
            stats.LastCleanUtc = report.FinishedUtc;
            foreach (var g in report.Items.GroupBy(i => i.Finding.Category))
                stats.ByCategory[g.Key] = stats.ByCategory.GetValueOrDefault(g.Key) + g.Sum(i => i.BytesFreed);
            JsonStore.Save(StatsFile, stats);

            var run = new HistoryRun(report.StartedUtc, report.FinishedUtc, report.TotalBytesFreed,
                report.Items.Select(i => new HistoryItem(i.Finding.Category, i.Finding.Name, i.Finding.Path, i.Status, i.BytesFreed, i.Message)).ToList());
            JsonStore.Save(Path.Combine(HistoryDirectory, $"{report.StartedUtc:yyyy-MM-dd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.json"), run);
        }
    }

    public List<HistoryRun> LoadHistory(int last = 50)
    {
        if (!Directory.Exists(HistoryDirectory))
            return [];
        return Directory.GetFiles(HistoryDirectory, "*.json")
            .OrderDescending(StringComparer.Ordinal)
            .Take(last)
            .Select(f =>
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<HistoryRun>(File.ReadAllText(f), JsonStore.Options);
                }
                catch (System.Text.Json.JsonException)
                {
                    return null;
                }
            })
            .OfType<HistoryRun>()
            .ToList();
    }
}
