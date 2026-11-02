using System.CommandLine;
using DevCleaner.Core;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Reports;
using DevCleaner.Infrastructure;
using Spectre.Console;

namespace DevCleaner.Cli.Commands;

internal static class ScanCommands
{
    private static Option<Category[]> CategoryOption() => new("--category", "-c")
    {
        Description = "Limit to categories (repeatable)",
        AllowMultipleArgumentsPerToken = true,
    };

    private static Option<string[]> RootOption() => new("--root", "-r")
    {
        Description = "Project root folders to scan (default: configured roots)",
        AllowMultipleArgumentsPerToken = true,
    };

    private static Option<string?> ExportOption() => new("--export", "-o")
    {
        Description = "Write a report to this file (.json, .csv or .html)",
    };

    private static Option<ReportFormat?> FormatOption() => new("--format")
    {
        Description = "Report format (default: inferred from --export extension)",
    };

    private static void Export(string? path, ReportFormat? format, Action<string, ReportFormat?> save)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var full = Path.GetFullPath(path);
        save(full, format);
        Console.Error.WriteLine($"Report written to {full}");
    }

    public static Command Scan(AppHost host)
    {
        var categories = CategoryOption();
        var roots = RootOption();
        var json = new Option<bool>("--json") { Description = "Output JSON" };
        var export = ExportOption();
        var format = FormatOption();
        var cmd = new Command("scan", "Scan for cleanable items") { categories, roots, json, export, format };
        cmd.SetAction(async (pr, ct) =>
        {
            var findings = await RunScan(host, pr.GetValue(roots), pr.GetValue(categories), pr.GetValue(json), ct);
            if (pr.GetValue(json))
                Output.WriteJson(findings);
            else
                Output.FindingsTable(findings, host.Store.LoadSettings().StaleDays);
            Export(pr.GetValue(export), pr.GetValue(format), (p, fmt) => ReportExporter.SaveFindings(p, findings, fmt));
            return Output.Ok;
        });
        return cmd;
    }

    public static Command Clean(AppHost host)
    {
        var categories = CategoryOption();
        var roots = RootOption();
        var maxRisk = new Option<RiskLevel>("--max-risk") { Description = "Highest risk level to include", DefaultValueFactory = _ => RiskLevel.Safe };
        var staleOnly = new Option<bool>("--stale-only") { Description = "Only clean project artefacts of stale projects" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be removed without deleting" };
        var permanent = new Option<bool>("--permanent") { Description = "Delete permanently instead of using the Recycle Bin" };
        var force = new Option<bool>("--force") { Description = "Clean even if related apps (browser/IDE) are running" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation" };
        var export = ExportOption();
        var format = FormatOption();
        var cmd = new Command("clean", "Scan and clean items") { categories, roots, maxRisk, staleOnly, dryRun, permanent, force, yes, export, format };
        cmd.SetAction(async (pr, ct) =>
        {
            var settings = host.Store.LoadSettings();
            var findings = (await RunScan(host, pr.GetValue(roots), pr.GetValue(categories), false, ct))
                .Where(f => f.Risk <= pr.GetValue(maxRisk))
                .Where(f => !pr.GetValue(staleOnly) || f.ProjectRoot is null || f.IsStale(settings.StaleDays))
                .ToList();
            if (findings.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]Nothing to clean.[/]");
                return Output.Ok;
            }

            Output.FindingsTable(findings, settings.StaleDays);
            var isDry = pr.GetValue(dryRun);
            if (!isDry && !pr.GetValue(yes) && !AnsiConsole.Confirm($"Clean {findings.Count} items ({Bytes.Format(findings.Sum(f => f.SizeBytes))})?", false))
                return Output.Ok;

            var options = new CleanOptions { DryRun = isDry, Permanent = pr.GetValue(permanent) || settings.PermanentDelete, Force = pr.GetValue(force) };
            var report = AnsiConsole.Status().Start("Cleaning...", ctx =>
                host.CreateCleanEngine().Clean(findings, options, new Progress<CleanProgress>(p => ctx.Status(
                    $"Cleaning {Math.Min(p.ItemsDone + 1, p.ItemsTotal)}/{p.ItemsTotal} ({p.Fraction:P0}, {Bytes.Format(p.BytesDone)} of {Bytes.Format(p.BytesTotal)}) {Markup.Escape(p.Current.Name)}")), ct));
            var code = Output.Report(report);
            Export(pr.GetValue(export), pr.GetValue(format), (p, fmt) => ReportExporter.SaveCleanReport(p, report, fmt));
            return code;
        });
        return cmd;
    }

    private static async Task<List<Finding>> RunScan(AppHost host, string[]? roots, Category[]? categories, bool quiet, CancellationToken ct)
    {
        if (!quiet)
            Output.Warnings(host.Warnings);
        var context = host.CreateScanContext(roots, categories is { Length: > 0 } ? categories.ToHashSet() : null);
        var engine = host.CreateScanEngine();
        List<Finding> findings;
        if (quiet)
        {
            findings = await engine.ScanAllAsync(context, null, ct);
        }
        else
        {
            AnsiConsole.MarkupLine($"Project roots: {Markup.Escape(string.Join(", ", context.Options.ProjectRoots))}");
            findings = await AnsiConsole.Status().StartAsync("Scanning...", async ctx =>
            {
                var count = 0;
                long total = 0;
                return await engine.ScanAllAsync(context, new Progress<Finding>(f =>
                {
                    count++;
                    total += f.SizeBytes;
                    ctx.Status($"Scanning... {count} items, {Bytes.Format(total)}");
                }), ct);
            });
            Output.Warnings(context.Errors);
        }

        return findings;
    }
}
