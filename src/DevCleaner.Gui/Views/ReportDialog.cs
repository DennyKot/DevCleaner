using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DevCleaner.Core;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Reports;
using DevCleaner.Gui.ViewModels;

namespace DevCleaner.Gui.Views;

internal static class ReportDialog
{
    public static Task ShowAsync(Window owner, CleanReport report)
    {
        var dialog = new Window
        {
            Title = report.DryRun ? "Dry run result" : "Cleanup complete",
            Width = 620,
            MaxHeight = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Icon = owner.Icon,
        };

        var done = report.Items.Count(i => i.Status is CleanStatus.Cleaned or CleanStatus.DryRun);
        var partial = report.Items.Count(i => i.Status == CleanStatus.Partial);
        var skipped = report.Items.Count(i => i.Status == CleanStatus.Skipped);
        var failed = report.Items.Count(i => i.Status == CleanStatus.Failed);

        var root = new StackPanel { Margin = new Thickness(28, 24), Spacing = 18 };
        root.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = report.DryRun ? "WOULD FREE" : "FREED", Classes = { "label" } },
                new TextBlock
                {
                    Text = Bytes.Format(report.TotalBytesFreed), Classes = { "metric" },
                    Foreground = new SolidColorBrush(Color.Parse(report.DryRun ? "#5046E5" : "#16A34A")),
                },
                new TextBlock
                {
                    Classes = { "sub" },
                    Text = $"{done} {(report.DryRun ? "would be cleaned" : "cleaned")}"
                        + (partial > 0 ? $"  ·  {partial} partially" : "")
                        + (skipped > 0 ? $"  ·  {skipped} skipped" : "")
                        + (failed > 0 ? $"  ·  {failed} failed" : ""),
                },
            },
        });

        var byCategory = new StackPanel { Spacing = 6 };
        foreach (var g in report.Items.GroupBy(i => i.Finding.Category).OrderByDescending(g => g.Sum(i => i.BytesFreed)))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(CategoryInfo.Color(g.Key)),
            });
            var name = new TextBlock { Text = CategoryInfo.Name(g.Key) };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var size = new TextBlock { Text = Bytes.Format(g.Sum(i => i.BytesFreed)), FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            byCategory.Children.Add(row);
        }

        root.Children.Add(Card("BY CATEGORY", byCategory));

        var issues = report.Items.Where(i => i.Status is CleanStatus.Partial or CleanStatus.Skipped or CleanStatus.Failed).ToList();
        if (issues.Count > 0)
        {
            var list = new StackPanel { Spacing = 8 };
            foreach (var i in issues.Take(100))
            {
                list.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{i.Status}: {i.Finding.Name}", FontWeight = FontWeight.Medium },
                        new TextBlock { Text = i.Finding.Path, Classes = { "sub" }, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = i.Message ?? "", Classes = { "sub" }, Foreground = new SolidColorBrush(Color.Parse("#B45309")), TextWrapping = TextWrapping.Wrap },
                    },
                });
            }

            root.Children.Add(Card("NEEDS ATTENTION", new ScrollViewer { MaxHeight = 220, Content = list }));
        }

        var export = new Button { Content = "Export…", Classes = { "secondary" } };
        AutomationProperties.SetName(export, "Export report");
        export.Click += async (_, _) =>
        {
            if (await PickSavePathAsync(dialog, report.DryRun ? "devcleaner-dry-run" : "devcleaner-clean") is not { } path)
                return;
            try
            {
                ReportExporter.SaveCleanReport(path, report);
                export.Content = "Exported ✓";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                export.Content = "Export failed";
                ToolTip.SetTip(export, ex.Message);
            }
        };
        var close = new Button { Content = "Done", Classes = { "primary" }, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { export, close },
        });

        dialog.Content = root;
        return dialog.ShowDialog(owner);
    }

    public static async Task<string?> PickSavePathAsync(TopLevel owner, string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export report",
            SuggestedFileName = $"{suggestedName}-{DateTime.Now:yyyyMMdd-HHmm}",
            DefaultExtension = "html",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("HTML report") { Patterns = ["*.html"] },
                new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                new FilePickerFileType("JSON") { Patterns = ["*.json"] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    private static Border Card(string title, Control content) => new()
    {
        Classes = { "card" },
        Padding = new Thickness(16, 12),
        Child = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = title, Classes = { "label" } }, content } },
    };
}
