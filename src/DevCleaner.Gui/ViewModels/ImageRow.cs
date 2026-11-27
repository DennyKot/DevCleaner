using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Core;
using DevCleaner.Core.Images;

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
