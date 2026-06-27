using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.FinTube.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTube.Services;

/// <summary>
/// Data describing a download request coming from the UI.
/// </summary>
public class FinTubeData
{
    public string ytid { get; set; } = "";
    public string targetlibrary { get; set; } = "";
    public string targetfolder { get; set; } = "";
    public string targetfilename { get; set; } = "";
    public bool audioonly { get; set; } = false;
    public bool preferfreeformat { get; set; } = false;
    public string videoresolution { get; set; } = "";
    public string artist { get; set; } = "";
    public string album { get; set; } = "";
    public string title { get; set; } = "";
    public int track { get; set; } = 0;
}

public enum FinTubeJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

/// <summary>
/// A single download job tracked by the queue.
/// </summary>
public class FinTubeJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public FinTubeData Data { get; set; } = new FinTubeData();

    public FinTubeJobStatus Status { get; set; } = FinTubeJobStatus.Queued;

    /// <summary>Download progress 0-100, or -1 when not applicable yet.</summary>
    public double Progress { get; set; } = 0;

    public string Log { get; set; } = "";
    public string? Error { get; set; }

    /// <summary>Video title resolved from yt-dlp, shown in the UI instead of the bare id.</summary>
    public string? ResolvedTitle { get; set; }

    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>A human friendly label for the UI.</summary>
    public string Label =>
        !string.IsNullOrWhiteSpace(Data.title) ? Data.title :
        !string.IsNullOrWhiteSpace(ResolvedTitle) ? ResolvedTitle :
        !string.IsNullOrWhiteSpace(Data.targetfilename) ? Data.targetfilename :
        Data.ytid;
}

/// <summary>
/// Background, strictly sequential download queue. A single worker drains the
/// channel one job at a time so that two yt-dlp processes never run concurrently.
/// Registered as a singleton so its state is shared across all controller instances.
/// </summary>
public class FinTubeDownloadQueue : IDisposable
{
    private readonly ILogger<FinTubeDownloadQueue> _logger;
    private readonly FinTubeDependencyManager _deps;
    private readonly Channel<FinTubeJob> _channel = Channel.CreateUnbounded<FinTubeJob>();
    private readonly ConcurrentDictionary<string, FinTubeJob> _jobs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    // Matches the line emitted by our --progress-template, e.g. "[fintube] 42.3%"
    private static readonly Regex ProgressRegex =
        new(@"\[fintube\]\s*([\d.]+)%", RegexOptions.Compiled);

    public FinTubeDownloadQueue(ILogger<FinTubeDownloadQueue> logger, FinTubeDependencyManager deps)
    {
        _logger = logger;
        _deps = deps;
        _worker = Task.Run(() => WorkerLoop(_cts.Token));
        _logger.LogInformation("FinTubeDownloadQueue started");
    }

    /// <summary>Enqueue a new download and return the created job.</summary>
    public FinTubeJob Enqueue(FinTubeData data)
    {
        var job = new FinTubeJob { Data = data };
        _jobs[job.Id] = job;
        _channel.Writer.TryWrite(job);
        _logger.LogInformation("Enqueued job {Id} for {Ytid}", job.Id, data.ytid);

        // Resolve the video title right away (best-effort, off the request thread)
        // so the queue shows the name instead of the bare id while it waits its
        // turn behind other downloads. RunJob still resolves it as a fallback.
        ResolveTitleInBackground(job);
        return job;
    }

    /// <summary>
    /// Fire-and-forget title resolution used at enqueue time. Swallows every
    /// failure: a missing title must never affect the actual download.
    /// </summary>
    private void ResolveTitleInBackground(FinTubeJob job)
    {
        _ = Task.Run(() =>
        {
            try
            {
                string? ytdlp = _deps.ResolveYtdlp();
                if (ytdlp is null)
                    return;

                var config = Plugin.Instance?.Configuration;
                if (config is null)
                    return;

                string preArgs = _deps.GetJsRuntimeArgs() + BuildCookieArgs(config);
                TryResolveTitle(ytdlp, preArgs, job.Data.ytid, job, _cts.Token);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Background title resolution failed for job {Id}", job.Id);
            }
        });
    }

    /// <summary>
    /// Re-queue a finished (typically failed) job for another attempt, reusing
    /// the original request. Resets its progress/log and puts it back on the
    /// channel. Returns the job, or null when the id is unknown. A job that is
    /// still queued or running is returned untouched.
    /// </summary>
    public FinTubeJob? Retry(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return null;

        if (job.Status is FinTubeJobStatus.Queued or FinTubeJobStatus.Running)
            return job;

        job.Status = FinTubeJobStatus.Queued;
        job.Progress = 0;
        job.Log = "";
        job.Error = null;
        job.StartedAt = null;
        job.FinishedAt = null;

        _channel.Writer.TryWrite(job);
        _logger.LogInformation("Re-queued job {Id} for {Ytid}", job.Id, job.Data.ytid);
        return job;
    }

    /// <summary>Snapshot of all jobs, newest first.</summary>
    public IReadOnlyList<FinTubeJob> GetJobs() =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    /// <summary>Drop finished (completed/failed) jobs from the tracking list.</summary>
    public void ClearFinished()
    {
        foreach (var job in _jobs.Values
                     .Where(j => j.Status is FinTubeJobStatus.Completed or FinTubeJobStatus.Failed)
                     .ToList())
        {
            _jobs.TryRemove(job.Id, out _);
        }
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    RunJob(job, ct);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Job {Id} failed", job.Id);
                    job.Status = FinTubeJobStatus.Failed;
                    job.Error = e.Message;
                    job.Log += $"\n<font color='red'>{e.Message}</font>";
                    job.FinishedAt = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void RunJob(FinTubeJob job, CancellationToken ct)
    {
        job.Status = FinTubeJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.Progress = 0;

        var data = job.Data;
        PluginConfiguration config = (Plugin.Instance
            ?? throw new Exception("Plugin not initialized")).Configuration;
        var status = new StringBuilder();

        // "Prefer free format" is a server-side setting (FinTube settings page),
        // not a per-download toggle, so take it from the configuration.
        data.preferfreeformat = config.preferFreeFormat;

        // check binaries
        string ytdlp = _deps.ResolveYtdlp()
            ?? throw new Exception("yt-dlp is not installed. Install it from the FinTube download page.");

        // Use the deno JS runtime when available so YouTube extraction stays complete.
        string jsRuntimeArgs = _deps.GetJsRuntimeArgs();

        // When cookies are configured (e.g. exported with the FinTube browser
        // extension), pass them to yt-dlp so age-restricted/private videos work.
        string cookieArgs = BuildCookieArgs(config);

        // Prefer the configured path, then the FinTube-managed copy, then PATH.
        string? id3v2 = _deps.ResolveId3v2();
        bool hasid3v2 = id3v2 is not null;

        // Resolve a human friendly title so the queue shows the video name
        // instead of the bare id. Best-effort: never fail the job over this.
        // Usually already done at enqueue time; only retry if that didn't stick.
        if (string.IsNullOrWhiteSpace(job.ResolvedTitle))
            TryResolveTitle(ytdlp, jsRuntimeArgs + cookieArgs, data.ytid, job, ct);

        // Ensure proper / separator
        data.targetfolder = string.Join("/", data.targetfolder.Split("/", StringSplitOptions.RemoveEmptyEntries));
        string targetPath = data.targetlibrary.EndsWith("/")
            ? data.targetlibrary + data.targetfolder
            : data.targetlibrary + "/" + data.targetfolder;

        if (!Directory.CreateDirectory(targetPath).Exists)
            throw new Exception("Directory could not be created");

        // Check for tags
        bool hasTags = 1 < (data.title.Length + data.album.Length + data.artist.Length + data.track.ToString().Length);

        // Audio and video downloads share the same output layout so the resulting
        // files live under the same channel/title/id structure regardless of mode.
        string targetFilename = Path.Combine(targetPath, "%(channel,uploader)s/%(title)s/%(id)s.%(ext)s");

        status.Append($"Filename: {targetFilename}<br>");

        // When tagging audio we need the real path yt-dlp produces (the output
        // template above is only resolved by yt-dlp), so have it print the final
        // file path to a temp file we read back afterwards.
        bool tagAudio = data.audioonly && hasid3v2 && hasTags;
        string? printPathFile = tagAudio
            ? Path.Combine(Path.GetTempPath(), $"fintube-{job.Id}.path")
            : null;

        string progressArgs = "--newline --progress-template \"download:[fintube] %(progress._percent_str)s\" ";
        string args = jsRuntimeArgs + cookieArgs + progressArgs + "--write-description --write-info-json --write-thumbnail --write-link --write-subs --audio-quality 0 ";
        if (data.audioonly)
        {
            args += "-x";
            if (data.preferfreeformat)
                args += " --prefer-free-format";
            else
                args += " --audio-format mp3";
            if (printPathFile is not null)
                args += $" --print-to-file after_move:filepath \"{printPathFile}\"";
        }
        else
        {
            if (data.preferfreeformat)
                args += "--prefer-free-format";
            else
                args += "-t mp4";
            if (!string.IsNullOrEmpty(data.videoresolution))
                args += $" -S res:{data.videoresolution}";
        }
        args += $" -o \"{targetFilename}\" {data.ytid}";

        status.Append($"Exec: {ytdlp} {args}<br>");
        job.Log = status.ToString();

        RunProcess(ytdlp, args, job, ct, parseProgress: true);

        // If audioonly AND id3v2 AND tags are set - Tag the produced audio file
        if (tagAudio && File.Exists(printPathFile))
        {
            string audioFile = File.ReadAllText(printPathFile).Trim();
            try { File.Delete(printPathFile!); } catch { /* ignore */ }

            if (!string.IsNullOrWhiteSpace(audioFile) && File.Exists(audioFile))
            {
                string id3args = $"-a \"{data.artist}\" -A \"{data.album}\" -t \"{data.title}\" -T \"{data.track}\" \"{audioFile}\"";
                status.Append($"Exec: {id3v2} {id3args}<br>");
                job.Log = status.ToString();
                RunProcess(id3v2!, id3args, job, ct, parseProgress: false);
            }
        }

        status.Append("<font color='green'>File Saved!</font>");
        job.Log = status.ToString();
        job.Progress = 100;
        job.Status = FinTubeJobStatus.Completed;
        job.FinishedAt = DateTime.UtcNow;
        _logger.LogInformation("Job {Id} completed", job.Id);
    }

    private void RunProcess(string exe, string args, FinTubeJob job, CancellationToken ct, bool parseProgress)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = startInfo };

        // Keep the tail of stderr/stdout so we can surface a useful reason in the
        // UI when the process fails instead of a bare "exited with code 1".
        const int maxTailLines = 40;
        var tail = new Queue<string>();
        void Capture(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > maxTailLines)
                    tail.Dequeue();
            }
        }

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            if (parseProgress)
            {
                var m = ProgressRegex.Match(e.Data);
                if (m.Success && double.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    job.Progress = pct;
                    return;
                }
            }
            Capture(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            _logger.LogDebug("[{Exe}] {Line}", exe, e.Data);
            Capture(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        while (!proc.WaitForExit(500))
        {
            if (ct.IsCancellationRequested)
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                throw new OperationCanceledException(ct);
            }
        }

        // Ensure async readers flushed.
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            string output;
            lock (tail)
                output = string.Join("\n", tail);

            string message = $"{Path.GetFileName(exe)} exited with code {proc.ExitCode}";
            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger.LogError("{Message}\n{Output}", message, output);
                message += "\n" + output;
            }

            throw new Exception(message);
        }
    }

    /// <summary>
    /// Persist the configured cookies to a Netscape cookie file inside the
    /// plugin data folder and return the matching <c>--cookies "..."</c> yt-dlp
    /// argument (with a trailing space). Returns an empty string when no cookies
    /// are configured. Best-effort: a write failure never blocks a download.
    /// </summary>
    private string BuildCookieArgs(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.cookies))
            return "";

        try
        {
            string dataDir = (Plugin.Instance
                ?? throw new Exception("Plugin not initialized")).DataFolderPath;
            Directory.CreateDirectory(dataDir);

            string cookiePath = Path.Combine(dataDir, "cookies.txt");

            // Normalize the pasted cookies into a strict Netscape file so a
            // malformed export (e.g. a domain/flag mismatch) can't make yt-dlp
            // reject the whole file.
            string contents = NormalizeCookies(config.cookies);

            File.WriteAllText(cookiePath, contents);

            // Keep the secrets out of group/other reach where the OS supports it.
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(cookiePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best effort */ }
            }

            return $"--cookies \"{cookiePath}\" ";
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not write cookies file; continuing without cookies");
            return "";
        }
    }

    /// <summary>
    /// Rewrite pasted cookies into a strict, yt-dlp friendly Netscape cookie file:
    /// Unix line endings, a leading magic header, and - crucially - an
    /// "include subdomains" flag (field 2) that always agrees with whether the
    /// domain (field 1) starts with a dot. A mismatch there makes Python's
    /// http.cookiejar throw an AssertionError ("http.cookiejar bug!") and yt-dlp
    /// refuses the entire file, failing every download. Records that don't have
    /// the expected 7 tab-separated fields are dropped rather than allowed to
    /// break the file.
    /// </summary>
    internal static string NormalizeCookies(string raw)
    {
        string text = raw.Replace("\r\n", "\n").Replace("\r", "\n");

        var sb = new StringBuilder();
        sb.Append("# Netscape HTTP Cookie File\n");

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0)
                continue;

            // yt-dlp honours a "#HttpOnly_" prefix on the domain; ordinary
            // comment/header lines are dropped (we emit our own header above).
            bool httpOnly = line.StartsWith("#HttpOnly_", StringComparison.Ordinal);
            if (line.StartsWith("#", StringComparison.Ordinal) && !httpOnly)
                continue;

            string prefix = httpOnly ? "#HttpOnly_" : "";
            string body = httpOnly ? line.Substring(prefix.Length) : line;

            string[] fields = body.Split('\t');
            if (fields.Length != 7)
                continue;

            // Force the flag to match the domain so the two can never disagree.
            fields[1] = fields[0].StartsWith(".", StringComparison.Ordinal) ? "TRUE" : "FALSE";

            sb.Append(prefix);
            sb.Append(string.Join("\t", fields));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Best-effort resolution of the video title via yt-dlp, used to label the
    /// job in the UI. Failures are swallowed so they never block a download.
    /// </summary>
    private void TryResolveTitle(string exe, string jsRuntimeArgs, string ytid, FinTubeJob job, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{jsRuntimeArgs}--no-warnings --skip-download --print \"%(title)s\" {ytid}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = startInfo };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();

            while (!proc.WaitForExit(250))
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(true); } catch { /* ignore */ }
                    return;
                }
            }
            proc.WaitForExit();

            string title = output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(title))
                job.ResolvedTitle = title;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not resolve title for {Ytid}", ytid);
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
