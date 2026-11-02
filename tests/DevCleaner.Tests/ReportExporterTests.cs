using System.Text.Json;
using DevCleaner.Core;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Reports;

namespace DevCleaner.Tests;

public sealed class ReportExporterTests : IDisposable
{
    private readonly TempTree _t = new();

    public void Dispose() => _t.Dispose();

    private static Finding F(string name, string path, long size, string? note = null) => new()
    {
        ProviderId = "test", Category = Category.NodeProjects, Name = name, Path = path,
        Kind = FindingKind.Directory, SizeBytes = size, Note = note,
    };

    [Theory]
    [InlineData("r.json", ReportFormat.Json)]
    [InlineData("r.CSV", ReportFormat.Csv)]
    [InlineData("r.htm", ReportFormat.Html)]
    [InlineData("r.txt", null)]
    public void FormatFromPath_UsesExtension(string path, ReportFormat? expected) =>
        Assert.Equal(expected, ReportExporter.FormatFromPath(path));

    [Fact]
    public void Findings_Json_RoundTrips()
    {
        var path = _t.P("out/scan.json");
        ReportExporter.SaveFindings(path, [F("node_modules", @"C:\a\node_modules", 2048), F("dist", @"C:\a\dist", 10)]);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2058, doc.RootElement.GetProperty("totalBytes").GetInt64());
        var first = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal("node_modules", first.GetProperty("name").GetString());
        Assert.Equal("NodeProjects", first.GetProperty("category").GetString());
    }

    [Fact]
    public void Findings_Csv_EscapesAndNeutralisesFormulas()
    {
        var path = _t.P("scan.csv");
        ReportExporter.SaveFindings(path, [F("a,\"b\"", @"C:\x", 1, "=HYPERLINK(1)")]);
        var lines = File.ReadAllLines(path);
        Assert.StartsWith("Category,Name,Path", lines[0]);
        Assert.Contains("\"a,\"\"b\"\"\"", lines[1]);
        Assert.Contains("'=HYPERLINK(1)", lines[1]);
    }

    [Fact]
    public void CleanReport_Html_IsEncoded()
    {
        var report = new CleanReport(DateTime.UtcNow, DateTime.UtcNow, true,
            [new CleanItemResult(F("<script>x</script>", @"C:\p", 5), CleanStatus.DryRun, 5, "ok & fine")]);
        var path = _t.P("clean.html");
        ReportExporter.SaveCleanReport(path, report);
        var html = File.ReadAllText(path);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("ok &amp; fine", html);
        Assert.Contains("dry-run", html);
    }

    [Fact]
    public void ExplicitFormat_OverridesExtension()
    {
        var path = _t.P("report.txt");
        ReportExporter.SaveFindings(path, [F("a", "b", 1)], ReportFormat.Csv);
        Assert.StartsWith("Category,", File.ReadAllLines(path)[0]);
    }
}
