using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevCleaner.Core.Abstractions;

namespace DevCleaner.Core.Providers;

/// <summary>Reports reclaimable Docker space via the docker CLI; hidden when docker is unavailable.</summary>
public sealed partial class DockerProvider : ICleanupProvider, ICustomCleaner
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PruneTimeout = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<string, (string Key, string Name, string[] Prune, RiskLevel Risk)> Types = new()
    {
        ["Images"] = ("images", "Unused images", ["image", "prune", "-a", "-f"], RiskLevel.Review),
        ["Containers"] = ("containers", "Stopped containers", ["container", "prune", "-f"], RiskLevel.Review),
        ["Local Volumes"] = ("volumes", "Unused volumes", ["volume", "prune", "-a", "-f"], RiskLevel.Caution),
        ["Build Cache"] = ("build-cache", "Build cache", ["builder", "prune", "-f"], RiskLevel.Safe),
    };

    public string Id => "docker";
    public string Name => "Docker";
    public IReadOnlyCollection<Category> Categories => [Category.Docker];

    public IEnumerable<Finding> Scan(ScanContext context, CancellationToken ct)
    {
        if (!context.Includes(Category.Docker) || context.Options.DisabledProviders.Contains(Id))
            return [];

        var result = ProcessRunner.Run("docker", ["system", "df", "--format", "{{json .}}"], ScanTimeout);
        if (result is null || result.ExitCode != 0)
            return [];

        var findings = new List<Finding>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("Type").GetString() ?? "";
                var reclaimable = doc.RootElement.GetProperty("Reclaimable").GetString() ?? "";
                if (!Types.TryGetValue(type, out var t))
                    continue;
                var size = ParseSize(reclaimable);
                if (size <= 0)
                    continue;
                findings.Add(new Finding
                {
                    ProviderId = Id,
                    Category = Category.Docker,
                    Name = t.Name,
                    Path = "docker://" + t.Key,
                    Kind = FindingKind.Docker,
                    SizeBytes = size,
                    Risk = t.Risk,
                    Note = "docker " + string.Join(' ', t.Prune),
                });
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                context.Errors.Enqueue($"docker: cannot parse '{line}'");
            }
        }

        return findings;
    }

    public long Clean(Finding finding, CancellationToken ct)
    {
        var t = Types.Values.FirstOrDefault(v => "docker://" + v.Key == finding.Path);
        if (t.Prune is null)
            throw new InvalidOperationException($"Unknown docker item {finding.Path}");

        var r = ProcessRunner.Run("docker", t.Prune, PruneTimeout)
            ?? throw new IOException("docker CLI not available or timed out");
        if (r.ExitCode != 0)
            throw new IOException(r.StdErr.Trim());

        var m = ReclaimedRegex().Match(r.StdOut);
        return m.Success ? ParseSize(m.Groups[1].Value) : finding.SizeBytes;
    }

    /// <summary>Parses docker sizes such as "1.2GB (45%)" (decimal units).</summary>
    internal static long ParseSize(string text)
    {
        var m = SizeRegex().Match(text);
        if (!m.Success)
            return 0;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mul = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => 1,
        };
        return (long)(n * mul);
    }

    [GeneratedRegex(@"^\s*([\d.]+)\s*([kKMGT]?B)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"Total reclaimed space:\s*(\S+)")]
    private static partial Regex ReclaimedRegex();
}
