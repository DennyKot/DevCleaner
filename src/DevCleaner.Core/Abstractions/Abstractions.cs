namespace DevCleaner.Core.Abstractions;

public interface ICleanupProvider
{
    string Id { get; }
    string Name { get; }
    IReadOnlyCollection<Category> Categories { get; }

    /// <summary>Synchronous, streaming scan; the engine runs providers in parallel.</summary>
    IEnumerable<Finding> Scan(ScanContext context, CancellationToken ct);
}

/// <summary>Implemented by providers whose findings are not plain file-system items (Docker, registry).</summary>
public interface ICustomCleaner
{
    /// <returns>Bytes freed.</returns>
    long Clean(Finding finding, CancellationToken ct);
}

public interface IRecycleBin
{
    bool IsSupported { get; }
    void Recycle(IReadOnlyList<string> paths);
}

public interface IFileLockDetector
{
    IReadOnlyList<string> GetLockingProcesses(string path);
}

public interface IPlatformServices
{
    IRecycleBin RecycleBin { get; }
    IFileLockDetector LockDetector { get; }
    IReadOnlyList<ICleanupProvider> Providers { get; }
}

public interface ICleanRecorder
{
    void Record(Engine.CleanReport report);
}

public sealed class ScanOptions
{
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];
    public IReadOnlySet<Category>? Categories { get; init; }
    public IReadOnlySet<string> DisabledProviders { get; init; } = new HashSet<string>();
    public int MaxProjectDepth { get; init; } = 12;
    public int MaxParallelism { get; init; } = 4;
}

public sealed class ScanContext(ScanOptions options, PathMatcher exclusions)
{
    public ScanOptions Options { get; } = options;
    public PathMatcher Exclusions { get; } = exclusions;
    public System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();

    public bool Includes(Category category) => Options.Categories is null || Options.Categories.Contains(category);
}
