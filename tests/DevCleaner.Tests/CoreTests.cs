using DevCleaner.Core;
using DevCleaner.Core.Abstractions;
using DevCleaner.Core.Duplicates;
using DevCleaner.Core.Engine;
using DevCleaner.Core.IO;
using DevCleaner.Core.Providers;
using DevCleaner.Core.Rules;
using DevCleaner.Core.Search;

namespace DevCleaner.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly TempTree _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void BuiltInRules_AreValid()
    {
        var rules = RuleLoader.LoadBuiltIn();
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Empty(RuleLoader.Validate(r)));
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void CustomRules_RejectTraversalTargets()
    {
        var file = _t.File("rules.json", System.Text.Encoding.UTF8.GetBytes(
            """[{ "id": "bad", "name": "Bad", "markers": ["x.txt"], "targets": ["..\\.."] }, { "id": "ok", "name": "Ok", "paths": ["{temp}"] }]"""));
        var errors = new List<string>();
        var rules = RuleLoader.LoadFile(file, errors);
        Assert.Single(rules);
        Assert.Equal("ok", rules[0].Id);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ProjectProvider_FindsArtefacts_WithProjectDate_AndRespectsExclusions()
    {
        var old = DateTime.UtcNow.AddDays(-200);
        _t.File(@"a\App.csproj", 10, old);
        _t.File(@"a\Program.cs", 10, old);
        _t.File(@"a\bin\Debug\app.dll", 1000);
        _t.File(@"a\obj\x.json", 500);
        _t.File(@"web\package.json", 10);
        _t.File(@"web\node_modules\lib\index.js", 2000);
        _t.File(@"keep\Keep.csproj", 10);
        _t.File(@"keep\bin\k.dll", 100);
        _t.File(@"noproject\bin\stray.dll", 100);

        var findings = Scan(new ProjectRuleProvider(RuleLoader.LoadBuiltIn()), new PathMatcher([_t.P("keep")]));

        Assert.Contains(findings, f => f.Path == _t.P(@"a\bin") && f.SizeBytes == 1000 && f.IsStale(90));
        Assert.Contains(findings, f => f.Path == _t.P(@"a\obj") && f.Category == Category.DotNetProjects);
        Assert.Contains(findings, f => f.Path == _t.P(@"web\node_modules") && f.Risk == RiskLevel.Review && !f.IsStale(90));
        Assert.DoesNotContain(findings, f => f.Path.StartsWith(_t.P("keep"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(findings, f => f.Path.Contains("noproject", StringComparison.OrdinalIgnoreCase));
        var a = findings.First(f => f.Path == _t.P(@"a\bin"));
        Assert.Equal(old, a.ProjectLastModifiedUtc!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ProjectProvider_FindsGameEngineAndTerraformArtefacts()
    {
        _t.File(@"game\ProjectSettings\ProjectVersion.txt", 10);
        _t.File(@"game\Library\cache.bin", 300);
        _t.File(@"fake\Library\cache.bin", 300);
        _t.File(@"ue\My.uproject", 10);
        _t.File(@"ue\Intermediate\x.obj", 400);
        _t.File(@"infra\main.tf", 10);
        _t.File(@"infra\.terraform\providers\p.exe", 500);

        var findings = Scan(new ProjectRuleProvider(RuleLoader.LoadBuiltIn()), PathMatcher.Empty);

        Assert.Contains(findings, f => f.Path == _t.P(@"game\Library") && f.Risk == RiskLevel.Review);
        Assert.DoesNotContain(findings, f => f.Path == _t.P(@"fake\Library"));
        Assert.Contains(findings, f => f.Path == _t.P(@"ue\Intermediate"));
        Assert.Contains(findings, f => f.Path == _t.P(@"infra\.terraform"));
    }

    [Fact]
    public void PathRuleProvider_ExpandsWildcards()
    {
        _t.File(@"browser\Default\Cache\f1", 50);
        _t.File(@"browser\Profile 1\Cache\f2", 70);
        var rule = new CleanupRule { Id = "t", Name = "T", Category = Category.Browsers, Risk = RiskLevel.Safe, Paths = [_t.P(@"browser\*\Cache")], ContentsOnly = true };
        var findings = Scan(new PathRuleProvider([rule]), PathMatcher.Empty);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(FindingKind.DirectoryContents, f.Kind));
        Assert.Equal(120, findings.Sum(f => f.SizeBytes));
    }

    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\work\proj\bin", true)]
    public void SafetyGuard_BlocksRootsAndSystemFolders(string path, bool expected)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(expected, SafetyGuard.IsSafeToDelete(path, out _));
        Assert.False(SafetyGuard.IsSafeToDelete(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _));
    }

    [Fact]
    public void PathMatcher_MatchesDescendantsOnly()
    {
        var m = new PathMatcher([_t.P("proj"), "*.keep"]);
        Assert.True(m.IsMatch(_t.P(@"proj\bin")));
        Assert.True(m.IsMatch(_t.P("proj")));
        Assert.False(m.IsMatch(_t.P("project2")));
        Assert.True(m.IsMatch(_t.P(@"x\file.keep")));
    }

    [Fact]
    public void CleanEngine_DryRunKeepsFiles_PermanentDeletes_ContentsKeepsRoot()
    {
        _t.File(@"p\P.csproj");
        _t.File(@"p\bin\a.dll", 300);
        _t.File(@"cache\x\1.tmp", 200);
        var dir = new Finding { ProviderId = "t", Category = Category.DotNetProjects, Name = "bin", Path = _t.P(@"p\bin"), Kind = FindingKind.Directory, SizeBytes = 300 };
        var contents = new Finding { ProviderId = "t", Category = Category.SystemTemp, Name = "cache", Path = _t.P("cache"), Kind = FindingKind.DirectoryContents, SizeBytes = 200 };
        var recorder = new Recorder();
        var engine = new CleanEngine(new FakePlatform(), [], PathMatcher.Empty, recorder);

        var dry = engine.Clean([dir, contents], new CleanOptions { DryRun = true });
        Assert.All(dry.Items, i => Assert.Equal(CleanStatus.DryRun, i.Status));
        Assert.True(Directory.Exists(_t.P(@"p\bin")));
        Assert.Null(recorder.Last);

        var real = engine.Clean([dir, contents], new CleanOptions { Permanent = true });
        Assert.Equal(500, real.TotalBytesFreed);
        Assert.False(Directory.Exists(_t.P(@"p\bin")));
        Assert.True(Directory.Exists(_t.P("cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_t.P("cache")));
        Assert.Same(real, recorder.Last);
    }

    [Fact]
    public void CleanEngine_ReportsStartAndCompletionProgress()
    {
        _t.File(@"a\x.bin", 100);
        _t.File(@"b\y.bin", 300);
        var a = new Finding { ProviderId = "t", Category = Category.Custom, Name = "a", Path = _t.P("a"), Kind = FindingKind.Directory, SizeBytes = 100 };
        var b = new Finding { ProviderId = "t", Category = Category.Custom, Name = "b", Path = _t.P("b"), Kind = FindingKind.DirectoryContents, SizeBytes = 300 };
        var events = new List<CleanProgress>();

        new CleanEngine(new FakePlatform(), [], PathMatcher.Empty).Clean([a, b], new CleanOptions { Permanent = true }, new SyncProgress<CleanProgress>(events.Add));

        Assert.Equal((0, "a", 0L), (events[0].ItemsDone, events[0].Current.Name, events[0].BytesDone));
        Assert.Null(events[0].Completed);
        Assert.Contains(events, e => e.Completed?.Finding == a && e.BytesDone == 100);
        Assert.Equal(400, events[^1].BytesTotal);
        Assert.Equal((2, 400L, 1.0), (events[^1].ItemsDone, events[^1].BytesDone, events[^1].Fraction));
        Assert.Equal(events.Select(e => e.BytesDone).Order(), events.Select(e => e.BytesDone));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public void CleanEngine_SkipsExcludedAndLockedFiles()
    {
        var locked = _t.File(@"l\locked.bin", 100);
        var excluded = _t.File(@"ex\a.bin", 100);
        var engine = new CleanEngine(new FakePlatform(), [], new PathMatcher([_t.P("ex")]));
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var report = engine.Clean(
        [
            new Finding { ProviderId = "t", Category = Category.Custom, Name = "l", Path = locked, Kind = FindingKind.File, SizeBytes = 100 },
            new Finding { ProviderId = "t", Category = Category.Custom, Name = "e", Path = excluded, Kind = FindingKind.File, SizeBytes = 100 },
        ], new CleanOptions { Permanent = true });

        if (OperatingSystem.IsWindows())
            Assert.Equal(CleanStatus.Failed, report.Items[0].Status);
        Assert.Equal(CleanStatus.Skipped, report.Items[1].Status);
        Assert.True(File.Exists(excluded));
    }

    [Fact]
    public void DuplicateFinder_GroupsIdenticalFiles_KeepsOldest()
    {
        var content = new byte[10_000];
        new Random(1).NextBytes(content);
        var a = _t.File(@"d1\a.bin", content);
        File.SetCreationTimeUtc(a, DateTime.UtcNow.AddDays(-10));
        _t.File(@"d2\b.bin", content);
        var different = (byte[])content.Clone();
        different[^1] ^= 0xFF;
        _t.File(@"d3\c.bin", different);

        var groups = DuplicateFinder.Find(new DuplicateOptions { Roots = [_t.Root], MinSize = 1 });

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Files.Count);
        Assert.Equal(a, g.Keeper.Path);
        Assert.Equal(10_000, g.WastedBytes);
        Assert.Single(DuplicateFinder.ToFindings(g));
    }

    [Fact]
    public void FileSearch_TempPresetAndFilters()
    {
        _t.File(@"s\a.tmp", 10);
        _t.File(@"s\~lock.docx", 10);
        _t.File(@"s\real.cs", 10);
        _t.File(@"s\big.log", 5000, DateTime.UtcNow.AddDays(-40));

        var temp = FileSearch.Search(new SearchCriteria { Roots = [_t.Root], TempFilesPreset = true }, null, default).ToList();
        Assert.Equal(3, temp.Count);

        var old = FileSearch.Search(new SearchCriteria { Roots = [_t.Root], MinSize = 1000, ModifiedBeforeUtc = DateTime.UtcNow.AddDays(-30) }, null, default).ToList();
        Assert.Equal("big.log", Assert.Single(old).Name);
    }

    [Theory]
    [InlineData("1.5GB (40%)", 1_500_000_000)]
    [InlineData("512kB", 512_000)]
    [InlineData("0B (0%)", 0)]
    public void Docker_ParsesSizes(string text, long expected) => Assert.Equal(expected, DockerProvider.ParseSize(text));

    [Theory]
    [InlineData("10MB", 10L * 1024 * 1024)]
    [InlineData("1.5 KB", 1536)]
    [InlineData("42", 42)]
    public void Bytes_Parses(string text, long expected)
    {
        Assert.True(Bytes.TryParse(text, out var b));
        Assert.Equal(expected, b);
    }

    private List<Finding> Scan(ICleanupProvider provider, PathMatcher exclusions)
    {
        var ctx = new ScanContext(new ScanOptions { ProjectRoots = [_t.Root] }, exclusions);
        return new ScanEngine([provider]).ScanAllAsync(ctx).GetAwaiter().GetResult();
    }

    private sealed class Recorder : ICleanRecorder
    {
        public CleanReport? Last { get; private set; }

        public void Record(CleanReport report) => Last = report;
    }

    private sealed class FakePlatform : IPlatformServices
    {
        public IRecycleBin RecycleBin => throw new NotSupportedException();
        public IFileLockDetector LockDetector { get; } = new NoLocks();
        public IReadOnlyList<ICleanupProvider> Providers => [];

        private sealed class NoLocks : IFileLockDetector
        {
            public IReadOnlyList<string> GetLockingProcesses(string path) => [];
        }
    }
}
