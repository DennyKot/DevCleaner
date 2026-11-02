using System.CommandLine;
using DevCleaner.Core;
using DevCleaner.Core.DiskUsage;
using DevCleaner.Core.Duplicates;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Search;
using DevCleaner.Infrastructure;
using Spectre.Console;

namespace DevCleaner.Cli.Commands;

internal static class ToolCommands
{
    public static Command Search(AppHost host)
    {
        var roots = new Argument<string[]>("roots") { Description = "Folders to search", Arity = ArgumentArity.OneOrMore };
        var name = new Option<string?>("--name") { Description = "File name glob, e.g. *.log" };
        var regex = new Option<string?>("--regex") { Description = "Regex matched against the full path" };
        var ext = new Option<string[]>("--ext") { Description = "Extensions (repeatable)", AllowMultipleArgumentsPerToken = true };
        var minSize = new Option<string?>("--min-size") { Description = "e.g. 10MB" };
        var maxSize = new Option<string?>("--max-size") { Description = "e.g. 1GB" };
        var olderThan = new Option<int?>("--older-than-days") { Description = "Modified more than N days ago" };
        var newerThan = new Option<int?>("--newer-than-days") { Description = "Modified within the last N days" };
        var notAccessed = new Option<int?>("--not-accessed-days") { Description = "Not accessed for N days" };
        var temp = new Option<bool>("--temp-files") { Description = "Only typical temp/leftover files (*.tmp, ~*, *.bak, *.dmp, ...)" };
        var noHidden = new Option<bool>("--no-hidden") { Description = "Skip hidden files" };
        var delete = new Option<bool>("--delete") { Description = "Move matching files to the Recycle Bin (asks first)" };
        var json = new Option<bool>("--json");
        var cmd = new Command("search", "Advanced file search") { roots, name, regex, ext, minSize, maxSize, olderThan, newerThan, notAccessed, temp, noHidden, delete, json };
        cmd.SetAction((pr, ct) =>
        {
            var criteria = new SearchCriteria
            {
                Roots = pr.GetValue(roots) ?? [],
                NamePattern = pr.GetValue(name),
                Regex = pr.GetValue(regex),
                Extensions = pr.GetValue(ext) ?? [],
                MinSize = ParseSize(pr.GetValue(minSize)),
                MaxSize = ParseSize(pr.GetValue(maxSize)),
                ModifiedBeforeUtc = pr.GetValue(olderThan) is { } o ? DateTime.UtcNow.AddDays(-o) : null,
                ModifiedAfterUtc = pr.GetValue(newerThan) is { } n ? DateTime.UtcNow.AddDays(-n) : null,
                AccessedBeforeUtc = pr.GetValue(notAccessed) is { } a ? DateTime.UtcNow.AddDays(-a) : null,
                TempFilesPreset = pr.GetValue(temp),
                IncludeHidden = !pr.GetValue(noHidden),
            };
            var results = FileSearch.Search(criteria, host.Store.ExclusionMatcher(), ct).ToList();
            if (pr.GetValue(json))
            {
                Output.WriteJson(results);
                return Task.FromResult(Output.Ok);
            }

            Output.FindingsTable(results, host.Store.LoadSettings().StaleDays);
            return Task.FromResult(pr.GetValue(delete) ? ConfirmAndClean(host, results, ct) : Output.Ok);
        });
        return cmd;
    }

    public static Command Dupes(AppHost host)
    {
        var roots = new Argument<string[]>("roots") { Description = "Folders to compare", Arity = ArgumentArity.OneOrMore };
        var minSize = new Option<string?>("--min-size") { Description = "Ignore files smaller than this (default from settings, 1MB)" };
        var delete = new Option<bool>("--delete") { Description = "Recycle all copies except the oldest (asks first)" };
        var json = new Option<bool>("--json");
        var cmd = new Command("dupes", "Find duplicate files") { roots, minSize, delete, json };
        cmd.SetAction((pr, ct) =>
        {
            var options = new DuplicateOptions
            {
                Roots = pr.GetValue(roots) ?? [],
                MinSize = ParseSize(pr.GetValue(minSize)) ?? host.Store.LoadSettings().DuplicateMinSize,
                Exclusions = host.Store.ExclusionMatcher(),
            };
            var groups = AnsiConsole.Status().Start("Finding duplicates...", ctx =>
                DuplicateFinder.Find(options, new Progress<string>(s => ctx.Status(Markup.Escape(s))), ct));
            if (pr.GetValue(json))
            {
                Output.WriteJson(groups);
                return Task.FromResult(Output.Ok);
            }

            foreach (var g in groups.Take(100))
            {
                var tree = new Tree($"[bold]{Bytes.Format(g.Size)}[/] x {g.Files.Count} (wasted {Bytes.Format(g.WastedBytes)})");
                foreach (var f in g.Files)
                    tree.AddNode((f == g.Keeper ? "[green]keep[/] " : "[grey]dup[/]  ") + Markup.Escape(f.Path));
                AnsiConsole.Write(tree);
            }

            AnsiConsole.MarkupLine($"[bold]{groups.Count} groups, {Bytes.Format(groups.Sum(g => g.WastedBytes))} wasted[/]");
            if (!pr.GetValue(delete))
                return Task.FromResult(Output.Ok);
            return Task.FromResult(ConfirmAndClean(host, groups.SelectMany(DuplicateFinder.ToFindings).ToList(), ct));
        });
        return cmd;
    }

    public static Command Usage()
    {
        var path = new Argument<string>("path") { Description = "Folder to analyse" };
        var depth = new Option<int>("--depth") { DefaultValueFactory = _ => 2 };
        var top = new Option<int>("--top") { DefaultValueFactory = _ => 20 };
        var cmd = new Command("usage", "Show largest folders") { path, depth, top };
        cmd.SetAction((pr, ct) =>
        {
            var node = AnsiConsole.Status().Start("Measuring...", _ => SizeTreeBuilder.Build(pr.GetValue(path)!, pr.GetValue(depth), ct));
            var tree = new Tree($"[bold]{Markup.Escape(node.Path)}[/] {Bytes.Format(node.Size)}");
            Add(tree, node, pr.GetValue(top), node.Size);
            AnsiConsole.Write(tree);
            return Task.FromResult(Output.Ok);
        });
        return cmd;

        static void Add(IHasTreeNodes parent, SizeNode node, int top, long total)
        {
            foreach (var c in node.Children.Where(c => c.IsDirectory).Take(top))
            {
                var pct = total == 0 ? 0 : c.Size * 100.0 / total;
                var child = parent.AddNode($"{Markup.Escape(c.Name)}  [blue]{Bytes.Format(c.Size)}[/] [grey]{pct:0.#}%[/]");
                Add(child, c, top, total);
            }
        }
    }

    private static int ConfirmAndClean(AppHost host, List<Finding> findings, CancellationToken ct)
    {
        if (findings.Count == 0 || !AnsiConsole.Confirm($"Move {findings.Count} files ({Bytes.Format(findings.Sum(f => f.SizeBytes))}) to the Recycle Bin?", false))
            return Output.Ok;
        var report = host.CreateCleanEngine().Clean(findings, new CleanOptions { Permanent = host.Store.LoadSettings().PermanentDelete }, null, ct);
        return Output.Report(report);
    }

    private static long? ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        return Bytes.TryParse(s, out var b) ? b : throw new ArgumentException($"Invalid size '{s}'");
    }
}
