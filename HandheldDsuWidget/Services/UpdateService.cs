using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace HandheldDsuWidget.Services;

public sealed record GitHubAssetInfo(string Name, string DownloadUrl, long SizeBytes);

public sealed record GitHubReleaseInfo(
    string TagName,
    string Name,
    bool IsPreRelease,
    string HtmlUrl,
    IReadOnlyList<GitHubAssetInfo> Assets);

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    bool IsPreRelease,
    GitHubReleaseInfo? Release,
    string? ErrorMessage = null);

/// <summary>
/// Self-contained GitHub Releases auto-updater supporting both Stable and Beta (Pre-Release) channels.
/// Supports in-place hot-swapping for both Portable ZIP installations and Single-File executables.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/brktlhylmz/HandheldGyroDSU/releases?per_page=15";
    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HandheldGyroDSU-AutoUpdater/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static string GetCurrentVersion()
    {
        string asmVersion = "1.0.0";
        try
        {
            Assembly? asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                int plusIdx = infoVer.IndexOf('+');
                string clean = plusIdx >= 0 ? infoVer[..plusIdx] : infoVer;
                asmVersion = clean.TrimStart('v', 'V').Trim();
            }
            else
            {
                Version? ver = asm.GetName().Version;
                if (ver is not null)
                {
                    asmVersion = $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
                }
            }
        }
        catch
        {
            // Ignore reflection errors
        }

        try
        {
            string tagFile = Path.Combine(AppContext.BaseDirectory, "version.tag");
            if (File.Exists(tagFile))
            {
                string tagVer = File.ReadAllText(tagFile).Trim().TrimStart('v', 'V');
                if (!string.IsNullOrWhiteSpace(tagVer) &&
                    IsRemoteVersionNewer(asmVersion, tagVer, tagVer.Contains("-beta", StringComparison.OrdinalIgnoreCase)))
                {
                    return tagVer;
                }
            }
        }
        catch
        {
            // Ignore tag file read errors
        }

        return asmVersion;
    }

    /// <summary>
    /// Removes any leftover *.old files renamed during a previous in-place self-update.
    /// </summary>
    public static void CleanupOldUpdateFiles()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (!Directory.Exists(baseDir))
                return;

            foreach (string oldFile in Directory.GetFiles(baseDir, "*.old", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(oldFile);
                }
                catch
                {
                    // Ignore if still locked briefly
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string channel = "stable", CancellationToken cancellationToken = default)
    {
        string currentVer = GetCurrentVersion();
        bool includeBeta = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(ReleasesApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    false,
                    currentVer,
                    currentVer,
                    false,
                    null,
                    $"HTTP {(int)response.StatusCode}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new UpdateCheckResult(false, currentVer, currentVer, false, null);
            }

            var candidates = new List<GitHubReleaseInfo>();
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("draft", out JsonElement draftProp) && draftProp.GetBoolean())
                    continue;

                bool isPre = item.TryGetProperty("prerelease", out JsonElement preProp) && preProp.GetBoolean();
                string tag = item.TryGetProperty("tag_name", out JsonElement tagProp) ? (tagProp.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                if (!includeBeta && (isPre || tag.Contains("-beta", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string title = item.TryGetProperty("name", out JsonElement nameProp) ? (nameProp.GetString() ?? tag) : tag;
                string htmlUrl = item.TryGetProperty("html_url", out JsonElement urlProp) ? (urlProp.GetString() ?? "") : "";

                var assets = new List<GitHubAssetInfo>();
                if (item.TryGetProperty("assets", out JsonElement assetsArray) && assetsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in assetsArray.EnumerateArray())
                    {
                        string aName = asset.TryGetProperty("name", out JsonElement anProp) ? (anProp.GetString() ?? "") : "";
                        string aUrl = asset.TryGetProperty("browser_download_url", out JsonElement auProp) ? (auProp.GetString() ?? "") : "";
                        long aSize = asset.TryGetProperty("size", out JsonElement asProp) && asProp.TryGetInt64(out long sz) ? sz : 0;
                        if (!string.IsNullOrWhiteSpace(aName) && !string.IsNullOrWhiteSpace(aUrl))
                        {
                            assets.Add(new GitHubAssetInfo(aName, aUrl, aSize));
                        }
                    }
                }

                if (assets.Count > 0)
                {
                    candidates.Add(new GitHubReleaseInfo(tag, title, isPre, htmlUrl, assets));
                }
            }

            if (candidates.Count == 0)
            {
                return new UpdateCheckResult(false, currentVer, currentVer, false, null);
            }

            // Pick the highest semantic version among eligible releases
            GitHubReleaseInfo best = candidates
                .OrderByDescending(r => ParseCoreVersion(r.TagName))
                .ThenByDescending(r => !IsPreReleaseTag(r.TagName, r.IsPreRelease))
                .First();

            string latestClean = best.TagName.TrimStart('v', 'V').Trim();
            bool newer = IsRemoteVersionNewer(currentVer, latestClean, best.IsPreRelease);

            return new UpdateCheckResult(
                newer,
                currentVer,
                latestClean,
                best.IsPreRelease || latestClean.Contains("-beta", StringComparison.OrdinalIgnoreCase),
                best);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, currentVer, currentVer, false, null, ex.Message);
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> DownloadAndApplyUpdateAsync(
        GitHubReleaseInfo release,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string processPath = Environment.ProcessPath ?? Path.Combine(baseDir, "HandheldGyroDSU.exe");
            bool isMultiFileInstall = File.Exists(Path.Combine(baseDir, "HandheldGyroDSU.dll"));

            GitHubAssetInfo? selectedAsset;
            if (isMultiFileInstall)
            {
                selectedAsset = release.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                long currentSize = File.Exists(processPath) ? new FileInfo(processPath).Length : 0;
                bool isStandalone = currentSize > 15 * 1024 * 1024;
                selectedAsset = isStandalone
                    ? release.Assets.FirstOrDefault(a => a.Name.Contains("Standalone", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    : release.Assets.FirstOrDefault(a => string.Equals(a.Name, "HandheldGyroDSU.exe", StringComparison.OrdinalIgnoreCase));
            }

            selectedAsset ??= release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (selectedAsset is null)
            {
                return (false, "No compatible package found in release assets.");
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), $"HandheldGyroDSU_Update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);

            string downloadFilePath = Path.Combine(tempRoot, selectedAsset.Name);
            using (HttpResponseMessage response = await _httpClient.GetAsync(
                selectedAsset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                long totalBytes = response.Content.Headers.ContentLength ?? selectedAsset.SizeBytes;

                await using Stream netStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using FileStream fileStream = new(downloadFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 32768, true);

                byte[] buffer = new byte[32768];
                long totalRead = 0;
                int read;
                while ((read = await netStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    if (totalBytes > 0)
                    {
                        int pct = (int)Math.Clamp((totalRead * 90L) / totalBytes, 1, 90);
                        progress?.Report(pct);
                    }
                }
            }

            if (selectedAsset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string extractDir = Path.Combine(tempRoot, "extracted");
                ZipFile.ExtractToDirectory(downloadFilePath, extractDir, overwriteFiles: true);
                progress?.Report(95);

                foreach (string srcFile in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(extractDir, srcFile);
                    string dstFile = Path.Combine(baseDir, relPath);
                    string? dstFolder = Path.GetDirectoryName(dstFile);
                    if (!string.IsNullOrEmpty(dstFolder))
                    {
                        Directory.CreateDirectory(dstFolder);
                    }

                    ReplaceFileInPlace(srcFile, dstFile);
                }
            }
            else
            {
                progress?.Report(95);
                ReplaceFileInPlace(downloadFilePath, processPath);
            }

            progress?.Report(100);

            try
            {
                File.WriteAllText(Path.Combine(baseDir, "version.tag"), release.TagName.TrimStart('v', 'V').Trim());
            }
            catch
            {
                // Ignore version tag stamp errors
            }

            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore temp cleanup error
            }

            // Relaunch the newly updated executable and close the current instance
            Process.Start(new ProcessStartInfo
            {
                FileName = processPath,
                WorkingDirectory = baseDir,
                UseShellExecute = true
            });

            Application.Current?.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void ReplaceFileInPlace(string sourceFilePath, string targetFilePath)
    {
        if (File.Exists(targetFilePath))
        {
            try
            {
                File.Copy(sourceFilePath, targetFilePath, overwrite: true);
                return;
            }
            catch
            {
                // Target file is currently locked by the running process/runtime; rename it to .old first
                string oldPath = targetFilePath + ".old";
                if (File.Exists(oldPath))
                {
                    try { File.Delete(oldPath); } catch { }
                }
                File.Move(targetFilePath, oldPath, overwrite: true);
            }
        }

        File.Copy(sourceFilePath, targetFilePath, overwrite: true);
    }

    private static bool IsRemoteVersionNewer(string currentRaw, string remoteRaw, bool remoteIsPreReleaseFlag)
    {
        Version currentCore = ParseCoreVersion(currentRaw);
        Version remoteCore = ParseCoreVersion(remoteRaw);

        if (remoteCore > currentCore)
            return true;
        if (remoteCore < currentCore)
            return false;

        bool currentIsPre = IsPreReleaseTag(currentRaw, false);
        bool remoteIsPre = IsPreReleaseTag(remoteRaw, remoteIsPreReleaseFlag);

        // Same numeric version: upgrading from a beta/pre-release to stable is considered newer
        return currentIsPre && !remoteIsPre;
    }

    private static Version ParseCoreVersion(string rawTag)
    {
        string clean = rawTag.Trim().TrimStart('v', 'V');
        int dashIdx = clean.IndexOf('-');
        if (dashIdx >= 0)
            clean = clean[..dashIdx];
        int plusIdx = clean.IndexOf('+');
        if (plusIdx >= 0)
            clean = clean[..plusIdx];

        if (Version.TryParse(clean, out Version? parsed))
        {
            return new Version(
                Math.Max(0, parsed.Major),
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build));
        }

        return new Version(1, 0, 0);
    }

    private static bool IsPreReleaseTag(string rawTag, bool apiFlag)
    {
        return apiFlag || rawTag.Contains('-', StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
