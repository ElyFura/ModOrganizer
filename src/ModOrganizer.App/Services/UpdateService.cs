using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ModOrganizer.App.Services;

/// <summary>A release newer than the one running.</summary>
public sealed record AvailableUpdate(Version Version, string DownloadUrl, long SizeBytes, string? Notes);

/// <summary>
/// Checks GitHub Releases for a newer build and installs it.
///
/// The awkward part on Windows is not the download but the swap: a running .exe cannot
/// overwrite itself. So the freshly downloaded build is started with --finish-update, waits
/// for this process to exit, copies itself over the old file and starts it again. That needs
/// no second binary and no batch script, which matters because we ship exactly one file.
/// </summary>
public sealed class UpdateService
{
    public const string FinishSwitch = "--finish-update";

    private readonly AppConfig _config;
    private readonly ILogger<UpdateService> _log;

    public UpdateService(AppConfig config, ILogger<UpdateService> log)
    {
        _config = config;
        _log = log;
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    private string Repository => string.IsNullOrWhiteSpace(_config.Update.Repository)
        ? "ElyFura/ModOrganizer"
        : _config.Update.Repository;

    private static string UpdateFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FFXIVModOrganizer", "update");

    /// <summary>
    /// Asks GitHub for the latest release. Returns null when we are current, when the repo
    /// is unreachable, or when the release carries no .exe - none of which is worth
    /// bothering the user with, so they only ever produce a log line.
    /// </summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        if (!_config.Update.Enabled) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            // GitHub rejects requests without a user agent.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "ModOrganizer", CurrentVersion.ToString()));
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var url = $"https://api.github.com/repos/{Repository}/releases/latest";
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 is the normal answer for a private repo or one with no releases yet.
                _log.LogInformation("Update check: GitHub answered {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()
                && !_config.Update.IncludePrereleases) return null;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!TryParseVersion(tag, out var version)) return null;

            if (version <= CurrentVersion)
            {
                _log.LogInformation("Update check: {Current} is current (latest {Latest})",
                    CurrentVersion, version);
                return null;
            }

            if (!root.TryGetProperty("assets", out var assets)) return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                var link = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (link is null) continue;

                var size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

                _log.LogInformation("Update available: {Version} ({Size} bytes)", version, size);
                return new AvailableUpdate(version, link, size, notes);
            }

            _log.LogInformation("Update check: release {Version} carries no .exe", version);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Update check failed");
            return null;
        }
    }

    /// <summary>Tags look like "v1.2.0"; anything else is not a release we understand.</summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var cleaned = tag.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[1..];

        return Version.TryParse(cleaned.Trim(), out version!);
    }

    /// <summary>
    /// Downloads into our own folder. The size from the release metadata is checked, because
    /// a truncated download would otherwise be swapped in as the new application.
    /// </summary>
    public async Task<string?> DownloadAsync(AvailableUpdate update,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdateFolder);
        var target = Path.Combine(UpdateFolder, $"ModOrganizer.App-{update.Version}.exe");
        var temp = target + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "ModOrganizer", CurrentVersion.ToString()));

        using (var response = await http.GetAsync(update.DownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var actual = new FileInfo(temp).Length;
        if (update.SizeBytes > 0 && actual != update.SizeBytes)
        {
            _log.LogWarning("Update download truncated: {Actual} of {Expected} bytes",
                actual, update.SizeBytes);
            try { File.Delete(temp); } catch { /* nothing to salvage */ }
            return null;
        }

        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Hands over to the downloaded build and asks the caller to shut down. Returns false if
    /// the handover could not even be started, in which case nothing has changed.
    /// </summary>
    public bool StartHandover(string downloadedExe)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
        {
            _log.LogWarning("Update: cannot determine our own path");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = downloadedExe,
                Arguments = $"{FinishSwitch} \"{current}\" {Environment.ProcessId}",
                UseShellExecute = false
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update: handover failed");
            return false;
        }
    }

    /// <summary>
    /// Runs inside the freshly downloaded build: wait for the old process to let go, copy
    /// ourselves over it, start it again. Returns an error message, or null on success.
    /// </summary>
    public static string? FinishUpdate(string targetPath, int oldProcessId)
    {
        try
        {
            try
            {
                var old = Process.GetProcessById(oldProcessId);
                if (!old.WaitForExit(30_000))
                    return "Die alte Version läuft noch. Schließe sie und starte das Update erneut.";
            }
            catch (ArgumentException)
            {
                // Already gone, which is exactly what we were waiting for.
            }

            var source = Environment.ProcessPath;
            if (string.IsNullOrEmpty(source)) return "Eigener Pfad nicht ermittelbar.";

            // The file can stay locked for a moment after the process disappears.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(source, targetPath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(500);
                }
            }

            Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Removes downloads left behind by an earlier update.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(UpdateFolder)) return;
            foreach (var f in Directory.EnumerateFiles(UpdateFolder))
            {
                try { File.Delete(f); } catch { /* still in use */ }
            }
        }
        catch { /* housekeeping only */ }
    }
}
