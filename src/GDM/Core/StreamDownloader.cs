using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Golan.Gdm.Core;

/// <summary>Detects DRM-protected streams and platforms (Widevine, PlayReady, FairPlay, CENC/SAMPLE-AES) so Golan refuses them cleanly without ever attempting circumvention.</summary>
public static class DrmDetector
{
    static readonly string[] KnownDrmOnlyDomains =
    [
        "netflix.com",
        "disneyplus.com",
        "primevideo.com",
        "tv.apple.com",
        "hulu.com",
        "max.com",
        "peacocktv.com",
    ];

    static readonly string[] DrmSchemeTokens =
    [
        "edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", // Widevine
        "9a04f079-9840-4286-ab92-e65be0885f95", // PlayReady
        "94ce86fb-07ff-4f43-adb8-93d2fa968ca2", // FairPlay
        "f239e769-efa3-4850-9c16-a903c6932efb", // Adobe Primetime
        "1077efec-c0b2-4d02-ace3-3c1e52e2fb4b", // W3C ClearKey / EME CENC
        "widevine",
        "playready",
        "fairplay",
        "streamingkeydelivery",
    ];

    public static bool IsKnownDrmPlatform(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        foreach (var d in KnownDrmOnlyDomains)
        {
            if (host == d || host.EndsWith("." + d, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsHlsDrmProtected(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return false;
        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.OrdinalIgnoreCase))
                continue;

            var attrs = StreamDownloader.ParseHlsAttributes(line[(line.IndexOf(':') + 1)..]);
            attrs.TryGetValue("METHOD", out var method);
            if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(method) &&
                (method.Equals("SAMPLE-AES", StringComparison.OrdinalIgnoreCase)
                 || method.Equals("SAMPLE-AES-CTR", StringComparison.OrdinalIgnoreCase)
                 || method.Equals("SAMPLE-AES-CENC", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (attrs.TryGetValue("URI", out var keyUri) && keyUri.StartsWith("skd://", StringComparison.OrdinalIgnoreCase))
                return true;

            if (attrs.TryGetValue("KEYFORMAT", out var kf))
            {
                foreach (var token in DrmSchemeTokens)
                {
                    if (kf.Contains(token, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }

    public static bool IsDashDrmProtected(string? mpdXml)
    {
        if (string.IsNullOrWhiteSpace(mpdXml)) return false;
        try
        {
            using var sr = new StringReader(mpdXml);
            using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var doc = XDocument.Load(xr);
            foreach (var cp in doc.Descendants().Where(e => e.Name.LocalName.Equals("ContentProtection", StringComparison.OrdinalIgnoreCase)))
            {
                var scheme = cp.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("schemeIdUri", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                var val = cp.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                if (scheme.Contains("mp4protection", StringComparison.OrdinalIgnoreCase)
                    && val.Equals("none", StringComparison.OrdinalIgnoreCase))
                    continue;
                return true;
            }
            return false;
        }
        catch
        {
            /* fallback check when XML has minor syntax quirks */
            return mpdXml.Contains("<ContentProtection", StringComparison.OrdinalIgnoreCase)
                   || mpdXml.Contains(":ContentProtection", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsDrmErrorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("DRM", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Widevine", StringComparison.OrdinalIgnoreCase)
               || text.Contains("PlayReady", StringComparison.OrdinalIgnoreCase)
               || text.Contains("FairPlay", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Native HLS (.m3u8 master/variant, AES-128, byte-range, #EXT-X-MAP) and MPEG-DASH (.mpd multi-representation + merge) downloader.</summary>
public static class StreamDownloader
{
    public static bool IsHlsMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return false;
        var m = mime.Split(';')[0].Trim();
        return m.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
               || m.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase)
               || m.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase)
               || m.Equals("audio/x-mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDashMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return false;
        var m = mime.Split(';')[0].Trim();
        return m.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase)
               || m.Equals("video/vnd.mpeg.dash.mpd", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStreamMime(string? mime) => IsHlsMime(mime) || IsDashMime(mime);

    public static bool IsDirectStreamUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        var path = u.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return true;
        var q = u.Query;
        return Regex.IsMatch(q, @"(?:^|[?&][^=]*=[^&]*?)\.(?:m3u8|mpd)(?:[&#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static async Task<MediaInfo> InspectAsync(
        string url,
        string userAgent = "",
        string referrer = "",
        string? cookieFile = null,
        CancellationToken ct = default)
    {
        if (DrmDetector.IsKnownDrmPlatform(url))
            throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            throw new DownloadException(Loc.T("core.stream.err_manifest"));

        var dummyItem = new DownloadItem
        {
            Url = url.Trim(),
            UserAgent = userAgent,
            Referrer = referrer,
            MediaCookieFile = cookieFile ?? "",
        };

        var (finalUri, contentType, text) = await FetchTextAsync(dummyItem, uri, ct).ConfigureAwait(false);
        var baseTitle = Path.GetFileNameWithoutExtension(FileNames.FromUrl(finalUri.ToString()));
        if (string.IsNullOrWhiteSpace(baseTitle) || baseTitle.Equals("master", StringComparison.OrdinalIgnoreCase)
            || baseTitle.Equals("index", StringComparison.OrdinalIgnoreCase)
            || baseTitle.Equals("playlist", StringComparison.OrdinalIgnoreCase)
            || baseTitle.Equals("manifest", StringComparison.OrdinalIgnoreCase))
        {
            baseTitle = finalUri.Host;
        }

        if (LooksLikeDash(finalUri, contentType, text))
            return InspectDash(baseTitle, text);

        if (LooksLikeHls(finalUri, contentType, text))
            return InspectHls(baseTitle, text);

        throw new DownloadException(Loc.T("core.stream.err_manifest"));
    }

    public static async Task RunAsync(DownloadItem item, SpeedLimiter limiter, CancellationToken ct)
    {
        if (DrmDetector.IsKnownDrmPlatform(item.Url) || DrmDetector.IsKnownDrmPlatform(item.ParentPage))
            throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };

        var targetUrl = !string.IsNullOrWhiteSpace(item.ResolvedUrl) ? item.ResolvedUrl! : item.Url;
        if (!Uri.TryCreate(targetUrl.Trim(), UriKind.Absolute, out var startUri))
            throw new DownloadException(Loc.T("core.stream.err_manifest"));
        if (startUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            item.DownloadedOverPlainHttp = true;

        item.Status = DownloadStatus.Connecting;
        item.LastTry = DateTime.Now;
        MediaDownloader.EnsureSafeSaveDir(item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
            MediaDownloader.EnsureSafeSaveDir(DownloadItem.TempRoot);
        Directory.CreateDirectory(item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
            Directory.CreateDirectory(DownloadItem.TempRoot);

        var (manifestUri, contentType, manifestText) = await FetchTextAsync(item, startUri, ct).ConfigureAwait(false);

        if (LooksLikeDash(manifestUri, contentType, manifestText))
        {
            await RunDashAsync(item, manifestUri, manifestText, limiter, ct).ConfigureAwait(false);
            return;
        }

        if (LooksLikeHls(manifestUri, contentType, manifestText))
        {
            await RunHlsAsync(item, manifestUri, manifestText, limiter, ct).ConfigureAwait(false);
            return;
        }

        throw new DownloadException(Loc.T("core.stream.err_manifest"));
    }

    static bool LooksLikeHls(Uri uri, string? mime, string text)
        => IsHlsMime(mime)
           || text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
           || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    static bool LooksLikeDash(Uri uri, string? mime, string text)
    {
        if (IsDashMime(mime) || uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return true;
        var t = text.TrimStart();
        return t.StartsWith("<", StringComparison.Ordinal) && t.Contains("<MPD", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- HLS ----------------

    sealed record HlsVariant(Uri Uri, long Bandwidth, int Width, int Height, string? AudioGroup);
    sealed record HlsAudioMedia(string GroupId, string Name, bool Default, Uri Uri);
    sealed record HlsKeySpec(Uri Uri, byte[]? ExplicitIv);
    sealed record HlsSegment(Uri Uri, long Sequence, long? RangeOffset, long? RangeLength, HlsKeySpec? Key);

    static MediaInfo InspectHls(string title, string manifest)
    {
        if (DrmDetector.IsHlsDrmProtected(manifest))
            throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };

        var info = new MediaInfo { Title = title };
        bool isMaster = manifest.Contains("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase);
        if (!isMaster)
        {
            EnsureHlsVod(manifest);
            double dur = SumExtInfDuration(manifest);
            info.Duration = dur;
            info.Qualities.Add(new MediaQuality { Key = "best", Label = Loc.T("core.media.best_quality") });
            return info;
        }

        var dummyBase = new Uri("https://stream.local/master.m3u8");
        var (variants, audios) = ParseHlsMaster(dummyBase, manifest);
        info.Qualities.Add(new MediaQuality { Key = "best", Label = Loc.T("core.media.best_quality") });
        foreach (var v in variants.Where(v => v.Height > 0).GroupBy(v => v.Height).Select(g => g.OrderByDescending(x => x.Bandwidth).First()).OrderByDescending(v => v.Height))
        {
            info.Qualities.Add(new MediaQuality
            {
                Key = v.Height.ToString(CultureInfo.InvariantCulture),
                Label = $"{v.Height}p",
            });
        }
        if (audios.Count > 0)
        {
            info.Qualities.Add(new MediaQuality { Key = "m4a", Label = Loc.T("core.media.audio_m4a"), AudioOnly = true });
        }
        return info;
    }

    static async Task RunHlsAsync(DownloadItem item, Uri manifestUri, string manifestText, SpeedLimiter limiter, CancellationToken ct)
    {
        if (DrmDetector.IsHlsDrmProtected(manifestText))
            throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };

        string mediaPlaylist = manifestText;
        Uri mediaUri = manifestUri;
        Uri? separateAudioUri = null;
        bool audioOnly = item.MediaQuality is "mp3" or "m4a" or "audio";

        if (manifestText.Contains("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
        {
            var (variants, audios) = ParseHlsMaster(manifestUri, manifestText);
            if (audioOnly && audios.Count > 0)
            {
                var chosenAudio = audios.FirstOrDefault(a => a.Default) ?? audios[0];
                mediaUri = chosenAudio.Uri;
                (_, _, mediaPlaylist) = await FetchTextAsync(item, mediaUri, ct).ConfigureAwait(false);
            }
            else if (variants.Count > 0)
            {
                var chosen = PickHlsVariant(variants, item.MediaQuality);
                mediaUri = chosen.Uri;
                if (!audioOnly && !string.IsNullOrEmpty(chosen.AudioGroup))
                {
                    var groupMatches = audios.Where(a => string.Equals(a.GroupId, chosen.AudioGroup, StringComparison.OrdinalIgnoreCase)).ToList();
                    var audioRendition = groupMatches.FirstOrDefault(a => a.Default) ?? groupMatches.FirstOrDefault();
                    if (audioRendition != null && !audioRendition.Uri.Equals(mediaUri))
                        separateAudioUri = audioRendition.Uri;
                }
                (_, _, mediaPlaylist) = await FetchTextAsync(item, mediaUri, ct).ConfigureAwait(false);
            }
            else
            {
                throw new DownloadException(Loc.T("core.stream.err_manifest"));
            }
        }

        if (DrmDetector.IsHlsDrmProtected(mediaPlaylist))
            throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };
        if (!HasHlsEndList(mediaPlaylist))
            CleanupItemStreamPartials(item);
        EnsureHlsVod(mediaPlaylist);

        var (initSeg, segments) = ParseHlsMediaPlaylist(mediaUri, mediaPlaylist);
        if (segments.Count == 0)
            throw new DownloadException(Loc.T("core.stream.err_manifest"));

        HlsSegment? audioInit = null;
        List<HlsSegment> audioSegs = [];
        if (separateAudioUri != null)
        {
            var (_, _, audioText) = await FetchTextAsync(item, separateAudioUri, ct).ConfigureAwait(false);
            if (DrmDetector.IsHlsDrmProtected(audioText))
                throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };
            if (!HasHlsEndList(audioText))
                CleanupItemStreamPartials(item);
            EnsureHlsVod(audioText);
            (audioInit, audioSegs) = ParseHlsMediaPlaylist(separateAudioUri, audioText);
            if (audioSegs.Count == 0)
                separateAudioUri = null;
        }

        string outExt = audioOnly ? ".m4a" : (initSeg != null || separateAudioUri != null || MediaTools.FfmpegReady ? ".mp4" : ".ts");
        EnsureStreamFileName(item, manifestUri, outExt);
        PreparePathClaimForResume(item);

        var reserved = PathRegistry.Reserve(item.Id, item.FullPath);
        item.FileName = Path.GetFileName(reserved);
        string baseTemp = item.TempPath;
        string workVideoTmp = baseTemp + ".vpart";
        string workAudioTmp = baseTemp + ".apart";
        try
        {
            if (separateAudioUri != null)
            {
                DeleteTrackPartial(baseTemp);
                string videoVariant = BuildHlsVariantKey("video", mediaUri, initSeg);
                string audioVariant = BuildHlsVariantKey("audio", separateAudioUri, audioInit);
                var videoUrls = segments.Select(FormatHlsPieceUrl).ToList();
                var audioUrls = audioSegs.Select(FormatHlsPieceUrl).ToList();

                bool resumeVideo = CanResumeTrack(item, workVideoTmp, videoVariant, initSeg != null, videoUrls, out var videoState);
                if (!resumeVideo) DeleteTrackPartial(workVideoTmp);
                bool resumeAudio = CanResumeTrack(item, workAudioTmp, audioVariant, audioInit != null, audioUrls, out var audioState);
                if (!resumeAudio) DeleteTrackPartial(workAudioTmp);

                int totalAll = segments.Count + audioSegs.Count;
                int doneAll = (resumeVideo ? videoState!.LastPieceIndex + 1 : 0) + (resumeAudio ? audioState!.LastPieceIndex + 1 : 0);
                item.Downloaded = (resumeVideo ? videoState!.CountedBytes : 0) + (resumeAudio ? audioState!.CountedBytes : 0);
                item.TotalSize = doneAll > 0 && doneAll < totalAll ? Math.Max(item.Downloaded, (item.Downloaded / doneAll) * totalAll) : -1;
                if (doneAll > 0) item.Stage = Loc.T("core.stream.seg_progress", doneAll, totalAll);
                item.Status = DownloadStatus.Downloading;

                await DownloadHlsPlaylistToFileAsync(item, videoVariant, initSeg, segments, videoUrls, resumeVideo ? videoState : null, workVideoTmp, 0, totalAll, limiter, ct).ConfigureAwait(false);
                await DownloadHlsPlaylistToFileAsync(item, audioVariant, audioInit, audioSegs, audioUrls, resumeAudio ? audioState : null, workAudioTmp, segments.Count, totalAll, limiter, ct).ConfigureAwait(false);

                item.Status = DownloadStatus.Merging;
                try
                {
                    await MergeTracksAsync(workVideoTmp, workAudioTmp, baseTemp, ct).ConfigureAwait(false);
                }
                catch
                {
                    MediaDownloader.TryDelete(baseTemp);
                    throw;
                }
                DeleteSidecars(baseTemp);
            }
            else
            {
                DeleteTrackPartial(workVideoTmp);
                DeleteTrackPartial(workAudioTmp);
                string mainVariant = BuildHlsVariantKey("main", mediaUri, initSeg);
                var mainUrls = segments.Select(FormatHlsPieceUrl).ToList();
                bool resumeMain = CanResumeTrack(item, baseTemp, mainVariant, initSeg != null, mainUrls, out var mainState);
                if (!resumeMain) DeleteTrackPartial(baseTemp);

                int doneMain = resumeMain ? mainState!.LastPieceIndex + 1 : 0;
                item.Downloaded = resumeMain ? mainState!.CountedBytes : 0;
                item.TotalSize = doneMain > 0 && doneMain < segments.Count ? Math.Max(item.Downloaded, (item.Downloaded / doneMain) * segments.Count) : -1;
                if (doneMain > 0) item.Stage = Loc.T("core.stream.seg_progress", doneMain, segments.Count);
                item.Status = DownloadStatus.Downloading;

                await DownloadHlsPlaylistToFileAsync(item, mainVariant, initSeg, segments, mainUrls, resumeMain ? mainState : null, baseTemp, 0, segments.Count, limiter, ct).ConfigureAwait(false);

                if (initSeg == null
                    && Path.GetExtension(item.FileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    && MediaTools.FfmpegReady)
                {
                    item.Status = DownloadStatus.Merging;
                    string remuxTmp = baseTemp + ".remux.mp4";
                    try
                    {
                        if (await TryRemuxTsToMp4Async(baseTemp, remuxTmp, ct).ConfigureAwait(false))
                        {
                            MediaDownloader.TryDelete(baseTemp + ".state");
                            File.Move(remuxTmp, baseTemp, overwrite: true);
                        }
                    }
                    finally
                    {
                        MediaDownloader.TryDelete(remuxTmp); // also when cancelled half-way
                    }
                }
            }

            FinalizeStreamFile(item, reserved);
        }
        finally
        {
            PathRegistry.Release(item.Id);
        }
    }

    static HlsVariant PickHlsVariant(List<HlsVariant> variants, string requestedFormat)
    {
        if (requestedFormat is "mp3" or "m4a" or "audio")
            return variants.OrderBy(v => v.Bandwidth > 0 ? v.Bandwidth : long.MaxValue).First();

        if (int.TryParse(requestedFormat, NumberStyles.None, CultureInfo.InvariantCulture, out int maxHeight) && maxHeight > 0)
        {
            var within = variants.Where(v => v.Height > 0 && v.Height <= maxHeight)
                .OrderByDescending(v => v.Height)
                .ThenByDescending(v => v.Bandwidth)
                .FirstOrDefault();
            if (within != null) return within;

            var lowestKnown = variants.Where(v => v.Height > 0).OrderBy(v => v.Height).FirstOrDefault();
            if (lowestKnown != null) return lowestKnown;
        }

        return variants
            .OrderByDescending(v => v.Height)
            .ThenByDescending(v => v.Bandwidth)
            .First();
    }

    static (List<HlsVariant> Variants, List<HlsAudioMedia> Audios) ParseHlsMaster(Uri baseUri, string manifest)
    {
        var variants = new List<HlsVariant>();
        var audios = new List<HlsAudioMedia>();
        var lines = manifest.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                var attrs = ParseHlsAttributes(line["#EXT-X-MEDIA:".Length..]);
                if (attrs.TryGetValue("TYPE", out var mediaType)
                    && mediaType.Equals("AUDIO", StringComparison.OrdinalIgnoreCase)
                    && attrs.TryGetValue("GROUP-ID", out var gid)
                    && attrs.TryGetValue("URI", out var uriStr)
                    && Uri.TryCreate(baseUri, uriStr, out var audioUri))
                {
                    bool isDef = attrs.TryGetValue("DEFAULT", out var def) && def.Equals("YES", StringComparison.OrdinalIgnoreCase);
                    attrs.TryGetValue("NAME", out var name);
                    audios.Add(new HlsAudioMedia(gid, name ?? gid, isDef, audioUri));
                }
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                var attrs = ParseHlsAttributes(line["#EXT-X-STREAM-INF:".Length..]);
                long bw = 0;
                if (attrs.TryGetValue("AVERAGE-BANDWIDTH", out var avgBw)) long.TryParse(avgBw, NumberStyles.None, CultureInfo.InvariantCulture, out bw);
                if (bw <= 0 && attrs.TryGetValue("BANDWIDTH", out var peakBw)) long.TryParse(peakBw, NumberStyles.None, CultureInfo.InvariantCulture, out bw);

                int w = 0, h = 0;
                if (attrs.TryGetValue("RESOLUTION", out var res))
                {
                    var parts = res.Split('x', 'X');
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out w);
                        int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out h);
                    }
                }
                attrs.TryGetValue("AUDIO", out var audioGroup);

                for (int j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j].Trim();
                    if (next.Length == 0) continue;
                    if (next.StartsWith('#')) continue;
                    i = j;
                    if (Uri.TryCreate(baseUri, next, out var varUri))
                        variants.Add(new HlsVariant(varUri, bw, w, h, audioGroup));
                    break;
                }
            }
        }
        return (variants, audios);
    }

    static bool HasHlsEndList(string playlist)
    {
        foreach (var raw in playlist.Split('\n'))
        {
            if (raw.Trim().Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static void EnsureHlsVod(string playlist)
    {
        if (!playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException(Loc.T("core.stream.err_manifest"));

        if (!HasHlsEndList(playlist))
            throw new DownloadException(Loc.T("core.stream.err_live")) { NonFallback = true };
    }

    static double SumExtInfDuration(string playlist)
    {
        double sum = 0;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line["#EXTINF:".Length..];
            int comma = rest.IndexOf(',');
            var numStr = (comma >= 0 ? rest[..comma] : rest).Trim();
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0)
                sum += d;
        }
        return sum;
    }

    static (HlsSegment? Init, List<HlsSegment> Segments) ParseHlsMediaPlaylist(Uri baseUri, string playlist)
    {
        HlsSegment? init = null;
        var segments = new List<HlsSegment>();
        long sequence = 0;
        HlsKeySpec? currentKey = null;
        long? pendingRangeLen = null;
        long? pendingRangeOff = null;
        var lastRangeEndByUri = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq))
                    sequence = seq;
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                var attrs = ParseHlsAttributes(line["#EXT-X-KEY:".Length..]);
                attrs.TryGetValue("METHOD", out var method);
                if (string.IsNullOrEmpty(method) || method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    currentKey = null;
                }
                else if (method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
                {
                    if (!attrs.TryGetValue("URI", out var keyUriStr) || !Uri.TryCreate(baseUri, keyUriStr, out var keyUri))
                        throw new DownloadException(Loc.T("core.stream.err_key"));
                    byte[]? iv = null;
                    if (attrs.TryGetValue("IV", out var ivHex))
                        iv = ParseHexIv(ivHex);
                    currentKey = new HlsKeySpec(keyUri, iv);
                }
                else
                {
                    throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var attrs = ParseHlsAttributes(line["#EXT-X-MAP:".Length..]);
                if (attrs.TryGetValue("URI", out var mapUriStr) && Uri.TryCreate(baseUri, mapUriStr, out var mapUri))
                {
                    long? mOff = null, mLen = null;
                    if (attrs.TryGetValue("BYTERANGE", out var br))
                        (mLen, mOff) = ParseByteRangeSpec(br, 0);
                    init = new HlsSegment(mapUri, 0, mOff, mLen, currentKey);
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                var spec = line["#EXT-X-BYTERANGE:".Length..].Trim();
                int at = spec.IndexOf('@');
                if (at >= 0)
                {
                    if (long.TryParse(spec[..at], NumberStyles.None, CultureInfo.InvariantCulture, out long l)
                        && long.TryParse(spec[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long o))
                    {
                        pendingRangeLen = l;
                        pendingRangeOff = o;
                    }
                }
                else if (long.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out long l))
                {
                    pendingRangeLen = l;
                    pendingRangeOff = null;
                }
                continue;
            }

            if (line.StartsWith('#')) continue;

            if (Uri.TryCreate(baseUri, line, out var segUri))
            {
                long? off = pendingRangeOff;
                long? len = pendingRangeLen;
                if (len.HasValue)
                {
                    if (!off.HasValue)
                        off = lastRangeEndByUri.TryGetValue(segUri.AbsoluteUri, out long prevEnd) ? prevEnd : 0;
                    lastRangeEndByUri[segUri.AbsoluteUri] = off.Value + len.Value;
                }
                pendingRangeLen = null;
                pendingRangeOff = null;
                segments.Add(new HlsSegment(segUri, sequence++, off, len, currentKey));
            }
        }

        return (init, segments);
    }

    static (long? Length, long? Offset) ParseByteRangeSpec(string spec, long defaultOffset)
    {
        int at = spec.IndexOf('@');
        if (at >= 0
            && long.TryParse(spec[..at], NumberStyles.None, CultureInfo.InvariantCulture, out long len)
            && long.TryParse(spec[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long off))
        {
            return (len, off);
        }
        if (long.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out long onlyLen))
            return (onlyLen, defaultOffset);
        return (null, null);
    }

    static byte[] ParseHexIv(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (s.Length > 32)
            throw new DownloadException(Loc.T("core.stream.err_decrypt"));
        s = s.PadLeft(32, '0');
        try { return Convert.FromHexString(s); }
        catch (FormatException) { throw new DownloadException(Loc.T("core.stream.err_decrypt")); }
    }

    static byte[] BuildSequenceIv(long sequence)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8, 8), sequence);
        return iv;
    }

    public static byte[] DecryptAes128Cbc(byte[] cipher, byte[] key, byte[] iv)
    {
        if (key == null || iv == null || cipher == null
            || key.Length != 16 || iv.Length != 16 || cipher.Length == 0 || (cipher.Length % 16) != 0)
            throw new DownloadException(Loc.T("core.stream.err_decrypt"));
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        try
        {
            return aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            throw new DownloadException(Loc.T("core.stream.err_decrypt"));
        }
    }

    static async Task DownloadHlsPlaylistToFileAsync(
        DownloadItem item,
        string variantKey,
        HlsSegment? initSeg,
        List<HlsSegment> segments,
        List<string> pieceUrls,
        StreamResumeState? resumeState,
        string outputPath,
        int completedOffset,
        int totalAllTracks,
        SpeedLimiter limiter,
        CancellationToken ct)
    {
        var keyCache = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);

        async Task<byte[]> GetKeyBytesAsync(Uri keyUri)
        {
            if (keyCache.TryGetValue(keyUri.AbsoluteUri, out var cached))
                return cached;
            var (_, _, rawKey) = await FetchBytesAsync(item, keyUri, null, null, limiter, trackProgress: false, ct).ConfigureAwait(false);
            if (rawKey.Length != 16)
                throw new DownloadException(Loc.T("core.stream.err_key"));
            keyCache[keyUri.AbsoluteUri] = rawKey;
            return rawKey;
        }

        async Task<(PiecePayload Payload, byte[]? Key, byte[]? Iv)> FetchSegmentPayloadAsync(HlsSegment seg)
        {
            byte[]? key = null;
            byte[]? iv = null;
            if (seg.Key != null)
            {
                key = await GetKeyBytesAsync(seg.Key.Uri).ConfigureAwait(false);
                iv = seg.Key.ExplicitIv ?? BuildSequenceIv(seg.Sequence);
            }
            var (_, _, payload) = await FetchPieceAsync(item, seg.Uri, seg.RangeOffset, seg.RangeLength, limiter, trackProgress: true, allowDiskSpill: true, ct).ConfigureAwait(false);
            return (payload, key, iv);
        }

        int total = segments.Count;
        int startIndex = 0;
        long trackCounted = 0;
        long committedDownloaded = item.Downloaded;

        FileStream outFs;
        if (resumeState != null)
        {
            outFs = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            if (outFs.Length != resumeState.BytesWritten)
                outFs.SetLength(resumeState.BytesWritten);
            outFs.Seek(resumeState.BytesWritten, SeekOrigin.Begin);
            startIndex = resumeState.LastPieceIndex + 1;
            trackCounted = resumeState.CountedBytes;
        }
        else
        {
            outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        }

        try
        {
            await using (outFs)
            {
                if (resumeState == null && initSeg != null)
                {
                    var (initPayload, initKey, initIv) = await FetchSegmentPayloadAsync(initSeg).ConfigureAwait(false);
                    try
                    {
                        await initPayload.WriteToAsync(outFs, initKey, initIv, ct).ConfigureAwait(false);
                        await outFs.FlushAsync(ct).ConfigureAwait(false);
                        trackCounted += initPayload.CountedBytes;
                        committedDownloaded += initPayload.CountedBytes;
                        SaveSidecar(outputPath, new StreamResumeState
                        {
                            ItemId = item.Id,
                            Variant = variantKey,
                            InitWritten = true,
                            TotalPieces = total,
                            LastPieceIndex = -1,
                            NextPieceUrl = total > 0 ? pieceUrls[0] : "",
                            LastPieceUrl = "",
                            BytesWritten = outFs.Position,
                            CountedBytes = trackCounted,
                        });
                    }
                    finally
                    {
                        initPayload.Dispose();
                    }
                }

                int concurrency = Math.Clamp(item.MaxConnections, 1, 4);
                int completed = completedOffset + startIndex;
                int effectiveTotal = Math.Max(1, totalAllTracks);

                for (int batchStart = startIndex; batchStart < total; batchStart += concurrency)
                {
                    int count = Math.Min(concurrency, total - batchStart);
                    var tasks = new Task<(PiecePayload Payload, byte[]? Key, byte[]? Iv)>[count];
                    item.ActiveConnections = count;
                    try
                    {
                        for (int k = 0; k < count; k++)
                        {
                            var seg = segments[batchStart + k];
                            tasks[k] = FetchSegmentPayloadAsync(seg);
                        }

                        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                        for (int k = 0; k < count; k++)
                        {
                            int segIdx = batchStart + k;
                            var (payload, key, iv) = results[k];
                            try
                            {
                                await payload.WriteToAsync(outFs, key, iv, ct).ConfigureAwait(false);
                                await outFs.FlushAsync(ct).ConfigureAwait(false);
                                trackCounted += payload.CountedBytes;
                                committedDownloaded += payload.CountedBytes;
                                SaveSidecar(outputPath, new StreamResumeState
                                {
                                    ItemId = item.Id,
                                    Variant = variantKey,
                                    InitWritten = initSeg != null,
                                    TotalPieces = total,
                                    LastPieceIndex = segIdx,
                                    NextPieceUrl = segIdx + 1 < total ? pieceUrls[segIdx + 1] : "",
                                    LastPieceUrl = pieceUrls[segIdx],
                                    BytesWritten = outFs.Position,
                                    CountedBytes = trackCounted,
                                });
                            }
                            finally
                            {
                                payload.Dispose();
                            }

                            completed++;
                            item.Stage = Loc.T("core.stream.seg_progress", completed, effectiveTotal);
                            if (completed > 0 && completed < effectiveTotal)
                            {
                                long avg = item.Downloaded / completed;
                                item.TotalSize = Math.Max(item.Downloaded, avg * effectiveTotal);
                            }
                        }
                    }
                    finally
                    {
                        foreach (var t in tasks)
                        {
                            if (t is { IsCompletedSuccessfully: true })
                                t.Result.Payload.Dispose();
                            else if (t != null)
                                _ = t.ContinueWith(done => { if (done.IsCompletedSuccessfully) done.Result.Payload.Dispose(); }, TaskScheduler.Default);
                        }
                    }
                }
            }
        }
        catch
        {
            long uncommitted = item.Downloaded - committedDownloaded;
            if (uncommitted != 0)
                item.AddDownloaded(-uncommitted);
            throw;
        }
        finally
        {
            item.ActiveConnections = 0;
        }
    }

    internal static Dictionary<string, string> ParseHlsAttributes(string attrLine)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < attrLine.Length)
        {
            while (i < attrLine.Length && (attrLine[i] == ',' || char.IsWhiteSpace(attrLine[i]))) i++;
            if (i >= attrLine.Length) break;

            int eq = attrLine.IndexOf('=', i);
            if (eq < 0) break;
            string key = attrLine[i..eq].Trim();
            i = eq + 1;
            if (i < attrLine.Length && attrLine[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < attrLine.Length && attrLine[i] != '"')
                {
                    if (attrLine[i] == '\\' && i + 1 < attrLine.Length) i++;
                    sb.Append(attrLine[i++]);
                }
                if (i < attrLine.Length && attrLine[i] == '"') i++;
                dict[key] = sb.ToString();
            }
            else
            {
                int comma = attrLine.IndexOf(',', i);
                string val = comma < 0 ? attrLine[i..].Trim() : attrLine[i..comma].Trim();
                i = comma < 0 ? attrLine.Length : comma + 1;
                dict[key] = val;
            }
        }
        return dict;
    }

    // ---------------- DASH ----------------

    internal sealed record DashSegmentRef(Uri Uri, long? RangeOffset, long? RangeLength)
    {
        public DashSegmentRef? PeriodInit { get; init; }
    }
    internal sealed record DashRep(string Id, string ContentKind, string MimeType, long Bandwidth, int Width, int Height, DashSegmentRef? Init, List<DashSegmentRef> Segments);
    sealed record DashPeriod(double Duration, List<DashRep> Videos, List<DashRep> Audios);

    static MediaInfo InspectDash(string title, string mpdXml)
    {
        if (DrmDetector.IsDashDrmProtected(mpdXml))
            throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };

        var (duration, videoReps, audioReps) = ParseDashMpd(new Uri("https://stream.local/manifest.mpd"), mpdXml);
        var info = new MediaInfo { Title = title, Duration = duration };
        info.Qualities.Add(new MediaQuality { Key = "best", Label = Loc.T("core.media.best_quality") });
        foreach (var v in videoReps.Where(r => r.Height > 0).GroupBy(r => r.Height).Select(g => g.OrderByDescending(x => x.Bandwidth).First()).OrderByDescending(r => r.Height))
        {
            info.Qualities.Add(new MediaQuality
            {
                Key = v.Height.ToString(CultureInfo.InvariantCulture),
                Label = $"{v.Height}p",
            });
        }
        if (audioReps.Count > 0)
        {
            info.Qualities.Add(new MediaQuality { Key = "m4a", Label = Loc.T("core.media.audio_m4a"), AudioOnly = true });
        }
        return info;
    }

    static async Task RunDashAsync(DownloadItem item, Uri mpdUri, string mpdXml, SpeedLimiter limiter, CancellationToken ct)
    {
        if (DrmDetector.IsDashDrmProtected(mpdXml))
            throw new DownloadException(Loc.T("core.media.err_drm")) { NonFallback = true };

        List<DashRep> videoReps;
        List<DashRep> audioReps;
        try
        {
            (_, videoReps, audioReps) = ParseDashMpd(mpdUri, mpdXml);
        }
        catch (DownloadException ex) when (ex.Message == Loc.T("core.stream.err_live"))
        {
            CleanupItemStreamPartials(item);
            throw;
        }
        bool audioOnly = item.MediaQuality is "mp3" or "m4a" or "audio";

        DashRep? chosenVideo = null;
        DashRep? chosenAudio = audioReps.OrderByDescending(r => r.Bandwidth).FirstOrDefault();

        if (!audioOnly && videoReps.Count > 0)
            chosenVideo = PickDashVideo(videoReps, item.MediaQuality);

        if (chosenVideo == null && chosenAudio == null)
            throw new DownloadException(Loc.T("core.stream.err_manifest"));

        string outExt = chosenVideo == null ? ".m4a"
            : chosenVideo.MimeType.Contains("webm", StringComparison.OrdinalIgnoreCase) && chosenAudio == null ? ".webm"
            : ".mp4";

        EnsureStreamFileName(item, mpdUri, outExt);
        PreparePathClaimForResume(item);
        var reserved = PathRegistry.Reserve(item.Id, item.FullPath);
        item.FileName = Path.GetFileName(reserved);

        string baseTemp = item.TempPath;
        string videoTmp = baseTemp + ".vpart";
        string audioTmp = baseTemp + ".apart";
        try
        {
            int totalSegs = (chosenVideo?.Segments.Count ?? 0) + (chosenAudio?.Segments.Count ?? 0);

            async Task DownloadRepAsync(DashRep rep, string variantKey, List<string> pieceUrls, StreamResumeState? resumeState, string destPath, int completedOffset)
            {
                int startIndex = 0;
                long trackCounted = 0;
                long committedDownloaded = item.Downloaded;

                FileStream fs;
                if (resumeState != null)
                {
                    fs = new FileStream(destPath, FileMode.Open, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                    if (fs.Length != resumeState.BytesWritten)
                        fs.SetLength(resumeState.BytesWritten);
                    fs.Seek(resumeState.BytesWritten, SeekOrigin.Begin);
                    startIndex = resumeState.LastPieceIndex + 1;
                    trackCounted = resumeState.CountedBytes;
                }
                else
                {
                    fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                }

                try
                {
                    await using (fs)
                    {
                        if (resumeState == null && rep.Init != null)
                        {
                            var (_, _, initPayload) = await FetchPieceAsync(item, rep.Init.Uri, rep.Init.RangeOffset, rep.Init.RangeLength, limiter, trackProgress: true, allowDiskSpill: true, ct).ConfigureAwait(false);
                            try
                            {
                                await initPayload.WriteToAsync(fs, null, null, ct).ConfigureAwait(false);
                                await fs.FlushAsync(ct).ConfigureAwait(false);
                                trackCounted += initPayload.CountedBytes;
                                committedDownloaded += initPayload.CountedBytes;
                                SaveSidecar(destPath, new StreamResumeState
                                {
                                    ItemId = item.Id,
                                    Variant = variantKey,
                                    InitWritten = true,
                                    TotalPieces = rep.Segments.Count,
                                    LastPieceIndex = -1,
                                    NextPieceUrl = rep.Segments.Count > 0 ? pieceUrls[0] : "",
                                    LastPieceUrl = "",
                                    BytesWritten = fs.Position,
                                    CountedBytes = trackCounted,
                                });
                            }
                            finally
                            {
                                initPayload.Dispose();
                            }
                        }

                        item.ActiveConnections = 1;
                        int effectiveTotal = Math.Max(1, totalSegs);
                        for (int idx = startIndex; idx < rep.Segments.Count; idx++)
                        {
                            var seg = rep.Segments[idx];
                            if (seg.PeriodInit != null)
                            {
                                var (_, _, pInitPayload) = await FetchPieceAsync(item, seg.PeriodInit.Uri, seg.PeriodInit.RangeOffset, seg.PeriodInit.RangeLength, limiter, trackProgress: true, allowDiskSpill: true, ct).ConfigureAwait(false);
                                try
                                {
                                    await pInitPayload.WriteToAsync(fs, null, null, ct).ConfigureAwait(false);
                                    trackCounted += pInitPayload.CountedBytes;
                                    committedDownloaded += pInitPayload.CountedBytes;
                                }
                                finally
                                {
                                    pInitPayload.Dispose();
                                }
                            }
                            var (_, _, segPayload) = await FetchPieceAsync(item, seg.Uri, seg.RangeOffset, seg.RangeLength, limiter, trackProgress: true, allowDiskSpill: true, ct).ConfigureAwait(false);
                            try
                            {
                                await segPayload.WriteToAsync(fs, null, null, ct).ConfigureAwait(false);
                                await fs.FlushAsync(ct).ConfigureAwait(false);
                                trackCounted += segPayload.CountedBytes;
                                committedDownloaded += segPayload.CountedBytes;
                                SaveSidecar(destPath, new StreamResumeState
                                {
                                    ItemId = item.Id,
                                    Variant = variantKey,
                                    InitWritten = rep.Init != null,
                                    TotalPieces = rep.Segments.Count,
                                    LastPieceIndex = idx,
                                    NextPieceUrl = idx + 1 < rep.Segments.Count ? pieceUrls[idx + 1] : "",
                                    LastPieceUrl = pieceUrls[idx],
                                    BytesWritten = fs.Position,
                                    CountedBytes = trackCounted,
                                });
                            }
                            finally
                            {
                                segPayload.Dispose();
                            }

                            int doneNow = completedOffset + idx + 1;
                            item.Stage = Loc.T("core.stream.seg_progress", doneNow, effectiveTotal);
                            if (doneNow > 0 && doneNow < effectiveTotal)
                            {
                                long avg = item.Downloaded / doneNow;
                                item.TotalSize = Math.Max(item.Downloaded, avg * effectiveTotal);
                            }
                        }
                    }
                }
                catch
                {
                    long uncommitted = item.Downloaded - committedDownloaded;
                    if (uncommitted != 0)
                        item.AddDownloaded(-uncommitted);
                    throw;
                }
                finally
                {
                    item.ActiveConnections = 0;
                }
            }

            bool hasMultiPeriodDistinctInit = MediaTools.FfmpegReady && (
                (chosenVideo?.Segments.Any(s => s.PeriodInit != null) ?? false) ||
                (chosenAudio?.Segments.Any(s => s.PeriodInit != null) ?? false));

            if (hasMultiPeriodDistinctInit)
            {
                DeleteTrackPartial(baseTemp);
                DeleteTrackPartial(videoTmp);
                DeleteTrackPartial(audioTmp);
                item.Downloaded = 0;
                item.TotalSize = -1;
                item.Status = DownloadStatus.Downloading;

                var vGroups = chosenVideo != null ? SplitRepByPeriod(chosenVideo) : [];
                var aGroups = chosenAudio != null ? SplitRepByPeriod(chosenAudio) : [];
                int groupCount = Math.Max(vGroups.Count, aGroups.Count);
                var periodOutputs = new List<string>(groupCount);
                string concatListPath = baseTemp + ".concat.txt";
                try
                {
                    int completedSegs = 0;
                    for (int pIdx = 0; pIdx < groupCount; pIdx++)
                    {
                        var vPartRep = pIdx < vGroups.Count ? vGroups[pIdx] : null;
                        var aPartRep = pIdx < aGroups.Count ? aGroups[pIdx] : null;
                        if (vPartRep == null && aPartRep == null) continue;

                        string pOut = $"{baseTemp}.p{pIdx}{outExt}";
                        periodOutputs.Add(pOut);
                        if (vPartRep != null && aPartRep != null)
                        {
                            string pV = $"{baseTemp}.p{pIdx}.vpart";
                            string pA = $"{baseTemp}.p{pIdx}.apart";
                            try
                            {
                                var vUrls = vPartRep.Segments.Select(FormatDashPieceUrl).ToList();
                                var aUrls = aPartRep.Segments.Select(FormatDashPieceUrl).ToList();
                                await DownloadRepAsync(vPartRep, $"p{pIdx}v", vUrls, null, pV, completedSegs).ConfigureAwait(false);
                                completedSegs += vPartRep.Segments.Count;
                                await DownloadRepAsync(aPartRep, $"p{pIdx}a", aUrls, null, pA, completedSegs).ConfigureAwait(false);
                                completedSegs += aPartRep.Segments.Count;
                                await MergeTracksAsync(pV, pA, pOut, ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                DeleteTrackPartial(pV);
                                DeleteTrackPartial(pA);
                            }
                        }
                        else
                        {
                            var onlyRep = (vPartRep ?? aPartRep)!;
                            var urls = onlyRep.Segments.Select(FormatDashPieceUrl).ToList();
                            await DownloadRepAsync(onlyRep, $"p{pIdx}s", urls, null, pOut, completedSegs).ConfigureAwait(false);
                            completedSegs += onlyRep.Segments.Count;
                            MediaDownloader.TryDelete(pOut + ".state");
                        }
                    }

                    item.Status = DownloadStatus.Merging;
                    if (periodOutputs.Count == 1)
                    {
                        File.Move(periodOutputs[0], baseTemp, overwrite: true);
                    }
                    else
                    {
                        await ConcatenatePeriodFilesAsync(periodOutputs, concatListPath, baseTemp, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    MediaDownloader.TryDelete(concatListPath);
                    foreach (var pFile in periodOutputs)
                        DeleteTrackPartial(pFile);
                }
                DeleteSidecars(baseTemp);
            }
            else if (chosenVideo != null && chosenAudio != null)
            {
                DeleteTrackPartial(baseTemp);
                string videoVariant = BuildDashVariantKey(mpdUri, chosenVideo);
                string audioVariant = BuildDashVariantKey(mpdUri, chosenAudio);
                var videoUrls = chosenVideo.Segments.Select(FormatDashPieceUrl).ToList();
                var audioUrls = chosenAudio.Segments.Select(FormatDashPieceUrl).ToList();

                bool resumeVideo = CanResumeTrack(item, videoTmp, videoVariant, chosenVideo.Init != null, videoUrls, out var videoState);
                if (!resumeVideo) DeleteTrackPartial(videoTmp);
                bool resumeAudio = CanResumeTrack(item, audioTmp, audioVariant, chosenAudio.Init != null, audioUrls, out var audioState);
                if (!resumeAudio) DeleteTrackPartial(audioTmp);

                int doneAll = (resumeVideo ? videoState!.LastPieceIndex + 1 : 0) + (resumeAudio ? audioState!.LastPieceIndex + 1 : 0);
                item.Downloaded = (resumeVideo ? videoState!.CountedBytes : 0) + (resumeAudio ? audioState!.CountedBytes : 0);
                item.TotalSize = doneAll > 0 && doneAll < totalSegs ? Math.Max(item.Downloaded, (item.Downloaded / doneAll) * totalSegs) : -1;
                if (doneAll > 0) item.Stage = Loc.T("core.stream.seg_progress", doneAll, Math.Max(1, totalSegs));
                item.Status = DownloadStatus.Downloading;

                await DownloadRepAsync(chosenVideo, videoVariant, videoUrls, resumeVideo ? videoState : null, videoTmp, 0).ConfigureAwait(false);
                await DownloadRepAsync(chosenAudio, audioVariant, audioUrls, resumeAudio ? audioState : null, audioTmp, chosenVideo.Segments.Count).ConfigureAwait(false);

                item.Status = DownloadStatus.Merging;
                try
                {
                    await MergeTracksAsync(videoTmp, audioTmp, baseTemp, ct).ConfigureAwait(false);
                }
                catch
                {
                    MediaDownloader.TryDelete(baseTemp);
                    throw;
                }
                DeleteSidecars(baseTemp);
            }
            else
            {
                DeleteTrackPartial(videoTmp);
                DeleteTrackPartial(audioTmp);
                var singleRep = chosenVideo ?? chosenAudio!;
                string repVariant = BuildDashVariantKey(mpdUri, singleRep);
                var repUrls = singleRep.Segments.Select(FormatDashPieceUrl).ToList();
                bool resumeSingle = CanResumeTrack(item, baseTemp, repVariant, singleRep.Init != null, repUrls, out var singleState);
                if (!resumeSingle) DeleteTrackPartial(baseTemp);

                int doneSingle = resumeSingle ? singleState!.LastPieceIndex + 1 : 0;
                item.Downloaded = resumeSingle ? singleState!.CountedBytes : 0;
                item.TotalSize = doneSingle > 0 && doneSingle < totalSegs ? Math.Max(item.Downloaded, (item.Downloaded / doneSingle) * totalSegs) : -1;
                if (doneSingle > 0) item.Stage = Loc.T("core.stream.seg_progress", doneSingle, Math.Max(1, totalSegs));
                item.Status = DownloadStatus.Downloading;

                await DownloadRepAsync(singleRep, repVariant, repUrls, resumeSingle ? singleState : null, baseTemp, 0).ConfigureAwait(false);
            }

            FinalizeStreamFile(item, reserved);
        }
        finally
        {
            PathRegistry.Release(item.Id);
        }
    }

    static DashRep PickDashVideo(List<DashRep> reps, string requestedFormat)
    {
        if (int.TryParse(requestedFormat, NumberStyles.None, CultureInfo.InvariantCulture, out int maxHeight) && maxHeight > 0)
        {
            var within = reps.Where(r => r.Height > 0 && r.Height <= maxHeight)
                .OrderByDescending(r => r.Height)
                .ThenByDescending(r => r.Bandwidth)
                .FirstOrDefault();
            if (within != null) return within;

            var lowest = reps.Where(r => r.Height > 0).OrderBy(r => r.Height).FirstOrDefault();
            if (lowest != null) return lowest;
        }
        return reps.OrderByDescending(r => r.Height).ThenByDescending(r => r.Bandwidth).First();
    }

    static List<DashRep> SplitRepByPeriod(DashRep rep)
    {
        var groups = new List<DashRep>();
        DashSegmentRef? curInit = rep.Init;
        var curSegs = new List<DashSegmentRef>();
        foreach (var seg in rep.Segments)
        {
            if (seg.PeriodInit != null && curSegs.Count > 0)
            {
                groups.Add(new DashRep(rep.Id, rep.ContentKind, rep.MimeType, rep.Bandwidth, rep.Width, rep.Height, curInit, curSegs));
                curInit = seg.PeriodInit;
                curSegs = [seg with { PeriodInit = null }];
            }
            else
            {
                if (seg.PeriodInit != null) curInit = seg.PeriodInit;
                curSegs.Add(seg with { PeriodInit = null });
            }
        }
        if (curSegs.Count > 0)
            groups.Add(new DashRep(rep.Id, rep.ContentKind, rep.MimeType, rep.Bandwidth, rep.Width, rep.Height, curInit, curSegs));
        return groups;
    }

    static async Task ConcatenatePeriodFilesAsync(List<string> periodFiles, string concatListPath, string outputPath, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var f in periodFiles)
        {
            var escaped = Path.GetFullPath(f).Replace("'", "'\\''");
            sb.Append("file '").Append(escaped).AppendLine("'");
        }
        await File.WriteAllTextAsync(concatListPath, sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo(MediaTools.Ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("concat");
        psi.ArgumentList.Add("-safe");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(concatListPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || outputPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) || outputPath.EndsWith(".gdm", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
        }
        psi.ArgumentList.Add(outputPath);

        int? exitCode = await RunToolProcessAsync(psi, ct).ConfigureAwait(false);
        if (exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            return;

        throw new DownloadException(Loc.T("core.media.err_ffmpeg"));
    }

    static bool SameDashSegmentRef(DashSegmentRef? a, DashSegmentRef? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Uri == b.Uri && a.RangeOffset == b.RangeOffset && a.RangeLength == b.RangeLength;
    }

    internal static (double Duration, List<DashRep> Video, List<DashRep> Audio) ParseDashMpd(Uri mpdUri, string mpdXml)
    {
        XDocument doc;
        try
        {
            using var sr = new StringReader(mpdXml);
            using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            doc = XDocument.Load(xr);
        }
        catch (XmlException)
        {
            throw new DownloadException(Loc.T("core.stream.err_manifest"));
        }

        var root = doc.Root;
        if (root == null || !root.Name.LocalName.Equals("MPD", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException(Loc.T("core.stream.err_manifest"));

        var mpdType = Attr(root, "type");
        if (string.Equals(mpdType, "dynamic", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException(Loc.T("core.stream.err_live")) { NonFallback = true };

        double mpdDuration = ParseIsoDurationSeconds(Attr(root, "mediaPresentationDuration"));
        var rootBase = ResolveBaseUrl(mpdUri, ChildText(root, "BaseURL"));

        var periodElements = Children(root, "Period").ToList();
        if (periodElements.Count == 0)
            throw new DownloadException(Loc.T("core.stream.err_manifest"));

        var explicitStarts = new double?[periodElements.Count];
        var explicitDurations = new double?[periodElements.Count];
        for (int i = 0; i < periodElements.Count; i++)
        {
            if (Attr(periodElements[i], "start") is { Length: > 0 } sAttr)
                explicitStarts[i] = ParseIsoDurationSeconds(sAttr);
            if (Attr(periodElements[i], "duration") is { Length: > 0 } dAttr)
            {
                double d = ParseIsoDurationSeconds(dAttr);
                if (d > 0) explicitDurations[i] = d;
            }
        }

        var parsedPeriods = new List<DashPeriod>(periodElements.Count);
        double elapsed = 0;

        for (int i = 0; i < periodElements.Count; i++)
        {
            var period = periodElements[i];
            double curStart = explicitStarts[i] ?? elapsed;
            double periodDuration;
            if (explicitDurations[i] is double expDur)
            {
                periodDuration = expDur;
            }
            else if (i + 1 < periodElements.Count && explicitStarts[i + 1] is double nextStart && nextStart > curStart)
            {
                periodDuration = nextStart - curStart;
            }
            else if (i == periodElements.Count - 1 && mpdDuration > curStart)
            {
                periodDuration = mpdDuration - curStart;
            }
            else if (periodElements.Count == 1)
            {
                periodDuration = mpdDuration;
            }
            else
            {
                periodDuration = 0;
            }

            elapsed = curStart + Math.Max(0, periodDuration);

            var periodBase = ResolveBaseUrl(rootBase, ChildText(period, "BaseURL"));
            var periodTemplate = Children(period, "SegmentTemplate").FirstOrDefault();
            var periodSegList = Children(period, "SegmentList").FirstOrDefault();

            var pVideos = new List<DashRep>();
            var pAudios = new List<DashRep>();

            foreach (var adapt in Children(period, "AdaptationSet"))
            {
                var adaptBase = ResolveBaseUrl(periodBase, ChildText(adapt, "BaseURL"));
                var adaptMime = Attr(adapt, "mimeType") ?? "";
                var adaptContent = Attr(adapt, "contentType") ?? "";
                var adaptTemplate = Children(adapt, "SegmentTemplate").FirstOrDefault() ?? periodTemplate;
                var adaptSegList = Children(adapt, "SegmentList").FirstOrDefault() ?? periodSegList;

                foreach (var rep in Children(adapt, "Representation"))
                {
                    var repBase = ResolveBaseUrl(adaptBase, ChildText(rep, "BaseURL"));
                    var repId = Attr(rep, "id") ?? "0";
                    var mime = Attr(rep, "mimeType") ?? adaptMime;
                    long.TryParse(Attr(rep, "bandwidth"), NumberStyles.None, CultureInfo.InvariantCulture, out long bw);
                    int.TryParse(Attr(rep, "width") ?? Attr(adapt, "width"), NumberStyles.None, CultureInfo.InvariantCulture, out int w);
                    int.TryParse(Attr(rep, "height") ?? Attr(adapt, "height"), NumberStyles.None, CultureInfo.InvariantCulture, out int h);

                    string kind = DetermineDashTrackKind(adaptContent, mime, w, h);
                    if (kind is not ("video" or "audio")) continue;

                    var repTemplate = Children(rep, "SegmentTemplate").FirstOrDefault() ?? adaptTemplate;
                    var repSegList = Children(rep, "SegmentList").FirstOrDefault() ?? adaptSegList;

                    DashSegmentRef? init = null;
                    var segs = new List<DashSegmentRef>();

                    if (repTemplate != null)
                    {
                        (init, segs) = ExpandSegmentTemplate(repBase, adaptTemplate, repTemplate, repId, bw, periodDuration);
                    }
                    else if (repSegList != null)
                    {
                        (init, segs) = ExpandSegmentList(repBase, repSegList);
                    }
                    else if (ChildText(rep, "BaseURL") != null || ChildText(adapt, "BaseURL") != null || ChildText(period, "BaseURL") != null)
                    {
                        segs.Add(new DashSegmentRef(repBase, null, null));
                    }

                    if (segs.Count == 0) continue;

                    var dashRep = new DashRep(repId, kind, mime, bw, w, h, init, segs);
                    if (kind == "video") pVideos.Add(dashRep);
                    else pAudios.Add(dashRep);
                }
            }

            parsedPeriods.Add(new DashPeriod(periodDuration, pVideos, pAudios));
        }

        double effectiveDuration = elapsed > 0 ? elapsed : mpdDuration;
        if (parsedPeriods.Count == 1)
            return (effectiveDuration, parsedPeriods[0].Videos, parsedPeriods[0].Audios);

        var stitchedVideos = StitchDashTrackAcrossPeriods(parsedPeriods, isVideo: true);
        var stitchedAudios = StitchDashTrackAcrossPeriods(parsedPeriods, isVideo: false);
        return (effectiveDuration, stitchedVideos, stitchedAudios);
    }

    static List<DashRep> StitchDashTrackAcrossPeriods(List<DashPeriod> periods, bool isVideo)
    {
        var anchorList = periods.Select(p => isVideo ? p.Videos : p.Audios).FirstOrDefault(l => l.Count > 0);
        if (anchorList == null || anchorList.Count == 0)
            return [];

        var templates = new List<DashRep>(anchorList);
        if (isVideo)
        {
            var seenHeights = new HashSet<int>(templates.Where(t => t.Height > 0).Select(t => t.Height));
            foreach (var p in periods)
            {
                foreach (var v in p.Videos)
                {
                    if (v.Height > 0 && seenHeights.Add(v.Height))
                        templates.Add(v);
                }
            }
        }

        var result = new List<DashRep>(templates.Count);
        foreach (var tpl in templates)
        {
            DashSegmentRef? firstInit = null;
            DashSegmentRef? prevInit = null;
            bool sawFirstPeriod = false;
            var combinedSegs = new List<DashSegmentRef>();

            foreach (var period in periods)
            {
                var candidates = isVideo ? period.Videos : period.Audios;
                if (candidates.Count == 0) continue;

                var chosen = MatchDashRepForPeriod(candidates, tpl, isVideo);
                if (chosen.Segments.Count == 0) continue;

                if (combinedSegs.Count + chosen.Segments.Count > MaxDashSegments)
                    throw new DownloadException(Loc.T("core.stream.err_manifest"));

                if (!sawFirstPeriod)
                {
                    firstInit = chosen.Init;
                    prevInit = chosen.Init;
                    sawFirstPeriod = true;
                    combinedSegs.AddRange(chosen.Segments);
                }
                else
                {
                    bool initChanged = chosen.Init != null && !SameDashSegmentRef(chosen.Init, prevInit);
                    for (int k = 0; k < chosen.Segments.Count; k++)
                    {
                        var s = chosen.Segments[k];
                        if (k == 0 && initChanged)
                            s = s with { PeriodInit = chosen.Init };
                        combinedSegs.Add(s);
                    }
                    if (chosen.Init != null)
                        prevInit = chosen.Init;
                }
            }

            if (combinedSegs.Count > 0)
                result.Add(new DashRep(tpl.Id, tpl.ContentKind, tpl.MimeType, tpl.Bandwidth, tpl.Width, tpl.Height, firstInit, combinedSegs));
        }

        return result;
    }

    static DashRep MatchDashRepForPeriod(List<DashRep> candidates, DashRep target, bool isVideo)
    {
        if (isVideo)
        {
            var exact = candidates.FirstOrDefault(c =>
                string.Equals(c.Id, target.Id, StringComparison.Ordinal) &&
                (target.Height <= 0 || c.Height == target.Height));
            if (exact != null) return exact;

            if (target.Height > 0)
            {
                var sameHeight = candidates
                    .Where(c => c.Height == target.Height)
                    .OrderBy(c => Math.Abs(c.Bandwidth - target.Bandwidth))
                    .FirstOrDefault();
                if (sameHeight != null) return sameHeight;

                return PickDashVideo(candidates, target.Height.ToString(CultureInfo.InvariantCulture));
            }

            return candidates.OrderByDescending(c => c.Height).ThenByDescending(c => c.Bandwidth).First();
        }
        else
        {
            var exact = candidates.FirstOrDefault(c => string.Equals(c.Id, target.Id, StringComparison.Ordinal));
            if (exact != null) return exact;

            return candidates
                .OrderBy(c => target.Bandwidth > 0 ? Math.Abs(c.Bandwidth - target.Bandwidth) : 0)
                .ThenByDescending(c => c.Bandwidth)
                .First();
        }
    }

    static string DetermineDashTrackKind(string contentType, string mime, int w, int h)
    {
        if (contentType.Equals("video", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || h > 0 || w > 0)
            return "video";
        if (contentType.Equals("audio", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return "audio";
        return "other";
    }

    static (DashSegmentRef? Init, List<DashSegmentRef> Segments) ExpandSegmentTemplate(
        Uri baseUri,
        XElement? adaptTemplate,
        XElement repTemplate,
        string repId,
        long bandwidth,
        double durationSec)
    {
        string? initTpl = Attr(repTemplate, "initialization") ?? (adaptTemplate != null ? Attr(adaptTemplate, "initialization") : null);
        var initElem = Children(repTemplate, "Initialization").FirstOrDefault()
                       ?? (adaptTemplate != null ? Children(adaptTemplate, "Initialization").FirstOrDefault() : null);
        if (initTpl == null && initElem != null)
            initTpl = Attr(initElem, "sourceURL");

        string? mediaTpl = Attr(repTemplate, "media") ?? (adaptTemplate != null ? Attr(adaptTemplate, "media") : null);
        long timescale = ParseLongAttr(repTemplate, "timescale") ?? (adaptTemplate != null ? ParseLongAttr(adaptTemplate, "timescale") : null) ?? 1;
        long startNumber = ParseLongAttr(repTemplate, "startNumber") ?? (adaptTemplate != null ? ParseLongAttr(adaptTemplate, "startNumber") : null) ?? 1;
        long? segDuration = ParseLongAttr(repTemplate, "duration") ?? (adaptTemplate != null ? ParseLongAttr(adaptTemplate, "duration") : null);

        DashSegmentRef? init = null;
        if (!string.IsNullOrEmpty(initTpl))
        {
            var expanded = ExpandDashVars(initTpl, repId, bandwidth, startNumber, 0);
            if (Uri.TryCreate(baseUri, expanded, out var initUri))
                init = new DashSegmentRef(initUri, null, null);
        }

        var segments = new List<DashSegmentRef>();
        if (string.IsNullOrEmpty(mediaTpl))
            return (init, segments);

        var timeline = Children(repTemplate, "SegmentTimeline").FirstOrDefault()
                       ?? (adaptTemplate != null ? Children(adaptTemplate, "SegmentTimeline").FirstOrDefault() : null);

        if (timeline != null)
        {
            long currentTime = 0;
            long number = startNumber;
            foreach (var s in Children(timeline, "S"))
            {
                if (ParseLongAttr(s, "t") is long explicitT)
                    currentTime = explicitT;
                long d = ParseLongAttr(s, "d") ?? 0;
                long r = ParseLongAttr(s, "r") ?? 0;
                if (d <= 0) continue;

                long count = r >= 0 ? r + 1 : (durationSec > 0 ? Math.Max(1, (long)Math.Ceiling((durationSec * timescale - currentTime) / d)) : 1);
                count = Math.Min(count, 20000);
                for (long k = 0; k < count; k++)
                {
                    // Each <S> is capped above, but a hostile manifest can carry any number of them.
                    if (segments.Count >= MaxDashSegments)
                        throw new DownloadException(Loc.T("core.stream.err_manifest"));
                    var rel = ExpandDashVars(mediaTpl, repId, bandwidth, number++, currentTime);
                    if (Uri.TryCreate(baseUri, rel, out var segUri))
                        segments.Add(new DashSegmentRef(segUri, null, null));
                    currentTime += d;
                }
            }
        }
        else if (segDuration is > 0 && durationSec > 0)
        {
            double segSec = segDuration.Value / (double)Math.Max(1, timescale);
            int total = (int)Math.Clamp(Math.Ceiling(durationSec / segSec), 1, 20000);
            long time = 0;
            for (int i = 0; i < total; i++)
            {
                var rel = ExpandDashVars(mediaTpl, repId, bandwidth, startNumber + i, time);
                if (Uri.TryCreate(baseUri, rel, out var segUri))
                    segments.Add(new DashSegmentRef(segUri, null, null));
                time += segDuration.Value;
            }
        }

        return (init, segments);
    }

    static (DashSegmentRef? Init, List<DashSegmentRef> Segments) ExpandSegmentList(Uri baseUri, XElement segList)
    {
        DashSegmentRef? init = null;
        var initEl = Children(segList, "Initialization").FirstOrDefault();
        if (initEl != null)
        {
            var src = Attr(initEl, "sourceURL");
            var (off, len) = ParseHttpRangeAttr(Attr(initEl, "range"));
            var u = !string.IsNullOrEmpty(src) && Uri.TryCreate(baseUri, src, out var iu) ? iu : baseUri;
            init = new DashSegmentRef(u, off, len);
        }

        var segs = new List<DashSegmentRef>();
        foreach (var segUrl in Children(segList, "SegmentURL"))
        {
            var media = Attr(segUrl, "media");
            var (off, len) = ParseHttpRangeAttr(Attr(segUrl, "mediaRange"));
            var u = !string.IsNullOrEmpty(media) && Uri.TryCreate(baseUri, media, out var su) ? su : baseUri;
            segs.Add(new DashSegmentRef(u, off, len));
        }
        return (init, segs);
    }

    static (long? Offset, long? Length) ParseHttpRangeAttr(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return (null, null);
        var parts = range.Split('-');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long end)
            && end >= start)
        {
            return (start, end - start + 1);
        }
        return (null, null);
    }

    internal static string ExpandDashVars(string template, string repId, long bandwidth, long number, long time)
    {
        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] != '$')
            {
                sb.Append(template[i++]);
                continue;
            }
            int end = template.IndexOf('$', i + 1);
            if (end < 0)
            {
                sb.Append(template[i++]);
                continue;
            }
            if (end == i + 1)
            {
                sb.Append('$');
                i = end + 1;
                continue;
            }
            string expr = template[(i + 1)..end];
            i = end + 1;

            int pct = expr.IndexOf('%');
            string varName = pct >= 0 ? expr[..pct] : expr;
            int padWidth = 0;
            if (pct >= 0)
            {
                var fmt = expr[(pct + 1)..].TrimEnd('d', 'i', 'u', 'x', 'X');
                int.TryParse(fmt, NumberStyles.None, CultureInfo.InvariantCulture, out padWidth);
                padWidth = Math.Clamp(padWidth, 0, 20); // a 64-bit number never needs more; "%0999999999d" must not allocate gigabytes
            }

            if (varName.Equals("RepresentationID", StringComparison.Ordinal))
            {
                sb.Append(repId);
            }
            else if (varName.Equals("Bandwidth", StringComparison.Ordinal))
            {
                sb.Append(FormatDashInt(bandwidth, padWidth));
            }
            else if (varName.Equals("Number", StringComparison.Ordinal))
            {
                sb.Append(FormatDashInt(number, padWidth));
            }
            else if (varName.Equals("Time", StringComparison.Ordinal))
            {
                sb.Append(FormatDashInt(time, padWidth));
            }
            else
            {
                sb.Append('$').Append(expr).Append('$');
            }
        }
        return sb.ToString();
    }

    static string FormatDashInt(long val, int width)
        => width > 0
            ? val.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')
            : val.ToString(CultureInfo.InvariantCulture);

    static Uri ResolveBaseUrl(Uri currentBase, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return currentBase;
        return Uri.TryCreate(currentBase, rel.Trim(), out var u) ? u : currentBase;
    }

    static double ParseIsoDurationSeconds(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        try { return XmlConvert.ToTimeSpan(iso.Trim()).TotalSeconds; }
        catch (FormatException) { return 0; }
    }

    static IEnumerable<XElement> Children(XElement el, string localName)
        => el.Elements().Where(c => c.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    static string? ChildText(XElement el, string localName)
        => Children(el, localName).FirstOrDefault()?.Value;

    static string? Attr(XElement el, string localName)
        => el.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    static long? ParseLongAttr(XElement el, string localName)
        => long.TryParse(Attr(el, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

    // ---------------- Track merging (ffmpeg + native fMP4 fallback) ----------------

    static async Task<bool> TryRemuxTsToMp4Async(string tsPath, string outMp4Path, CancellationToken ct)
    {
        if (!MediaTools.FfmpegReady) return false;
        try
        {
            var psi = new ProcessStartInfo(MediaTools.Ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tsPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-bsf:a");
            psi.ArgumentList.Add("aac_adtstoasc");
            psi.ArgumentList.Add(outMp4Path);

            int? exitCode = await RunToolProcessAsync(psi, ct).ConfigureAwait(false);
            return exitCode == 0 && File.Exists(outMp4Path) && new FileInfo(outMp4Path).Length > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* keep valid MPEG-TS payload if remux fails */ return false; }
    }

    /// <summary>
    /// Runs a media tool (ffmpeg) to completion. Redirected stderr is drained while it runs, so a chatty tool
    /// can never fill the pipe and hang; on cancellation the tool (and its children) is killed so it does not
    /// keep running on the download's files. Returns the exit code, or null when the tool could not start.
    /// </summary>
    internal static async Task<int?> RunToolProcessAsync(ProcessStartInfo psi, CancellationToken ct, Action<int>? onStarted = null)
    {
        using var proc = Process.Start(psi);
        if (proc == null) return null;
        onStarted?.Invoke(proc.Id);

        var errTask = psi.RedirectStandardError ? proc.StandardError.ReadToEndAsync(CancellationToken.None) : Task.FromResult("");
        var outTask = psi.RedirectStandardOutput ? proc.StandardOutput.ReadToEndAsync(CancellationToken.None) : Task.FromResult("");
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already exited */ }
            throw;
        }
        await Task.WhenAll(errTask, outTask).ConfigureAwait(false);
        return proc.ExitCode;
    }

    internal static async Task MergeTracksAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct)
    {
        if (MediaTools.FfmpegReady)
        {
            var psi = new ProcessStartInfo(MediaTools.Ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(videoPath);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(audioPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(outputPath);

            int? exitCode = await RunToolProcessAsync(psi, ct).ConfigureAwait(false);
            if (exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return;
        }

        // Native fragmented-MP4 (ISO-BMFF) track merger when ffmpeg is not installed.
        if (IsoBmffMerger.TryMergeFragmentedMp4(videoPath, audioPath, outputPath))
            return;

        // Neither merger could join the tracks. Gluing the two files end to end would be saved as "completed" and
        // never play, so the download fails with a clear message instead.
        throw new DownloadException(Loc.T("core.media.err_ffmpeg"));
    }

    internal static void EnsureStreamFileName(DownloadItem item, Uri uri, string desiredExt)
    {
        if (item.NameLocked && !string.IsNullOrWhiteSpace(item.FileName))
        {
            var locked = FileNames.Sanitize(Path.GetFileName(item.FileName));
            // What gets saved is the video, never the playlist: a locked "film.m3u8" keeps its name but takes the
            // real container's extension, or it would be saved as a video nobody can open.
            var lockedExt = Path.GetExtension(locked);
            if (lockedExt.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) || lockedExt.Equals(".mpd", StringComparison.OrdinalIgnoreCase))
                locked = FileNames.Sanitize(Path.GetFileNameWithoutExtension(locked) + desiredExt);
            item.FileName = locked;
            return;
        }

        string stem = !string.IsNullOrWhiteSpace(item.FileName)
            ? Path.GetFileNameWithoutExtension(item.FileName)
            : Path.GetFileNameWithoutExtension(FileNames.FromUrl(uri.ToString()));

        var curExt = Path.GetExtension(item.FileName);
        if (curExt.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
            || curExt.Equals(".mpd", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(curExt))
        {
            if (string.IsNullOrWhiteSpace(stem)
                || stem.Equals("master", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("playlist", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("manifest", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("index", StringComparison.OrdinalIgnoreCase))
            {
                stem = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : uri.Host;
            }
            item.FileName = FileNames.Sanitize(stem + desiredExt);
        }
        else
        {
            item.FileName = FileNames.Sanitize(Path.GetFileName(item.FileName));
        }
    }

    static void FinalizeStreamFile(DownloadItem item, string reservedPath)
    {
        item.Status = DownloadStatus.Merging;
        var tempPath = item.TempPath;
        DeleteSidecars(tempPath);
        MarkOfTheWeb.Write(tempPath, item.Url, item.Referrer);
        var finalTarget = File.Exists(reservedPath) ? PathRegistry.Reserve(item.Id, item.FullPath) : reservedPath;
        File.Move(tempPath, finalTarget, overwrite: true);
        item.FileName = Path.GetFileName(finalTarget);
        item.ActiveTempPath = "";
        DeleteSidecars(item.FullPath + ".gdm");
        var fi = new FileInfo(item.FullPath);
        if (fi.Exists)
        {
            item.Downloaded = fi.Length;
            item.TotalSize = fi.Length;
        }
        item.Stage = "";
        MarkOfTheWeb.Write(item.FullPath, item.Url, item.Referrer);
    }

    // ---------------- Stream resume sidecar helpers ----------------

    sealed class StreamResumeState
    {
        public Guid ItemId { get; set; }
        public string Variant { get; set; } = "";
        public bool InitWritten { get; set; }
        public int TotalPieces { get; set; }
        public int LastPieceIndex { get; set; } = -1;
        public string NextPieceUrl { get; set; } = "";
        public string LastPieceUrl { get; set; } = "";
        public long BytesWritten { get; set; }
        public long CountedBytes { get; set; }
    }

    static readonly string[] SidecarSuffixes = [".state", ".vpart", ".vpart.state", ".apart", ".apart.state", ".remux.mp4", ".concat.txt"];

    public static void DeleteSidecars(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath)) return;
        foreach (var suffix in SidecarSuffixes)
            MediaDownloader.TryDelete(tempPath + suffix);
        DeleteOrphanPieceFiles(tempPath);
    }

    /// <summary>A large piece spills to "&lt;temp&gt;.piece.&lt;guid&gt;.tmp"; a killed process leaves it behind (up to 8 GB).</summary>
    static void DeleteOrphanPieceFiles(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(tempPath);
            var name = Path.GetFileName(tempPath);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, name + ".piece.*.tmp"))
                MediaDownloader.TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort: a leftover piece file is only wasted disk space.
        }
    }

    public static void MoveSidecars(string? oldTempPath, string? newTempPath)
    {
        if (string.IsNullOrWhiteSpace(oldTempPath) || string.IsNullOrWhiteSpace(newTempPath)
            || string.Equals(oldTempPath, newTempPath, StringComparison.OrdinalIgnoreCase))
            return;

        string[] movable = [".state", ".vpart", ".vpart.state", ".apart", ".apart.state"];
        foreach (var suffix in movable)
        {
            var src = oldTempPath + suffix;
            var dst = newTempPath + suffix;
            if (!File.Exists(src)) continue;
            try { File.Move(src, dst, overwrite: true); }
            catch (IOException) { /* best-effort move of stream resume sidecar */ }
        }
    }

    static void DeleteTrackPartial(string trackPath)
    {
        MediaDownloader.TryDelete(trackPath);
        MediaDownloader.TryDelete(trackPath + ".state");
    }

    static void CleanupItemStreamPartials(DownloadItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.SaveDir) && !string.IsNullOrWhiteSpace(item.FileName)
            && !PathRegistry.IsClaimedByOther(item.Id, item.FullPath))
        {
            var t = item.TempPath;
            MediaDownloader.TryDelete(t);
            DeleteSidecars(t);
            MediaDownloader.TryDelete(item.FullPath + ".gdm");
            DeleteSidecars(item.FullPath + ".gdm");
        }
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
        {
            var rootTmp = Path.Combine(DownloadItem.TempRoot, $"{item.Id:N}.gdm");
            MediaDownloader.TryDelete(rootTmp);
            DeleteSidecars(rootTmp);
        }
    }

    static void PreparePathClaimForResume(DownloadItem item)
    {
        if (PathRegistry.IsClaimedByOther(item.Id, item.FullPath))
            return;

        string baseTemp = item.TempPath;
        // One run per item at a time: any piece file here is a leftover of a run that was killed.
        DeleteOrphanPieceFiles(baseTemp);
        string[] tracks = [baseTemp, baseTemp + ".vpart", baseTemp + ".apart"];
        bool matchedItem = false;
        foreach (var tr in tracks)
        {
            if (!File.Exists(tr + ".state")) continue;
            var st = TryLoadSidecarRaw(tr + ".state");
            if (st == null)
            {
                // Corrupt sidecar next to an unclaimed partial: clean it so Reserve does not mistake it for another item.
                DeleteTrackPartial(tr);
            }
            else if (st.ItemId == item.Id)
            {
                matchedItem = true;
            }
        }
        if (matchedItem)
            PathRegistry.Claim(item.Id, item.FullPath);
    }

    static StreamResumeState? TryLoadSidecarRaw(string statePath)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<StreamResumeState>(File.ReadAllText(statePath, Encoding.UTF8), Json.Options);
        }
        catch
        {
            /* corrupt sidecar: treat as unresumable */
            return null;
        }
    }

    static void SaveSidecar(string trackPath, StreamResumeState state)
    {
        try
        {
            Json.WriteAtomic(trackPath + ".state", System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
        }
        catch (Exception ex)
        {
            Log.Write("stream sidecar save failed: " + ex.Message);
        }
    }

    static bool CanResumeTrack(
        DownloadItem item,
        string trackPath,
        string variantKey,
        bool hasInit,
        List<string> pieceUrls,
        out StreamResumeState? validState)
    {
        validState = null;
        if (pieceUrls.Count == 0 || !File.Exists(trackPath) || !File.Exists(trackPath + ".state"))
            return false;

        var st = TryLoadSidecarRaw(trackPath + ".state");
        if (st == null) return false;
        if (st.ItemId != item.Id) return false;
        if (!string.Equals(st.Variant, variantKey, StringComparison.Ordinal)) return false;
        if (st.InitWritten != hasInit) return false;
        if (st.TotalPieces != pieceUrls.Count) return false;
        if (st.LastPieceIndex < -1 || st.LastPieceIndex >= pieceUrls.Count) return false;
        if (!st.InitWritten && st.LastPieceIndex < 0) return false;
        if (st.BytesWritten < 0 || st.CountedBytes < 0) return false;
        if (st.LastPieceIndex >= 0 && st.BytesWritten == 0) return false;

        int nextIdx = st.LastPieceIndex + 1;
        if (nextIdx < pieceUrls.Count)
        {
            if (!string.Equals(st.NextPieceUrl, pieceUrls[nextIdx], StringComparison.Ordinal))
                return false;
        }
        else
        {
            if (!string.Equals(st.LastPieceUrl, pieceUrls[st.LastPieceIndex], StringComparison.Ordinal))
                return false;
        }

        long diskLen = new FileInfo(trackPath).Length;
        if (diskLen < st.BytesWritten)
            return false;

        validState = st;
        return true;
    }

    static string FormatHlsPieceUrl(HlsSegment seg)
        => seg.RangeLength.HasValue
            ? $"{seg.Uri.AbsoluteUri}@{seg.RangeOffset ?? 0}:{seg.RangeLength.Value}"
            : seg.Uri.AbsoluteUri;

    static string BuildHlsVariantKey(string role, Uri playlistUri, HlsSegment? initSeg)
        => $"hls|{role}|{playlistUri.AbsoluteUri}|{(initSeg != null ? FormatHlsPieceUrl(initSeg) : "")}";

    static string FormatDashPieceUrl(DashSegmentRef seg)
        => seg.RangeLength.HasValue
            ? $"{seg.Uri.AbsoluteUri}@{seg.RangeOffset ?? 0}:{seg.RangeLength.Value}"
            : seg.Uri.AbsoluteUri;

    static string BuildDashVariantKey(Uri mpdUri, DashRep rep)
        => $"dash|{rep.ContentKind}|{mpdUri.AbsoluteUri}|{rep.Id}|{rep.Bandwidth}|{rep.Width}x{rep.Height}|{rep.Segments.Count}|{(rep.Init != null ? FormatDashPieceUrl(rep.Init) : "")}";

    // ---------------- HTTP helpers with Netscape + host cookie matching ----------------

    static async Task<(Uri FinalUri, string? Mime, string Text)> FetchTextAsync(DownloadItem item, Uri uri, CancellationToken ct)
    {
        var (finalUri, mime, bytes) = await FetchBytesAsync(item, uri, null, null, null, trackProgress: false, ct).ConfigureAwait(false);
        return (finalUri, mime, Encoding.UTF8.GetString(bytes));
    }

    const int MaxDashSegments = 200_000;
    const int SegmentRetries = 3;
    internal const long PieceDiskThresholdBytes = 32L * 1024 * 1024;
    const long MaxPieceBytes = 8L * 1024 * 1024 * 1024;
    const long MaxControlBytes = 256L * 1024 * 1024;

    sealed class PiecePayload : IDisposable
    {
        public byte[]? MemoryData { get; }
        public string? TempFilePath { get; private set; }
        public long SliceOffset { get; }
        public long Length { get; }
        public long CountedBytes { get; }

        public PiecePayload(byte[] data, long countedBytes)
        {
            MemoryData = data;
            Length = data.LongLength;
            CountedBytes = countedBytes;
        }

        public PiecePayload(string tempFilePath, long sliceOffset, long length, long countedBytes)
        {
            TempFilePath = tempFilePath;
            SliceOffset = sliceOffset;
            Length = length;
            CountedBytes = countedBytes;
        }

        public async Task WriteToAsync(Stream destination, byte[]? aesKey, byte[]? aesIv, CancellationToken ct)
        {
            if (MemoryData != null)
            {
                byte[] outBytes = aesKey != null ? DecryptAes128Cbc(MemoryData, aesKey, aesIv!) : MemoryData;
                await destination.WriteAsync(outBytes, ct).ConfigureAwait(false);
                return;
            }

            var srcPath = TempFilePath;
            if (string.IsNullOrEmpty(srcPath))
                return;

            if (aesKey == null)
                await CopySliceToAsync(srcPath, SliceOffset, Length, destination, ct).ConfigureAwait(false);
            else
                await DecryptSliceToAsync(srcPath, SliceOffset, Length, aesKey, aesIv!, destination, ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            var path = TempFilePath;
            TempFilePath = null;
            if (!string.IsNullOrEmpty(path))
                MediaDownloader.TryDelete(path);
        }

        static async Task CopySliceToAsync(string srcPath, long offset, long length, Stream destination, CancellationToken ct)
        {
            await using var inFs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (offset > 0) inFs.Seek(offset, SeekOrigin.Begin);
            byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long remaining = length;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int n = await inFs.ReadAsync(buf.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (n <= 0)
                        throw new DownloadException(Loc.T("core.http.err_incomplete"), FailKind.Retryable);
                    await destination.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    remaining -= n;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }
        }

        static async Task DecryptSliceToAsync(string srcPath, long offset, long length, byte[] key, byte[] iv, Stream destination, CancellationToken ct)
        {
            if (key == null || iv == null || key.Length != 16 || iv.Length != 16 || length <= 0 || (length % 16) != 0)
                throw new DownloadException(Loc.T("core.stream.err_decrypt"));

            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();

            await using var inFs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (offset > 0) inFs.Seek(offset, SeekOrigin.Begin);

            byte[] inBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            byte[] outBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024 + 32);
            try
            {
                long bodyRemaining = length - 16;
                int carry = 0;
                while (bodyRemaining > 0)
                {
                    int maxChunk = (int)Math.Min(64 * 1024 - carry, bodyRemaining);
                    int n = await inFs.ReadAsync(inBuf.AsMemory(carry, maxChunk), ct).ConfigureAwait(false);
                    if (n <= 0)
                        throw new DownloadException(Loc.T("core.http.err_incomplete"), FailKind.Retryable);
                    bodyRemaining -= n;
                    int available = carry + n;
                    int blockBytes = available & ~15;
                    if (blockBytes > 0)
                    {
                        int plainLen;
                        try
                        {
                            plainLen = decryptor.TransformBlock(inBuf, 0, blockBytes, outBuf, 0);
                        }
                        catch (CryptographicException)
                        {
                            throw new DownloadException(Loc.T("core.stream.err_decrypt"));
                        }
                        if (plainLen > 0)
                            await destination.WriteAsync(outBuf.AsMemory(0, plainLen), ct).ConfigureAwait(false);
                    }
                    carry = available - blockBytes;
                    if (carry > 0 && blockBytes > 0)
                        Buffer.BlockCopy(inBuf, blockBytes, inBuf, 0, carry);
                }

                int tailGot = carry;
                while (tailGot < 16)
                {
                    int n = await inFs.ReadAsync(inBuf.AsMemory(tailGot, 16 - tailGot), ct).ConfigureAwait(false);
                    if (n <= 0)
                        throw new DownloadException(Loc.T("core.http.err_incomplete"), FailKind.Retryable);
                    tailGot += n;
                }

                byte[] finalPlain;
                try
                {
                    finalPlain = decryptor.TransformFinalBlock(inBuf, 0, 16);
                }
                catch (CryptographicException)
                {
                    throw new DownloadException(Loc.T("core.stream.err_decrypt"));
                }
                if (finalPlain.Length > 0)
                    await destination.WriteAsync(finalPlain, ct).ConfigureAwait(false);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(inBuf);
                System.Buffers.ArrayPool<byte>.Shared.Return(outBuf);
            }
        }
    }

    static string CreatePieceTempPath(DownloadItem item)
    {
        string baseTemp = item.TempPath;
        if (string.IsNullOrWhiteSpace(baseTemp))
        {
            string dir = !string.IsNullOrWhiteSpace(DownloadItem.TempRoot) ? DownloadItem.TempRoot : item.SaveDir;
            baseTemp = Path.Combine(dir, $"{item.Id:N}.gdm");
        }
        var parent = Path.GetDirectoryName(baseTemp);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
        return $"{baseTemp}.piece.{Guid.NewGuid():N}.tmp";
    }

    internal static async Task<(Uri FinalUri, string? Mime, byte[] Data)> FetchBytesAsync(
        DownloadItem item,
        Uri startUri,
        long? rangeOffset,
        long? rangeLength,
        SpeedLimiter? limiter,
        bool trackProgress,
        CancellationToken ct)
    {
        var (finalUri, mime, payload) = await FetchPieceAsync(item, startUri, rangeOffset, rangeLength, limiter, trackProgress, allowDiskSpill: false, ct).ConfigureAwait(false);
        using (payload)
        {
            return (finalUri, mime, payload.MemoryData ?? []);
        }
    }

    static async Task<(Uri FinalUri, string? Mime, PiecePayload Payload)> FetchPieceAsync(
        DownloadItem item,
        Uri startUri,
        long? rangeOffset,
        long? rangeLength,
        SpeedLimiter? limiter,
        bool trackProgress,
        bool allowDiskSpill,
        CancellationToken ct)
    {
        // Segment, key and init addresses come out of the playlist, which is text the server wrote. A playlist from
        // the internet must not send this PC to its own network (router, localhost), and an https one must not drop
        // to http: the same rule redirects already follow.
        var itemUrl = !string.IsNullOrWhiteSpace(item.ResolvedUrl) ? item.ResolvedUrl! : item.Url;
        if (Uri.TryCreate((itemUrl ?? "").Trim(), UriKind.Absolute, out var originUri)
            && (originUri.Scheme == Uri.UriSchemeHttp || originUri.Scheme == Uri.UriSchemeHttps)
            && !HttpDownloader.IsAllowedRedirectTarget(originUri, startUri))
            throw new DownloadException(Loc.T("core.http.err_redirect_invalid"));

        // One slow or dropped piece out of hundreds should not fail the whole video: media pieces get a few tries.
        var counted = trackProgress ? new System.Runtime.CompilerServices.StrongBox<long>() : null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await FetchPieceOnceAsync(item, startUri, rangeOffset, rangeLength, limiter, trackProgress, allowDiskSpill, counted, ct).ConfigureAwait(false);
            }
            catch (DownloadException ex) when (trackProgress && ex.Retryable && attempt < SegmentRetries && !ct.IsCancellationRequested)
            {
                if (counted is { Value: > 0 })
                {
                    item.AddDownloaded(-counted.Value); // the failed try's bytes are downloaded again
                    counted.Value = 0;
                }
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
            }
        }
    }

    static async Task<(Uri FinalUri, string? Mime, PiecePayload Payload)> FetchPieceOnceAsync(
        DownloadItem item,
        Uri startUri,
        long? rangeOffset,
        long? rangeLength,
        SpeedLimiter? limiter,
        bool trackProgress,
        bool allowDiskSpill,
        System.Runtime.CompilerServices.StrongBox<long>? counted,
        CancellationToken ct)
    {
        var netscapeCookies = ParseNetscapeJar(ReadProtectedCookieFile(item.MediaCookieFile));
        var current = startUri;

        for (int hop = 0; hop <= 10; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("User-Agent",
                string.IsNullOrWhiteSpace(item.UserAgent) ? HttpDownloader.DefaultUserAgent : item.UserAgent);

            // Same rule as ordinary downloads: another site (a key server, an ad host) gets the page's origin only,
            // never its full address with sign-in tokens in it, and nothing at all on a step down from https.
            if (HttpDownloader.ReferrerForRequest(item.Referrer, current) is { } safeRef)
                req.Headers.TryAddWithoutValidation("Referer", safeRef);

            var cookieHeader = BuildCookieHeader(item, current, netscapeCookies);
            if (!string.IsNullOrEmpty(cookieHeader))
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            if (rangeOffset.HasValue && rangeLength.HasValue && rangeLength.Value > 0)
                req.Headers.Range = new RangeHeaderValue(rangeOffset.Value, rangeOffset.Value + rangeLength.Value - 1);

            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerCts.CancelAfter(HttpDownloader.HeaderTimeout);
            HttpResponseMessage resp;
            try
            {
                resp = await HttpDownloader.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException(Loc.T("core.http.err_timeout"), FailKind.Retryable);
            }
            catch (HttpRequestException)
            {
                throw new DownloadException(Loc.T("core.err.network"), FailKind.Retryable);
            }

            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code is >= 300 and < 400 && resp.Headers.Location is { } loc)
                {
                    if (!Uri.TryCreate(current, loc, out var next)
                        || !HttpDownloader.IsAllowedRedirectTarget(startUri, next)
                        || !HttpDownloader.IsAllowedRedirectTarget(current, next))
                        throw new DownloadException(Loc.T("core.http.err_redirect_invalid"));
                    current = next;
                    continue;
                }

                if (code is < 200 or >= 300)
                {
                    FailKind kind = code is 401 or 403 or 410 ? FailKind.Expired
                        : code is 408 or 429 or >= 500 ? FailKind.Retryable
                        : FailKind.General;
                    throw new DownloadException(Loc.T("core.http.err_status", code, resp.ReasonPhrase ?? ""), kind) { StatusCode = code };
                }

                long maxPiece = allowDiskSpill ? MaxPieceBytes : MaxControlBytes;
                long? knownLen = resp.Content.Headers.ContentLength;
                if (knownLen is long kl0)
                {
                    if (kl0 > maxPiece || (trackProgress && HttpDownloader.MaxDownloadSize > 0 && item.Downloaded + kl0 > HttpDownloader.MaxDownloadSize))
                        throw new DownloadException(Loc.T("core.http.err_file_too_large"));
                }

                var mime = resp.Content.Headers.ContentType?.MediaType;
                await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                string? pieceTempPath = null;
                FileStream? pieceFs = null;
                MemoryStream? ms = null;
                bool ownershipTransferred = false;
                byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    if (allowDiskSpill && knownLen is long kl && kl > PieceDiskThresholdBytes)
                    {
                        pieceTempPath = CreatePieceTempPath(item);
                        pieceFs = new FileStream(pieceTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                    }
                    else
                    {
                        ms = new MemoryStream();
                    }

                    long totalRead = 0;
                    while (true)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(HttpDownloader.ReadTimeout);
                        int n;
                        try { n = await body.ReadAsync(buf.AsMemory(0, 64 * 1024), readCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new DownloadException(Loc.T("core.http.err_timeout"), FailKind.Retryable);
                        }
                        catch (IOException)
                        {
                            throw new DownloadException(Loc.T("core.http.err_disconnected"), FailKind.Retryable);
                        }
                        if (n == 0) break;

                        if (totalRead + n > maxPiece
                            || (trackProgress && HttpDownloader.MaxDownloadSize > 0 && item.Downloaded + n > HttpDownloader.MaxDownloadSize))
                        {
                            throw new DownloadException(Loc.T("core.http.err_file_too_large"));
                        }

                        if (limiter != null) await limiter.WaitAsync(n, ct).ConfigureAwait(false);
                        if (trackProgress)
                        {
                            await item.FileLimiter.WaitAsync(n, ct).ConfigureAwait(false);
                            item.AddDownloaded(n);
                            if (counted != null) counted.Value += n;
                        }

                        if (pieceFs == null && allowDiskSpill && ms!.Length + n > PieceDiskThresholdBytes)
                        {
                            pieceTempPath = CreatePieceTempPath(item);
                            pieceFs = new FileStream(pieceTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                            if (ms.Length > 0)
                                await pieceFs.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct).ConfigureAwait(false);
                            ms.Dispose();
                            ms = null;
                        }

                        if (pieceFs != null)
                            await pieceFs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        else
                            ms!.Write(buf, 0, n);

                        totalRead += n;
                    }

                    long countedTotal = counted?.Value ?? totalRead;
                    if (pieceFs != null)
                    {
                        await pieceFs.FlushAsync(ct).ConfigureAwait(false);
                        await pieceFs.DisposeAsync().ConfigureAwait(false);
                        pieceFs = null;

                        long sliceOff = 0;
                        long sliceLen = totalRead;
                        if (code == 200 && rangeOffset.HasValue && rangeLength.HasValue && rangeOffset.Value >= 0
                            && rangeOffset.Value + rangeLength.Value <= totalRead)
                        {
                            sliceOff = rangeOffset.Value;
                            sliceLen = rangeLength.Value;
                        }

                        ownershipTransferred = true;
                        return (current, mime, new PiecePayload(pieceTempPath!, sliceOff, sliceLen, countedTotal));
                    }

                    var data = ms!.ToArray();
                    // If server ignored Range and returned 200 OK for a byte-range request, slice the requested range locally.
                    if (code == 200 && rangeOffset.HasValue && rangeLength.HasValue && rangeOffset.Value >= 0
                        && rangeOffset.Value + rangeLength.Value <= data.LongLength)
                    {
                        data = data.AsSpan((int)rangeOffset.Value, (int)rangeLength.Value).ToArray();
                    }

                    return (current, mime, new PiecePayload(data, countedTotal));
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buf);
                    ms?.Dispose();
                    if (pieceFs != null)
                        await pieceFs.DisposeAsync().ConfigureAwait(false);
                    if (!ownershipTransferred && !string.IsNullOrEmpty(pieceTempPath))
                        MediaDownloader.TryDelete(pieceTempPath);
                }
            }
        }
        throw new DownloadException(Loc.T("core.http.err_too_many_redirects"));
    }

    sealed record NetscapeCookieEntry(string Domain, bool IncludeSubdomains, string Path, bool Secure, long ExpiresUnix, string Name, string Value);

    static string? ReadProtectedCookieFile(string? cookieFile)
    {
        if (string.IsNullOrWhiteSpace(cookieFile)) return null;
        try
        {
            if (MediaDownloader.IsUnderCookiesDir(cookieFile) && File.Exists(cookieFile))
            {
                var raw = File.ReadAllText(cookieFile, Encoding.UTF8).Trim();
                return Secret.Unprotect(raw);
            }
        }
        catch { /* best-effort cookie file read */ }
        return null;
    }

    static List<NetscapeCookieEntry> ParseNetscapeJar(string? text)
    {
        var list = new List<NetscapeCookieEntry>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (line.Length == 0) continue;
            if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                line = line["#HttpOnly_".Length..];
            else if (line.StartsWith('#'))
                continue;

            var cols = line.Split('\t');
            if (cols.Length < 7) continue;
            string dom = cols[0].Trim();
            bool includeSub = cols[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase) || dom.StartsWith('.');
            string path = string.IsNullOrEmpty(cols[2]) ? "/" : cols[2];
            bool secure = cols[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            long.TryParse(cols[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long exp);
            string name = cols[5];
            string val = cols[6];
            if (name.Length > 0)
                list.Add(new NetscapeCookieEntry(dom.TrimStart('.').ToLowerInvariant(), includeSub, path, secure, exp, name, val));
        }
        return list;
    }

    static string BuildCookieHeader(DownloadItem item, Uri uri, List<NetscapeCookieEntry> jar)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var hostCookies = item.CookiesFor(uri);
        if (!string.IsNullOrEmpty(hostCookies))
        {
            foreach (var part in hostCookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                    merged[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }

        if (jar.Count > 0)
        {
            string host = uri.Host.ToLowerInvariant();
            string path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            bool isHttps = uri.Scheme == Uri.UriSchemeHttps;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var c in jar)
            {
                if (c.Secure && !isHttps) continue;
                if (c.ExpiresUnix > 0 && c.ExpiresUnix < now) continue;
                bool domainOk = host == c.Domain || (c.IncludeSubdomains && host.EndsWith("." + c.Domain, StringComparison.Ordinal));
                if (!domainOk) continue;
                // "/app" covers "/app" and "/app/x", not "/app-other".
                if (!path.StartsWith(c.Path, StringComparison.Ordinal)
                    || (path.Length > c.Path.Length && !c.Path.EndsWith('/') && path[c.Path.Length] != '/')) continue;
                merged[c.Name] = c.Value;
            }
        }

        if (merged.Count == 0) return "";
        return string.Join("; ", merged.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // ---------------- Minimal ISO-BMFF fMP4 two-track muxer ----------------

    static class IsoBmffMerger
    {
        sealed record Box(string Type, byte[] Payload);

        public static bool TryMergeFragmentedMp4(string videoPath, string audioPath, string outputPath)
        {
            try
            {
                var vBytes = File.ReadAllBytes(videoPath);
                var aBytes = File.ReadAllBytes(audioPath);
                if (!TryReadBoxes(vBytes, out var vBoxes) || !TryReadBoxes(aBytes, out var aBoxes))
                    return false;

                var vMoov = vBoxes.FirstOrDefault(b => b.Type == "moov");
                var aMoov = aBoxes.FirstOrDefault(b => b.Type == "moov");
                if (vMoov == null || aMoov == null)
                    return false;

                var ftyp = vBoxes.FirstOrDefault(b => b.Type == "ftyp") ?? aBoxes.FirstOrDefault(b => b.Type == "ftyp");
                if (!TryReadBoxes(vMoov.Payload, out var vMoovChildren) || !TryReadBoxes(aMoov.Payload, out var aMoovChildren))
                    return false;

                var mvhd = vMoovChildren.FirstOrDefault(b => b.Type == "mvhd");
                var vTrak = vMoovChildren.FirstOrDefault(b => b.Type == "trak");
                var aTrak = aMoovChildren.FirstOrDefault(b => b.Type == "trak");
                if (mvhd == null || vTrak == null || aTrak == null)
                    return false;

                uint videoTrackId = ExtractTrackIdFromTrak(vTrak.Payload) ?? 1u;
                uint audioTrackId = videoTrackId == 2u ? 3u : 2u;

                var rewrittenAudioTrak = RewriteTrakId(aTrak.Payload, audioTrackId);
                var patchedMvhd = PatchMvhdNextTrackId(mvhd.Payload, Math.Max(videoTrackId, audioTrackId) + 1u);

                var vMvex = vMoovChildren.FirstOrDefault(b => b.Type == "mvex");
                var aMvex = aMoovChildren.FirstOrDefault(b => b.Type == "mvex");
                byte[]? mergedMvex = null;
                if (vMvex != null && aMvex != null
                    && TryReadBoxes(vMvex.Payload, out var vMvexKids)
                    && TryReadBoxes(aMvex.Payload, out var aMvexKids))
                {
                    var mvexBuf = new List<byte>();
                    foreach (var k in vMvexKids)
                        mvexBuf.AddRange(MakeBox(k.Type, k.Payload));
                    foreach (var k in aMvexKids.Where(k => k.Type == "trex"))
                        mvexBuf.AddRange(MakeBox("trex", RewriteTrexId(k.Payload, audioTrackId)));
                    mergedMvex = MakeBox("mvex", mvexBuf.ToArray());
                }

                var moovPayload = new List<byte>();
                moovPayload.AddRange(MakeBox("mvhd", patchedMvhd));
                moovPayload.AddRange(MakeBox("trak", vTrak.Payload));
                moovPayload.AddRange(MakeBox("trak", rewrittenAudioTrak));
                if (mergedMvex != null)
                    moovPayload.AddRange(mergedMvex);

                using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                if (ftyp != null)
                    outFs.Write(MakeBox("ftyp", ftyp.Payload));
                outFs.Write(MakeBox("moov", moovPayload.ToArray()));

                foreach (var b in vBoxes)
                {
                    if (b.Type is "ftyp" or "moov") continue;
                    outFs.Write(MakeBox(b.Type, b.Payload));
                }

                foreach (var b in aBoxes)
                {
                    if (b.Type is "ftyp" or "moov") continue;
                    if (b.Type == "moof")
                        outFs.Write(MakeBox("moof", RewriteMoofTrackId(b.Payload, audioTrackId)));
                    else
                        outFs.Write(MakeBox(b.Type, b.Payload));
                }

                return true;
            }
            catch
            {
                /* fall back to raw stream concatenation when inputs are not standard fMP4 */
                return false;
            }
        }

        static bool TryReadBoxes(ReadOnlySpan<byte> data, out List<Box> boxes)
        {
            boxes = [];
            int pos = 0;
            while (pos + 8 <= data.Length)
            {
                uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
                string type = Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
                int headerLen = 8;
                long boxSize = size32;
                if (size32 == 1)
                {
                    if (pos + 16 > data.Length) return false;
                    boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos + 8, 8));
                    headerLen = 16;
                }
                else if (size32 == 0)
                {
                    boxSize = data.Length - pos;
                }

                if (boxSize < headerLen || pos + boxSize > data.Length)
                    return false;
                foreach (char c in type)
                {
                    if (c is < ' ' or > '~') return false;
                }

                boxes.Add(new Box(type, data.Slice(pos + headerLen, (int)(boxSize - headerLen)).ToArray()));
                pos += (int)boxSize;
            }
            return pos == data.Length && boxes.Count > 0;
        }

        static byte[] MakeBox(string type, ReadOnlySpan<byte> payload)
        {
            var buf = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)buf.Length);
            Encoding.ASCII.GetBytes(type, buf.AsSpan(4, 4));
            payload.CopyTo(buf.AsSpan(8));
            return buf;
        }

        static uint? ExtractTrackIdFromTrak(byte[] trakPayload)
        {
            if (!TryReadBoxes(trakPayload, out var kids)) return null;
            var tkhd = kids.FirstOrDefault(k => k.Type == "tkhd");
            if (tkhd == null || tkhd.Payload.Length < 20) return null;
            int idOffset = tkhd.Payload[0] == 1 ? 20 : 12;
            if (tkhd.Payload.Length < idOffset + 4) return null;
            return BinaryPrimitives.ReadUInt32BigEndian(tkhd.Payload.AsSpan(idOffset, 4));
        }

        static byte[] RewriteTrakId(byte[] trakPayload, uint newTrackId)
        {
            if (!TryReadBoxes(trakPayload, out var kids)) return trakPayload;
            var outBuf = new List<byte>(trakPayload.Length);
            foreach (var k in kids)
            {
                if (k.Type == "tkhd" && k.Payload.Length >= 16)
                {
                    var copy = (byte[])k.Payload.Clone();
                    int idOffset = copy[0] == 1 ? 20 : 12;
                    if (copy.Length >= idOffset + 4)
                        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(idOffset, 4), newTrackId);
                    outBuf.AddRange(MakeBox("tkhd", copy));
                }
                else
                {
                    outBuf.AddRange(MakeBox(k.Type, k.Payload));
                }
            }
            return outBuf.ToArray();
        }

        static byte[] PatchMvhdNextTrackId(byte[] mvhdPayload, uint nextTrackId)
        {
            var copy = (byte[])mvhdPayload.Clone();
            int offset = copy.Length >= 100 && copy[0] == 0 ? 96 : (copy.Length >= 112 && copy[0] == 1 ? 108 : -1);
            if (offset >= 0 && copy.Length >= offset + 4)
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(offset, 4), nextTrackId);
            return copy;
        }

        static byte[] RewriteTrexId(byte[] trexPayload, uint newTrackId)
        {
            var copy = (byte[])trexPayload.Clone();
            if (copy.Length >= 8)
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(4, 4), newTrackId);
            return copy;
        }

        static byte[] RewriteMoofTrackId(byte[] moofPayload, uint newTrackId)
        {
            if (!TryReadBoxes(moofPayload, out var kids)) return moofPayload;
            var outBuf = new List<byte>(moofPayload.Length);
            foreach (var k in kids)
            {
                if (k.Type == "traf" && TryReadBoxes(k.Payload, out var trafKids))
                {
                    var trafBuf = new List<byte>(k.Payload.Length);
                    foreach (var tk in trafKids)
                    {
                        if (tk.Type == "tfhd" && tk.Payload.Length >= 8)
                        {
                            var copy = (byte[])tk.Payload.Clone();
                            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(4, 4), newTrackId);
                            trafBuf.AddRange(MakeBox("tfhd", copy));
                        }
                        else
                        {
                            trafBuf.AddRange(MakeBox(tk.Type, tk.Payload));
                        }
                    }
                    outBuf.AddRange(MakeBox("traf", trafBuf.ToArray()));
                }
                else
                {
                    outBuf.AddRange(MakeBox(k.Type, k.Payload));
                }
            }
            return outBuf.ToArray();
        }
    }
}
