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

public sealed class DuplicateGroupViewModel(DuplicateGroup group)
{
    public string Header { get; } = $"{Bytes.Format(group.Size)} x {group.Files.Count}  (wasted {Bytes.Format(group.WastedBytes)})";

    public List<SelectableFinding> Files { get; } = group.Files
        .Select(f => new SelectableFinding(new Finding
        {
            ProviderId = "duplicates",
            Category = Category.Duplicates,
            Name = Path.GetFileName(f.Path),
            Path = f.Path,
            Kind = FindingKind.File,
            SizeBytes = f.Size,
            LastModifiedUtc = f.LastWriteUtc,
            Risk = RiskLevel.Review,
            Note = f == group.Keeper ? "keep (oldest)" : $"duplicate of {group.Keeper.Path}",
        }, f != group.Keeper))
        .ToList();
}

public sealed partial class DuplicatesViewModel(AppHost host, MainViewModel main) : ObservableObject
{
    [ObservableProperty] private string _roots = SearchViewModel.JoinExisting(
    [
        SearchViewModel.KnownFolder("Downloads"),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
    ]);
    [ObservableProperty] private string _minSize = "1MB";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    [RelayCommand]
    private async Task FindAsync()
    {
        Groups.Clear();
        IsBusy = true;
        try
        {
            var options = new DuplicateOptions
            {
                Roots = SearchViewModel.Split(Roots),
                MinSize = SearchViewModel.Size(MinSize) ?? 1,
                Exclusions = host.Store.ExclusionMatcher(),
            };
            var progress = new Progress<string>(s => Summary = s);
            var groups = await Task.Run(() => DuplicateFinder.Find(options, progress));
            foreach (var g in groups.Take(1000))
                Groups.Add(new DuplicateGroupViewModel(g));
            Summary = $"{groups.Count} groups, {Bytes.Format(groups.Sum(g => g.WastedBytes))} wasted";
        }
        catch (ArgumentException ex)
        {
            Summary = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = Groups.SelectMany(g => g.Files).Where(f => f.IsChecked).Select(f => f.Finding).ToList();
        var allSelected = Groups.Where(g => g.Files.All(f => f.IsChecked)).ToList();
        if (allSelected.Count > 0)
        {
            Summary = $"{allSelected.Count} group(s) have every copy selected; keep at least one.";
            return;
        }

        var report = await main.CleanAsync(selected);
        if (report is not null && !report.DryRun)
            await FindAsync();
    }
}

public sealed partial class DiskUsageViewModel : ObservableObject
{
    private readonly Stack<SizeNode> _back = new();

    [ObservableProperty] private string _path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] private SizeNode? _current;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<SizeNode> Largest { get; } = [];

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (!Directory.Exists(Path))
        {
            Summary = "Folder not found";
            return;
        }

        IsBusy = true;
        Summary = "Measuring...";
        _back.Clear();
        var path = Path;
        var root = await Task.Run(() => SizeTreeBuilder.Build(path, 3));
        IsBusy = false;
        Show(root);
    }

    [RelayCommand]
    private void Up()
    {
        if (_back.Count > 0)
            Show(_back.Pop());
    }

    public void DrillInto(SizeNode node)
    {
        if (node.IsDirectory && node.Children.Count > 0 && Current is not null)
        {
            _back.Push(Current);
            Show(node);
        }
    }

    private void Show(SizeNode node)
    {
        Current = node;
        Largest.Clear();
        foreach (var c in node.Children.Take(50))
            Largest.Add(c);
        Summary = $"{node.Path}: {Bytes.Format(node.Size)}";
    }
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppHost _host;

    public HistoryViewModel(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public ObservableCollection<string> Runs { get; } = [];
    public ObservableCollection<string> ByCategory { get; } = [];

    [ObservableProperty] private string _total = "";

    [RelayCommand]
    public void Refresh()
    {
        var stats = _host.Store.LoadStats();
        Total = $"Lifetime: {Bytes.Format(stats.TotalBytesFreed)} in {stats.Runs} runs" +
            (stats.FirstCleanUtc is { } f ? $" since {f.ToLocalTime():yyyy-MM-dd}" : "");
        ByCategory.Clear();
        foreach (var (cat, bytes) in stats.ByCategory.OrderByDescending(kv => kv.Value))
            ByCategory.Add($"{cat}: {Bytes.Format(bytes)}");
        Runs.Clear();
        foreach (var run in _host.Store.LoadHistory(100))
        {
            var failed = run.Items.Count(i => i.Status is CleanStatus.Failed or CleanStatus.Partial or CleanStatus.Skipped);
            Runs.Add($"{run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}   {Bytes.Format(run.TotalBytesFreed),10}   {run.Items.Count} items" +
                (failed > 0 ? $", {failed} skipped/failed" : ""));
        }
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        Reload();
    }

    [ObservableProperty] private string _projectRoots = "";
    [ObservableProperty] private decimal _staleDays;
    [ObservableProperty] private bool _permanentDelete;
    [ObservableProperty] private decimal _maxParallelism;
    [ObservableProperty] private string _status = "";

    public string DataFolder => _host.Store.Root;
    public string RulesFolder => _host.Store.RulesDirectory;
    public ObservableCollection<ExclusionEntry> Exclusions { get; } = [];

    public void Reload()
    {
        var s = _host.Store.LoadSettings();
        ProjectRoots = string.Join(Environment.NewLine, s.ProjectRoots);
        StaleDays = s.StaleDays;
        PermanentDelete = s.PermanentDelete;
        MaxParallelism = s.MaxParallelism;
        Exclusions.Clear();
        foreach (var e in _host.Store.LoadExclusions().Items)
            Exclusions.Add(e);
    }

    [RelayCommand]
    private void Save()
    {
        var s = _host.Store.LoadSettings();
        var roots = SearchViewModel.Split(ProjectRoots);
        var missing = roots.Where(r => !Directory.Exists(r)).ToList();
        s.ProjectRoots = roots.Where(Directory.Exists).Select(System.IO.Path.GetFullPath).ToList();
        s.StaleDays = (int)Math.Max(1, StaleDays);
        s.PermanentDelete = PermanentDelete;
        s.MaxParallelism = (int)Math.Clamp(MaxParallelism, 1, 32);
        _host.Store.SaveSettings(s);
        Status = missing.Count > 0 ? "Saved. Ignored missing folders: " + string.Join(", ", missing) : "Saved.";
        Reload();
    }

    [RelayCommand]
    private void RemoveExclusion(ExclusionEntry entry)
    {
        _host.Store.RemoveExclusion(entry.Pattern);
        Exclusions.Remove(entry);
    }
}
