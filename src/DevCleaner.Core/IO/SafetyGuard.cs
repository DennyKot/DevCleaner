namespace DevCleaner.Core.IO;

/// <summary>Last line of defence: refuses to delete roots, well-known user/system folders or their ancestors.</summary>
public static class SafetyGuard
{
    private static readonly Lazy<string[]> Protected = new(BuildProtected);

    public static bool IsSafeToDelete(string path, out string? reason)
    {
        reason = null;
        string full;
        try
        {
            full = PathMatcher.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "Invalid path";
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || full.Length <= root.Length + 1)
        {
            reason = "Refusing to delete a drive/file-system root";
            return false;
        }

        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            reason = "Refusing to delete a top-level folder";
            return false;
        }

        foreach (var p in Protected.Value)
        {
            if (full.Equals(p, OsInfo.PathComparison) ||
                p.StartsWith(full + Path.DirectorySeparatorChar, OsInfo.PathComparison))
            {
                reason = $"Protected location: {p}";
                return false;
            }
        }

        return true;
    }

    private static string[] BuildProtected()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
        };
        var list = folders.Select(Environment.GetFolderPath).ToList();
        list.Add(Path.GetTempPath());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        list.Add(Path.Combine(home, ".ssh"));
        list.Add(Path.Combine(home, ".gnupg"));
        if (!OperatingSystem.IsWindows())
            list.AddRange(["/usr", "/etc", "/bin", "/sbin", "/var", "/home", "/opt", "/Library", "/System", "/Users"]);
        return list.Where(p => !string.IsNullOrEmpty(p)).Select(PathMatcher.Normalize).Distinct(OsInfo.PathComparer).ToArray();
    }
}
