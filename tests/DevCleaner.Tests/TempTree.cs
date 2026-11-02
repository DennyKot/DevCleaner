namespace DevCleaner.Tests;

/// <summary>Disposable temp folder tree for file-system tests.</summary>
public sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "devcleaner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, int size = 100, DateTime? modifiedUtc = null)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', size).ToArray());
        if (modifiedUtc is { } m)
            System.IO.File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    public string File(string relative, byte[] content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public string P(string relative) => Path.Combine(Root, relative);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // git marks object files read-only.
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
