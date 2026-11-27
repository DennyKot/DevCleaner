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

public sealed partial class ImagesViewModel
{
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
