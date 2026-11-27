using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Infrastructure;
using DevCleaner.Infrastructure.Persistence;

namespace DevCleaner.Gui.ViewModels;

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
