using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Api.Endpoints;

public static class UfcFightPassEndpoints
{
    public static IEndpointRouteBuilder MapUfcFightPassEndpoints(this IEndpointRouteBuilder app)
    {

// =============================================================================
// GET /api/ufc/settings — Read UFC settings from config.xml
// =============================================================================
app.MapGet("/api/ufc/settings", async (ConfigService configService) =>
{
    var config = await configService.GetConfigAsync();
    return Results.Ok(new
    {
        ufcEnabled            = config.UfcEnabled,
        ufcEmail              = config.UfcEmail,
        // Never echo password back to the browser.
        ufcPasswordSet        = !string.IsNullOrEmpty(config.UfcPassword),
        ufcQualityFormat      = config.UfcQualityFormat,
        ufcConcurrentFragments= config.UfcConcurrentFragments,
        ufcOutputPath         = config.UfcOutputPath,
        ufcYtDlpPath          = config.UfcYtDlpPath,
        // Cookie path is internal; expose only whether one exists.
        ufcCookieExists       = !string.IsNullOrEmpty(config.UfcCookiePath),
    });
});

// =============================================================================
// PUT /api/ufc/settings — Persist UFC settings to config.xml
// =============================================================================
app.MapPut("/api/ufc/settings", async (HttpRequest request, ConfigService configService, ILogger<Program> logger) =>
{
    using var reader = new System.IO.StreamReader(request.Body);
    var json     = await reader.ReadToEndAsync();
    var settings = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

    await configService.UpdateConfigAsync(config =>
    {
        if (settings.TryGetProperty("ufcEnabled", out var enabled))
            config.UfcEnabled = enabled.GetBoolean();

        if (settings.TryGetProperty("ufcEmail", out var email))
            config.UfcEmail = email.GetString() ?? "";

        // Only overwrite password when the caller actually sends one.
        if (settings.TryGetProperty("ufcPassword", out var pwd) &&
            !string.IsNullOrEmpty(pwd.GetString()))
            config.UfcPassword = pwd.GetString()!;

        if (settings.TryGetProperty("ufcQualityFormat", out var fmt))
            config.UfcQualityFormat = fmt.GetString() ?? "bestvideo+bestaudio/best";

        if (settings.TryGetProperty("ufcConcurrentFragments", out var frags))
            config.UfcConcurrentFragments = Math.Clamp(frags.GetInt32(), 1, 16);

        if (settings.TryGetProperty("ufcOutputPath", out var outPath))
            config.UfcOutputPath = outPath.GetString() ?? "";

        if (settings.TryGetProperty("ufcYtDlpPath", out var ytDlpPath))
            config.UfcYtDlpPath = ytDlpPath.GetString() ?? "";
    });

    logger.LogInformation("[UFC] Settings saved to config.xml");
    return Results.Ok(new { success = true });
});

// =============================================================================
// GET /api/ufc/status — yt-dlp binary discovery + version
// =============================================================================
app.MapGet("/api/ufc/status", async (IUfcFightPassService ufcService) =>
{
    var (found, path, version) = await ufcService.DiscoverYtDlpAsync();
    return Results.Ok(new { found, path, version });
});

// =============================================================================
// POST /api/ufc/auth/test — Validate credentials against Fight Pass
// =============================================================================
app.MapPost("/api/ufc/auth/test", async (IUfcFightPassService ufcService, ILogger<Program> logger) =>
{
    logger.LogInformation("[UFC] Auth test requested via API");
    var (success, message) = await ufcService.TestAuthAsync();
    if (!success)
        return Results.BadRequest(new { success = false, message });
    return Results.Ok(new { success = true, message });
});

// =============================================================================
// POST /api/ufc/download — Enqueue a UFC VOD download by URL
// =============================================================================
app.MapPost("/api/ufc/download", async (HttpRequest request, IUfcFightPassService ufcService, ILogger<Program> logger) =>
{
    using var reader = new System.IO.StreamReader(request.Body);
    var json = await reader.ReadToEndAsync();
    var body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

    if (!body.TryGetProperty("url", out var urlProp) || string.IsNullOrWhiteSpace(urlProp.GetString()))
        return Results.BadRequest(new { error = "Missing required field: url" });

    var url         = urlProp.GetString()!;
    var customTitle = body.TryGetProperty("customTitle", out var ct) ? ct.GetString() : null;

    logger.LogInformation("[UFC] Download requested for {Url}", url);

    try
    {
        var downloadId = await ufcService.StartDownloadAsync(url, customTitle);
        return Results.Ok(new { success = true, downloadId });
    }
    catch (InvalidOperationException ex)
    {
        logger.LogWarning("[UFC] Download rejected: {Reason}", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[UFC] Unexpected error starting download for {Url}", url);
        return Results.Problem($"Failed to start download: {ex.Message}");
    }
});

// =============================================================================
// GET /api/ufc/download — List active in-memory UFC download jobs
// =============================================================================
app.MapGet("/api/ufc/download", (IUfcFightPassService ufcService) =>
{
    var jobs = ufcService.GetActiveJobs().Select(j => new
    {
        downloadId  = j.DownloadId,
        eventTitle  = j.EventTitle,
        fightPassUrl= j.FightPassUrl,
        outputPath  = j.OutputPath,
        startedAt   = j.StartedAt,
        isRunning   = !j.Process.HasExited,
    });
    return Results.Ok(jobs);
});

// =============================================================================
// GET /api/ufc/download/history — Recent UFC queue items from the database
// =============================================================================
app.MapGet("/api/ufc/download/history", async (SportarrDbContext db, int? limit) =>
{
    var take = Math.Clamp(limit ?? 50, 1, 200);
    var items = await db.DownloadQueue
        .Where(q => q.Protocol == "UFC")
        .OrderByDescending(q => q.Added)
        .Take(take)
        .Select(q => new
        {
            q.Id,
            q.DownloadId,
            q.Title,
            q.Status,
            q.Progress,
            q.ErrorMessage,
            q.Added,
            q.CompletedAt,
            q.LastUpdate,
        })
        .ToListAsync();
    return Results.Ok(items);
});

// =============================================================================
// DELETE /api/ufc/download/{id} — Cancel an active UFC download
// =============================================================================
app.MapDelete("/api/ufc/download/{id}", async (string id, IUfcFightPassService ufcService, ILogger<Program> logger) =>
{
    logger.LogInformation("[UFC] Cancel requested for {DownloadId}", id);
    var cancelled = await ufcService.CancelDownloadAsync(id);
    if (!cancelled)
        return Results.NotFound(new { error = $"No active UFC download with id '{id}'." });
    return Results.Ok(new { success = true });
});

        return app;
    }
}
