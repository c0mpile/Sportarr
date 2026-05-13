using Sportarr.Api.Models;

namespace Sportarr.Api.Services.Interfaces;

/// <summary>
/// Interface for the UFC Fight Pass archiving service.
/// Manages yt-dlp process lifecycle, authentication cookie refresh,
/// and DownloadQueueItem persistence for UFC VOD downloads.
/// </summary>
public interface IUfcFightPassService
{
    /// <summary>
    /// Locate the yt-dlp binary and return its version string.
    /// Search order: config UfcYtDlpPath → /usr/local/bin/yt-dlp → PATH.
    /// </summary>
    Task<(bool Found, string Path, string Version)> DiscoverYtDlpAsync();

    /// <summary>
    /// Validate credentials by running yt-dlp in --simulate mode against a
    /// known-good UFC Fight Pass URL. On success the cookie file is refreshed.
    /// </summary>
    Task<(bool Success, string Message)> TestAuthAsync();

    /// <summary>
    /// Enqueue a UFC Fight Pass VOD URL for download.
    /// If customTitle is null the service fetches the title from yt-dlp first.
    /// Returns the DownloadId (Guid string) assigned to the queue item.
    /// Throws InvalidOperationException if UfcEnabled = false or credentials missing.
    /// </summary>
    Task<string> StartDownloadAsync(string fightPassUrl, string? customTitle = null);

    /// <summary>
    /// Cancel an active download by DownloadId.
    /// Sends SIGTERM, waits 5 s, then SIGKILL if still running.
    /// Returns false if the downloadId is not found.
    /// </summary>
    Task<bool> CancelDownloadAsync(string downloadId);

    /// <summary>
    /// Return a snapshot of all currently active yt-dlp jobs.
    /// </summary>
    IReadOnlyList<UfcDownloadJob> GetActiveJobs();

    /// <summary>
    /// Return the UfcDownloadJob for a specific downloadId, or null if not found.
    /// </summary>
    UfcDownloadJob? GetJob(string downloadId);
}
