namespace DevCleaner.Core;

public enum Category
{
    SystemTemp,
    Browsers,
    Ide,
    DotNetProjects,
    NodeProjects,
    OtherProjects,
    PackageCaches,
    Docker,
    Registry,
    Logs,
    Custom,
    Duplicates,
    GitIgnored,
}

public enum RiskLevel
{
    Safe,
    Review,
    Caution,
}

public enum FindingKind
{
    Directory,
    DirectoryContents,
    File,
    Docker,
    Registry,
}

public enum OsPlatform
{
    Windows,
    Linux,
    MacOS,
}

public sealed record Finding
{
    public required string ProviderId { get; init; }
    public required Category Category { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required FindingKind Kind { get; init; }
    public long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public RiskLevel Risk { get; init; }
    public string? ProjectRoot { get; init; }
    public DateTime? ProjectLastModifiedUtc { get; init; }
    public string? Note { get; init; }

    /// <summary>Enclosing Git work tree, when the item lives inside one.</summary>
    public string? GitRepoRoot { get; init; }

    /// <summary>Uncommitted changes (modified, staged or untracked) in <see cref="GitRepoRoot"/>; null if unknown.</summary>
    public int? GitUncommittedChanges { get; init; }

    public bool InDirtyRepo => GitUncommittedChanges > 0;

    /// <summary>Process names that must not be running while this item is cleaned.</summary>
    public IReadOnlyList<string> Processes { get; init; } = [];

    public string Id => $"{Kind}:{Path}";

    public bool IsFileSystem => Kind is FindingKind.Directory or FindingKind.DirectoryContents or FindingKind.File;

    public bool IsStale(int staleDays) =>
        ProjectLastModifiedUtc is { } d && d < DateTime.UtcNow.AddDays(-staleDays);
}

public static class OsInfo
{
    public static OsPlatform Current =>
        OperatingSystem.IsWindows() ? OsPlatform.Windows
        : OperatingSystem.IsMacOS() ? OsPlatform.MacOS
        : OsPlatform.Linux;

    public static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
