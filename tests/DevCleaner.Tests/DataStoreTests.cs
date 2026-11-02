using DevCleaner.Core;
using DevCleaner.Core.Engine;
using DevCleaner.Infrastructure.Persistence;

namespace DevCleaner.Tests;

public sealed class DataStoreTests : IDisposable
{
    private readonly TempTree _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Record_AccumulatesLifetimeStats_AndWritesHistory()
    {
        var store = new DataStore(_t.Root);
        store.Record(Report(100, Category.Browsers));
        store.Record(Report(250, Category.DotNetProjects));
        store.Record(Report(999, Category.Browsers) with { DryRun = true });

        var stats = store.LoadStats();
        Assert.Equal(350, stats.TotalBytesFreed);
        Assert.Equal(2, stats.Runs);
        Assert.Equal(100, stats.ByCategory[Category.Browsers]);
        Assert.Equal(2, store.LoadHistory().Count);
        Assert.Empty(Directory.GetFiles(_t.Root, "*.tmp"));
    }

    [Fact]
    public void Exclusions_AddRemove_AreNormalizedAndDeduplicated()
    {
        var store = new DataStore(_t.Root);
        var path = _t.P("proj");
        Assert.True(store.AddExclusion(path + Path.DirectorySeparatorChar));
        Assert.False(store.AddExclusion(path));
        Assert.True(store.ExclusionMatcher().IsMatch(Path.Combine(path, "bin")));
        Assert.True(store.RemoveExclusion(path));
        Assert.Empty(store.LoadExclusions().Items);
    }

    [Fact]
    public void CorruptSettings_FallBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_t.Root, "settings.json"), "{ not json");
        var s = new DataStore(_t.Root).LoadSettings();
        Assert.Equal(90, s.StaleDays);
        Assert.True(File.Exists(Path.Combine(_t.Root, "settings.json.corrupt")));
    }

    private static CleanReport Report(long bytes, Category category)
    {
        var f = new Finding { ProviderId = "t", Category = category, Name = "n", Path = "p", Kind = FindingKind.Directory, SizeBytes = bytes };
        return new CleanReport(DateTime.UtcNow, DateTime.UtcNow.AddTicks(Random.Shared.Next(1, 1000)), false, [new CleanItemResult(f, CleanStatus.Cleaned, bytes)]);
    }
}
