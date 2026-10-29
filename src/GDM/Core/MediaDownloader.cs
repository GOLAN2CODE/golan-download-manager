using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

public sealed record SubtitleTrack(string Lang, string Name, bool IsAuto);

public sealed record MediaChapter(string Title, double StartTime, double EndTime);

public sealed record MediaOptions
{
    public string Quality { get; init; } = "best";
    public IReadOnlyList<string> SubtitleLangs { get; init; } = [];
    public bool IncludeAutoSubs { get; init; }
    public bool EmbedSubs { get; init; }
    public bool WriteSubs { get; init; }
    public bool ConvertSubsToSrt { get; init; } = true;
    public string? PlaylistItems { get; init; }
    public int? PlaylistFrom { get; init; }
    public int? PlaylistTo { get; init; }
    public bool WriteThumbnail { get; init; }
    public bool EmbedThumbnail { get; init; }
    public bool EmbedChapters { get; init; }
    public bool SplitChapters { get; init; }
    public int Mp3BitrateKbps { get; init; } = 192;
    public bool EmbedMetadata { get; init; }
}

/// <summary>Pure, validated yt-dlp argument list builder (never constructs shell strings).</summary>
public static class MediaArgs
{
    static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex LangCodeRx = new(@"^[a-zA-Z]{2,3}(-[a-zA-Z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex PlaylistItemsRx = new(@"^\d+(-\d+)?(,\d+(-\d+)?)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);

    public static bool IsValidLangCode(string? lang)
        => !string.IsNullOrWhiteSpace(lang) && lang.Length <= 35 && LangCodeRx.IsMatch(lang);

    public static string? ResolvePlaylistItems(string? playlistItems, int? from = null, int? to = null)
    {
        string? raw = playlistItems;
        if (string.IsNullOrWhiteSpace(raw) && (from.HasValue || to.HasValue))
        {
            int f = from ?? 1;
            int t = to ?? f;
            if (f <= 0 || t <= 0 || f > t)
                throw new ArgumentException("Invalid playlist range: 'from' and 'to' must be >= 1 with from <= to.");
            raw = f == t
                ? f.ToString(CultureInfo.InvariantCulture)
                : $"{f.ToString(CultureInfo.InvariantCulture)}-{t.ToString(CultureInfo.InvariantCulture)}";
        }
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        if (!PlaylistItemsRx.IsMatch(trimmed))
            throw new ArgumentException($"Invalid playlist items specification: '{trimmed}'.");

        foreach (var part in trimmed.Split(','))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) || idx <= 0)
                    throw new ArgumentException($"Invalid playlist item index '{part}'.");
            }
            else
            {
                if (!int.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int a) ||
                    !int.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int b) ||
                    a <= 0 || b <= 0 || a > b)
                {
                    throw new ArgumentException($"Invalid playlist item range '{part}'.");
                }
            }
        }
        return trimmed;
    }

    /// <summary>Parses a validated playlist items specification (e.g. "3-7,10") into 1-based item indices.</summary>
    public static HashSet<int> ParsePlaylistIndices(string? resolvedSpec)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(resolvedSpec)) return set;
        var validated = ResolvePlaylistItems(resolvedSpec);
        if (validated == null) return set;

        foreach (var part in validated.Split(','))
        {
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx > 0)
                    set.Add(idx);
            }
            else
            {
                if (int.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int a) &&
                    int.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int b) &&
                    a > 0 && b >= a && (b - a) <= 10000)
                {
                    for (int i = a; i <= b; i++) set.Add(i);
                }
            }
        }
        return set;
    }

    public static List<string> Build(
        MediaOptions options,
        string? outputTemplate = null,
        string? ffmpegDir = null,
        long rateLimitBytesPerSec = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mp3BitrateKbps is not (128 or 192 or 320))
            throw new ArgumentException($"Invalid MP3 bitrate {options.Mp3BitrateKbps}; allowed values are 128, 192, or 320.");

        var langs = options.SubtitleLangs ?? [];
        if (langs.Count > 10)
            throw new ArgumentException("At most 10 subtitle languages may be specified.");
        foreach (var l in langs)
        {
            if (!IsValidLangCode(l))
                throw new ArgumentException($"Invalid subtitle language code: '{l}'.");
        }

        var resolvedPlaylist = ResolvePlaylistItems(options.PlaylistItems, options.PlaylistFrom, options.PlaylistTo);

        var args = new List<string>();
        var q = string.IsNullOrWhiteSpace(options.Quality) ? "best" : options.Quality.Trim();
        switch (q)
        {
            case "mp3":
                args.AddRange(["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", $"{options.Mp3BitrateKbps}K"]);
                break;
            case "m4a":
                args.AddRange(["-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a"]);
                break;
            case "best":
                args.AddRange(["-f", "bv*+ba/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"]);
                break;
            default:
                if (int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out int h) && h > 0)
                    args.AddRange(["-f", $"bv*[height<={h}]+ba/b[height<={h}]/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"]);
                else
                    args.AddRange(["-f", "b"]);
                break;
        }

        if (langs.Count > 0 && (options.WriteSubs || options.EmbedSubs || options.IncludeAutoSubs))
        {
            if (options.WriteSubs) args.Add("--write-subs");
            if (options.IncludeAutoSubs) args.Add("--write-auto-subs");
            if (options.EmbedSubs) args.Add("--embed-subs");
            args.Add("--sub-langs");
            args.Add(string.Join(",", langs));
            if (options.ConvertSubsToSrt)
            {
                args.Add("--convert-subs");
                args.Add("srt");
            }
        }

        if (resolvedPlaylist != null)
        {
            args.Add("--playlist-items");
            args.Add(resolvedPlaylist);
        }
        else
        {
            args.Add("--no-playlist");
        }

        if (options.WriteThumbnail) args.Add("--write-thumbnail");
        if (options.EmbedThumbnail) args.Add("--embed-thumbnail");
        if (options.WriteThumbnail || options.EmbedThumbnail)
        {
            args.Add("--convert-thumbnails");
            args.Add("jpg");
        }

        if (options.EmbedChapters) args.Add("--embed-chapters");
        if (options.SplitChapters) args.Add("--split-chapters");
        if (options.EmbedMetadata) args.Add("--embed-metadata");

        if (!string.IsNullOrWhiteSpace(ffmpegDir))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegDir);
        }
        if (!string.IsNullOrWhiteSpace(outputTemplate))
        {
            args.Add("-o");
            args.Add(outputTemplate);
        }
        if (rateLimitBytesPerSec > 0)
        {
            args.Add("--limit-rate");
            args.Add(Math.Max(1, rateLimitBytesPerSec / 1024).ToString(CultureInfo.InvariantCulture) + "K");
        }

        return args;
    }
}

public sealed class MediaQuality
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public long? Size { get; init; }
    public bool AudioOnly { get; init; }
    public string SizeText => Size is > 0 ? "≈ " + Fmt.Size(Size.Value) : "";
}

public sealed class PlaylistEntry : ObservableObject
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public double Duration { get; init; }
    public string DurationText => Fmt.Duration(Duration);
    bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class MediaInfo
{
    public string Title { get; set; } = "";
    public string? Thumbnail { get; set; }
    public string? ThumbnailUrl { get => Thumbnail; set => Thumbnail = value; }
    public double Duration { get; set; }
    public string Uploader { get; set; } = "";
    public string WebpageUrl { get; set; } = "";
    public string Extractor { get; set; } = "";
    public List<MediaQuality> Qualities { get; } = [];
    public bool IsPlaylist { get; set; }
    public List<PlaylistEntry> Entries { get; } = [];
    public List<SubtitleTrack> Subtitles { get; } = [];
    public List<MediaChapter> Chapters { get; } = [];
}

/// <summary>Downloads and keeps yt-dlp and ffmpeg in %LOCALAPPDATA%\Golan\GDM\tools.</summary>
public static class MediaTools
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string YtDlpSumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    public const string YtDlpAssetName = "yt-dlp.exe";
    public const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    public const string FfmpegSumsUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256";
    public const string FfmpegAssetName = "ffmpeg-master-latest-win64-gpl.zip";

    public static string YtDlp => Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe");
    public static string Ffmpeg => Path.Combine(AppPaths.ToolsDir, "ffmpeg.exe");
    public static bool YtDlpReady => File.Exists(YtDlp);
    public static bool FfmpegReady => File.Exists(Ffmpeg);
    public static bool Ready => YtDlpReady && FfmpegReady;

    // One gate per tool: the small yt-dlp never waits behind the large ffmpeg download.
    static readonly SemaphoreSlim YtDlpGate = new(1, 1);
    static readonly SemaphoreSlim FfmpegGate = new(1, 1);

    /// <summary>Latest tool-preparation progress from any caller (background prefetch or a window), for anyone who wants to show it.</summary>
    public static event Action<string, double>? ProgressChanged;
    public static string LastProgressText { get; private set; } = "";
    public static double LastProgressPercent { get; private set; } = -1;

    static void Report(IProgress<(string Text, double Percent)>? progress, string text, double percent)
    {
        LastProgressText = text;
        LastProgressPercent = percent;
        progress?.Report((text, percent));
        ProgressChanged?.Invoke(text, percent);
    }

    public static async Task EnsureAsync(IProgress<(string Text, double Percent)>? progress, CancellationToken ct)
    {
        await EnsureYtDlpAsync(progress, ct);
        await EnsureFfmpegAsync(progress, ct);
    }

    /// <summary>yt-dlp only (≈18 MB): enough to read a video's qualities, so the choice appears quickly.</summary>
    public static async Task EnsureYtDlpAsync(IProgress<(string Text, double Percent)>? progress, CancellationToken ct)
    {
        if (YtDlpReady) return;
        await YtDlpGate.WaitAsync(ct);
        try
        {
            if (YtDlpReady) return;
            Directory.CreateDirectory(AppPaths.ToolsDir);
            await DownloadFileAsync(YtDlpUrl, YtDlp, Loc.T("core.media.dl_ytdlp"), progress, ct, YtDlpSumsUrl, YtDlpAssetName);
        }
        finally { YtDlpGate.Release(); }
    }

    /// <summary>ffmpeg (merging audio and video, MP3): needed only when a download is merged or converted.</summary>
    public static async Task EnsureFfmpegAsync(IProgress<(string Text, double Percent)>? progress, CancellationToken ct)
    {
        if (FfmpegReady) return;
        await FfmpegGate.WaitAsync(ct);
        try
        {
            if (FfmpegReady) return;
            Directory.CreateDirectory(AppPaths.ToolsDir);
            var zip = Path.Combine(AppPaths.ToolsDir, "ffmpeg.zip");
            await DownloadFileAsync(FfmpegUrl, zip, Loc.T("core.media.dl_ffmpeg"), progress, ct, FfmpegSumsUrl, FfmpegAssetName);
            Report(progress, Loc.T("core.media.extracting"), 100);
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries)
                {
                    var name = entry.Name.ToLowerInvariant();
                    if (name is "ffmpeg.exe" or "ffprobe.exe")
                    {
                        // Extract next to the target first so a half-written ffmpeg.exe never looks "ready".
                        var target = Path.Combine(AppPaths.ToolsDir, name);
                        entry.ExtractToFile(target + ".tmp", overwrite: true);
                        File.Move(target + ".tmp", target, overwrite: true);
                    }
                }
            }, ct);
            File.Delete(zip);
        }
        finally { FfmpegGate.Release(); }
    }

    static int _prefetchStarted;

    /// <summary>
    /// Prepares the video tools quietly in the background shortly after GDM starts, so the first video download needs no
    /// waiting. Interrupted downloads resume next time. Tests and isolated runs never prefetch (no internet in tests).
    /// </summary>
    public static void StartPrefetch(TimeSpan delay)
    {
        if (Ready || AppPaths.IsIsolated || Interlocked.Exchange(ref _prefetchStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await EnsureYtDlpAsync(null, CancellationToken.None);
                await EnsureFfmpegAsync(null, CancellationToken.None);
                Report(null, "", 100);
            }
            catch (Exception ex)
            {
                Log.Write("video tools prefetch failed (will retry on next start or when needed): " + ex.Message);
                Report(null, "", -1);
            }
            finally { Interlocked.Exchange(ref _prefetchStarted, 0); }
        });
    }

    /// <summary>Starts preparing ffmpeg in the background (no waiting) — used once a video's qualities are shown.</summary>
    public static void WarmUpFfmpeg()
    {
        if (FfmpegReady || AppPaths.IsIsolated) return;
        _ = Task.Run(async () =>
        {
            try { await EnsureFfmpegAsync(null, CancellationToken.None); }
            catch (Exception ex) { Log.Write("ffmpeg warm-up failed: " + ex.Message); }
        });
    }

    static async Task<HttpResponseMessage> GetFollowHttpsAsync(string url, CancellationToken ct, long resumeFrom = 0, bool requireHttps = true)
    {
        var current = new Uri(url);
        for (int hop = 0; hop < 10; hop++)
        {
            if (requireHttps && current.Scheme != Uri.UriSchemeHttps)
                throw new DownloadException(Loc.T("core.media.err_https"));
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("User-Agent", HttpDownloader.DefaultUserAgent);
            if (resumeFrom > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
            var resp = await HttpDownloader.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                resp.Dispose();
                current = next;
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return resp;
        }
        throw new DownloadException(Loc.T("core.media.err_redirects"));
    }

    /// <summary>"Preparing the merge tool · 45 MB of 190 MB · 5.2 MB/s · 28 s left" (pure, testable).</summary>
    public static string FormatToolProgress(string what, long done, long total, double bytesPerSecond)
    {
        var parts = new List<string> { what, total > 0 ? Loc.T("core.media.tool_of", Fmt.Size(done), Fmt.Size(total)) : Fmt.Size(done) };
        if (bytesPerSecond > 0) parts.Add(Fmt.Speed(bytesPerSecond));
        if (total > 0 && bytesPerSecond > 0)
        {
            var eta = Fmt.Eta(TimeSpan.FromSeconds((total - done) / bytesPerSecond));
            if (eta.Length > 0) parts.Add(eta);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="partPath"/>, resuming an earlier partial file when the server
    /// honours Range (GitHub release assets do), reporting size, speed and time left. Returns the final size.
    /// </summary>
    public static async Task<long> DownloadResumableAsync(string url, string partPath, string what,
        IProgress<(string Text, double Percent)>? progress, CancellationToken ct, bool requireHttps = true)
    {
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var resp = await GetFollowHttpsAsync(url, ct, existing, requireHttps);
        bool resumed = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent
                       && resp.Content.Headers.ContentRange?.From == existing;
        if (!resumed) existing = 0;
        long total = resp.Content.Headers.ContentRange?.Length
                     ?? (resp.Content.Headers.ContentLength is { } len ? len + existing : -1);

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        var buf = new byte[128 * 1024];
        long done = existing;
        int n;
        var tick = Stopwatch.StartNew();
        var window = Stopwatch.StartNew();
        long windowBytes = 0;
        double speed = 0;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            windowBytes += n;
            if (window.ElapsedMilliseconds >= 1000)
            {
                var instant = windowBytes / window.Elapsed.TotalSeconds;
                speed = speed <= 0 ? instant : speed * 0.6 + instant * 0.4; // smoothed, so the time left does not jump around
                windowBytes = 0;
                window.Restart();
            }
            if (tick.ElapsedMilliseconds > 250)
            {
                Report(progress, FormatToolProgress(what, done, total, speed), total > 0 ? done * 100.0 / total : -1);
                tick.Restart();
            }
        }
        if (total > 0 && done != total)
            throw new DownloadException(Loc.T("core.http.err_incomplete"), FailKind.Retryable);
        return done;
    }

    static async Task DownloadFileAsync(string url, string path, string text, IProgress<(string, double)>? progress, CancellationToken ct, string sumsUrl, string assetName)
    {
        var tmp = path + ".part";
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await DownloadResumableAsync(url, tmp, text, progress, ct);
                last = null;
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or DownloadException { Retryable: true } && !ct.IsCancellationRequested)
            {
                last = ex; // the .part file stays: the next attempt continues from where this one stopped
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
        if (last != null) throw last;
        await VerifyToolHashAsync(tmp, sumsUrl, assetName, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public static string? ParseExpectedHashFromSums(string? sumsContent, string assetName)
    {
        if (string.IsNullOrWhiteSpace(sumsContent) || string.IsNullOrWhiteSpace(assetName))
            return null;

        foreach (var line in sumsContent.Split('\n'))
        {
            var parts = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var candidateHash = parts[0].Trim();
            if (candidateHash.Length != 64 || !candidateHash.All(Uri.IsHexDigit)) continue;
            var candidateName = Path.GetFileName(parts[1].Trim().TrimStart('*'));
            if (candidateName.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return candidateHash;
        }
        return null;
    }

    public static async Task VerifyFileAgainstSumsContentAsync(string filePath, string? sumsContent, string assetName, CancellationToken ct = default)
    {
        var expectedHash = ParseExpectedHashFromSums(sumsContent, assetName);
        if (string.IsNullOrEmpty(expectedHash) || !File.Exists(filePath))
        {
            MediaDownloader.TryDelete(filePath);
            throw new DownloadException(Loc.T("core.media.err_hash"));
        }

        byte[] actualBytes;
        await using (var fs = File.OpenRead(filePath))
        {
            actualBytes = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct);
        }

        byte[] expectedBytes = Convert.FromHexString(expectedHash);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
        {
            MediaDownloader.TryDelete(filePath);
            throw new DownloadException(Loc.T("core.media.err_hash"));
        }
    }

    static Task VerifyYtDlpHashAsync(string filePath, CancellationToken ct) =>
        VerifyToolHashAsync(filePath, YtDlpSumsUrl, YtDlpAssetName, ct);

    static async Task VerifyToolHashAsync(string filePath, string sumsUrl, string assetName, CancellationToken ct)
    {
        string sums;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await GetFollowHttpsAsync(sumsUrl, cts.Token);
            sums = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Write($"{assetName} hash fetch failed: " + ex.Message);
            MediaDownloader.TryDelete(filePath);
            throw new DownloadException(Loc.T("core.media.err_hash"));
        }

        await VerifyFileAgainstSumsContentAsync(filePath, sums, assetName, ct);
    }

    public static async Task<string> UpdateYtDlpAsync(CancellationToken ct)
    {
        if (!File.Exists(YtDlp)) { await EnsureAsync(null, ct); return Loc.T("core.media.tool_downloaded"); }
        var (code, output, _) = await MediaDownloader.RunToolAsync(["-U"], ct);
        return code == 0
            ? (output.Contains("up to date", StringComparison.OrdinalIgnoreCase) ? Loc.T("core.media.tool_uptodate") : Loc.T("core.media.tool_updated"))
            : Loc.T("core.media.tool_update_failed");
    }

    public static async Task<string?> VersionAsync()
    {
        if (!File.Exists(YtDlp)) return null;
        try
        {
            var (code, output, _) = await MediaDownloader.RunToolAsync(["--version"], CancellationToken.None);
            return code == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }
}

/// <summary>Runs yt-dlp for YouTube and ~1800 other sites.</summary>
public static class MediaDownloader
{
    static ProcessStartInfo NewStartInfo(IEnumerable<string> args, string? targetUrl = null)
    {
        var psi = new ProcessStartInfo(MediaTools.YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.ToolsDir,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        if (ProxyForTarget(targetUrl) is { } proxy)
        {
            psi.Environment["HTTP_PROXY"] = proxy;
            psi.Environment["HTTPS_PROXY"] = proxy;
            psi.Environment["ALL_PROXY"] = proxy;
            if (proxy.Length == 0) psi.Environment["NO_PROXY"] = "*";
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static string? ProxyForTarget(string? targetUrl)
        => HttpDownloader.ProxyForTools(targetUrl);

    public static async Task<(int Code, string Out, string Err)> RunToolAsync(IEnumerable<string> args, CancellationToken ct, string? targetUrl = null)
    {
        using var p = Process.Start(NewStartInfo(args, targetUrl))!;
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { TryKill(p); throw; }
        return (p.ExitCode, await outTask, await errTask);
    }

    static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* process already exited */ }
    }

    static List<string> CommonArgs(string userAgent, string referrer, string? materializedCookieFile, string? targetUrl = null)
    {
        var args = new List<string> { "--no-warnings", "--no-color", "--encoding", "utf-8" };
        if (ProxyForTarget(targetUrl) is { } proxy)
        {
            args.Add("--proxy");
            args.Add(proxy);
        }
        if (!string.IsNullOrWhiteSpace(userAgent)) { args.Add("--user-agent"); args.Add(userAgent); }
        if (!string.IsNullOrWhiteSpace(referrer)) { args.Add("--referer"); args.Add(referrer); }
        if (!string.IsNullOrWhiteSpace(materializedCookieFile) && File.Exists(materializedCookieFile))
        {
            args.Add("--cookies");
            args.Add(materializedCookieFile);
        }
        return args;
    }

    internal static bool IsUnderCookiesDir(string? path)
        => !string.IsNullOrWhiteSpace(path) && IsUnderDir(path, AppPaths.CookiesDir);

    /// <summary>Decrypts a DPAPI-protected cookie file to a short-lived temp file only while yt-dlp runs.</summary>
    internal static string? MaterializeCookieFile(string? cookieFile)
    {
        if (string.IsNullOrWhiteSpace(cookieFile) || !IsUnderCookiesDir(cookieFile) || !File.Exists(cookieFile)) return null;
        try
        {
            var raw = File.ReadAllText(cookieFile, Encoding.UTF8).Trim();
            if (raw.StartsWith("# Netscape", StringComparison.Ordinal)) return null;
            var plain = Secret.Unprotect(raw);
            if (string.IsNullOrWhiteSpace(plain) || !plain.StartsWith("# Netscape", StringComparison.Ordinal)) return null;
            Directory.CreateDirectory(AppPaths.CookiesDir);
            var tmp = Path.Combine(AppPaths.CookiesDir, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, plain, new UTF8Encoding(false));
            return tmp;
        }
        catch { /* best-effort cookie decryption */ return null; }
    }

    public static async Task<MediaInfo> FetchInfoAsync(string url, string userAgent, string referrer, string cookieFile, CancellationToken ct)
    {
        var tempCookie = MaterializeCookieFile(cookieFile);
        int code;
        string output, err;
        try
        {
            var args = CommonArgs(userAgent, referrer, tempCookie, url);
            args.AddRange(["-J", "--flat-playlist", "--no-playlist", "--", url]);
            (code, output, err) = await RunToolAsync(args, ct, url);
        }
        finally
        {
            TryDelete(tempCookie);
        }
        if (code != 0 || string.IsNullOrWhiteSpace(output))
            throw new DownloadException(TranslateError(err));

        return ParseInfo(output, url);
    }

    public static MediaInfo ParseInfo(string json, string fallbackUrl = "")
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject root)
            throw new DownloadException(Loc.T("win.video.err_fetch_failed"));

        var rawThumb = Str(root, "thumbnail");
        var info = new MediaInfo
        {
            Title = Str(root, "title") ?? "video",
            Thumbnail = FileNames.IsHttpUrl(rawThumb) ? rawThumb : null,
            Duration = Num(root, "duration") ?? 0,
            Uploader = Str(root, "uploader") ?? Str(root, "channel") ?? "",
            WebpageUrl = Str(root, "webpage_url") ?? fallbackUrl,
            Extractor = Str(root, "extractor_key") ?? "",
        };

        info.Thumbnail ??= (root["thumbnails"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(t => Str(t, "url"))
            .Where(FileNames.IsHttpUrl)
            .LastOrDefault();

        ParseSubtitles(info, root["subtitles"] as JsonObject, isAuto: false);
        ParseSubtitles(info, root["automatic_captions"] as JsonObject, isAuto: true);
        ParseChapters(info, root["chapters"] as JsonArray);

        if (Str(root, "_type") == "playlist" && root["entries"] is JsonArray entries)
        {
            info.IsPlaylist = true;
            foreach (var e in entries.OfType<JsonObject>())
            {
                var entryUrl = Str(e, "url") ?? Str(e, "webpage_url");
                if (entryUrl == null) continue;
                if (!entryUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Str(e, "ie_key") == "Youtube")
                    entryUrl = "https://www.youtube.com/watch?v=" + entryUrl;
                if (!FileNames.IsHttpUrl(entryUrl)) continue;
                info.Entries.Add(new PlaylistEntry { Url = entryUrl, Title = Str(e, "title") ?? entryUrl, Duration = Num(e, "duration") ?? 0 });
            }
            BuildGenericQualities(info);
            return info;
        }

        BuildQualities(info, root);
        return info;
    }

    static void ParseSubtitles(MediaInfo info, JsonObject? subsObj, bool isAuto)
    {
        if (subsObj == null) return;
        foreach (var (lang, node) in subsObj)
        {
            if (!MediaArgs.IsValidLangCode(lang)) continue;
            if (info.Subtitles.Any(s => s.Lang.Equals(lang, StringComparison.OrdinalIgnoreCase) && s.IsAuto == isAuto))
                continue;
            string name = lang;
            if (node is JsonArray arr)
            {
                var firstName = arr.OfType<JsonObject>().Select(o => Str(o, "name")).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrWhiteSpace(firstName)) name = firstName!;
            }
            info.Subtitles.Add(new SubtitleTrack(lang, name, isAuto));
        }
    }

    static void ParseChapters(MediaInfo info, JsonArray? chapsArr)
    {
        if (chapsArr == null) return;
        foreach (var c in chapsArr.OfType<JsonObject>())
        {
            var title = Str(c, "title") ?? "";
            double start = Num(c, "start_time") ?? 0;
            double end = Num(c, "end_time") ?? 0;
            info.Chapters.Add(new MediaChapter(title, start, end));
        }
    }

    static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static double? Num(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    static void BuildGenericQualities(MediaInfo info)
    {
        info.Qualities.Add(new MediaQuality { Key = "best", Label = Loc.T("core.media.best_quality") });
        foreach (var h in new[] { 1080, 720, 480, 360 })
            info.Qualities.Add(new MediaQuality { Key = h.ToString(), Label = $"{h}p" });
        info.Qualities.Add(new MediaQuality { Key = "mp3", Label = Loc.T("core.media.audio_mp3"), AudioOnly = true });
    }

    static void BuildQualities(MediaInfo info, JsonObject root)
    {
        var formats = (root["formats"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        double duration = Num(root, "duration") ?? 0;
        // YouTube often omits filesize; estimate it from the bitrate (kbit/s) and the duration.
        long Size(JsonObject f)
        {
            var exact = Num(f, "filesize") ?? Num(f, "filesize_approx");
            if (exact is > 0) return (long)exact.Value;
            var kbps = Num(f, "tbr") ?? Num(f, "vbr") ?? Num(f, "abr");
            return kbps is > 0 && duration > 0 ? (long)(kbps.Value * 1000 / 8 * duration) : 0;
        }
        bool HasVideo(JsonObject f) => (Str(f, "vcodec") ?? "none") != "none";
        bool HasAudio(JsonObject f) => (Str(f, "acodec") ?? "none") != "none";

        var bestAudio = formats.Where(f => HasAudio(f) && !HasVideo(f)).OrderByDescending(f => Num(f, "abr") ?? Num(f, "tbr") ?? 0).FirstOrDefault();
        long audioSize = bestAudio != null ? Size(bestAudio) : 0;

        var heights = formats.Where(HasVideo).Select(f => (int)(Num(f, "height") ?? 0)).Where(h => h > 0).Distinct().OrderByDescending(h => h).ToList();

        if (heights.Count == 0)
        {
            long single = formats.Count > 0 ? formats.Max(Size) : (long)(Num(root, "filesize") ?? Num(root, "filesize_approx") ?? 0);
            info.Qualities.Add(new MediaQuality { Key = "best", Label = Loc.T("core.media.best_quality"), Size = single > 0 ? single : null });
        }
        else
        {
            bool first = true;
            foreach (var h in heights.Where(h => h >= 144))
            {
                // Prefer what the "-S res,vcodec:h264" sort will pick: h264 first, then the highest bitrate.
                var best = formats.Where(f => HasVideo(f) && (int)(Num(f, "height") ?? 0) == h)
                    .OrderByDescending(f => (Str(f, "vcodec") ?? "").StartsWith("avc1") ? 1 : 0)
                    .ThenByDescending(f => Num(f, "tbr") ?? 0).First();
                long size = Size(best) + (HasAudio(best) ? 0 : audioSize);
                var fps = Num(best, "fps") is double fv && fv > 30 ? $" {fv:0}fps" : "";
                var label = h switch { >= 2160 => "4K", >= 1440 => "2K", _ => "" };
                info.Qualities.Add(new MediaQuality
                {
                    Key = first ? "best" : h.ToString(CultureInfo.InvariantCulture),
                    Label = $"{h}p{fps}{(label.Length > 0 ? " · " + label : "")}{(first ? " · " + Loc.T("core.media.best_quality") : "")}",
                    Size = size > 0 ? size : null,
                });
                first = false;
            }
        }

        if (bestAudio != null || formats.Any(HasAudio))
        {
            info.Qualities.Add(new MediaQuality { Key = "mp3", Label = Loc.T("core.media.audio_mp3"), AudioOnly = true, Size = audioSize > 0 ? audioSize : null });
            info.Qualities.Add(new MediaQuality { Key = "m4a", Label = Loc.T("core.media.audio_m4a"), AudioOnly = true, Size = audioSize > 0 ? audioSize : null });
        }
    }

    static List<string> FormatArgs(string quality) => quality switch
    {
        "mp3" => ["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0"],
        "m4a" => ["-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a"],
        "best" => ["-f", "bv*+ba/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"],
        _ when int.TryParse(quality, out var h) =>
            ["-f", $"bv*[height<={h}]+ba/b[height<={h}]/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"],
        _ => ["-f", "b"],
    };

    static bool IsUnderDir(string childPath, string rootDir)
    {
        if (string.IsNullOrWhiteSpace(childPath) || string.IsNullOrWhiteSpace(rootDir)) return false;
        try
        {
            var fullChild = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullChild.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullChild.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool IsUnderConfiguredRoot(string fullSaveDir)
    {
        var opts = HttpDownloader.Options;
        if (IsUnderDir(fullSaveDir, opts.DownloadRoot)) return true;
        if (!string.IsNullOrWhiteSpace(opts.TempDir) && IsUnderDir(fullSaveDir, opts.TempDir)) return true;
        foreach (var cat in opts.CategoryDefs)
        {
            if (!string.IsNullOrWhiteSpace(cat.Folder) && IsUnderDir(fullSaveDir, cat.Folder)) return true;
            if (!string.IsNullOrWhiteSpace(cat.LastPath) && IsUnderDir(fullSaveDir, cat.LastPath)) return true;
        }
        return false;
    }

    internal static string EnsureSafeSaveDir(string saveDir)
    {
        if (string.IsNullOrWhiteSpace(saveDir))
            throw new DownloadException(Loc.T("core.http.err_invalid_save_dir"));
        var fullSaveDir = Path.GetFullPath(saveDir.Trim());
        bool isUnc = fullSaveDir.StartsWith(@"\\", StringComparison.Ordinal) || fullSaveDir.StartsWith("//", StringComparison.Ordinal)
            || (Uri.TryCreate(fullSaveDir, UriKind.Absolute, out var saveUri) && saveUri.IsUnc);
        if (isUnc && !IsUnderConfiguredRoot(fullSaveDir))
            throw new DownloadException(Loc.T("core.media.err_unc"));
        return fullSaveDir;
    }

    /// <summary>Downloads a media item, reporting progress on the item. Pausing = cancelling (yt-dlp resumes .part files).</summary>
    public static async Task RunAsync(DownloadItem item, SpeedLimiter limiter, CancellationToken ct)
    {
        item.Status = DownloadStatus.Connecting;
        item.LastTry = DateTime.Now;
        await MediaTools.EnsureAsync(new Progress<(string Text, double Percent)>(p => item.Stage = p.Text), ct);
        item.Stage = "";

        var fullSaveDir = EnsureSafeSaveDir(item.SaveDir);
        Directory.CreateDirectory(fullSaveDir);
        var escapedSaveDir = fullSaveDir.Replace("%", "%%");

        var tempCookie = MaterializeCookieFile(item.MediaCookieFile);
        try
        {
            long rateBytes = 0;
            if (limiter.Enabled && limiter.BytesPerSecond > 0) rateBytes = limiter.BytesPerSecond;
            if (item.SpeedLimitKB > 0)
            {
                long fileBytes = item.SpeedLimitKB * 1024L;
                rateBytes = rateBytes > 0 ? Math.Min(rateBytes, fileBytes) : fileBytes;
            }

            // YouTube: download each stream as parallel pieces (like IDM's multiple connections). If that fails for
            // any reason, the same attempt continues the standard way, so speed never costs reliability.
            bool youTube = IsYouTubeUrl(item.Url);
            for (int attempt = 0; ; attempt++)
            {
            bool parallelPieces = youTube && attempt == 0;
            var args = CommonArgs(item.UserAgent, item.Referrer, tempCookie, item.Url);
            if (item.MediaOptions != null)
            {
                var built = MediaArgs.Build(
                    item.MediaOptions with { Quality = item.MediaQuality },
                    outputTemplate: Path.Combine(escapedSaveDir, "%(title).150B.%(ext)s"),
                    ffmpegDir: AppPaths.ToolsDir,
                    rateLimitBytesPerSec: rateBytes);
                args.AddRange(built);
                if (item.MediaOptions.SplitChapters)
                {
                    args.Add("-P");
                    args.Add(fullSaveDir);
                }
                args.AddRange([
                    "--no-mtime", "--newline", "--continue",
                    "--concurrent-fragments", ParallelPieces.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--windows-filenames",
                    "--progress", "--no-simulate",
                    "--progress-template", "download:GDMP|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s",
                    "--print", "before_dl:GDMS|%(filename)s",
                    "--print", "post_process:GDMM",
                    "--print", "after_move:GDMF|%(filepath)s",
                ]);
            }
            else
            {
                args.AddRange(FormatArgs(item.MediaQuality));
                args.AddRange([
                    "--ffmpeg-location", AppPaths.ToolsDir,
                    "--no-playlist", "--no-mtime", "--newline", "--continue",
                    "--concurrent-fragments", ParallelPieces.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--windows-filenames",
                    "-o", Path.Combine(escapedSaveDir, "%(title).150B.%(ext)s"),
                    "--progress", "--no-simulate",
                    "--progress-template", "download:GDMP|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s",
                    "--print", "before_dl:GDMS|%(filename)s",
                    "--print", "post_process:GDMM",
                    "--print", "after_move:GDMF|%(filepath)s",
                ]);
                if (rateBytes > 0)
                {
                    args.Add("--limit-rate");
                    args.Add(Math.Max(1, rateBytes / 1024) + "K");
                }
            }
            if (parallelPieces) args.AddRange(["--extractor-args", "youtube:formats=dashy"]);
            args.Add("--");
            args.Add(item.Url);

            var preExistingFiles = new HashSet<string>(
                Directory.Exists(fullSaveDir)
                    ? Directory.EnumerateFiles(fullSaveDir, "*", SearchOption.AllDirectories).Select(Path.GetFullPath)
                    : [],
                StringComparer.OrdinalIgnoreCase);
            var producedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var p = new Process { StartInfo = NewStartInfo(args, item.Url), EnableRaisingEvents = true };
            var errors = new StringBuilder();
            string? finalPath = null;
            long completedStreams = 0, lastDownloaded = 0, lastTotal = 0;
            string lastFormat = "";
            int stream = 1;

            void HandleLine(string? line)
            {
                if (string.IsNullOrEmpty(line)) return;
                line = line.Trim();
                if (line.StartsWith("GDMP|"))
                {
                    var parts = line.Split('|');
                    long done = ParseLong(parts[1]);
                    if (done < 0) return;
                    long total = ParseLong(parts[2]);
                    if (total <= 0) total = ParseLong(parts[3]);
                    double speed = ParseDouble(parts[4]);
                    double eta = ParseDouble(parts[5]);
                    var fmt = parts.Length > 6 ? parts[6] : "";

                    // A new stream (e.g. audio after video) restarts the byte counter.
                    if ((fmt != lastFormat && lastFormat.Length > 0) || done + 1024 * 1024 < lastDownloaded)
                    {
                        completedStreams += Math.Max(lastTotal, lastDownloaded);
                        stream++;
                    }
                    lastFormat = fmt;
                    lastDownloaded = done;
                    lastTotal = total;

                    item.Status = DownloadStatus.Downloading;
                    item.ActiveConnections = 1;
                    item.Downloaded = completedStreams + done;
                    long estTotal = completedStreams + Math.Max(total, done);
                    if (item.TotalSize < estTotal) item.TotalSize = estTotal;
                    item.Speed = speed;
                    item.Eta = eta > 0 ? TimeSpan.FromSeconds(eta) : null;
                    item.Stage = stream > 1 ? Loc.T("core.media.part", stream) : "";
                }
                else if (line.StartsWith("GDMS|"))
                {
                    var name = Path.GetFileName(line[5..].Trim().Trim('"'));
                    if (!string.IsNullOrEmpty(name) && !item.NameLocked) item.FileName = FileNames.Sanitize(FinalName(name, item.MediaQuality));
                }
                else if (line.StartsWith("GDMM"))
                {
                    item.Status = DownloadStatus.Merging;
                }
                else if (line.StartsWith("GDMF|"))
                {
                    var candidate = line[5..].Trim().Trim('"');
                    if (IsUnderDir(candidate, fullSaveDir))
                    {
                        finalPath = candidate;
                        producedPaths.Add(candidate);
                    }
                }
            }
            p.OutputDataReceived += (_, e) => { lock (errors) HandleLine(e.Data); };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (errors)
                {
                    if (e.Data.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) errors.AppendLine(e.Data);
                }
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(ct);
                p.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                TryKill(p);
                throw;
            }

            if (p.ExitCode != 0)
            {
                if (parallelPieces && !ct.IsCancellationRequested)
                {
                    Log.Write("parallel YouTube download failed, continuing the standard way: " + errors.ToString().Trim());
                    continue;
                }
                throw new DownloadException(TranslateError(errors.ToString()));
            }

            if (finalPath != null && IsUnderDir(finalPath, fullSaveDir) && File.Exists(finalPath))
            {
                item.SaveDir = Path.GetDirectoryName(finalPath)!;
                item.FileName = Path.GetFileName(finalPath);
                item.TotalSize = new FileInfo(finalPath).Length;
                item.Downloaded = item.TotalSize;
                producedPaths.Add(finalPath);
            }
            if (File.Exists(item.FullPath) && IsUnderDir(item.FullPath, fullSaveDir))
                producedPaths.Add(item.FullPath);

            ApplyMarkOfTheWebToMediaOutputs(fullSaveDir, producedPaths, preExistingFiles, item.Url, item.Referrer);

            item.Stage = "";
            TryDelete(item.MediaCookieFile);
            item.MediaCookieFile = "";
            break;
            }
        }
        finally
        {
            TryDelete(tempCookie);
            PathRegistry.Release(item.Id);
        }
    }

    /// <summary>Pieces of a video stream downloaded at the same time (yt-dlp concurrent fragments).</summary>
    public const int ParallelPieces = 8;

    public static bool IsYouTubeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        return host is "youtube.com" or "youtu.be" or "music.youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal);
    }

    public static void ApplyMarkOfTheWebToMediaOutputs(
        string saveDir,
        IEnumerable<string>? reportedPaths,
        IReadOnlySet<string>? preExistingFiles,
        string? hostUrl,
        string? referrer = null)
    {
        if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir))
            return;

        string fullSaveDir = Path.GetFullPath(saveDir);
        var stamped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (reportedPaths != null)
        {
            foreach (var p in reportedPaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    var full = Path.GetFullPath(p);
                    if (IsUnderDir(full, fullSaveDir) && File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0 && stamped.Add(full))
                        MarkOfTheWeb.Write(full, hostUrl, referrer);
                }
                catch { /* best-effort MOTW stamp */ }
            }
        }

        foreach (var file in Directory.EnumerateFiles(fullSaveDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var full = Path.GetFullPath(file);
                if (stamped.Contains(full)) continue;
                if (preExistingFiles != null && preExistingFiles.Contains(full)) continue;
                var ext = Path.GetExtension(full);
                if (ext.Equals(".gdm", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".part", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".ytdl", StringComparison.OrdinalIgnoreCase))
                    continue;

                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
                if (stamped.Add(full))
                    MarkOfTheWeb.Write(full, hostUrl, referrer);
            }
            catch { /* best-effort MOTW stamp */ }
        }
    }

    static string FinalName(string name, string quality)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return quality switch
        {
            "mp3" => stem + ".mp3",
            "m4a" => stem + ".m4a",
            _ when quality == "best" || int.TryParse(quality, out _) => stem + ".mp4",
            _ => name,
        };
    }

    static long ParseLong(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : -1;
    static double ParseDouble(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>Deletes yt-dlp leftovers of an unfinished download: "title.f313.webm.part", "title.mp4.ytdl", ...</summary>
    public static void DeletePartials(DownloadItem item)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(item.FileName);
            if (stem.Length == 0 || !Directory.Exists(item.SaveDir)) return;
            foreach (var f in Directory.EnumerateFiles(item.SaveDir))
            {
                var name = Path.GetFileName(f);
                if (!name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(".part-Frag", StringComparison.OrdinalIgnoreCase) || name.Contains(".temp.", StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
            }
        }
        catch (IOException ex)
        {
            Log.Write("media partial cleanup failed: " + ex.Message);
        }
    }

    public static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length is > 0 and <= 4 * 1024 * 1024)
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        var zeros = new byte[(int)info.Length];
                        fs.Write(zeros, 0, zeros.Length);
                        fs.Flush(flushToDisk: true);
                    }
                }
                catch { /* best-effort zero-scrub before delete */ }
                File.Delete(path);
            }
        }
        catch (IOException) { /* best-effort temp/partial file delete */ }
        catch (UnauthorizedAccessException) { /* best-effort temp/partial file delete */ }
    }

    public static string TranslateError(string err)
    {
        var line = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? err.Trim();
        if (line.Length == 0) return Loc.T("core.media.err_generic");
        if (line.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return Loc.T("core.media.err_unsupported");
        if (line.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) || line.Contains("not a bot", StringComparison.OrdinalIgnoreCase))
            return Loc.T("core.media.err_signin");
        if (line.Contains("Private video", StringComparison.OrdinalIgnoreCase)) return Loc.T("core.media.err_private");
        if (line.Contains("age", StringComparison.OrdinalIgnoreCase) && line.Contains("restrict", StringComparison.OrdinalIgnoreCase)) return Loc.T("core.media.err_age");
        if (line.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)) return Loc.T("core.media.err_unavailable");
        if (line.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)) return Loc.T("core.media.err_403");
        if (line.Contains("Unable to download", StringComparison.OrdinalIgnoreCase) || line.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase))
            return Loc.T("core.media.err_network");
        if (line.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) return Loc.T("core.media.err_ffmpeg");
        return line.StartsWith("ERROR:") ? Loc.T("core.media.err_prefix", line[6..].Trim()) : line;
    }

    static string CleanCookieField(string? s)
        => string.IsNullOrEmpty(s) ? "" : s.Replace("\r", "").Replace("\n", "").Replace("\t", "").Trim();

    /// <summary>Writes cookies sent by the browser extension as a DPAPI-protected Netscape cookie file for yt-dlp.</summary>
    public static string WriteCookieFile(JsonArray? cookies)
    {
        if (cookies == null || cookies.Count == 0) return "";
        Directory.CreateDirectory(AppPaths.CookiesDir);
        var path = Path.Combine(AppPaths.CookiesDir, Guid.NewGuid().ToString("N") + ".txt");
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in cookies.OfType<JsonObject>())
        {
            var domain = CleanCookieField(c["domain"]?.GetValue<string>());
            var name = CleanCookieField(c["name"]?.GetValue<string>());
            if (domain.Length == 0 || name.Length == 0) continue;
            bool hostOnly = c["hostOnly"] is JsonValue h && h.TryGetValue<bool>(out var ho) && ho;
            if (!hostOnly && !domain.StartsWith('.')) domain = "." + domain;
            var path2 = CleanCookieField(c["path"]?.GetValue<string>());
            if (path2.Length == 0) path2 = "/";
            bool secure = c["secure"] is JsonValue s && s.TryGetValue<bool>(out var sv) && sv;
            long exp = c["expirationDate"] is JsonValue ev && ev.TryGetValue<double>(out var ed) ? (long)ed : 0;
            var value = CleanCookieField(c["value"]?.GetValue<string>());
            var prefix = c["httpOnly"] is JsonValue hv && hv.TryGetValue<bool>(out var hoy) && hoy ? "#HttpOnly_" : "";
            sb.Append($"{prefix}{domain}\t{(hostOnly ? "FALSE" : "TRUE")}\t{path2}\t{(secure ? "TRUE" : "FALSE")}\t{exp}\t{name}\t{value}\n");
        }
        File.WriteAllText(path, Secret.Protect(sb.ToString()), new UTF8Encoding(false));
        return path;
    }

    static readonly string[] MediaHosts =
    [
        "youtube.com", "youtu.be", "facebook.com", "fb.watch", "instagram.com", "tiktok.com", "twitter.com", "x.com",
        "vimeo.com", "dailymotion.com", "twitch.tv", "soundcloud.com", "reddit.com", "bilibili.com", "pinterest.com",
        "linkedin.com", "snapchat.com", "rumble.com", "kick.com", "ok.ru", "vk.com", "threads.net", "aparat.com",
    ];

    /// <summary>True for page links that need yt-dlp rather than a direct file download.</summary>
    public static bool LooksLikeMediaPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        if (u.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return true;
        return MediaHosts.Any(h => host == h || host.EndsWith("." + h));
    }
}
