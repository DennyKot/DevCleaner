using DevCleaner.Core;
using DevCleaner.Core.Abstractions;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Git;
using DevCleaner.Core.Providers;

namespace DevCleaner.Tests;

public sealed class GitTests : IDisposable
{
    private readonly TempTree _t = new();
    private readonly GitInspector _git = new();

    public void Dispose() => _t.Dispose();

    private void Git(string repo, params string[] args)
    {
        string[] all = ["-c", "user.email=t@t", "-c", "user.name=t", "-c", "commit.gpgsign=false", "-C", repo, .. args];
        var r = ProcessRunner.Run("git", all, TimeSpan.FromSeconds(30));
        Assert.True(r is { ExitCode: 0 }, r?.StdErr);
    }

    private string Repo(string name)
    {
        var repo = _t.P(name);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        _t.File($@"{name}\.gitignore", System.Text.Encoding.UTF8.GetBytes("target/\n_build/\n.env\n.idea/\n*.log\nsecrets/\n"));
        _t.File($@"{name}\src\main.rs", 50);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "init");
        return repo;
    }

    private static ScanContext Ctx(string root) =>
        new(new ScanOptions { ProjectRoots = [root] }, new PathMatcher([]));

    [Fact]
    public void Provider_ReportsIgnoredFoldersOnly_AndSkipsProtected()
    {
        if (!_git.IsAvailable)
            return;
        var repo = Repo("rust");
        _t.File(@"rust\target\debug\app.exe", 1000);
        _t.File(@"rust\lib\_build\out.o", 300);
        _t.File(@"rust\.idea\workspace.xml", 100);
        _t.File(@"rust\secrets\key.pem", 100);
        _t.File(@"rust\.env", 100);
        _t.File(@"rust\run.log", 100);

        var findings = new GitIgnoredProvider(_git).Scan(Ctx(_t.Root), default).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Path == Path.Combine(repo, "target") && f.SizeBytes == 1000 && f.Category == Category.GitIgnored && f.ProjectRoot == repo);
        Assert.Contains(findings, f => f.Path == Path.Combine(repo, "lib", "_build"));
    }

    [Fact]
    public void Provider_DoesNotReportFoldersContainingTrackedFiles()
    {
        if (!_git.IsAvailable)
            return;
        var repo = Repo("r");
        _t.File(@"r\target\keep.txt", 10);
        Git(repo, "add", "-f", "target/keep.txt");
        Git(repo, "commit", "-q", "-m", "force");
        _t.File(@"r\target\junk.bin", 500);

        var findings = new GitIgnoredProvider(_git).Scan(Ctx(_t.Root), default).ToList();

        Assert.DoesNotContain(findings, f => f.Path == Path.Combine(repo, "target"));
    }

    [Fact]
    public void Annotate_WarnsAboutDirtyRepo()
    {
        if (!_git.IsAvailable)
            return;
        var clean = Repo("clean");
        var dirty = Repo("dirty");
        _t.File(@"dirty\src\main.rs", 60);
        _t.File(@"dirty\new.txt", 5);

        Finding Make(string root) => new()
        {
            ProviderId = "x", Category = Category.OtherProjects, Name = "n", Path = Path.Combine(root, "target"),
            Kind = FindingKind.Directory, ProjectRoot = Path.Combine(root, "src"), Risk = RiskLevel.Safe, Note = "hint",
        };

        var c = ScanEngine.AnnotateGit(Make(clean), _git);
        var d = ScanEngine.AnnotateGit(Make(dirty), _git);

        Assert.Equal(clean, c.GitRepoRoot);
        Assert.Equal(0, c.GitUncommittedChanges);
        Assert.Equal(RiskLevel.Safe, c.Risk);
        Assert.Equal(2, d.GitUncommittedChanges);
        Assert.True(d.InDirtyRepo);
        Assert.Equal(RiskLevel.Caution, d.Risk);
        Assert.Equal("Repo has 2 uncommitted changes · hint", d.Note);
    }

    [Fact]
    public void Annotate_LeavesNonRepoFindingsAlone()
    {
        var f = new Finding { ProviderId = "x", Category = Category.SystemTemp, Name = "n", Path = _t.P("a"), Kind = FindingKind.Directory, ProjectRoot = _t.Root };
        Assert.Same(f, ScanEngine.AnnotateGit(f, _git));
    }

    [Fact]
    public void Parsers_HandleGitOutput()
    {
        var dirs = GitInspector.ParseIgnoredDirectories(_t.Root, "bin/\0a.log\0sub/out/\0../evil/\0");
        Assert.Equal([_t.P("bin"), _t.P(@"sub\out")], dirs);
        Assert.Equal(3, GitInspector.CountStatusEntries(" M a.txt\0R  new.txt\0old.txt\0?? x\0"));
        Assert.Equal(0, GitInspector.CountStatusEntries(""));
    }
}
