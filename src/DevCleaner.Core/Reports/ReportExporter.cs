using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevCleaner.Core.Engine;

namespace DevCleaner.Core.Reports;

public enum ReportFormat
{
    Json,
    Csv,
    Html,
}

/// <summary>Writes scan findings and clean reports as JSON, CSV or self-contained HTML.</summary>
public static class ReportExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] FindingColumns =
        ["Category", "Name", "Path", "Kind", "SizeBytes", "Size", "FileCount", "LastModifiedUtc", "Risk", "ProjectRoot", "ProjectLastModifiedUtc", "Note", "Provider"];

    private static readonly string[] CleanColumns =
        ["Status", "Category", "Name", "Path", "Kind", "SizeBytes", "BytesFreed", "Freed", "Risk", "Message"];

    public static ReportFormat? FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => ReportFormat.Json,
        ".csv" => ReportFormat.Csv,
        ".html" or ".htm" => ReportFormat.Html,
        _ => null,
    };

    public static void SaveFindings(string path, IReadOnlyList<Finding> findings, ReportFormat? format = null)
    {
        var fmt = format ?? FormatFromPath(path) ?? ReportFormat.Json;
        using var writer = OpenWriter(path, fmt);
        WriteFindings(writer, findings, fmt);
    }

    public static void SaveCleanReport(string path, CleanReport report, ReportFormat? format = null)
    {
        var fmt = format ?? FormatFromPath(path) ?? ReportFormat.Json;
        using var writer = OpenWriter(path, fmt);
        WriteCleanReport(writer, report, fmt);
    }

    private static StreamWriter OpenWriter(string path, ReportFormat format)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // BOM for CSV so Excel detects UTF-8.
        return new StreamWriter(path, false, new UTF8Encoding(format == ReportFormat.Csv));
    }

    public static void WriteFindings(TextWriter writer, IReadOnlyList<Finding> findings, ReportFormat format)
    {
        var ordered = findings.OrderBy(f => f.Category).ThenByDescending(f => f.SizeBytes).ToList();
        switch (format)
        {
            case ReportFormat.Json:
                writer.Write(JsonSerializer.Serialize(new
                {
                    generatedUtc = DateTime.UtcNow,
                    machine = Environment.MachineName,
                    totalBytes = ordered.Sum(f => f.SizeBytes),
                    count = ordered.Count,
                    findings = ordered,
                }, Json));
                break;
            case ReportFormat.Csv:
                WriteCsv(writer, FindingColumns, ordered.Select(FindingRow));
                break;
            default:
                var summary = ordered.GroupBy(f => f.Category)
                    .Select(g => (g.Key.ToString(), g.Count(), g.Sum(f => f.SizeBytes)))
                    .OrderByDescending(x => x.Item3).ToList();
                WriteHtml(writer, "DevCleaner scan report",
                    $"{ordered.Count} items · {Bytes.Format(ordered.Sum(f => f.SizeBytes))} reclaimable",
                    summary, FindingColumns, ordered.Select(FindingRow));
                break;
        }
    }

    public static void WriteCleanReport(TextWriter writer, CleanReport report, ReportFormat format)
    {
        switch (format)
        {
            case ReportFormat.Json:
                writer.Write(JsonSerializer.Serialize(new
                {
                    report.StartedUtc,
                    report.FinishedUtc,
                    report.DryRun,
                    machine = Environment.MachineName,
                    totalBytesFreed = report.TotalBytesFreed,
                    items = report.Items.Select(i => new { i.Status, i.BytesFreed, i.Message, finding = i.Finding }),
                }, Json));
                break;
            case ReportFormat.Csv:
                WriteCsv(writer, CleanColumns, report.Items.Select(CleanRow));
                break;
            default:
                var summary = report.Items.GroupBy(i => i.Status)
                    .Select(g => (g.Key.ToString(), g.Count(), g.Sum(i => i.BytesFreed))).ToList();
                var verb = report.DryRun ? "would be freed" : "freed";
                WriteHtml(writer, report.DryRun ? "DevCleaner dry-run report" : "DevCleaner clean report",
                    $"{report.Items.Count} items · {Bytes.Format(report.TotalBytesFreed)} {verb} · started {report.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}",
                    summary, CleanColumns, report.Items.Select(CleanRow));
                break;
        }
    }

    private static string?[] FindingRow(Finding f) =>
    [
        f.Category.ToString(), f.Name, f.Path, f.Kind.ToString(), Num(f.SizeBytes), Bytes.Format(f.SizeBytes), Num(f.FileCount),
        Date(f.LastModifiedUtc), f.Risk.ToString(), f.ProjectRoot, Date(f.ProjectLastModifiedUtc), f.Note, f.ProviderId,
    ];

    private static string?[] CleanRow(CleanItemResult i) =>
    [
        i.Status.ToString(), i.Finding.Category.ToString(), i.Finding.Name, i.Finding.Path, i.Finding.Kind.ToString(),
        Num(i.Finding.SizeBytes), Num(i.BytesFreed), Bytes.Format(i.BytesFreed), i.Finding.Risk.ToString(), i.Message,
    ];

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string? Date(DateTime? d) => d?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static void WriteCsv(TextWriter writer, string[] header, IEnumerable<string?[]> rows)
    {
        writer.WriteLine(string.Join(',', header.Select(CsvCell)));
        foreach (var row in rows)
            writer.WriteLine(string.Join(',', row.Select(CsvCell)));
    }

    public static string CsvCell(string? value)
    {
        value ??= "";
        // Neutralise spreadsheet formula injection: Excel evaluates cells starting with = + - @.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static void WriteHtml(TextWriter w, string title, string subtitle, List<(string Name, int Count, long Bytes)> summary,
        string[] header, IEnumerable<string?[]> rows)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        w.WriteLine("<!DOCTYPE html>");
        w.WriteLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        w.WriteLine($"<title>{E(title)}</title>");
        w.WriteLine("""
            <style>
            body{font-family:Segoe UI,system-ui,sans-serif;margin:32px;color:#1f2937;background:#f9fafb}
            h1{margin:0 0 4px;font-size:24px}.sub{color:#6b7280;margin-bottom:24px}
            .cards{display:flex;flex-wrap:wrap;gap:12px;margin-bottom:24px}
            .card{background:#fff;border:1px solid #e5e7eb;border-radius:8px;padding:12px 16px;min-width:140px}
            .card b{display:block;font-size:18px}.card span{color:#6b7280;font-size:12px}
            table{border-collapse:collapse;width:100%;background:#fff;font-size:13px}
            th,td{border:1px solid #e5e7eb;padding:6px 8px;text-align:left;vertical-align:top}
            th{background:#f3f4f6;position:sticky;top:0}td{word-break:break-all}
            </style></head><body>
            """);
        w.WriteLine($"<h1>{E(title)}</h1><div class=\"sub\">{E(subtitle)} · generated {E(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} on {E(Environment.MachineName)}</div>");
        w.WriteLine("<div class=\"cards\">");
        foreach (var s in summary)
            w.WriteLine($"<div class=\"card\"><span>{E(s.Name)}</span><b>{E(Bytes.Format(s.Bytes))}</b><span>{s.Count} items</span></div>");
        w.WriteLine("</div>");
        w.Write("<table><thead><tr>");
        foreach (var h in header)
            w.Write($"<th>{E(h)}</th>");
        w.WriteLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            w.Write("<tr>");
            foreach (var cell in row)
                w.Write($"<td>{E(cell)}</td>");
            w.WriteLine("</tr>");
        }

        w.WriteLine("</tbody></table></body></html>");
    }
}
