using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Core;
using DevCleaner.Core.Images;
using DevCleaner.Infrastructure;

namespace DevCleaner.Gui.ViewModels;

/// <summary>One photo in the image list.</summary>
public sealed partial class ImageRow(ImageCandidate candidate, ImageFolderRow folder) : ObservableObject
{
    public ImageCandidate Candidate { get; } = candidate;
    public ImageFolderRow Folder { get; } = folder;
    public string Name => Candidate.Name;
    public string Path => Candidate.Path;
    public string SizeText => Bytes.Format(Candidate.SizeBytes);
    public string DateText => Candidate.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText), nameof(SavedText), nameof(DetailText), nameof(CanSelect), nameof(IsCompressible), nameof(ToolTip))]
    private ImageAnalysis? _analysis;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Pending or compressible; "already optimal" and unsupported images cannot be selected.</summary>
    public bool CanSelect => Analysis is null || Analysis.Status == ImageAnalysisStatus.Compressible;

    public bool IsCompressible => Analysis?.Status == ImageAnalysisStatus.Compressible;

    public string ResultText => Analysis switch
    {
        null => "…",
        { Status: ImageAnalysisStatus.Compressible } a => Bytes.Format(a.CompressedSize),
        { Status: ImageAnalysisStatus.NoGain } => "optimal",
        { Status: ImageAnalysisStatus.Unsupported } => "skipped",
        _ => "error",
    };

    public string SavedText => IsCompressible ? $"−{Analysis!.SavedFraction:P0}" : "";

    public string DetailText => Analysis switch
    {
        { Status: ImageAnalysisStatus.Compressible or ImageAnalysisStatus.NoGain } a => $"{DateText}  ·  {a.Width}×{a.Height}  ·  {Describe(a)}",
        { Message: { } m } => $"{DateText}  ·  {m}",
        _ => DateText,
    };

    public string ToolTip => Path + (Analysis?.Message is { } m ? "\n" + m : "");

    internal static string Describe(ImageAnalysis a) =>
        a.Lossless || a.Quality is null
            ? $"lossless {a.OutputFormatName}"
            : $"{a.OutputFormatName} q{a.Quality}" + (a.OriginalQuality is { } oq && a.OutputFormatName == "JPEG" ? $" (was ~{oq})" : "");

    partial void OnIsCheckedChanged(bool value) => Folder.ChildCheckChanged();

    partial void OnAnalysisChanged(ImageAnalysis? value)
    {
        if (!CanSelect)
            IsChecked = false;
        Folder.ChildAnalysisChanged();
    }
}

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

public sealed partial class ImagesViewModel : ObservableObject
{
    private static readonly int[] MaxDimensions = [0, 3840, 2560, 1920];

    private readonly AppHost _host;
    private readonly MainViewModel _main;
    private readonly List<ImageFolderRow> _folders = [];
    private readonly DispatcherTimer _totalsTimer;
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _previewCts;
    private bool _dirty;
    private bool _rebuilding;

    public ImagesViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        _totalsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (_dirty)
                UpdateTotals();
        });
        _totalsTimer.Start();
    }

    [ObservableProperty] private string _roots = SearchViewModel.JoinExisting(
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        System.IO.Path.Combine(Environment.GetEnvironmentVariable("OneDrive") ?? "-", "Pictures"),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        SearchViewModel.KnownFolder("Downloads"),
    ]);

    [ObservableProperty] private string _minSize = "200KB";
    [ObservableProperty] private int _modeIndex = 1;

    /// <summary>Quality choices in <see cref="ImageQualityMode"/> order; the hint is shown in the drop-down only.</summary>
    public static IReadOnlyList<QualityChoice> QualityModes { get; } =
    [
        new("Lossless only", "Pixel-identical to the original. Smallest savings."),
        new("Visually lossless", "Recommended. No visible difference, even zoomed in."),
        new("Smaller files", "Biggest savings. Slight softening may show at 100% zoom."),
    ];
    [ObservableProperty] private int _maxDimensionIndex;
    [ObservableProperty] private bool _includePng = true;
    [ObservableProperty] private bool _includeBmp = true;
    [ObservableProperty] private bool _includeTiff = true;
    [ObservableProperty] private bool _includeHeic = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompatibilityNote), nameof(HasCompatibilityNote))]
    private int _formatIndex;

    private ImageOutputFormat Format => (ImageOutputFormat)Math.Clamp(FormatIndex, 0, 4);

    /// <summary>Warning shown while a format with limited app support is selected.</summary>
    public string CompatibilityNote => ImageCompressor.CompatibilityNote(Format) ?? "";

    public bool HasCompatibilityNote => CompatibilityNote.Length > 0;
    [ObservableProperty] private bool _allowDropExtras;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveCopies))]
    private int _originalsIndex;

    [ObservableProperty] private string _outputFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Compressed by DevCleaner");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(CompressCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(CompressCommand))]
    private bool _isApplying;

    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Find photos, scans and screenshots that can be stored smaller. Nothing is selected until you choose.";
    [ObservableProperty] private string _selectedText = "0 B";
    [ObservableProperty] private string _selectedDetail = "Nothing selected";
    [ObservableProperty] private string _foundText = "";
    [ObservableProperty] private string _foundDetail = "";

    [ObservableProperty] private object? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private Bitmap? _previewBefore;

    [ObservableProperty] private Bitmap? _previewAfter;
    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewSizesText = "";
    [ObservableProperty] private string _previewSavedText = "";
    [ObservableProperty] private string _previewNote = "Select a photo to compare the original with the compressed result.";
    [ObservableProperty] private ImageRow? _previewRow;

    public ObservableCollection<object> Rows { get; } = [];
    public bool HasResults => _folders.Count > 0;
    public bool IsBusy => IsScanning || IsAnalyzing || IsApplying;
    public bool SaveCopies => OriginalsIndex == 2;
    public bool HasPreview => PreviewBefore is not null;

    private ImageCompressionOptions CompressionOptions => new()
    {
        Mode = (ImageQualityMode)Math.Clamp(ModeIndex, 0, 2),
        Format = Format,
        MaxDimension = MaxDimensions[Math.Clamp(MaxDimensionIndex, 0, MaxDimensions.Length - 1)],
        DropEmbeddedExtras = AllowDropExtras,
    };

    private bool CanStart => !IsScanning && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task FindAsync()
    {
        CancelWork();
        var ct = (_workCts = new CancellationTokenSource()).Token;
        _folders.Clear();
        Rows.Clear();
        ClearPreview();
        OnPropertyChanged(nameof(HasResults));
        IsScanning = true;
        IsProgressIndeterminate = true;
        try
        {
            var roots = SearchViewModel.Split(Roots);
            var options = _host.CreateImageScanOptions(roots, SearchViewModel.Size(MinSize) ?? 0, IncludePng, IncludeBmp, IncludeTiff, IncludeHeic);
            var progress = new Progress<string>(dir => Status = "Looking in " + dir);
            var found = await Task.Run(() => ImageScanner.Find(options, progress, ct), ct);
            foreach (var g in found.GroupBy(c => c.Folder, OsInfo.PathComparer).OrderByDescending(g => g.Sum(c => c.SizeBytes)))
                _folders.Add(new ImageFolderRow(this, g.Key, g));
            // Small result sets open fully; large ones start as a folder overview.
            SetExpanded(found.Count <= 300);
            OnPropertyChanged(nameof(HasResults));
            Status = found.Count == 0 ? "No images found." : $"Found {found.Count} images in {_folders.Count} folders. Estimating savings…";
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsScanning = false;
            UpdateTotals();
        }

        if (_folders.Count > 0 && !ct.IsCancellationRequested)
            await AnalyzeAsync(ct);
    }

    /// <summary>Estimates every image in the background (parallel); rows update as results arrive.</summary>
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        var rows = _folders.SelectMany(f => f.Images).Where(r => r.Analysis is null).ToList();
        if (rows.Count == 0)
            return;
        var options = CompressionOptions;
        var done = 0;
        IsAnalyzing = true;
        IsProgressIndeterminate = false;
        Progress = 0;
        try
        {
            await Task.Run(() => Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = ImageCompressor.DefaultParallelism, CancellationToken = ct }, row =>
            {
                ImageAnalysis a;
                try
                {
                    a = ImageCompressor.Analyze(row.Path, options, ct);
                }
                catch (OperationCanceledException)
                {
                    // Handled here, inside our own code, so the debugger does not stop on a "user-unhandled" exception.
                    return;
                }

                var n = Interlocked.Increment(ref done);
                Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;
                    row.Analysis = a;
                    Progress = n * 100.0 / rows.Count;
                    Status = $"Estimating savings… {n}/{rows.Count}";
                });
            }), ct);
            Status = "Done. Tick folders or photos to compress; click a photo to compare.";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsAnalyzing = false;
            IsProgressIndeterminate = true;
            UpdateTotals();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelWork();
        Status = "Cancelled";
    }

    private void CancelWork()
    {
        _workCts?.Cancel();
        _workCts = null;
    }

    [RelayCommand]
    private void SelectAll() => Select(_ => true);

    [RelayCommand]
    private void SelectNone() => Select(_ => false);

    /// <summary>Only images that shrink by at least a third.</summary>
    [RelayCommand]
    private void SelectBest() => Select(r => r.Analysis is { Status: ImageAnalysisStatus.Compressible } a && a.SavedFraction >= 0.33);

    private void Select(Func<ImageRow, bool> predicate)
    {
        foreach (var r in _folders.SelectMany(f => f.Images))
            r.IsChecked = r.CanSelect && predicate(r);
        UpdateTotals();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandAllText))]
    private bool _allExpanded;

    public string ExpandAllText => AllExpanded ? "Collapse" : "Expand";

    [RelayCommand]
    private void ToggleAllExpanded() => SetExpanded(!AllExpanded);

    private void SetExpanded(bool value)
    {
        AllExpanded = value;
        _rebuilding = true;
        foreach (var f in _folders)
            f.IsExpanded = value;
        _rebuilding = false;
        RebuildRows();
    }

    internal void OnFolderExpanded(ImageFolderRow folder)
    {
        if (_rebuilding)
            return;
        var at = Rows.IndexOf(folder);
        if (at < 0)
            return;
        if (folder.IsExpanded)
        {
            for (var i = 0; i < folder.Images.Count; i++)
                Rows.Insert(at + 1 + i, folder.Images[i]);
        }
        else
        {
            while (at + 1 < Rows.Count && Rows[at + 1] is ImageRow r && r.Folder == folder)
                Rows.RemoveAt(at + 1);
        }
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var f in _folders)
        {
            Rows.Add(f);
            if (f.IsExpanded)
            {
                foreach (var i in f.Images)
                    Rows.Add(i);
            }
        }
    }

    private bool CanCompress => !IsScanning && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanCompress))]
    private async Task CompressAsync()
    {
        var selected = _folders.SelectMany(f => f.Images).Where(r => r.IsChecked && r.CanSelect).ToList();
        if (selected.Count == 0)
        {
            Status = "Nothing selected: tick folders or photos first.";
            return;
        }

        if (SaveCopies && string.IsNullOrWhiteSpace(OutputFolder))
        {
            Status = "Choose a folder for the compressed copies.";
            return;
        }

        var saved = selected.Sum(r => r.Analysis?.SavedBytes ?? 0);
        var pending = selected.Count(r => r.Analysis is null);
        var originals = OriginalsIndex switch
        {
            1 => "The originals will be PERMANENTLY deleted.",
            2 => $"Originals stay untouched; compressed copies go to {OutputFolder}.",
            _ => "The originals will be moved to the Recycle Bin.",
        };
        var message = $"Compress {selected.Count} images (saves ~{Bytes.Format(saved)}"
            + (pending > 0 ? $", {pending} not estimated yet" : "") + ").\n" + originals
            + "\nMetadata (Exif, GPS, colour profile) and file dates are kept."
            + (Format is ImageOutputFormat.WebP or ImageOutputFormat.Avif or ImageOutputFormat.JpegXl
                ? $"\n\nPhotos will be converted to {Format switch { ImageOutputFormat.WebP => "WebP", ImageOutputFormat.Avif => "AVIF", _ => "JPEG XL" }}. {CompatibilityNote}"
                : "");
        if (!await _main.Confirm(message))
            return;

        CancelWork();
        var ct = (_workCts = new CancellationTokenSource()).Token;
        IsApplying = true;
        IsProgressIndeterminate = false;
        Progress = 0;
        var options = new ImageApplyOptions
        {
            Compression = CompressionOptions,
            Originals = (ImageOriginalsMode)Math.Clamp(OriginalsIndex, 0, 2),
            OutputFolder = SaveCopies ? OutputFolder.Trim() : null,
        };
        try
        {
            var progress = new Progress<ImageApplyProgress>(p =>
            {
                Progress = p.Done * 100.0 / Math.Max(1, p.Total);
                Status = $"Compressing {p.Done + 1}/{p.Total} · {p.Current.Name}";
            });
            var candidates = selected.Select(r => r.Candidate).ToList();
            var (results, report) = await Task.Run(() => _host.CompressImages(candidates, options, progress, ct));

            var byPath = selected.ToDictionary(r => r.Path, OsInfo.PathComparer);
            foreach (var r in results)
            {
                if (!byPath.TryGetValue(r.Candidate.Path, out var row))
                    continue;
                if (r.Status == ImageApplyStatus.Compressed)
                    RemoveRow(row);
                else
                    row.Analysis = (row.Analysis ?? new ImageAnalysis { Status = ImageAnalysisStatus.Failed }) with
                    {
                        Status = r.Status == ImageApplyStatus.NoGain ? ImageAnalysisStatus.NoGain : ImageAnalysisStatus.Failed,
                        Message = r.Message,
                    };
            }

            var done = results.Count(r => r.Status == ImageApplyStatus.Compressed);
            var problems = results.Count(r => r.Status is ImageApplyStatus.Failed or ImageApplyStatus.Skipped);
            Status = $"Compressed {done} images, saved {Bytes.Format(results.Sum(r => r.SavedBytes))}"
                + (SaveCopies ? $" (copies in {OutputFolder})" : "")
                + (problems > 0 ? $"; {problems} skipped or failed" : "")
                + (results.Count < candidates.Count ? "; cancelled" : "");
            _main.RefreshStats();
            await _main.ShowReport(report);
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsApplying = false;
            IsProgressIndeterminate = true;
            UpdateTotals();
        }
    }

    public void Exclude(ImageRow row)
    {
        _host.Store.AddExclusion(row.Path, "Excluded from image compression");
        RemoveRow(row);
        _main.Settings.Reload();
        Status = $"Excluded {row.Path}";
    }

    private void RemoveRow(ImageRow row)
    {
        row.IsChecked = false;
        row.Folder.Images.Remove(row);
        Rows.Remove(row);
        if (row.Folder.Images.Count == 0)
        {
            _folders.Remove(row.Folder);
            Rows.Remove(row.Folder);
            OnPropertyChanged(nameof(HasResults));
        }
        else
        {
            row.Folder.ChildAnalysisChanged();
        }

        if (PreviewRow == row)
            ClearPreview();
    }

    internal void MarkDirty() => _dirty = true;

    private void UpdateTotals()
    {
        _dirty = false;
        var all = _folders.SelectMany(f => f.Images).ToList();
        var compressible = all.Where(r => r.IsCompressible).ToList();
        FoundText = all.Count == 0 ? "" : Bytes.Format(compressible.Sum(r => r.Analysis!.SavedBytes));
        FoundDetail = all.Count == 0 ? "" : $"{compressible.Count} of {all.Count} images · {Bytes.Format(all.Sum(r => r.Candidate.SizeBytes))}";

        var selected = all.Where(r => r.IsChecked).ToList();
        var known = selected.Where(r => r.IsCompressible).ToList();
        SelectedText = Bytes.Format(known.Sum(r => r.Analysis!.SavedBytes));
        var pending = selected.Count - known.Count;
        SelectedDetail = selected.Count == 0
            ? "Nothing selected"
            : $"{selected.Count} images  ·  {Bytes.Format(known.Sum(r => r.Analysis!.OriginalSize))} → {Bytes.Format(known.Sum(r => r.Analysis!.CompressedSize))}"
              + (pending > 0 ? $"  ·  {pending} still estimating" : "");
    }

    // Changing quality settings invalidates estimates and the preview.
    partial void OnModeIndexChanged(int value) => SettingsChanged();

    partial void OnMaxDimensionIndexChanged(int value) => SettingsChanged();

    partial void OnAllowDropExtrasChanged(bool value) => SettingsChanged();

    partial void OnFormatIndexChanged(int value) => SettingsChanged();

    private void SettingsChanged()
    {
        // During a scan the analysis that follows already uses the new settings.
        if (IsApplying || IsScanning || _folders.Count == 0)
            return;
        CancelWork();
        foreach (var r in _folders.SelectMany(f => f.Images))
            r.Analysis = null;
        var ct = (_workCts = new CancellationTokenSource()).Token;
        _ = AnalyzeAsync(ct);
        if (PreviewRow is { } row)
            _ = LoadPreviewAsync(row);
    }

    partial void OnSelectedRowChanged(object? value)
    {
        if (value is ImageRow row)
            _ = LoadPreviewAsync(row);
    }

    private async Task LoadPreviewAsync(ImageRow row)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var ct = cts.Token;
        PreviewRow = row;
        PreviewTitle = row.Name;
        IsPreviewLoading = true;
        PreviewNote = "Compressing for preview…";
        try
        {
            await Task.Delay(120, ct); // let fast arrow-key scrolling settle
            var options = CompressionOptions;
            var preview = await Task.Run(() =>
            {
                try
                {
                    return ImageCompressor.Preview(row.Path, options, 6000, ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }, ct);
            if (preview is null || ct.IsCancellationRequested)
                return;
            var before = preview.Original is { } o ? ToBitmap(o) : null;
            var after = preview.Compressed is { } c ? ToBitmap(c) : null;
            var (oldBefore, oldAfter) = (PreviewBefore, PreviewAfter);
            PreviewBefore = before;
            PreviewAfter = after;
            oldBefore?.Dispose();
            oldAfter?.Dispose();
            row.Analysis = preview.Analysis;
            ShowStats(preview.Analysis);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            PreviewNote = "Preview failed: " + ex.Message;
        }
        finally
        {
            if (_previewCts == cts)
                IsPreviewLoading = false;
        }
    }

    private void ShowStats(ImageAnalysis a)
    {
        var original = $"{Bytes.Format(a.OriginalSize)} ({a.Width}×{a.Height}" + (a.OriginalQuality is { } oq ? $", ~q{oq})" : ")");
        if (a.Status is ImageAnalysisStatus.Compressible or ImageAnalysisStatus.NoGain)
        {
            var outDims = a.OutputWidth != a.Width ? $"{a.OutputWidth}×{a.OutputHeight}, " : "";
            PreviewSizesText = $"{original}  →  {Bytes.Format(a.CompressedSize)} ({outDims}{ImageRow.Describe(a)})";
            PreviewSavedText = a.Status == ImageAnalysisStatus.Compressible
                ? $"{Bytes.Format(a.SavedBytes)} ({a.SavedFraction:P0})"
                : "—";
            PreviewNote = a.Lossless
                ? "Pixel-identical to the original (verified)."
                : a.SsimWorst is { } w
                    ? $"Similarity {a.Ssim:P1} overall, {w:P1} in the most affected areas."
                      + (a.Status == ImageAnalysisStatus.NoGain ? $"  {a.Message}." : "  Drag the divider; scroll to zoom, double-click for 100%.")
                    : a.Message ?? "";
        }
        else
        {
            PreviewSizesText = original;
            PreviewSavedText = "—";
            PreviewNote = a.Message ?? "This image cannot be compressed.";
        }
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        var (b, a) = (PreviewBefore, PreviewAfter);
        PreviewBefore = PreviewAfter = null;
        b?.Dispose();
        a?.Dispose();
        PreviewRow = null;
        PreviewTitle = PreviewSizesText = PreviewSavedText = "";
        PreviewNote = "Select a photo to compare the original with the compressed result.";
    }

    private static Bitmap ToBitmap(PreviewImage p)
    {
        var handle = GCHandle.Alloc(p.Rgba, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(), new PixelSize(p.Width, p.Height), new Vector(96, 96), p.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}

public sealed record QualityChoice(string Title, string Hint);
