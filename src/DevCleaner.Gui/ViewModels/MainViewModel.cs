using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Core;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Reports;
using DevCleaner.Infrastructure;

namespace DevCleaner.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Dictionary<string, ResultNode> _leaves = [];
    private CancellationTokenSource? _cts;

    public MainViewModel(AppHost host)
    {
        _host = host;
        Search = new SearchViewModel(host, this);
        Duplicates = new DuplicatesViewModel(host, this);
        DiskUsage = new DiskUsageViewModel();
        History = new HistoryViewModel(host);
        Settings = new SettingsViewModel(host);
        RefreshLifetime();
        ScanRootsText = string.Join("\n", host.EffectiveProjectRoots());
        Categories.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
        if (host.Warnings.Count > 0)
            Status = "Rule warnings: " + string.Join("; ", host.Warnings);
    }

    public SearchViewModel Search { get; }
    public DuplicatesViewModel Duplicates { get; }
    public DiskUsageViewModel DiskUsage { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Set by the view: shows a confirmation dialog.</summary>
    public Func<string, Task<bool>> Confirm { get; set; } = _ => Task.FromResult(true);

    public ObservableCollection<ResultNode> Categories { get; } = [];
    public ObservableCollection<ProjectRow> Projects { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanSelectedCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _status =  "Scan your projects and caches to find reclaimable space.";

    [ObservableProperty]
    private string _lifetimeFreed = "";

    [ObservableProperty]
    private string _lifetimeRuns = "";

    [ObservableProperty]
    private string _scanRootsText = "";

    [ObservableProperty]
    private string _foundCount = "";

    public bool HasResults => Categories.Count > 0;

    [ObservableProperty]
    private int _selectedPage;

    [ObservableProperty]
    private string _reclaimableText = "0 B";

    [ObservableProperty]
    private string _selectedText = "0 B";

    [ObservableProperty]
    private bool _dryRun;

    [ObservableProperty]
    private bool _force;

    [ObservableProperty]
    private bool _permanentDelete = true;

    [ObservableProperty]
    private string _filterText = "";

    /// <summary>Set by the view: shows the result of a clean run.</summary>
    public Func<CleanReport, Task> ShowReport { get; set; } = _ => Task.CompletedTask;

    /// <summary>Set by the view: asks for a report file path (null when cancelled).</summary>
    public Func<string, Task<string?>> PickExportPath { get; set; } = _ => Task.FromResult<string?>(null);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ExportAsync()
    {
        var findings = _leaves.Values.Select(l => l.Finding!).ToList();
        if (findings.Count == 0)
        {
            Status = "Nothing to export - run a scan first";
            return;
        }

        if (await PickExportPath("devcleaner-scan") is not { } path)
            return;
        try
        {
            ReportExporter.SaveFindings(path, findings);
            Status = $"Exported {findings.Count} items to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        foreach (var c in Categories)
            c.ApplyFilter(value.Trim());
        UpdateTotals();
    }

    [RelayCommand]
    private void SelectSafe() => SelectWhere(l => l.Finding!.Risk == RiskLevel.Safe);

    [RelayCommand]
    private void SelectStale() => SelectWhere(l => l.Parent?.IsStale == true);

    [RelayCommand]
    private void SelectNone() => SelectWhere(_ => false);

    private void SelectWhere(Func<ResultNode, bool> predicate)
    {
        foreach (var leaf in _leaves.Values)
            leaf.IsChecked = predicate(leaf);
        UpdateTotals();
    }

    private bool CanRun => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Categories.Clear();
        Projects.Clear();
        _leaves.Clear();
        var staleDays = _host.Store.LoadSettings().StaleDays;
        var findings = new List<Finding>();
        try
        {
            var context = _host.CreateScanContext();
            ScanRootsText = string.Join("\n", context.Options.ProjectRoots);
            Status = "Scanning " + string.Join(", ", context.Options.ProjectRoots) + " and known locations...";
            await foreach (var f in _host.CreateScanEngine().ScanAsync(context, _cts.Token))
            {
                findings.Add(f);
                AddFinding(f, staleDays);
                UpdateTotals();
            }

            BuildProjects(findings, staleDays);
            var ordered = Categories.OrderByDescending(c => c.Size).ToList();
            for (var i = 0; i < ordered.Count; i++)
                Categories.Move(Categories.IndexOf(ordered[i]), i);
            foreach (var c in Categories)
                c.SortBySize();
            if (FilterText.Length > 0)
                OnFilterTextChanged(FilterText);
            var errors = context.Errors.ToList();
            Status = $"Found {findings.Count} items" + (errors.Count > 0 ? $" ({errors.Count} warnings: {errors[0]})" : "");
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CleanSelectedAsync()
    {
        var selected = Categories.SelectMany(c => c.CheckedFindings()).ToList();
        var report = await CleanAsync(selected);
        if (report is null || report.DryRun)
            return;
        foreach (var item in report.Items.Where(i => i.Status == CleanStatus.Cleaned))
            RemoveLeaf(item.Finding.Id);
        foreach (var item in report.Items.Where(i => i.Status == CleanStatus.Partial))
        {
            if (_leaves.TryGetValue(item.Finding.Id, out var leaf))
                leaf.UpdateSize(Math.Max(0, item.Finding.SizeBytes - item.BytesFreed));
        }

        UpdateTotals();
    }

    public async Task<CleanReport?> CleanAsync(IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0)
        {
            Status = "Nothing selected";
            return null;
        }

        var settings = _host.Store.LoadSettings();
        var risky = findings.Count(f => f.Risk != RiskLevel.Safe);
        var mode = DryRun ? "Dry run" : PermanentDelete ? "PERMANENTLY delete" : "Move to Recycle Bin";
        var message = $"{mode}: {findings.Count} items, {Bytes.Format(findings.Sum(f => f.SizeBytes))}."
            + (risky > 0 ? $"\n{risky} item(s) are marked Review/Caution." : "");
        if (!await Confirm(message))
            return null;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        CleanReport? report = null;
        CleanProgress? last = null;
        var verb = DryRun ? "Checking" : "Cleaning";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void ShowProgress()
        {
            if (last is null)
                return;
            ProgressValue = last.Fraction * 100;
            var current = last.Completed is null ? $" · {last.Current.Name}" : "";
            Status = $"{verb} {Math.Min(last.ItemsDone + 1, last.ItemsTotal)}/{last.ItemsTotal} · "
                + $"{Bytes.Format(last.BytesDone)} of {Bytes.Format(last.BytesTotal)} ({last.Fraction:P0}) · {watch.Elapsed:mm\\:ss}{current}";
        }

        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            var options = new CleanOptions { DryRun = DryRun, Permanent = PermanentDelete, Force = Force };
            var progress = new Progress<CleanProgress>(p =>
            {
                last = p;
                ShowProgress();
            });
            var engine = _host.CreateCleanEngine();
            var ct = _cts.Token;
            var work = Task.Run(() => engine.Clean(findings, options, progress, ct), ct);
            // Keep the elapsed time ticking while a single large item is being processed.
            while (!work.IsCompleted && await Task.WhenAny(work, ticker.WaitForNextTickAsync().AsTask()) != work)
                ShowProgress();
            report = await work;
            var failed = report.Items.Count(i => i.Status is CleanStatus.Failed or CleanStatus.Partial or CleanStatus.Skipped);
            Status = (report.DryRun ? "Would free " : "Freed ") + Bytes.Format(report.TotalBytesFreed)
                + (failed > 0 ? $"; {failed} skipped or failed" : "");
            RefreshLifetime();
            History.Refresh();
        }
        catch (OperationCanceledException)
        {
            Status = "Clean cancelled";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = true;
        }

        if (report is not null)
            await ShowReport(report);
        return report;
    }

    public void Exclude(string path)
    {
        _host.Store.AddExclusion(path, "Excluded from GUI");
        var matcher = new PathMatcher([path]);
        foreach (var id in _leaves.Where(kv => matcher.IsMatch(kv.Value.Path!)).Select(kv => kv.Key).ToList())
            RemoveLeaf(id);
        foreach (var p in Projects.Where(p => matcher.IsMatch(p.Root)).ToList())
            Projects.Remove(p);
        Settings.Reload();
        UpdateTotals();
        Status = $"Excluded {path}";
    }

    private void AddFinding(Finding f, int staleDays)
    {
        var category = Categories.FirstOrDefault(c => c.Category == f.Category);
        if (category is null)
        {
            category = new ResultNode(f.Category.ToString(), null) { Category = f.Category };
            category.CheckChanged += UpdateTotals;
            Categories.Add(category);
        }

        var parent = category;
        if (f.ProjectRoot is not null)
        {
            parent = category.Children.FirstOrDefault(c => c.Path == f.ProjectRoot);
            if (parent is null)
            {
                parent = new ResultNode(System.IO.Path.GetFileName(f.ProjectRoot), category, path: f.ProjectRoot)
                {
                    ModifiedUtc = f.ProjectLastModifiedUtc,
                    IsStale = f.IsStale(staleDays),
                };
                category.Children.Add(parent);
            }
        }

        var leaf = new ResultNode(f.Name, parent, f)
        {
            ModifiedUtc = f.LastModifiedUtc,
            Size = f.SizeBytes,
            IsChecked = f.Risk == RiskLevel.Safe,
        };
        _leaves[f.Id] = leaf;
        parent.AddChild(leaf);
    }

    private void RemoveLeaf(string id)
    {
        if (!_leaves.Remove(id, out var leaf))
            return;
        leaf.Parent?.RemoveChild(leaf);
        foreach (var empty in Categories.Where(c => c.Children.Count == 0).ToList())
            Categories.Remove(empty);
    }

    private void BuildProjects(List<Finding> findings, int staleDays)
    {
        var rows = findings.Where(f => f.ProjectRoot is not null)
            .GroupBy(f => f.ProjectRoot!)
            .Select(g => new ProjectRow(g.Key, string.Join(", ", g.Select(f => System.IO.Path.GetFileName(f.Path)).Distinct(OsInfo.PathComparer)), g.Sum(f => f.SizeBytes),
                g.First().ProjectLastModifiedUtc, g.First().IsStale(staleDays)))
            .OrderByDescending(r => r.Size);
        foreach (var r in rows)
            Projects.Add(r);
    }

    private void UpdateTotals()
    {
        var total = Categories.Sum(c => c.Size);
        foreach (var c in Categories)
            c.Percent = total == 0 ? 0 : c.Size * 100.0 / total;
        ReclaimableText = Bytes.Format(total);
        FoundCount = $"{_leaves.Count} items in {Categories.Count} categories";
        SelectedText = Bytes.Format(Categories.SelectMany(c => c.CheckedFindings()).Sum(f => f.SizeBytes));
    }

    private void RefreshLifetime()
    {
        var s = _host.Store.LoadStats();
        LifetimeFreed = Bytes.Format(s.TotalBytesFreed);
        LifetimeRuns = $"{s.Runs} cleanup runs";
    }
}
