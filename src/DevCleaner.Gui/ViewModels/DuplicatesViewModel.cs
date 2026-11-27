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
