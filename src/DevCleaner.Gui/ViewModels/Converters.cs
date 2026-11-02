using Avalonia.Data.Converters;
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
        _ => Icons.Search,
    });

    public static Geometry Geometry(string data)
    {
        if (!GeometryCache.TryGetValue(data, out var g))
            GeometryCache[data] = g = StreamGeometry.Parse(data);
        return g;
    }
}

public static class Icons
{
    public const string Trash = "M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z";
    public const string Globe = "M17.9,17.39C17.64,16.59 16.89,16 16,16H15V13A1,1 0 0,0 14,12H8V10H10A1,1 0 0,0 11,9V7H13A2,2 0 0,0 15,5V4.59C17.93,5.77 20,8.64 20,12C20,14.08 19.2,15.97 17.9,17.39M11,19.93C7.05,19.44 4,16.08 4,12C4,11.38 4.08,10.78 4.21,10.21L9,15V16A2,2 0 0,0 11,18M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z";
    public const string Code = "M14.6,16.6L19.2,12L14.6,7.4L16,6L22,12L16,18L14.6,16.6M9.4,16.6L4.8,12L9.4,7.4L8,6L2,12L8,18L9.4,16.6Z";
    public const string Cube = "M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.790,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5Z";
    public const string Database = "M12,3C7.58,3 4,4.79 4,7C4,9.21 7.58,11 12,11C16.42,11 20,9.21 20,7C20,4.79 16.42,3 12,3M4,9V12C4,14.21 7.58,16 12,16C16.42,16 20,14.21 20,12V9C20,11.21 16.42,13 12,13C7.58,13 4,11.21 4,9M4,14V17C4,19.21 7.58,21 12,21C16.42,21 20,19.21 20,17V14C20,16.21 16.42,18 12,18C7.58,18 4,16.21 4,14Z";
    public const string File = "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20Z";
    public const string Copy = "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z";
    public const string Search = "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z";
}

public static class Converters
{
    public static readonly IValueConverter Bytes =
        new FuncValueConverter<long, string>(b => Core.Bytes.Format(b));

    public static readonly IValueConverter StaleBrush =
        new FuncValueConverter<bool, IBrush>(stale => new SolidColorBrush(Color.Parse(stale ? "#D97706" : "#6B7280")));

    public static readonly IValueConverter CategoryBrush =
        new FuncValueConverter<Category?, IBrush>(c => new SolidColorBrush(c is { } v ? CategoryInfo.Color(v) : Colors.Gray));

    public static readonly IValueConverter CategoryTint =
        new FuncValueConverter<Category?, IBrush>(c => new SolidColorBrush(c is { } v ? CategoryInfo.Color(v) : Colors.Gray, 0.14));

    public static readonly IValueConverter CategoryIcon =
        new FuncValueConverter<Category?, Geometry?>(c => c is { } v ? CategoryInfo.Icon(v) : null);

    public static readonly IValueConverter RiskBrush =
        new FuncValueConverter<string?, IBrush>(r => new SolidColorBrush(RiskColor(r)));

    public static readonly IValueConverter RiskTint =
        new FuncValueConverter<string?, IBrush>(r => new SolidColorBrush(RiskColor(r), 0.14));

    private static Color RiskColor(string? r) => Color.Parse(r switch
    {
        nameof(RiskLevel.Safe) => "#16A34A",
        nameof(RiskLevel.Review) => "#D97706",
        nameof(RiskLevel.Caution) => "#DC2626",
        _ => "#6B7280",
    });
}
