using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Core;
using DevCleaner.Core.DiskUsage;
using DevCleaner.Core.Duplicates;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Search;
using DevCleaner.Infrastructure;
using DevCleaner.Infrastructure.Persistence;

namespace DevCleaner.Gui.ViewModels;

public sealed partial class SearchViewModel(AppHost host, MainViewModel main) : ObservableObject
{
    [ObservableProperty] private string _roots = DefaultRoots(host);
    [ObservableProperty] private string _namePattern = "";
    [ObservableProperty] private string _regex = "";
    [ObservableProperty] private string _extensions = "";
    [ObservableProperty] private string _minSize = "";
    [ObservableProperty] private string _maxSize = "";
    [ObservableProperty] private decimal? _olderThanDays;
    [ObservableProperty] private bool _tempFilesOnly = true;
    [ObservableProperty] private string _summary = "";

    public ObservableCollection<SelectableFinding> Results { get; } = [];

    [RelayCommand]
    private async Task RunAsync()
    {
        Results.Clear();
        try
        {
            var criteria = new SearchCriteria
            {
                Roots = Split(Roots),
                NamePattern = NullIfEmpty(NamePattern),
                Regex = NullIfEmpty(Regex),
                Extensions = Split(Extensions),
                MinSize = Size(MinSize),
                MaxSize = Size(MaxSize),
                ModifiedBeforeUtc = OlderThanDays is { } d ? DateTime.UtcNow.AddDays(-(double)d) : null,
                TempFilesPreset = TempFilesOnly,
            };
            var exclusions = host.Store.ExclusionMatcher();
            Summary = "Searching...";
            var found = await Task.Run(() => FileSearch.Search(criteria, exclusions, CancellationToken.None).ToList());
            foreach (var f in found.OrderByDescending(f => f.SizeBytes))
                Results.Add(new SelectableFinding(f, true));
            Summary = $"{found.Count} files, {Bytes.Format(found.Sum(f => f.SizeBytes))}";
        }
        catch (ArgumentException ex)
        {
            Summary = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var report = await main.CleanAsync(Results.Where(r => r.IsChecked).Select(r => r.Finding).ToList());
        if (report is null || report.DryRun)
            return;
        var cleaned = report.Items.Where(i => i.Status == CleanStatus.Cleaned).Select(i => i.Finding.Id).ToHashSet();
        foreach (var r in Results.Where(r => cleaned.Contains(r.Finding.Id)).ToList())
            Results.Remove(r);
    }

    internal static List<string> Split(string s) =>
        s.Split([';', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal static long? Size(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : Bytes.TryParse(s, out var b) ? b : throw new ArgumentException($"Invalid size '{s}'");

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public string PresetHint { get; } = "Matches: " + string.Join("  ", FileSearch.TempPatterns);

    /// <summary>Downloads, temp and project folders — where leftovers usually pile up.</summary>
    private static string DefaultRoots(AppHost host) =>
        JoinExisting([KnownFolder("Downloads"), Path.GetTempPath(), .. host.EffectiveProjectRoots()]);

    internal static string KnownFolder(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name);

    internal static string JoinExisting(IEnumerable<string> paths) =>
        string.Join("; ", paths.Select(p => Path.TrimEndingDirectorySeparator(p)).Where(Directory.Exists).Distinct(OsInfo.PathComparer));
}
