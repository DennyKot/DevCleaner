using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DevCleaner.Core.Abstractions;
using DevCleaner.Core.Git;

namespace DevCleaner.Core.Engine;

public sealed class ScanEngine(IReadOnlyList<ICleanupProvider> providers, GitInspector? git = null)
{
    public IReadOnlyList<ICleanupProvider> Providers { get; } = providers;

    /// <summary>Tags project findings with their Git repo and raises risk when that repo has uncommitted changes.</summary>
    public static Finding AnnotateGit(Finding f, GitInspector git)
    {
        if (!f.IsFileSystem || f.ProjectRoot is null || git.FindRepoRoot(f.ProjectRoot) is not { } repo)
            return f;
        var status = git.GetStatus(repo);
        if (status is null)
            return f with { GitRepoRoot = repo };
        if (!status.IsDirty)
            return f with { GitRepoRoot = repo, GitUncommittedChanges = 0 };

        var n = status.UncommittedChanges;
        var warning = $"Repo has {n} uncommitted change{(n == 1 ? "" : "s")}";
        return f with
        {
            GitRepoRoot = repo,
            GitUncommittedChanges = n,
            Risk = RiskLevel.Caution,
            Note = f.Note is { Length: > 0 } note ? $"{warning} · {note}" : warning,
        };
    }

    /// <summary>Runs all applicable providers in parallel and streams de-duplicated findings.</summary>
    public async IAsyncEnumerable<Finding> ScanAsync(ScanContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<Finding>(new UnboundedChannelOptions { SingleReader = true });
        var seen = new ConcurrentDictionary<string, byte>();
        var selected = Providers
            .Where(p => !context.Options.DisabledProviders.Contains(p.Id))
            .Where(p => context.Options.Categories is null || p.Categories.Any(context.Options.Categories.Contains))
            .ToList();

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    selected,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Options.MaxParallelism), CancellationToken = ct },
                    (provider, token) =>
                    {
                        try
                        {
                            foreach (var f in provider.Scan(context, token))
                            {
                                if (!context.Includes(f.Category))
                                    continue;
                                if (f.IsFileSystem && context.Exclusions.IsMatch(f.Path))
                                    continue;
                                if (seen.TryAdd(f.Id, 0))
                                    channel.Writer.TryWrite(git is null ? f : AnnotateGit(f, git));
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            context.Errors.Enqueue($"{provider.Name}: {ex.Message}");
                        }

                        return ValueTask.CompletedTask;
                    });
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        await foreach (var f in channel.Reader.ReadAllAsync(ct))
            yield return f;
        await producer;
    }

    public async Task<List<Finding>> ScanAllAsync(ScanContext context, IProgress<Finding>? progress = null, CancellationToken ct = default)
    {
        var list = new List<Finding>();
        await foreach (var f in ScanAsync(context, ct))
        {
            list.Add(f);
            progress?.Report(f);
        }

        return list;
    }
}
