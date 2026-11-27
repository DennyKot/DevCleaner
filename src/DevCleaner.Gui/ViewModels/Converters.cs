using Avalonia.Data.Converters;
using Avalonia.Media;
using DevCleaner.Core;

namespace DevCleaner.Gui.ViewModels;

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
