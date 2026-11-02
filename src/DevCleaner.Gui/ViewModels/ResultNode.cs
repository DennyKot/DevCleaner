using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevCleaner.Core;

namespace DevCleaner.Gui.ViewModels;

/// <summary>Tree node for results: category → (project) → finding, with tri-state check propagation.</summary>
public sealed partial class ResultNode : ObservableObject
{
    private bool _syncing;

    public ResultNode(string title, ResultNode? parent, Finding? finding = null, string? path = null)
    {
        Title = title;
        Parent = parent;
        Finding = finding;
        Path = finding?.Path ?? path;
    }

    public string Title { get; }
    public string? Path { get; }
    public ResultNode? Parent { get; }
    public Finding? Finding { get; }
    public Category? Category { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public bool IsStale { get; init; }
    public ObservableCollection<ResultNode> Children { get; } = [];

    public bool IsCategory => Category is not null;
    public bool HasRisk => Finding is not null;
    public string DisplayTitle => Category is { } c ? CategoryInfo.Name(c) : Title;

    public string Subtitle => Category is not null
        ? $"{LeafCount()} items"
        : Finding is null
            ? (IsStale ? "Stale  ·  " : "") + Path
            : Path + (Finding.Note is { } n ? "  ·  " + n : "");

    public string ModifiedText => ModifiedUtc?.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public string RiskText => Finding?.Risk.ToString() ?? "";
    public string SizeText => Size == 0 && Finding is not null ? "—" : Bytes.Format(Size);

    [ObservableProperty]
    private double _percent;

    private int LeafCount() => Finding is not null ? 1 : Children.Sum(c => c.LeafCount());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _size;

    [ObservableProperty]
    private bool? _isChecked = false;

    [ObservableProperty]
    private bool _isShown = true;

    public event Action? CheckChanged;

    public void AddChild(ResultNode child)
    {
        Children.Add(child);
        AddSize(child.Size);
        SyncFromChildren();
    }

    public void RemoveChild(ResultNode child)
    {
        Children.Remove(child);
        AddSize(-child.Size);
        SyncFromChildren();
        if (Children.Count == 0)
            Parent?.RemoveChild(this);
    }

    /// <summary>Only items visible under the current filter are cleaned.</summary>
    public IEnumerable<Finding> CheckedFindings() =>
        !IsShown ? []
        : Finding is not null
            ? (IsChecked == true ? [Finding] : [])
            : Children.SelectMany(c => c.CheckedFindings());

    public ResultNode Root => Parent?.Root ?? this;

    public void UpdateSize(long newSize) => AddSize(newSize - Size);

    /// <returns>Whether this node is shown.</returns>
    public bool ApplyFilter(string text)
    {
        var self = text.Length == 0
            || DisplayTitle.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (Path?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
        var any = false;
        foreach (var c in Children)
            any |= c.ApplyFilter(self ? "" : text);
        IsShown = self || any;
        return IsShown;
    }

    public void SortBySize()
    {
        var ordered = Children.OrderByDescending(c => c.Size).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = Children.IndexOf(ordered[i]);
            if (from != i)
                Children.Move(from, i);
            ordered[i].SortBySize();
        }
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_syncing)
            return;
        if (value is bool b)
        {
            foreach (var c in Children)
                c.SetFromParent(b);
        }

        Parent?.SyncFromChildren();
        Root.CheckChanged?.Invoke();
    }

    private void AddSize(long delta)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            n.Size += delta;
            n.OnPropertyChanged(nameof(Subtitle));
        }
    }

    private void SetFromParent(bool value)
    {
        _syncing = true;
        IsChecked = value;
        _syncing = false;
        foreach (var c in Children)
            c.SetFromParent(value);
    }

    private void SyncFromChildren()
    {
        if (Finding is not null || Children.Count == 0)
            return;
        bool? state = Children.All(c => c.IsChecked == true) ? true
            : Children.All(c => c.IsChecked == false) ? false
            : null;
        _syncing = true;
        IsChecked = state;
        _syncing = false;
        Parent?.SyncFromChildren();
    }
}

public sealed class ProjectRow(string root, string types, long size, DateTime? modifiedUtc, bool isStale)
{
    public string Root { get; } = root;
    public string Name { get; } = System.IO.Path.GetFileName(root);
    public string Types { get; } = types;
    public string SizeText { get; } = Bytes.Format(size);
    public long Size { get; } = size;
    public string ModifiedText { get; } = modifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";
    public bool IsStale { get; } = isStale;
}

public sealed partial class SelectableFinding(Finding finding, bool isChecked) : ObservableObject
{
    public Finding Finding { get; } = finding;
    public string SizeText => Bytes.Format(Finding.SizeBytes);
    public string ModifiedText => Finding.LastModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";

    [ObservableProperty]
    private bool _isChecked = isChecked;
}
