using DevCleaner.Core;
using DevCleaner.Core.Abstractions;
using Microsoft.Win32;

namespace DevCleaner.Platform.Windows;

/// <summary>Safe registry cleanup: MRU history lists and uninstall entries whose install folder is gone. Always backs up to .reg first.</summary>
public sealed class RegistryProvider(string backupDirectory) : ICleanupProvider, ICustomCleaner
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKeyWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (string Name, string Key)[] MruKeys =
    [
        ("Recent documents list", @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs"),
        ("Run dialog history", @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU"),
        ("Explorer typed paths", @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths"),
    ];

    public string Id => "registry";
    public string Name => "Registry";
    public IReadOnlyCollection<Category> Categories => [Category.Registry];

    public IEnumerable<Finding> Scan(ScanContext context, CancellationToken ct)
    {
        if (!context.Includes(Category.Registry) || context.Options.DisabledProviders.Contains(Id))
            yield break;

        foreach (var (name, key) in MruKeys)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            if (k is null)
                continue;
            var entries = k.ValueCount + k.SubKeyCount;
            if (entries == 0)
                continue;
            yield return Make(name, @"HKCU\" + key, RiskLevel.Review, $"{entries} entries");
        }

        foreach (var (hive, prefix) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            foreach (var baseKey in new[] { UninstallKey, UninstallKeyWow })
            {
                ct.ThrowIfCancellationRequested();
                foreach (var f in ScanUninstall(hive, prefix, baseKey))
                    yield return f;
            }
        }
    }

    public long Clean(Finding finding, CancellationToken ct)
    {
        Backup(finding.Path);
        var (hive, subKey) = Split(finding.Path);
        if (MruKeys.Any(m => m.Key.Equals(subKey, StringComparison.OrdinalIgnoreCase)))
        {
            using var k = hive.OpenSubKey(subKey, writable: true) ?? throw new IOException("Key not found");
            foreach (var v in k.GetValueNames())
                k.DeleteValue(v, throwOnMissingValue: false);
            foreach (var s in k.GetSubKeyNames())
                k.DeleteSubKeyTree(s, throwOnMissingSubKey: false);
        }
        else if (subKey.StartsWith(UninstallKey + @"\", StringComparison.OrdinalIgnoreCase) ||
                 subKey.StartsWith(UninstallKeyWow + @"\", StringComparison.OrdinalIgnoreCase))
        {
            hive.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        else
        {
            throw new InvalidOperationException($"Refusing to modify unexpected key {finding.Path}");
        }

        return 0;
    }

    private IEnumerable<Finding> ScanUninstall(RegistryKey hive, string prefix, string baseKey)
    {
        using var root = hive.OpenSubKey(baseKey);
        if (root is null)
            yield break;

        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sub);
            if (k is null || k.GetValue("SystemComponent") is 1)
                continue;
            if (k.GetValue("InstallLocation") is not string loc || string.IsNullOrWhiteSpace(loc))
                continue;
            loc = loc.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(loc) || Directory.Exists(loc))
                continue;

            var display = k.GetValue("DisplayName") as string ?? sub;
            yield return Make($"Orphaned uninstall entry: {display}", $@"{prefix}\{baseKey}\{sub}", RiskLevel.Caution,
                $"Install location missing: {loc}" + (prefix == "HKLM" ? " (requires administrator)" : ""));
        }
    }

    private Finding Make(string name, string path, RiskLevel risk, string note) => new()
    {
        ProviderId = Id,
        Category = Category.Registry,
        Name = name,
        Path = path,
        Kind = FindingKind.Registry,
        Risk = risk,
        Note = note,
    };

    private void Backup(string keyPath)
    {
        Directory.CreateDirectory(backupDirectory);
        var safeName = string.Concat(keyPath.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safeName.Length > 120)
            safeName = safeName[^120..];
        var file = Path.Combine(backupDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}.reg");
        var r = ProcessRunner.Run("reg.exe", ["export", keyPath, file, "/y"], TimeSpan.FromSeconds(30));
        if (r is null || r.ExitCode != 0)
            throw new IOException($"Registry backup failed: {r?.StdErr.Trim()}");
    }

    private static (RegistryKey Hive, string SubKey) Split(string path)
    {
        var idx = path.IndexOf('\\', StringComparison.Ordinal);
        var hive = path[..idx] switch
        {
            "HKCU" => Registry.CurrentUser,
            "HKLM" => Registry.LocalMachine,
            _ => throw new InvalidOperationException($"Unsupported hive in {path}"),
        };
        return (hive, path[(idx + 1)..]);
    }
}
