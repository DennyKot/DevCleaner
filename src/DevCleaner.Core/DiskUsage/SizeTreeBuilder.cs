using DevCleaner.Core.IO;

namespace DevCleaner.Core.DiskUsage;

public sealed class SizeNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; set; }
    public List<SizeNode> Children { get; } = [];
}

public static class SizeTreeBuilder
{
    private const int MaxChildren = 200;

    /// <summary>Builds a size tree; nodes deeper than <paramref name="depth"/> are measured but not kept.</summary>
    public static SizeNode Build(string root, int depth, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(root);
        var node = new SizeNode { Name = full, Path = full, IsDirectory = true };
        var subdirs = new List<DirectoryInfo>();
        foreach (var e in FsUtil.SafeEntries(full))
        {
            if (e is FileInfo f)
            {
                node.Size += f.Length;
                node.Children.Add(new SizeNode { Name = f.Name, Path = f.FullName, Size = f.Length });
            }
            else if (e is DirectoryInfo d && !FsUtil.IsReparsePoint(d))
            {
                subdirs.Add(d);
            }
        }

        var childNodes = new SizeNode[subdirs.Count];
        Parallel.For(0, subdirs.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            i => childNodes[i] = BuildRec(subdirs[i], depth - 1, ct));
        foreach (var c in childNodes)
        {
            node.Size += c.Size;
            node.Children.Add(c);
        }

        Trim(node);
        return node;
    }

    private static SizeNode BuildRec(DirectoryInfo dir, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var node = new SizeNode { Name = dir.Name, Path = dir.FullName, IsDirectory = true };
        foreach (var e in FsUtil.SafeEntries(dir.FullName))
        {
            if (e is FileInfo f)
            {
                node.Size += f.Length;
                if (depth > 0)
                    node.Children.Add(new SizeNode { Name = f.Name, Path = f.FullName, Size = f.Length });
            }
            else if (e is DirectoryInfo d && !FsUtil.IsReparsePoint(d))
            {
                var child = BuildRec(d, depth - 1, ct);
                node.Size += child.Size;
                if (depth > 0)
                    node.Children.Add(child);
            }
        }

        Trim(node);
        return node;
    }

    private static void Trim(SizeNode node)
    {
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (node.Children.Count <= MaxChildren)
            return;
        var rest = node.Children.Skip(MaxChildren).Sum(c => c.Size);
        node.Children.RemoveRange(MaxChildren, node.Children.Count - MaxChildren);
        node.Children.Add(new SizeNode { Name = "(other)", Path = node.Path, Size = rest });
    }
}
