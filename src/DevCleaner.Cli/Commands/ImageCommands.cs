using System.Collections.Concurrent;
using System.CommandLine;
using DevCleaner.Core;
using DevCleaner.Core.Images;
using DevCleaner.Infrastructure;
using Spectre.Console;

namespace DevCleaner.Cli.Commands;

internal static class ImageCommands
{
    public static Command Images(AppHost host)
    {
        var roots = new Argument<string[]>("roots") { Description = "Folders with photos", Arity = ArgumentArity.OneOrMore };
        var minSize = new Option<string?>("--min-size") { Description = "Ignore images smaller than this (default 200KB)" };
        var mode = new Option<ImageQualityMode>("--mode")
        {
            Description = "Lossless (PNG/BMP only), VisuallyLossless (default) or Smaller",
            DefaultValueFactory = _ => ImageQualityMode.VisuallyLossless,
        };
        var format = new Option<ImageOutputFormat>("--format")
        {
            Description = "Original (default), Jpeg (TIFF/HEIC become JPEG), WebP, Avif or JpegXl. Converted files get a new extension",
            DefaultValueFactory = _ => ImageOutputFormat.Original,
        };
        var maxDimension = new Option<int>("--max-dimension") { Description = "Downscale so the longest edge is at most N pixels (default: keep size)" };
        var minSaving = new Option<int>("--min-saving") { Description = "Only offer images that shrink by at least N percent", DefaultValueFactory = _ => 10 };
        var noPng = new Option<bool>("--no-png") { Description = "Skip PNG files" };
        var noBmp = new Option<bool>("--no-bmp") { Description = "Skip BMP files (they are converted to PNG)" };
        var noTiff = new Option<bool>("--no-tiff") { Description = "Skip TIFF files" };
        var noHeic = new Option<bool>("--no-heic") { Description = "Skip HEIC/HEIF files (only converted with --format WebP/Avif/JpegXl)" };
        var compress = new Option<bool>("--compress") { Description = "Compress the images found (asks first). Originals go to the Recycle Bin" };
        var permanent = new Option<bool>("--permanent") { Description = "With --compress: delete originals instead of recycling them" };
        var output = new Option<string?>("--output") { Description = "With --compress: keep originals and write compressed copies under this folder" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be written without changing anything" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation" };
        var json = new Option<bool>("--json");
        var cmd = new Command("images", "Find photos that can be compressed without visible quality loss")
        {
            roots, minSize, mode, format, maxDimension, minSaving, noPng, noBmp, noTiff, noHeic, compress, permanent, output, dryRun, yes, json,
        };
        cmd.SetAction((pr, ct) =>
        {
            var size = pr.GetValue(minSize) is { } s ? Bytes.TryParse(s, out var b) ? b : throw new ArgumentException($"Invalid size '{s}'") : 200 * 1024;
            var scan = host.CreateImageScanOptions(pr.GetValue(roots) ?? [], size, !pr.GetValue(noPng), !pr.GetValue(noBmp), !pr.GetValue(noTiff), !pr.GetValue(noHeic));
            var compression = new ImageCompressionOptions
            {
                Mode = pr.GetValue(mode),
                Format = pr.GetValue(format),
                MaxDimension = Math.Max(0, pr.GetValue(maxDimension)),
                MinSavingFraction = Math.Clamp(pr.GetValue(minSaving), 0, 95) / 100.0,
            };
            var quiet = pr.GetValue(json);
            var note = ImageCompressor.CompatibilityNote(compression.Format);
            if (note is not null && !quiet)
                AnsiConsole.MarkupLine($"[yellow]Compatibility: {Markup.Escape(note)}[/]");

            var candidates = quiet
                ? ImageScanner.Find(scan, null, ct)
                : AnsiConsole.Status().Start("Looking for images...", _ => ImageScanner.Find(scan, null, ct));
            var analyses = new ConcurrentDictionary<ImageCandidate, ImageAnalysis>();
            void AnalyzeAll(Action? step)
            {
                Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = ImageCompressor.DefaultParallelism, CancellationToken = ct }, c =>
                {
                    analyses[c] = ImageCompressor.Analyze(c.Path, compression, ct);
                    step?.Invoke();
                });
            }

            if (quiet)
            {
                AnalyzeAll(null);
            }
            else
            {
                AnsiConsole.Progress().Start(ctx =>
                {
                    var task = ctx.AddTask($"Analyzing {candidates.Count} images", maxValue: Math.Max(1, candidates.Count));
                    AnalyzeAll(() => task.Increment(1));
                });
            }

            var good = candidates.Where(c => analyses[c].Status == ImageAnalysisStatus.Compressible)
                .OrderByDescending(c => analyses[c].SavedBytes)
                .ToList();
            if (quiet && !pr.GetValue(compress))
            {
                Output.WriteJson(candidates.Select(c => new { c.Path, Analysis = analyses[c] }));
                return Task.FromResult(Output.Ok);
            }

            if (!quiet)
                PrintTable(good, analyses, candidates.Count);
            if (!pr.GetValue(compress) && !pr.GetValue(dryRun))
                return Task.FromResult(Output.Ok);
            if (good.Count == 0)
                return Task.FromResult(Output.Ok);

            var outputFolder = pr.GetValue(output);
            var apply = new ImageApplyOptions
            {
                Compression = compression,
                DryRun = pr.GetValue(dryRun),
                OutputFolder = outputFolder,
                Originals = outputFolder is not null ? ImageOriginalsMode.SaveCopies
                    : pr.GetValue(permanent) ? ImageOriginalsMode.DeletePermanently
                    : ImageOriginalsMode.RecycleBin,
            };
            var saved = good.Sum(c => analyses[c].SavedBytes);
            var what = apply.Originals switch
            {
                ImageOriginalsMode.SaveCopies => $"write compressed copies to {outputFolder}",
                ImageOriginalsMode.DeletePermanently => "PERMANENTLY delete the originals",
                _ => "move the originals to the Recycle Bin",
            };
            var into = compression.Format is ImageOutputFormat.WebP or ImageOutputFormat.Avif or ImageOutputFormat.JpegXl ? $" as {compression.Format}" : "";
            if (!apply.DryRun && !pr.GetValue(yes) && !AnsiConsole.Confirm($"Compress {good.Count} images{into} (saves ~{Bytes.Format(saved)}) and {what}?", false))
                return Task.FromResult(Output.Ok);

            var (results, report) = AnsiConsole.Progress().Start(ctx =>
            {
                var task = ctx.AddTask("Compressing", maxValue: good.Count);
                return host.CompressImages(good, apply, new Progress<ImageApplyProgress>(p => task.Value = p.Done), ct);
            });
            if (quiet)
            {
                Output.WriteJson(results);
                return Task.FromResult(Output.Ok);
            }

            AnsiConsole.MarkupLine($"[bold]{results.Count(r => r.Status is ImageApplyStatus.Compressed or ImageApplyStatus.DryRun)} images, saved {Bytes.Format(results.Sum(r => r.SavedBytes))}[/]");
            return Task.FromResult(Output.Report(report));
        });
        return cmd;
    }

    private static void PrintTable(List<ImageCandidate> good, ConcurrentDictionary<ImageCandidate, ImageAnalysis> analyses, int total)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Image").AddColumn(new TableColumn("Size").RightAligned()).AddColumn(new TableColumn("Compressed").RightAligned())
            .AddColumn(new TableColumn("Saved").RightAligned()).AddColumn("Output");
        foreach (var c in good.Take(50))
        {
            var a = analyses[c];
            var quality = (a.Lossless ? $"lossless {a.OutputFormatName}" : $"{a.OutputFormatName} q{a.Quality}") + (a.OriginalQuality is { } oq && !a.Lossless ? $" (was ~{oq})" : "");
            table.AddRow(Markup.Escape(c.Path), Bytes.Format(a.OriginalSize), Bytes.Format(a.CompressedSize), $"[green]{a.SavedFraction:P0}[/]", quality);
        }

        if (good.Count > 0)
            AnsiConsole.Write(table);
        var other = analyses.Values.GroupBy(a => a.Status).Where(g => g.Key != ImageAnalysisStatus.Compressible).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}");
        AnsiConsole.MarkupLine($"[bold]{good.Count} of {total} images can be compressed: {Bytes.Format(good.Sum(c => analyses[c].OriginalSize))} → "
            + $"{Bytes.Format(good.Sum(c => analyses[c].CompressedSize))}, saves {Bytes.Format(good.Sum(c => analyses[c].SavedBytes))}[/]"
            + (other.Any() ? $" [grey]({string.Join(", ", other)})[/]" : ""));
    }
}
