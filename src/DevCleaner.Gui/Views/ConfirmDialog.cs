using Avalonia.Controls;
using Avalonia.Layout;

namespace DevCleaner.Gui.Views;

internal static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string message)
    {
        var dialog = new Window
        {
            Title = "Confirm",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var ok = new Button { Content = "Continue", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };
        return dialog.ShowDialog<bool>(owner);
    }
}
