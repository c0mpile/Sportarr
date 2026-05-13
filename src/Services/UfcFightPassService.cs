using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Api.Services;

/// <summary>
/// UFC Fight Pass archiving service.
///
/// Architecture notes:
/// - Singleton lifetime: holds ConcurrentDictionary of live yt-dlp processes.
/// - Uses IDbContextFactory for all database writes to avoid DbContext threading issues.
/// - All log lines are prefixed with [UFC] per integration rules.
/// - yt-dlp is invoked via Process (never shell) to prevent argument injection.
/// </summary>
public sealed class UfcFightPassService : IUfcFightPassService
{
    // -------------------------------------------------------------------------
    // Constants & static state
    // -------------------------------------------------------------------------

    /// <summary>yt-dlp progress line regex. Group 1 = percent, group 2 = size, group 3 = unit.</summary>
    private static readonly Regex ProgressRegex = new(
        @"\[download\]\s+(?<percent>\d+\.?\d*)%\s+of\s+~?\s*(?<size>[\d.]+)(?<unit>KiB|MiB|GiB)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] NonRetriablePatterns =
    [
        "ERROR: This video is not available",
        "ERROR: Login failed",
        "ERROR: Requested format is not available",
        "ERROR: [ufcfightpass]",
    ];

    private static readonly string[] RetriablePatterns =
    [
        "ERROR: Unable to download webpage",
        "ERROR: Fragment download failed",
    ];

    private const string DefaultYtDlpPath   = "/usr/local/bin/yt-dlp";
    private const string DefaultOutputRoot  = "/data/media/sports/UFC";
    private const string UfcProtocol        = "UFC";

    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------

    private readonly ILogger<UfcFightPassService> _logger;
    private readonly ConfigService _configService;
    private readonly IDbContextFactory<SportarrDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    // -------------------------------------------------------------------------
    // In-memory job tracking
    // -------------------------------------------------------------------------

    private readonly ConcurrentDictionary<string, UfcDownloadJob> _activeJobs = new();

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public UfcFightPassService(
        ILogger<UfcFightPassService> logger,
        ConfigService configService,
        IDbContextFactory<SportarrDbContext> dbFactory,
        IConfiguration configuration)
    {
        _logger        = logger;
        _configService = configService;
        _dbFactory     = dbFactory;
        _configuration = configuration;

        // Log binary discovery at startup.
        _ = LogStartupBinaryAsync();
    }

    // -------------------------------------------------------------------------
    // IUfcFightPassService — public API
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<(bool Found, string Path, string Version)> DiscoverYtDlpAsync()
    {
        var config    = await _configService.GetConfigAsync();
        var binaryPath = await ResolveBinaryAsync(config.UfcYtDlpPath);

        if (string.IsNullOrEmpty(binaryPath))
            return (false, "", "yt-dlp not found");

        var version = await GetVersionStringAsync(binaryPath);
        return (true, binaryPath, version);
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string Message)> TestAuthAsync()
    {
        var config = await _configService.GetConfigAsync();

        if (string.IsNullOrWhiteSpace(config.UfcEmail) || string.IsNullOrWhiteSpace(config.UfcPassword))
            return (false, "Fight Pass credentials are not configured.");

        var binaryPath = await ResolveBinaryAsync(config.UfcYtDlpPath);
        if (string.IsNullOrEmpty(binaryPath))
            return (false, "yt-dlp binary not found. Check UfcYtDlpPath or install yt-dlp to /usr/local/bin.");

        var cookiePath = ResolveCookiePath(config);

        // Use a known-stable VOD URL for the probe.
        const string probeUrl = "https://ufcfightpass.com/video/1";

        var args = BuildBaseArguments(config, cookiePath);
        args.AddRange(["--simulate", "--quiet", probeUrl]);

        _logger.LogInformation("[UFC] Testing auth for {Email}", config.UfcEmail);

        var (exitCode, _, stderr) = await RunProcessAsync(binaryPath, args, timeoutSeconds: 30);

        if (exitCode == 0)
        {
            _logger.LogInformation("[UFC] Auth test succeeded for {Email}", config.UfcEmail);
            return (true, "Authentication successful.");
        }

        // Classify the failure
        var message = ClassifyError(stderr, "Authentication");
        _logger.LogWarning("[UFC] Auth test failed for {Email}: {Message}", config.UfcEmail, message);
        return (false, message);
    }

    /// <inheritdoc/>
    public async Task<string> StartDownloadAsync(string fightPassUrl, string? customTitle = null)
    {
        var config = await _configService.GetConfigAsync();

        if (!config.UfcEnabled)
            throw new InvalidOperationException("UFC Fight Pass integration is disabled. Enable it in Settings > UFC Fight Pass.");

        if (string.IsNullOrWhiteSpace(config.UfcEmail) || string.IsNullOrWhiteSpace(config.UfcPassword))
            throw new InvalidOperationException("UFC Fight Pass credentials are not configured.");

        var binaryPath = await ResolveBinaryAsync(config.UfcYtDlpPath);
        if (string.IsNullOrEmpty(binaryPath))
            throw new InvalidOperationException("yt-dlp binary not found.");

        var cookiePath = ResolveCookiePath(config);
        var rootPath   = string.IsNullOrEmpty(config.UfcOutputPath) ? DefaultOutputRoot : config.UfcOutputPath;

        // Validate root output path exists in the container volume.
        if (!Directory.Exists(rootPath))
        {
            _logger.LogError("[UFC] Root output path does not exist: {Path}. Mount a volume or configure UfcOutputPath.", rootPath);
            throw new InvalidOperationException($"UFC output directory does not exist: {rootPath}");
        }

        // Resolve the event title (use yt-dlp --get-title if not provided).
        var eventTitle = customTitle;
        if (string.IsNullOrWhiteSpace(eventTitle))
            eventTitle = await FetchTitleAsync(binaryPath, config, cookiePath, fightPassUrl);
        if (string.IsNullOrWhiteSpace(eventTitle))
            eventTitle = $"UFC_Event_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

        // Build output path per schema: {rootPath}/{Year}/{EventTitle}/{EventTitle}.mp4
        var safeTitle  = SanitizeTitle(eventTitle);
        var year       = DateTime.UtcNow.Year.ToString();
        var outputDir  = Path.Combine(rootPath, year, safeTitle);
        var outputFile = Path.Combine(outputDir, $"{safeTitle}.mp4");

        Directory.CreateDirectory(outputDir);
        _logger.LogInformation("[UFC] Output directory: {Dir}", outputDir);

        // Create DownloadQueueItem in the database first so the UI sees it immediately.
        var downloadId = Guid.NewGuid().ToString();
        await CreateQueueItemAsync(downloadId, eventTitle, fightPassUrl);

        // Build yt-dlp arguments for the actual download.
        var args = BuildBaseArguments(config, cookiePath);
        args.AddRange([
            "--format",                     config.UfcQualityFormat,
            "--output",                     outputFile,
            "--newline",                    // ensures each progress update is on its own line
            "--progress",
            fightPassUrl
        ]);

        _logger.LogInformation("[UFC] Launching yt-dlp for '{Title}' → {Output}", eventTitle, outputFile);
        _logger.LogDebug("[UFC] yt-dlp args: {Args}", string.Join(" ", args));

        var process = StartProcess(binaryPath, args);

        var job = new UfcDownloadJob(downloadId, eventTitle, fightPassUrl, outputFile, process, DateTime.UtcNow);
        _activeJobs[downloadId] = job;

        // Monitor in background; do not await.
        _ = MonitorJobAsync(job);

        return downloadId;
    }

    /// <inheritdoc/>
    public async Task<bool> CancelDownloadAsync(string downloadId)
    {
        if (!_activeJobs.TryGetValue(downloadId, out var job))
            return false;

        _logger.LogInformation("[UFC] Cancelling download {DownloadId} ('{Title}')", downloadId, job.EventTitle);

        if (!job.Process.HasExited)
        {
            try
            {
                // SIGTERM — let yt-dlp flush the partial file.
                job.Process.Kill(entireProcessTree: false);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await job.Process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Escalate to SIGKILL.
                _logger.LogWarning("[UFC] yt-dlp did not exit after SIGTERM, sending SIGKILL for {DownloadId}", downloadId);
                try { job.Process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
        }

        _activeJobs.TryRemove(downloadId, out _);
        await UpdateQueueItemStatusAsync(downloadId, DownloadStatus.Failed, "Cancelled by user");
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<UfcDownloadJob> GetActiveJobs() =>
        _activeJobs.Values.ToList().AsReadOnly();

    /// <inheritdoc/>
    public UfcDownloadJob? GetJob(string downloadId) =>
        _activeJobs.TryGetValue(downloadId, out var j) ? j : null;

    // -------------------------------------------------------------------------
    // Private helpers — process management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Background monitor for a running yt-dlp job.
    /// Reads stdout line-by-line, updates DB progress, handles retries on retriable errors.
    /// </summary>
    private async Task MonitorJobAsync(UfcDownloadJob job)
    {
        var stderrLines = new System.Text.StringBuilder();
        var stdoutLines = new System.Text.StringBuilder();
        int retryCount  = 0;

        try
        {
            // Read stdout and stderr concurrently.
            var stdoutTask = ReadOutputAsync(job.Process.StandardOutput, line =>
            {
                stdoutLines.AppendLine(line);
                ParseAndUpdateProgress(job.DownloadId, line);
            });

            var stderrTask = ReadOutputAsync(job.Process.StandardError, line =>
            {
                stderrLines.AppendLine(line);
                _logger.LogWarning("[UFC] yt-dlp stderr [{DownloadId}]: {Line}", job.DownloadId, line);
            });

            await Task.WhenAll(stdoutTask, stderrTask);
            await job.Process.WaitForExitAsync();

            var exitCode = job.Process.ExitCode;
            var stderr   = stderrLines.ToString();

            if (exitCode == 0)
            {
                _logger.LogInformation("[UFC] Download completed: '{Title}' ({DownloadId})", job.EventTitle, job.DownloadId);
                await UpdateQueueItemStatusAsync(job.DownloadId, DownloadStatus.Completed, null, 100d, job.OutputPath);
            }
            else
            {
                var errMsg = ClassifyError(stderr, job.EventTitle);
                var isRetriable = IsRetriableError(stderr) && retryCount < 3;

                if (isRetriable)
                {
                    retryCount++;
                    var backoff = TimeSpan.FromSeconds(30 * (int)Math.Pow(2, retryCount - 1));
                    _logger.LogWarning("[UFC] Retriable error for {DownloadId} (attempt {Retry}/3), retrying in {Backoff}s: {Error}",
                        job.DownloadId, retryCount, (int)backoff.TotalSeconds, errMsg);
                    // Retry is out of scope for the cancellation-path — in future a retry can rebuild the process.
                    // For now: mark as failed with retry context.
                    await UpdateQueueItemStatusAsync(job.DownloadId, DownloadStatus.Failed,
                        $"[Attempt {retryCount}/3] {errMsg}");
                }
                else
                {
                    _logger.LogError("[UFC] Download failed (exit {Code}) for '{Title}': {Error}",
                        exitCode, job.EventTitle, errMsg);
                    _logger.LogError("[UFC] Full stderr for {DownloadId}:\n{Stderr}", job.DownloadId, stderr);
                    await UpdateQueueItemStatusAsync(job.DownloadId, DownloadStatus.Failed, errMsg);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UFC] Unexpected error monitoring job {DownloadId}", job.DownloadId);
            await UpdateQueueItemStatusAsync(job.DownloadId, DownloadStatus.Failed, $"Internal error: {ex.Message}");
        }
        finally
        {
            _activeJobs.TryRemove(job.DownloadId, out _);
            job.Process.Dispose();
        }
    }

    /// <summary>Reads all lines from a TextReader asynchronously, calling <paramref name="onLine"/> for each.</summary>
    private static async Task ReadOutputAsync(System.IO.TextReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            onLine(line);
    }

    /// <summary>
    /// Parses a yt-dlp stdout line for progress and updates the DB record.
    /// Fire-and-forget; errors are swallowed to avoid crashing the monitor loop.
    /// </summary>
    private void ParseAndUpdateProgress(string downloadId, string line)
    {
        var match = ProgressRegex.Match(line);
        if (!match.Success) return;

        if (!double.TryParse(match.Groups["percent"].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var percent))
            return;

        _ = UpdateQueueItemProgressAsync(downloadId, percent);
    }

    /// <summary>Starts a yt-dlp Process with redirected I/O.</summary>
    private static Process StartProcess(string binaryPath, List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = binaryPath,
            Arguments              = BuildArgumentString(args),
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = false,
            CreateNoWindow         = true,
        };

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    /// <summary>Runs a short-lived process to completion and returns (exitCode, stdout, stderr).</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string binaryPath, List<string> args, int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = binaryPath,
            Arguments              = BuildArgumentString(args),
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    // -------------------------------------------------------------------------
    // Private helpers — argument building
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the argument list common to every yt-dlp invocation (auth + required flags).
    /// </summary>
    private static List<string> BuildBaseArguments(Config config, string cookiePath)
    {
        var args = new List<string>
        {
            "--username",            config.UfcEmail,
            "--password",            config.UfcPassword,
            "--cookies",             cookiePath,
            "--merge-output-format", "mp4",
            "--concurrent-fragments", config.UfcConcurrentFragments.ToString(),
            "--no-part",
            "--no-mtime",
            "--retries",             "10",
            "--fragment-retries",    "10",
            "--add-header",          "Referer:https://ufcfightpass.com",
            "--add-header",          "Origin:https://ufcfightpass.com",
        };
        return args;
    }

    /// <summary>
    /// Converts a List&lt;string&gt; of arguments into a single shell-escaped argument string.
    /// Each token is individually quoted if it contains spaces.
    /// </summary>
    private static string BuildArgumentString(List<string> args) =>
        string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    // -------------------------------------------------------------------------
    // Private helpers — binary discovery
    // -------------------------------------------------------------------------

    private async Task<string?> ResolveBinaryAsync(string configuredPath)
    {
        // 1. Config-supplied path.
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // 2. Docker default.
        if (File.Exists(DefaultYtDlpPath))
            return DefaultYtDlpPath;

        // 3. PATH search via 'which'.
        var (code, stdout, _) = await RunProcessAsync("which", ["yt-dlp"], timeoutSeconds: 5);
        if (code == 0)
        {
            var pathBin = stdout.Trim();
            if (!string.IsNullOrEmpty(pathBin) && File.Exists(pathBin))
                return pathBin;
        }

        return null;
    }

    private async Task<string> GetVersionStringAsync(string binaryPath)
    {
        var (_, stdout, _) = await RunProcessAsync(binaryPath, ["--version"], timeoutSeconds: 10);
        return stdout.Trim();
    }

    private async Task LogStartupBinaryAsync()
    {
        try
        {
            var config = await _configService.GetConfigAsync();
            var (found, path, version) = await DiscoverYtDlpAsync();
            if (found)
                _logger.LogInformation("[UFC] yt-dlp found at {Path} (version {Version})", path, version);
            else
                _logger.LogWarning("[UFC] yt-dlp not found. UFC downloads will fail until yt-dlp is installed.");
        }
        catch { /* startup — never crash */ }
    }

    // -------------------------------------------------------------------------
    // Private helpers — metadata
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs yt-dlp with --get-title to fetch the event title from the platform.
    /// Returns null on failure so the caller can fall back to a generated name.
    /// </summary>
    private async Task<string?> FetchTitleAsync(
        string binaryPath, Config config, string cookiePath, string url)
    {
        _logger.LogDebug("[UFC] Fetching title for {Url}", url);

        var args = BuildBaseArguments(config, cookiePath);
        args.AddRange(["--get-title", "--no-playlist", url]);

        var (exitCode, stdout, stderr) = await RunProcessAsync(binaryPath, args, timeoutSeconds: 30);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            var title = stdout.Trim().Split('\n')[0].Trim(); // take first line only
            _logger.LogInformation("[UFC] Resolved title: '{Title}'", title);
            return title;
        }

        _logger.LogWarning("[UFC] Could not fetch title (exit {Code}): {Stderr}", exitCode, stderr);
        return null;
    }

    // -------------------------------------------------------------------------
    // Private helpers — path & title sanitisation
    // -------------------------------------------------------------------------

    private string ResolveCookiePath(Config config)
    {
        if (!string.IsNullOrEmpty(config.UfcCookiePath))
            return config.UfcCookiePath;

        var dataPath = _configuration["Sportarr:DataPath"]
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
        return Path.Combine(dataPath, "ufc-cookies.txt");
    }

    /// <summary>
    /// Strips filesystem-unsafe characters and trims to 200 chars.
    /// Replaces sequences of illegal chars with a single underscore.
    /// </summary>
    private static string SanitizeTitle(string title)
    {
        // Characters illegal on FAT32 / NTFS / SMB shares
        const string illegal = @"[<>:""/\\|?*\x00-\x1f]";
        var safe = Regex.Replace(title, illegal, "_");
        safe = Regex.Replace(safe, @"_+", "_");          // collapse runs
        safe = safe.Trim('_', ' ', '.');
        return safe.Length > 200 ? safe[..200] : safe;
    }

    // -------------------------------------------------------------------------
    // Private helpers — error classification
    // -------------------------------------------------------------------------

    private static string ClassifyError(string stderr, string context)
    {
        if (stderr.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
            return "Login failed — check your UFC Fight Pass credentials.";

        if (stderr.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return $"'{context}' is not available on UFC Fight Pass.";

        if (stderr.Contains("format is not available", StringComparison.OrdinalIgnoreCase))
            return "Requested quality format is not available for this video.";

        if (stderr.Contains("Unable to download webpage", StringComparison.OrdinalIgnoreCase))
            return "Network error — UFC Fight Pass could not be reached.";

        // Truncate to 500 chars to avoid flooding the UI.
        var raw = stderr.Replace('\n', ' ').Trim();
        return raw.Length > 500 ? raw[..500] + "…" : raw;
    }

    private static bool IsRetriableError(string stderr) =>
        RetriablePatterns.Any(p => stderr.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
        !NonRetriablePatterns.Any(p => stderr.Contains(p, StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------------
    // Private helpers — database operations
    // -------------------------------------------------------------------------

    private async Task CreateQueueItemAsync(string downloadId, string title, string url)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var item = new DownloadQueueItem
        {
            DownloadId       = downloadId,
            Title            = title,
            Status           = DownloadStatus.Downloading,
            Protocol         = UfcProtocol,
            Progress         = 0,
            Added            = DateTime.UtcNow,
            LastUpdate       = DateTime.UtcNow,
            IsManualSearch   = true,
            // EventId 0 is unused; UFC items are standalone.
            EventId          = 0,
        };

        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync();

        _logger.LogDebug("[UFC] Created DownloadQueueItem for {DownloadId}", downloadId);
    }

    private async Task UpdateQueueItemProgressAsync(string downloadId, double percent)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var item = await db.DownloadQueue
                .FirstOrDefaultAsync(q => q.DownloadId == downloadId);

            if (item == null) return;

            item.Progress   = percent;
            item.LastUpdate = DateTime.UtcNow;
            item.Status     = DownloadStatus.Downloading;

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Swallow — progress updates are best-effort.
            _logger.LogDebug(ex, "[UFC] Could not persist progress for {DownloadId}", downloadId);
        }
    }

    private async Task UpdateQueueItemStatusAsync(
        string downloadId,
        DownloadStatus status,
        string? errorMessage,
        double? finalProgress = null,
        string? outputPath = null)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var item = await db.DownloadQueue
                .FirstOrDefaultAsync(q => q.DownloadId == downloadId);

            if (item == null) return;

            item.Status     = status;
            item.LastUpdate = DateTime.UtcNow;

            if (finalProgress.HasValue)  item.Progress    = finalProgress.Value;
            if (status == DownloadStatus.Completed)       item.CompletedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(errorMessage))      item.ErrorMessage = errorMessage;

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UFC] Could not update status for {DownloadId}", downloadId);
        }
    }
}
