using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace DevCleaner.Gui;

/// <summary>
/// Checks GitHub Releases on startup, downloads any newer version in the background and
/// applies it silently once the app exits, so the next launch runs the new version.
/// </summary>
internal static class AutoUpdater
{
    private const string RepoUrl = "https://github.com/DennyKot/DevCleaner";

    public static void StartInBackground()
    {
        if (Environment.GetEnvironmentVariable("DEVCLEANER_NO_UPDATE") == "1")
            return;
        _ = Task.Run(CheckAndStageAsync);
    }

    private static async Task CheckAndStageAsync()
    {
        try
        {
            // Local folder or URL feed, used to test updates without publishing a GitHub release.
            var feed = Environment.GetEnvironmentVariable("DEVCLEANER_UPDATE_URL");
            var manager = string.IsNullOrWhiteSpace(feed)
                ? new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false))
                : new UpdateManager(feed);
            // Dev builds and portable copies aren't Velopack installs; nothing to update.
            if (!manager.IsInstalled)
                return;

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return;

            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, etc. Updating must never break the app.
            Trace.WriteLine($"Auto-update failed: {ex}");
        }
    }
}
