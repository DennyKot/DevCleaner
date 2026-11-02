using DevCleaner.Core;
using DevCleaner.Core.Abstractions;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Git;
using DevCleaner.Core.Providers;
using DevCleaner.Core.Rules;
using DevCleaner.Infrastructure.Persistence;

namespace DevCleaner.Infrastructure;

/// <summary>Composition root shared by the CLI and GUI.</summary>
public sealed class AppHost
{
    private AppHost(DataStore store, IPlatformServices platform, IReadOnlyList<CleanupRule> rules, IReadOnlyList<ICleanupProvider> providers,     List<string> warnings, GitInspector git)
    {
            Git = git;
        Store = store;
        Platform = platform;
        Rules = rules;
        Providers = providers;
        Warnings = warnings;
    }

    public DataStore Store { get; }
    public IPlatformServices Platform { get; }
    public IReadOnlyList<CleanupRule> Rules { get; }
    public IReadOnlyList<ICleanupProvider> Providers { get; }
    public IReadOnlyList<string> Warnings { get; }
    public GitInspector Git { get; }

    public static AppHost Create(string? dataRoot = null)
    {
        var store = new DataStore(dataRoot);
        var git = new GitInspector();
        IPlatformServices platform = OperatingSystem.IsWindows()
            ? new Platform.Windows.WindowsPlatformServices(store.BackupsDirectory)
            : new Platform.Unix.UnixPlatformServices();

        var warnings = new List<string>();
        var rules = RuleLoader.LoadBuiltIn().ToList();
        foreach (var file in Directory.GetFiles(store.RulesDirectory, "*.json"))
            rules.AddRange(RuleLoader.LoadFile(file, warnings));

        var dupIds = rules.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
        warnings.AddRange(dupIds.Select(id => $"Duplicate rule id '{id}'"));

        List<ICleanupProvider> providers = [new PathRuleProvider(rules), new ProjectRuleProvider(rules), new GitIgnoredProvider(git), new DockerProvider(), .. platform.Providers];
        return new AppHost(store, platform, rules, providers, warnings, git);
    }

    public IReadOnlyList<string> EffectiveProjectRoots(IEnumerable<string>? overrideRoots = null)
    {
        var roots = overrideRoots?.ToList() ?? [];
        if (roots.Count == 0)
            roots = Store.LoadSettings().ProjectRoots;
        if (roots.Count == 0)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots = DefaultRootNames
                .Select(d => Path.Combine(home, d))
                .Where(Directory.Exists)
                .ToList();
            roots.AddRange(DriveProjectRoots());
        }

        return roots.Select(Path.GetFullPath).Distinct(OsInfo.PathComparer).ToList();
    }

    private static readonly string[] DefaultRootNames =
        ["source", "sources", "repos", "projects", "project", "dev", "src", "code", "git", "github", "workspace", "work"];

    private const int DriveProbeDepth = 3;

    private static readonly HashSet<string> SkippedDriveFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "PerfLogs", "Recovery",
        "System Volume Information", "Intel", "AMD", "NVIDIA", "Dell", "inetpub", "OneDriveTemp", "Temp", "tmp",
    };

    private static readonly HashSet<string> SkippedUnixRootFolders = new(StringComparer.Ordinal)
    {
        "bin", "boot", "dev", "etc", "lib", "lib32", "lib64", "proc", "run", "sbin", "sys", "usr", "var", "snap",
        "System", "Library", "Applications", "Volumes", "private", "cores", "home", "Users", "mnt", "media", "opt", "srv", "root",
    };

    private static readonly string[] ProjectMarkerFiles =
        ["package.json", "Cargo.toml", "go.mod", "pom.xml", "build.gradle", "build.gradle.kts", "pyproject.toml", "requirements.txt", "composer.json", "Gemfile", "CMakeLists.txt", "pubspec.yaml"];

    private static readonly string[] ProjectMarkerPatterns = ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj"];

    // Cheap bounded probe: looks for a .git folder or project file within the first few levels.
    private static bool ContainsProject(DirectoryInfo dir, int depth)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return true;
            if (ProjectMarkerFiles.Any(f => File.Exists(Path.Combine(dir.FullName, f))))
                return true;
            if (ProjectMarkerPatterns.Any(p => dir.EnumerateFiles(p).Any()))
                return true;
            if (depth <= 1)
                return false;
            foreach (var sub in dir.EnumerateDirectories().Take(200))
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) || sub.Name.StartsWith('.')
                    || sub.Name is "node_modules" or "bin" or "obj")
                    continue;
                if (ContainsProject(sub, depth - 1))
                    return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    // Only top-level folders of fixed drives are inspected (e.g. C:\src, D:\Projects); deeper search happens in the scan.
    private static IEnumerable<string> DriveProjectRoots()
    {
        var names = DefaultRootNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }

        foreach (var drive in drives)
        {
            List<string> found = [];
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;
                if (!OperatingSystem.IsWindows() && drive.RootDirectory.FullName != "/")
                    continue;
                foreach (var dir in drive.RootDirectory.EnumerateDirectories())
                {
                    if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || SkippedDriveFolders.Contains(dir.Name)
                        || (!OperatingSystem.IsWindows() && SkippedUnixRootFolders.Contains(dir.Name))
                        || dir.Name.StartsWith('$') || dir.Name.StartsWith('.')
                        || (dir.Attributes.HasFlag(FileAttributes.Hidden) && dir.Attributes.HasFlag(FileAttributes.System)))
                        continue;
                    if (names.Contains(dir.Name) || ContainsProject(dir, DriveProbeDepth))
                        found.Add(dir.FullName);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            foreach (var f in found)
                yield return f;
        }
    }

    public ScanContext CreateScanContext(IEnumerable<string>? roots = null, IReadOnlySet<Category>? categories = null)
    {
        var settings = Store.LoadSettings();
        return new ScanContext(
            new ScanOptions
            {
                ProjectRoots = EffectiveProjectRoots(roots),
                Categories = categories,
                MaxParallelism = settings.MaxParallelism,
                DisabledProviders = settings.DisabledProviders.ToHashSet(StringComparer.OrdinalIgnoreCase),
            },
            Store.ExclusionMatcher());
    }

    public ScanEngine CreateScanEngine() => new(Providers, Git);

    public CleanEngine CreateCleanEngine() => new(Platform, Providers, Store.ExclusionMatcher(), Store);
}
