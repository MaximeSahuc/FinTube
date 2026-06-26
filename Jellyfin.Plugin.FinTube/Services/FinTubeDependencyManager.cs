using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTube.Services;

/// <summary>
/// Live progress of an in-flight dependency installation.
/// </summary>
public class InstallProgress
{
    /// <summary>True while a download/extract is running.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    /// <summary>idle | downloading | extracting | done | error</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "idle";

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("downloaded")]
    public long Downloaded { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Status of a single managed external dependency (yt-dlp or deno).
/// </summary>
public class DependencyStatus
{
    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>True when the binary used is the one FinTube downloaded itself.</summary>
    [JsonPropertyName("managed")]
    public bool Managed { get; set; }

    /// <summary>True when FinTube is able to download this dependency on this platform.</summary>
    [JsonPropertyName("downloadable")]
    public bool Downloadable { get; set; }
}

/// <summary>
/// Detects and, when missing, downloads the external binaries FinTube relies on
/// (yt-dlp and the deno JavaScript runtime) directly into the plugin's data
/// folder so the user doesn't have to install them in the container by hand.
/// </summary>
public class FinTubeDependencyManager
{
    private readonly ILogger<FinTubeDependencyManager> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new();

    public FinTubeDependencyManager(ILogger<FinTubeDependencyManager> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public InstallProgress GetProgress(string name) =>
        _progress.TryGetValue(name.ToLowerInvariant(), out var p) ? p : new InstallProgress();

    /// <summary>
    /// Kick off a background install for the named dependency and return its
    /// progress handle. A no-op (returns the existing handle) if one is running.
    /// </summary>
    public InstallProgress StartInstall(string name)
    {
        name = name.ToLowerInvariant();
        if (name is not ("ytdlp" or "deno"))
            throw new Exception($"Unknown dependency '{name}'");

        if (_progress.TryGetValue(name, out var existing) && existing.Active)
            return existing;

        var progress = new InstallProgress { Active = true, Phase = "downloading", Percent = 0 };
        _progress[name] = progress;

        // Detached from the HTTP request: use CancellationToken.None so the
        // download isn't aborted when the originating request returns.
        _ = Task.Run(() => RunInstall(name, progress));
        return progress;
    }

    private async Task RunInstall(string name, InstallProgress progress)
    {
        try
        {
            if (name == "ytdlp")
                await InstallYtdlpAsync(progress, CancellationToken.None).ConfigureAwait(false);
            else
                await InstallDenoAsync(progress, CancellationToken.None).ConfigureAwait(false);

            progress.Percent = 100;
            progress.Phase = "done";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Install of {Name} failed", name);
            progress.Phase = "error";
            progress.Error = e.Message;
        }
        finally
        {
            progress.Active = false;
        }
    }

    private static string ExeName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    /// <summary>Directory where FinTube stores binaries it downloads itself.</summary>
    public string BinDir
    {
        get
        {
            string root = (Plugin.Instance
                ?? throw new InvalidOperationException("Plugin not initialized")).DataFolderPath;
            return Path.Combine(root, "bin");
        }
    }

    public string ManagedYtdlpPath => Path.Combine(BinDir, ExeName("yt-dlp"));
    public string ManagedDenoPath => Path.Combine(BinDir, ExeName("deno"));

    /// <summary>
    /// Resolve the yt-dlp executable to use: the configured path if it exists,
    /// otherwise the managed copy, otherwise whatever is found on PATH.
    /// Returns null when none is available.
    /// </summary>
    public string? ResolveYtdlp()
    {
        string? configured = Plugin.Instance?.Configuration.exec_YTDL;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        if (File.Exists(ManagedYtdlpPath))
            return ManagedYtdlpPath;

        return FindOnPath(ExeName("yt-dlp"));
    }

    /// <summary>
    /// Resolve the deno executable: the managed copy first, then PATH.
    /// Returns null when deno is unavailable (it is optional).
    /// </summary>
    public string? ResolveDeno()
    {
        if (File.Exists(ManagedDenoPath))
            return ManagedDenoPath;

        return FindOnPath(ExeName("deno"));
    }

    /// <summary>The yt-dlp argument that points it at the deno runtime, or empty when deno is missing.</summary>
    public string GetJsRuntimeArgs()
    {
        string? deno = ResolveDeno();
        return string.IsNullOrEmpty(deno) ? "" : $"--js-runtimes \"deno:{deno}\" ";
    }

    public DependencyStatus GetYtdlpStatus()
    {
        string? path = ResolveYtdlp();
        return new DependencyStatus
        {
            Installed = path is not null,
            Path = path ?? ManagedYtdlpPath,
            Managed = path is not null && PathsEqual(path, ManagedYtdlpPath),
            Version = path is not null ? QueryVersion(path, "--version") : "",
            Downloadable = GetYtdlpDownloadUrl() is not null
        };
    }

    public DependencyStatus GetDenoStatus()
    {
        string? path = ResolveDeno();
        string version = "";
        if (path is not null)
        {
            // deno prints "deno x.y.z (...)" on the first line.
            string raw = QueryVersion(path, "--version");
            version = raw.Split('\n').FirstOrDefault()?.Replace("deno", "").Trim() ?? "";
        }

        return new DependencyStatus
        {
            Installed = path is not null,
            Path = path ?? ManagedDenoPath,
            Managed = path is not null && PathsEqual(path, ManagedDenoPath),
            Version = version,
            Downloadable = GetDenoDownloadUrl() is not null
        };
    }

    /// <summary>Download the latest yt-dlp binary into the managed bin directory.</summary>
    public async Task InstallYtdlpAsync(InstallProgress progress, CancellationToken ct)
    {
        string? url = GetYtdlpDownloadUrl()
            ?? throw new Exception("No yt-dlp build is available for this platform/architecture.");

        Directory.CreateDirectory(BinDir);
        _logger.LogInformation("Downloading yt-dlp from {Url}", url);

        progress.Phase = "downloading";
        await DownloadFileAsync(url, ManagedYtdlpPath, progress, ct).ConfigureAwait(false);
        MakeExecutable(ManagedYtdlpPath);

        _logger.LogInformation("yt-dlp installed at {Path}", ManagedYtdlpPath);
    }

    /// <summary>Download the latest deno runtime (a zip) and extract the binary into the managed bin directory.</summary>
    public async Task InstallDenoAsync(InstallProgress progress, CancellationToken ct)
    {
        string? url = GetDenoDownloadUrl()
            ?? throw new Exception("No deno build is available for this platform/architecture.");

        Directory.CreateDirectory(BinDir);
        string zipPath = ManagedDenoPath + ".zip";
        _logger.LogInformation("Downloading deno from {Url}", url);

        try
        {
            progress.Phase = "downloading";
            await DownloadFileAsync(url, zipPath, progress, ct).ConfigureAwait(false);

            progress.Phase = "extracting";
            string denoEntryName = ExeName("deno");
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                                string.Equals(Path.GetFileName(e.FullName), denoEntryName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new Exception($"'{denoEntryName}' not found inside the downloaded archive.");

                if (File.Exists(ManagedDenoPath))
                    File.Delete(ManagedDenoPath);
                entry.ExtractToFile(ManagedDenoPath, overwrite: true);
            }

            MakeExecutable(ManagedDenoPath);
            _logger.LogInformation("deno installed at {Path}", ManagedDenoPath);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* ignore */ }
        }
    }

    // ---- helpers ----------------------------------------------------------

    private async Task DownloadFileAsync(string url, string destination, InstallProgress progress, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;
        progress.Total = total;
        progress.Downloaded = 0;
        progress.Percent = 0;

        string tmp = destination + ".part";
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                progress.Downloaded = read;
                progress.Percent = total > 0 ? Math.Round(read * 100.0 / total, 1) : 0;
            }
        }

        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(tmp, destination);
    }

    private void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not set executable bit on {Path}", path);
        }
    }

    private string QueryVersion(string exe, string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            return output.Trim();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not query version of {Exe}", exe);
            return "";
        }
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* malformed PATH entry */ }
        }

        return null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? GetYtdlpDownloadUrl()
    {
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

        if (OperatingSystem.IsWindows())
            return baseUrl + "yt-dlp.exe";

        if (OperatingSystem.IsMacOS())
            return baseUrl + "yt-dlp_macos";

        // Linux
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => baseUrl + "yt-dlp_linux",
            Architecture.Arm64 => baseUrl + "yt-dlp_linux_aarch64",
            Architecture.Arm => baseUrl + "yt-dlp_linux_armv7l",
            _ => null
        };
    }

    private static string? GetDenoDownloadUrl()
    {
        const string baseUrl = "https://github.com/denoland/deno/releases/latest/download/";
        var arch = RuntimeInformation.ProcessArchitecture;

        if (OperatingSystem.IsWindows())
            return arch == Architecture.X64 ? baseUrl + "deno-x86_64-pc-windows-msvc.zip" : null;

        if (OperatingSystem.IsMacOS())
            return arch switch
            {
                Architecture.Arm64 => baseUrl + "deno-aarch64-apple-darwin.zip",
                Architecture.X64 => baseUrl + "deno-x86_64-apple-darwin.zip",
                _ => null
            };

        // Linux
        return arch switch
        {
            Architecture.X64 => baseUrl + "deno-x86_64-unknown-linux-gnu.zip",
            Architecture.Arm64 => baseUrl + "deno-aarch64-unknown-linux-gnu.zip",
            _ => null
        };
    }
}
