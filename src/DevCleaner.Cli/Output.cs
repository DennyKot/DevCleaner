using System.Text.Json;
using System.Text.Json.Serialization;
using DevCleaner.Core;
using DevCleaner.Core.Engine;
using Spectre.Console;

namespace DevCleaner.Cli;

internal static class Output
{
    public const int Ok = 0;
    public const int Partial = 1;
    public const int Error = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));

    public static string Risk(RiskLevel r) => r switch
    {
        RiskLevel.Safe => "[green]Safe[/]",
        RiskLevel.Review => "[yellow]Review[/]",
        _ => "[red]Caution[/]",
    };

    public static string Date(DateTime? utc) => utc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";

    public static void FindingsTable(IReadOnlyList<Finding> findings, int staleDays)
    {
        foreach (var group in findings.GroupBy(f => f.Category).OrderByDescending(g => g.Sum(f => f.SizeBytes)))
        {
            var table = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]{group.Key}[/] - {Bytes.Format(group.Sum(f => f.SizeBytes))}")
                .AddColumn("Item").AddColumn("Path").AddColumn(new TableColumn("Size").RightAligned())
                .AddColumn("Modified").AddColumn("Risk").AddColumn("Note");
            foreach (var f in group.OrderByDescending(f => f.SizeBytes))
            {
                var modified = f.ProjectRoot is null
                    ? Date(f.LastModifiedUtc)
                    : Date(f.ProjectLastModifiedUtc) + (f.IsStale(staleDays) ? " [grey](stale)[/]" : "");
                table.AddRow(
                    Markup.Escape(f.Name),
                    Markup.Escape(f.Path),
                    Bytes.Format(f.SizeBytes),
                    modified,
                    Risk(f.Risk),
                    Markup.Escape(f.Note ?? ""));
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.MarkupLine($"[bold]Total:[/] {findings.Count} items, {Bytes.Format(findings.Sum(f => f.SizeBytes))}");
    }

    public static int Report(CleanReport report)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Status").AddColumn("Item").AddColumn("Path").AddColumn(new TableColumn("Freed").RightAligned()).AddColumn("Message");
        foreach (var i in report.Items)
        {
            var status = i.Status switch
            {
                CleanStatus.Cleaned => "[green]Cleaned[/]",
                CleanStatus.DryRun => "[blue]Dry run[/]",
                CleanStatus.Partial => "[yellow]Partial[/]",
                CleanStatus.Skipped => "[grey]Skipped[/]",
                _ => "[red]Failed[/]",
            };
            table.AddRow(status, Markup.Escape(i.Finding.Name), Markup.Escape(i.Finding.Path), Bytes.Format(i.BytesFreed), Markup.Escape(i.Message ?? ""));
        }

        AnsiConsole.Write(table);
        var verb = report.DryRun ? "Would free" : "Freed";
        AnsiConsole.MarkupLine($"[bold]{verb}:[/] {Bytes.Format(report.TotalBytesFreed)}");
        return report.Items.Any(i => i.Status is CleanStatus.Failed or CleanStatus.Partial) ? Partial : Ok;
    }

    public static void Warnings(IEnumerable<string> warnings)
    {
        foreach (var w in warnings)
            AnsiConsole.MarkupLine($"[yellow]warning:[/] {Markup.Escape(w)}");
    }
}
