using Avalonia.Media;
using DevCleaner.Core;

namespace DevCleaner.Gui.ViewModels;

public static class CategoryInfo
{
    private static readonly Dictionary<string, Geometry> GeometryCache = [];

    public static string Name(Category c) => c switch
    {
        Category.SystemTemp => "System & temp files",
        Category.Browsers => "Browser caches",
        Category.Ide => "IDE caches",
        Category.DotNetProjects => ".NET projects",
        Category.NodeProjects => "Node.js projects",
        Category.OtherProjects => "Other projects",
        Category.PackageCaches => "Package caches",
        Category.Docker => "Docker",
        Category.Registry => "Registry",
        Category.Logs => "Logs & crash dumps",
        Category.Custom => "Custom",
        Category.Duplicates => "Duplicates",
        Category.GitIgnored => "Git-ignored output",
        Category.Images => "Image compression",
        _ => c.ToString(),
    };

    public static Color Color(Category c) => Avalonia.Media.Color.Parse(c switch
    {
        Category.SystemTemp => "#0EA5E9",
        Category.Browsers => "#F59E0B",
        Category.Ide => "#8B5CF6",
        Category.DotNetProjects => "#6366F1",
        Category.NodeProjects => "#22C55E",
        Category.OtherProjects => "#14B8A6",
        Category.PackageCaches => "#EC4899",
        Category.Docker => "#3B82F6",
        Category.Registry => "#64748B",
        Category.Logs => "#EF4444",
        Category.GitIgnored => "#F97316",
        Category.Images => "#06B6D4",
        _ => "#6B7280",
    });

    public static Geometry Icon(Category c) => Geometry(c switch
    {
        Category.SystemTemp => Icons.Trash,
        Category.Browsers => Icons.Globe,
        Category.Ide or Category.DotNetProjects or Category.NodeProjects or Category.OtherProjects or Category.GitIgnored => Icons.Code,
        Category.PackageCaches or Category.Docker => Icons.Cube,
        Category.Registry => Icons.Database,
        Category.Logs => Icons.File,
        Category.Duplicates => Icons.Copy,
        Category.Images => Icons.Image,
        _ => Icons.Search,
    });

    public static Geometry Geometry(string data)
    {
        if (!GeometryCache.TryGetValue(data, out var g))
            GeometryCache[data] = g = StreamGeometry.Parse(data);
        return g;
    }
}
