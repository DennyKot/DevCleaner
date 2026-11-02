using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DevCleaner.Gui.ViewModels;

namespace DevCleaner.Gui.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Treemap.NodeActivated += (_, node) => Vm?.DiskUsage.DrillInto(node);
        FitToScreen();
    }

    private void FitToScreen()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        // Working area is in physical pixels; window size is in DIPs.
        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        Width = Math.Clamp(Math.Min(1320, area.Width * 0.85), Math.Min(MinWidth, area.Width), area.Width);
        Height = Math.Clamp(Math.Min(860, area.Height * 0.85), Math.Min(MinHeight, area.Height), area.Height);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            vm.Confirm = message => ConfirmDialog.ShowAsync(this, message);
            vm.ShowReport = report => ReportDialog.ShowAsync(this, report);
            vm.PickExportPath = name => ReportDialog.PickSavePathAsync(this, name);
        }
    }

    private static ResultNode? NodeOf(object? sender) => (sender as Control)?.DataContext as ResultNode;

    private void OnExclude(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { Path: { } path } node && node.Category is null)
            Vm?.Exclude(path);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender)?.Path is not { } path)
            return;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender)?.Path is { } path && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(path);
    }

    private void OnExcludeProject(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ProjectRow row)
            Vm?.Exclude(row.Root);
    }

    private async void OnBrowseSearch(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolderAsync() is { } path)
            vm.Search.Roots = Append(vm.Search.Roots, path, "; ");
    }

    private async void OnBrowseDuplicates(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolderAsync() is { } path)
            vm.Duplicates.Roots = Append(vm.Duplicates.Roots, path, "; ");
    }

    private async void OnBrowseDiskUsage(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolderAsync() is { } path)
            vm.DiskUsage.Path = path;
    }

    private async void OnBrowseProjectRoot(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolderAsync() is { } path)
        {
            vm.Settings.ProjectRoots = Append(vm.Settings.ProjectRoots, path, Environment.NewLine);
            vm.Settings.SaveCommand.Execute(null);
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder" });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static string Append(string existing, string path, string separator) =>
        string.IsNullOrWhiteSpace(existing) ? path : existing.TrimEnd() + separator + path;
}
