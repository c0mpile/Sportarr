using System.Diagnostics;

namespace Sportarr.Api.Models;

/// <summary>
/// UFC Fight Pass integration settings.
/// Persisted to config.xml via ConfigService — never to the SQLite database.
/// Credentials are stored plain-text (same pattern as IPTV source credentials)
/// because config.xml is admin-only and is not committed to version control.
/// </summary>
public class UfcFightPassSettings
{
    /// <summary>
    /// Master switch. When false the UFC endpoints still respond but
    /// StartDownloadAsync refuses to launch yt-dlp.
    /// </summary>
    public bool UfcEnabled { get; set; } = false;

    /// <summary>UFC Fight Pass account email address.</summary>
    public string UfcEmail { get; set; } = "";

    /// <summary>
    /// UFC Fight Pass account password (plain-text, admin-only).
    /// Passed to yt-dlp via --password; never echoed back to the browser.
    /// </summary>
    public string UfcPassword { get; set; } = "";

    /// <summary>
    /// yt-dlp format selector string.
    /// Default: best available quality with audio merged.
    /// UI maps human-readable labels to these strings:
    ///   Best    → "bestvideo+bestaudio/best"
    ///   1080p   → "bestvideo[height<=1080]+bestaudio/best[height<=1080]"
    ///   720p    → "bestvideo[height<=720]+bestaudio/best[height<=720]"
    ///   Smallest→ "worstvideo+worstaudio/worst"
    /// </summary>
    public string UfcQualityFormat { get; set; } = "bestvideo+bestaudio/best";

    /// <summary>
    /// Number of fragments to download in parallel (--concurrent-fragments).
    /// Default 4 per the UFC integration rules.
    /// </summary>
    public int UfcConcurrentFragments { get; set; } = 4;

    /// <summary>
    /// Root output directory for UFC downloads.
    /// Empty = "/data/media/sports/UFC" (Docker volume default).
    /// Final path: {UfcOutputPath}/{Year}/{EventTitle}/{EventTitle}.mp4
    /// </summary>
    public string UfcOutputPath { get; set; } = "";

    /// <summary>
    /// Path to the yt-dlp binary.
    /// Empty = auto-discover: tries /usr/local/bin/yt-dlp first, then PATH.
    /// </summary>
    public string UfcYtDlpPath { get; set; } = "";

    /// <summary>
    /// Path to the Netscape cookie file used by yt-dlp for authenticated sessions.
    /// Empty = auto-managed at {DataPath}/ufc-cookies.txt.
    /// Regenerated on every successful TestAuthAsync() call.
    /// </summary>
    public string UfcCookiePath { get; set; } = "";
}

/// <summary>
/// In-memory record for an active yt-dlp UFC download job.
/// Keyed by DownloadId in UfcFightPassService._activeJobs.
/// </summary>
public record UfcDownloadJob(
    string DownloadId,
    string EventTitle,
    string FightPassUrl,
    string OutputPath,
    Process Process,
    DateTime StartedAt
);
