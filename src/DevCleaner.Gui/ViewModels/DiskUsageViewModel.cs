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
