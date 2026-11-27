using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Core;
using DevCleaner.Core.Images;

namespace DevCleaner.Gui.ViewModels;

/// <summary>A folder of photos; its checkbox selects every selectable image inside.</summary>
public sealed partial class ImageFolderRow : ObservableObject
{
    private readonly ImagesViewModel _owner;
    private bool _syncing;

    public ImageFolderRow(ImagesViewModel owner, string path, IEnumerable<ImageCandidate> images)
    {
        _owner = owner;
        Path = path;
        Images = images.OrderByDescending(i => i.SizeBytes).Select(i => new ImageRow(i, this)).ToList();
        TotalSize = Images.Sum(i => i.Candidate.SizeBytes);
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } n ? n : Path;
    public List<ImageRow> Images { get; }
    public long TotalSize { get; }
    public string CountText => $"{Images.Count} {(Images.Count == 1 ? "image" : "images")}  ·  {Bytes.Format(TotalSize)}";

    [ObservableProperty]
    private bool _isExpanded;

    public long SavedBytes => Images.Sum(i => i.Analysis?.SavedBytes ?? 0);

    public string SavingText
    {
        get
        {
            var analyzed = Images.Count(i => i.Analysis is not null);
            if (analyzed < Images.Count)
                return $"analyzing {analyzed}/{Images.Count}";
            var saved = SavedBytes;
            return saved > 0 ? $"−{Bytes.Format(saved)}" : "nothing to gain";
        }
    }

    public bool? IsChecked
    {
        get
        {
            var selectable = Images.Where(i => i.CanSelect).ToList();
            var n = selectable.Count(i => i.IsChecked);
            return n == 0 ? false : n == selectable.Count ? true : null;
        }
        set
        {
            _syncing = true;
            foreach (var i in Images.Where(i => i.CanSelect))
                i.IsChecked = value == true;
            _syncing = false;
            ChildCheckChanged();
        }
    }

    internal void ChildCheckChanged()
    {
        if (_syncing)
            return;
        OnPropertyChanged(nameof(IsChecked));
        _owner.MarkDirty();
    }

    internal void ChildAnalysisChanged()
    {
        OnPropertyChanged(nameof(SavingText));
        OnPropertyChanged(nameof(IsChecked));
        _owner.MarkDirty();
    }

    partial void OnIsExpandedChanged(bool value) => _owner.OnFolderExpanded(this);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
