using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Core;
using DevCleaner.Core.Engine;
using DevCleaner.Infrastructure;

namespace DevCleaner.Gui.ViewModels;

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
