using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Golan.Gdm.Core;

/// <summary>Multi-connection HTTP downloader with dynamic segment splitting, resume and per-host/per-file limits.</summary>
public sealed partial class HttpDownloader
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    const long MinSplit = 256 * 1024;
    const int BufferSize = 64 * 1024;
    const int MaxRedirects = 15;
    public static int MaxRetries { get; set; } = 6;
    public static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public static TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public static TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(1);
    public static TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum allowed download size when Content-Length / Content-Range is known (default 64 GB).</summary>
    public static long MaxDownloadSize { get; set; } = 64L * 1024 * 1024 * 1024;

    /// <summary>Maximum allowed download size when Content-Length is missing (default 512 MB).</summary>
    public static long MaxUnknownSize { get; set; } = 512L * 1024 * 1024;

    /// <summary>Current options (proxy, logins, limits). Set by <see cref="Configure"/>.</summary>
    public static AppSettings Options { get; private set; } = new();

    public static HttpClient Client { get; private set; } = Build(new AppSettings { ProxyMode = "system" });

    readonly DownloadItem _item;
    readonly SpeedLimiter _limiter;
    int _activeWorkers;
    readonly object _reResolveLock = new();
    Task? _reResolveTask;
    string? _reResolvedUrl;

    public HttpDownloader(DownloadItem item, SpeedLimiter limiter)
    {
        _item = item;
        _limiter = limiter;
    }

    // ---------------- requests ----------------

    /// <summary>Sends GET with manual redirects; cookies/credentials only go to the hosts they belong to and never downgrade https->http.</summary>
    static async Task<HttpResponseMessage> SendAsync(Uri start, RequestContext ctx, RangeHeaderValue? range, string? ifRange, CancellationToken ct)
    {
        if (start.Scheme != Uri.UriSchemeHttp && start.Scheme != Uri.UriSchemeHttps)
            throw new DownloadException(Loc.T("core.http.err_unsupported_scheme"));

        var uri = start;
        var origUri = ctx.OriginalUri ?? start;
        bool sameHostAndSecure =
            uri.Host.Equals(origUri.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == origUri.Port &&
            uri.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == start.Port &&
            (origUri.Scheme != Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttps) &&
            (start.Scheme != Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttps);

        for (int hop = 0; ; hop++)
        {
            using var req = NewRequest(uri, ctx, sameHostAndSecure);
            if (range != null) req.Headers.Range = range;
            if (!string.IsNullOrEmpty(ifRange)) req.Headers.TryAddWithoutValidation("If-Range", ifRange);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HeaderTimeout);
            HttpResponseMessage resp;
            try { resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException(Loc.T("core.http.err_timeout"), FailKind.Retryable);
            }

            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                var loc = resp.Headers.Location;
                if (loc is null)
                {
                    if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        resp.Dispose();
                        throw new DownloadException(Loc.T("core.http.err_redirect_missing"));
                    }
                }
                else
                {
                    resp.Dispose();
                    if (hop >= MaxRedirects) throw new DownloadException(Loc.T("core.http.err_too_many_redirects"));
                    var locStr = RecoverUtf8HeaderString(loc.OriginalString);
                    Uri? resolvedLoc = loc;
                    if (locStr != loc.OriginalString && !Uri.TryCreate(locStr, UriKind.RelativeOrAbsolute, out resolvedLoc))
                        throw new DownloadException(Loc.T("core.http.err_redirect_invalid"));
                    Uri? next;
                    if (resolvedLoc.IsAbsoluteUri)
                        next = resolvedLoc;
                    else if (!Uri.TryCreate(uri, resolvedLoc, out next))
                        throw new DownloadException(Loc.T("core.http.err_redirect_invalid"));
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        throw new DownloadException(Loc.T("core.http.err_redirect_invalid"));
                    bool wasHttps = uri.Scheme == Uri.UriSchemeHttps || origUri.Scheme == Uri.UriSchemeHttps || start.Scheme == Uri.UriSchemeHttps;
                    bool downgrade = wasHttps && next.Scheme != Uri.UriSchemeHttps;
                    if (downgrade && CarriesSecrets(ctx, origUri))
                        throw new DownloadException(Loc.T("core.http.err_redirect_insecure", next.Host));
                    // Public download mirrors (VideoLAN, SourceForge...) often send https visitors to a plain-http mirror.
                    // It is followed like browsers and IDM do, but no cookie, login or referrer ever goes along.
                    var checkTarget = downgrade ? new UriBuilder(next) { Scheme = Uri.UriSchemeHttps, Port = next.IsDefaultPort ? -1 : next.Port }.Uri : next;
                    if (!IsAllowedRedirectTarget(origUri, checkTarget) || !IsAllowedRedirectTarget(uri, checkTarget))
                        throw new DownloadException(Loc.T("core.http.err_redirect_invalid"));
                    if (downgrade) Log.Write($"following a plain-http mirror: {next.Host}");
                    sameHostAndSecure = sameHostAndSecure &&
                        next.Host.Equals(origUri.Host, StringComparison.OrdinalIgnoreCase) &&
                        next.Port == origUri.Port &&
                        (!wasHttps || next.Scheme == Uri.UriSchemeHttps);
                    uri = next;
                    continue;
                }
            }
            // Keep the final address on the response for the caller.
            resp.RequestMessage ??= new HttpRequestMessage(HttpMethod.Get, uri);
            resp.RequestMessage.RequestUri = uri;
            return resp;
        }
    }

    static string RecoverUtf8HeaderString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        bool hasHighLatin1 = false;
        foreach (char c in raw)
        {
            if (c > 0xFF) return raw;
            if (c >= 0x80) hasHighLatin1 = true;
        }
        if (!hasHighLatin1) return raw;
        try
        {
            var bytes = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++) bytes[i] = (byte)raw[i];
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return raw;
        }
    }

    public static bool IsAllowedRedirectTarget(Uri origUri, Uri nextUri)
    {
        if (nextUri.Scheme != Uri.UriSchemeHttp && nextUri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (origUri.Scheme == Uri.UriSchemeHttps && nextUri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (Resolvers.HostResolvers.IsPublicHost(origUri.DnsSafeHost) && !Resolvers.HostResolvers.IsPublicHost(nextUri.DnsSafeHost))
            return false;
        return true;
    }

    static async Task<HttpResponseMessage> SendProbeWithRetryAsync(Uri uri, RequestContext ctx, RangeHeaderValue? range, CancellationToken ct)
    {
        const int maxProbeRetries = 2;
        Exception? last = null;
        for (int attempt = 0; attempt <= maxProbeRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await SendAsync(uri, ctx, range, null, ct).ConfigureAwait(false);
                try { EnsureOk(resp); }
                catch { resp.Dispose(); throw; }
                return resp;
            }
            catch (DownloadException ex) when (!ex.Retryable) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == maxProbeRetries) break;
                await Task.Delay(Backoff(attempt, (ex as DownloadException)?.RetryAfter), ct).ConfigureAwait(false);
            }
        }
        throw Friendly(last);
    }

    /// <summary>Asks the server for size, name and range support without downloading the body.</summary>
    public static Task<ProbeResult> ProbeAsync(string url, string referrer, string cookies, string userAgent, CancellationToken ct)
    {
        var item = new DownloadItem { Url = url, Referrer = referrer, UserAgent = userAgent };
        if (!string.IsNullOrEmpty(cookies)) { item.Cookies = cookies; item.CookieHost = item.Host; }
        return ProbeAsync(item, ct);
    }

    public static async Task<ProbeResult> ProbeAsync(DownloadItem item, CancellationToken ct)
    {
        var uri = new Uri(item.Url);
        using var gate = await HostGate.AcquireAsync(uri.Host, Options.ServerLimitFor(uri.Host), ct).ConfigureAwait(false);
        HttpResponseMessage resp;
        try
        {
            resp = await SendProbeWithRetryAsync(uri, ContextFor(item), new RangeHeaderValue(0, 0), ct).ConfigureAwait(false);
        }
        catch (DownloadException ex) when (ex.Kind == FailKind.Mismatch && !ct.IsCancellationRequested)
        {
            resp = await SendProbeWithRetryAsync(uri, ContextFor(item), null, ct).ConfigureAwait(false);
        }
        using (resp)
        {
            var probe = ReadProbe(resp, item.Url);
            await DrainProbeAsync(resp, ct).ConfigureAwait(false);
            return probe;
        }
    }

    static async Task DrainProbeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentLength is > 0 and <= 1024)
        {
            var buf = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                while (await s.ReadAsync(buf, ct).ConfigureAwait(false) > 0) { }
            }
            catch (Exception) { /* best-effort drain so connection returns to pool */ }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    /// <summary>For the synchronization queue: true when the remote file changed since the last download.</summary>
    public static async Task<bool> HasChangedAsync(DownloadItem item, CancellationToken ct)
    {
        var p = await ProbeAsync(item, ct).ConfigureAwait(false);
        if (p.ETag.Length > 0 && item.ETag.Length > 0) return p.ETag != item.ETag;
        if (p.LastModified != null && item.LastModified != null) return p.LastModified != item.LastModified;
        return p.Size > 0 && p.Size != item.TotalSize;
    }

    static bool HasContentEncoding(HttpResponseMessage resp)
        => resp.Content.Headers.ContentEncoding.Any(e =>
            !string.IsNullOrWhiteSpace(e) && !string.Equals(e.Trim(), "identity", StringComparison.OrdinalIgnoreCase));

    static ProbeResult ReadProbe(HttpResponseMessage resp, string url)
    {
        var final = resp.RequestMessage?.RequestUri ?? new Uri(url);
        bool encoded = HasContentEncoding(resp);
        var cr = resp.Content.Headers.ContentRange;
        long size;
        bool resumable;
        if (!encoded
            && resp.StatusCode == HttpStatusCode.PartialContent
            && cr is { HasRange: true, From: 0, Length: long len }
            && (cr.To == 0 || cr.To == len - 1))
        {
            size = len;
            resumable = true;
        }
        else if (encoded)
        {
            size = -1;
            resumable = false;
        }
        else
        {
            size = cr?.Length ?? resp.Content.Headers.ContentLength ?? -1;
            resumable = false;
        }

        string? name = ExtractContentDispositionFileName(resp);
        if (string.IsNullOrEmpty(name)) name = FileNames.FromUrl(final.ToString());

        var mime = resp.Content.Headers.ContentType?.MediaType;
        if (!Path.HasExtension(name) && mime != null && MimeToExt(mime) is { } ext) name += "." + ext;

        var etag = resp.Headers.ETag is { IsWeak: false } t ? t.Tag : "";
        return new ProbeResult(final, size, resumable, name, mime, etag, resp.Content.Headers.LastModified);
    }

    static string? ExtractContentDispositionFileName(HttpResponseMessage resp)
    {
        string? fileNameStar = null;
        string? fileName = null;

        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var rawValues))
        {
            foreach (var raw in rawValues)
            {
                foreach (var part in SplitHeaderPartsRespectingQuotes(raw))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = part[..eq].Trim();
                    var val = part[(eq + 1)..].Trim();
                    if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
                    {
                        var decoded = DecodeRfc5987FileNameStar(val);
                        if (!string.IsNullOrWhiteSpace(decoded)) fileNameStar = decoded;
                    }
                    else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase))
                    {
                        var decoded = DecodeQuotedOrTokenFileName(val);
                        if (!string.IsNullOrWhiteSpace(decoded)) fileName = decoded;
                    }
                }
            }
        }

        if (fileNameStar == null && fileName == null && resp.Content.Headers.ContentDisposition is { } cd)
        {
            fileNameStar = cd.FileNameStar?.Trim('"', ' ');
            if (fileNameStar == null && cd.FileName is { } fn)
                fileName = DecodeQuotedOrTokenFileName(fn);
        }

        var chosen = !string.IsNullOrWhiteSpace(fileNameStar) ? fileNameStar : fileName;
        if (string.IsNullOrWhiteSpace(chosen)) return null;
        return FileNames.Sanitize(chosen);
    }

    static List<string> SplitHeaderPartsRespectingQuotes(string input)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(input)) return parts;
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\\' && inQuotes && i + 1 < input.Length)
            {
                sb.Append(c);
                sb.Append(input[++i]);
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
                continue;
            }
            if (c == ';' && !inQuotes)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    static string DecodeRfc5987FileNameStar(string rawVal)
    {
        var v = rawVal.Trim().Trim('"');
        int firstQuote = v.IndexOf('\'');
        if (firstQuote >= 0)
        {
            int secondQuote = v.IndexOf('\'', firstQuote + 1);
            if (secondQuote >= 0)
            {
                v = v[(secondQuote + 1)..];
            }
        }
        try { return Uri.UnescapeDataString(v); }
        catch (UriFormatException) { return v; }
    }

    static string DecodeQuotedOrTokenFileName(string rawVal)
    {
        var v = rawVal.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            v = v[1..^1];
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length && v[i + 1] is '"' or '\\')
                    sb.Append(v[++i]);
                else
                    sb.Append(v[i]);
            }
            v = sb.ToString();
        }
        v = RecoverUtf8HeaderString(v);
        if (v.Contains('%', StringComparison.Ordinal))
        {
            try { v = Uri.UnescapeDataString(v); }
            catch (UriFormatException) { /* keep raw header value if percent-encoding is malformed */ }
        }
        return v;
    }

    static string? MimeToExt(string mime) => mime switch
    {
        "application/zip" => "zip",
        "application/pdf" => "pdf",
        "video/mp4" => "mp4",
        "video/webm" => "webm",
        "audio/mpeg" => "mp3",
        "audio/mp4" => "m4a",
        "application/x-msdownload" => "exe",
        "application/vnd.android.package-archive" => "apk",
        "application/x-rar-compressed" or "application/vnd.rar" => "rar",
        "application/x-7z-compressed" => "7z",
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "text/html" => "html",
        _ => null,
    };

    static void EnsureOk(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var code = (int)resp.StatusCode;
        if (IsBrowserChallenge(resp))
            throw new DownloadException(Loc.T("core.http.err_challenge", resp.RequestMessage?.RequestUri?.Host ?? ""), FailKind.Challenge);
        var retryAfter = resp.Headers.RetryAfter?.Delta ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        throw code switch
        {
            401 => new DownloadException(Loc.T("core.http.err_401"), FailKind.Expired),
            403 or 410 => new DownloadException(Loc.T("core.http.err_auth", code), FailKind.Expired),
            404 => new DownloadException(Loc.T("core.http.err_404")),
            416 => new DownloadException(Loc.T("core.http.err_416"), FailKind.Mismatch),
            429 or 503 => new DownloadException(Loc.T("core.http.err_busy", code), FailKind.Rejected) { RetryAfter = retryAfter },
            >= 500 => new DownloadException(Loc.T("core.http.err_server", code), FailKind.Retryable),
            _ => new DownloadException(Loc.T("core.http.err_status", code, resp.ReasonPhrase ?? "")),
        };
    }

    // ---------------- run ----------------

    Uri CurrentUri => new(_item.ResolvedUrl ?? _item.Url);

    int EffectiveMaxConnections()
    {
        int max = Math.Clamp(_item.MaxConnections, 1, 32);
        var limit = Options.ServerLimitFor(CurrentUri.Host) ?? Options.ServerLimitFor(new Uri(_item.Url).Host);
        if (limit is > 0 and int srv) max = Math.Min(max, srv);
        if ((LearnedHostLimit(CurrentUri.Host) ?? LearnedHostLimit(new Uri(_item.Url).Host)) is int learned) max = Math.Min(max, learned);
        return max;
    }

    string? IfRangeValue()
    {
        if (_item.ETag.Length > 0 && !_item.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) return _item.ETag;
        if (_item.LastModified is { } lm) return lm.ToString("r");
        return null;
    }

    /// <summary>
    /// Network (UNC) folders are allowed only when the user configured them himself (download root, a category folder or the temp folder);
    /// a UNC path coming from anywhere else (e.g. an imported list) could leak the Windows login hash to a foreign server.
    /// </summary>
    static void EnsureSafeLocalDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new DownloadException(Loc.T("core.http.err_invalid_save_dir"));
        string full;
        try { full = Path.GetFullPath(dir.Trim()); }
        catch (Exception ex) { throw new DownloadException(Loc.T("core.http.err_invalid_save_dir_detail", ex.Message)); }
        bool unc = full.StartsWith(@"\\", StringComparison.Ordinal) || (Uri.TryCreate(full, UriKind.Absolute, out var u) && u.IsUnc);
        if (unc && !UserConfiguredRoots().Any(root => IsUnder(full, root)))
            throw new DownloadException(Loc.T("core.http.err_unc_not_configured"));
    }

    static IEnumerable<string> UserConfiguredRoots()
    {
        yield return Options.DownloadRoot;
        yield return Options.TempDir;
        foreach (var c in Options.CategoryDefs)
        {
            yield return c.Folder;
            yield return c.LastPath;
        }
    }

    static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var r = Path.GetFullPath(root.Trim()).TrimEnd('\\', '/') + "\\";
            return (path.TrimEnd('\\', '/') + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Invalid path characters in configured root.
            return false;
        }
    }

    static void EnsureSizeAllowed(string path, long size)
    {
        if (size > MaxDownloadSize)
            throw new DownloadException(Loc.T("core.http.err_file_too_large"));
        if (size > 0)
        {
            try
            {
                if (Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root && !root.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.AvailableFreeSpace < size)
                        throw new DownloadException(Loc.T("core.http.err_disk_full"));
                }
            }
            catch (DownloadException) { throw; }
            catch (Exception ex)
            {
                Log.Write("drive free space check failed: " + ex.Message);
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _item.Status = DownloadStatus.Connecting;
        _item.LastTry = DateTime.Now;
        if (_item.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            _item.ETag = "";
        EnsureSafeLocalDir(_item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot)) EnsureSafeLocalDir(DownloadItem.TempRoot);
        if (!string.IsNullOrEmpty(_item.FileName)) _item.FileName = FileNames.Sanitize(Path.GetFileName(_item.FileName));
        Directory.CreateDirectory(_item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot)) Directory.CreateDirectory(DownloadItem.TempRoot);

        bool canResume;
        lock (_item.SegLock)
            canResume = !PathRegistry.IsClaimedByOther(_item.Id, _item.FullPath)
                        && _item.Resumable && _item.Segments.Count > 0 && _item.TotalSize > 0 && _item.TotalSize <= MaxDownloadSize
                        && File.Exists(_item.TempPath)
                        && _item.Segments.All(s => s.Start >= 0 && s.End >= s.Start && s.End < _item.TotalSize && s.Downloaded >= 0 && s.Downloaded <= s.Length);

        try
        {
            if (canResume)
            {
                PathRegistry.Claim(_item.Id, _item.FullPath);
                lock (_item.SegLock) _item.Downloaded = _item.Segments.Sum(s => s.Downloaded);
                await VerifyBeforeResumeAsync(ct).ConfigureAwait(false);
                await RunSegmentedAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await StartFreshAsync(ct, allowRange: true).ConfigureAwait(false);
            }
        }
        catch (DownloadException ex) when (ex.Kind == FailKind.Mismatch && !ct.IsCancellationRequested)
        {
            // The file changed on the server (or the server sent another range): start over once.
            Log.Write($"restart after mismatch: {_item.Url} {ex.Message}");
            bool remoteChanged = ex.Message == Loc.T("core.http.err_remote_changed");
            try
            {
                await StartFreshAsync(ct, allowRange: remoteChanged).ConfigureAwait(false);
            }
            catch (DownloadException ex2) when (remoteChanged && ex2.Kind == FailKind.Mismatch && !ct.IsCancellationRequested)
            {
                await StartFreshAsync(ct, allowRange: false).ConfigureAwait(false);
            }
        }

        await FinishAsync().ConfigureAwait(false);
    }

    async Task StartFreshAsync(CancellationToken ct, bool allowRange = true)
    {
        _item.ResetProgress();
        _item.ResolvedUrl = null;
        lock (_reResolveLock)
        {
            _reResolveTask = null;
            _reResolvedUrl = null;
        }

        var start = new Uri(_item.Url);
        var gate = await HostGate.AcquireAsync(start.Host, Options.ServerLimitFor(start.Host), ct).ConfigureAwait(false);
        HttpResponseMessage? resp = null;
        try
        {
            ProbeResult probe;
            for (int pageTry = 0; ; pageTry++)
            {
                try
                {
                    if (allowRange)
                    {
                        try
                        {
                            resp = await SendWithRetryAsync(start, new RangeHeaderValue(0, 0), ct).ConfigureAwait(false);
                        }
                        catch (DownloadException ex) when (ex.Kind == FailKind.Mismatch && !ct.IsCancellationRequested)
                        {
                            allowRange = false;
                            resp = await SendWithRetryAsync(start, null, ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        resp = await SendWithRetryAsync(start, null, ct).ConfigureAwait(false);
                    }
                }
                catch (DownloadException ex) when (ex.Kind == FailKind.Challenge && pageTry == 0
                                                   && !string.Equals(_item.UserAgent, ToolUserAgent, StringComparison.Ordinal) && !ct.IsCancellationRequested)
                {
                    // Bot protection dislikes a "browser" that is not one; an honest download tool often passes.
                    Log.Write($"{start.Host} wants a browser check: asking again as a download tool");
                    LearnHostLimit(start.Host, 1);
                    _item.UserAgent = ToolUserAgent;
                    continue;
                }

                probe = ReadProbe(resp, _item.Url);
                var expected = _item.NameLocked && _item.FileName.Length > 0 ? _item.FileName : FileNames.FromUrl(_item.Url);
                if (!IsPageInsteadOfFile(probe.Mime, expected)) break;
                resp.Dispose();
                resp = null;
                if (pageTry == 0 && !string.Equals(_item.UserAgent, ToolUserAgent, StringComparison.Ordinal))
                {
                    // Mirror sites often show a web page to browsers and hand the file to download tools.
                    Log.Write($"{start.Host} answered a web page instead of {expected}: asking again as a download tool");
                    _item.UserAgent = ToolUserAgent;
                    continue;
                }
                throw new DownloadException(Loc.T("core.http.err_web_page", start.Host));
            }
            _item.ResolvedUrl = probe.FinalUri.ToString();
            _item.ETag = probe.ETag;
            _item.LastModified = probe.LastModified;
            if (!_item.NameLocked && probe.FileName != null) _item.FileName = probe.FileName;
            if (string.IsNullOrEmpty(_item.FileName)) _item.FileName = probe.FileName ?? "download";

            // Pick a free name now so we never overwrite an existing file or another download.
            var reserved = PathRegistry.Reserve(_item.Id, _item.FullPath);
            _item.FileName = Path.GetFileName(reserved);
            MediaDownloader.TryDelete(_item.TempPath);
            MediaDownloader.TryDelete(_item.FullPath + ".gdm");
            _item.ActiveTempPath = "";

            EnsureSizeAllowed(_item.TempPath, probe.Size);
            _item.TotalSize = probe.Size;
            _item.Resumable = allowRange && probe.Resumable && probe.Size > 0;

            if (_item.Resumable)
            {
                await DrainProbeAsync(resp, ct).ConfigureAwait(false);
                resp.Dispose();
                resp = null;
                gate.Dispose();
                CreateInitialSegments(probe.Size);
                await using (new FileStream(_item.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
                await RunSegmentedAsync(ct).ConfigureAwait(false);
            }
            else
            {
                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    await DrainProbeAsync(resp, ct).ConfigureAwait(false);
                    resp.Dispose();
                    resp = await SendWithRetryAsync(new Uri(_item.ResolvedUrl ?? _item.Url), null, ct).ConfigureAwait(false);
                    var fullProbe = ReadProbe(resp, _item.Url);
                    EnsureSizeAllowed(_item.TempPath, fullProbe.Size);
                    _item.TotalSize = fullProbe.Size;
                }
                await RunSingleStreamAsync(resp, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            resp?.Dispose();
            gate.Dispose();
        }
    }

    async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, RangeHeaderValue? range, CancellationToken ct)
    {
        Exception? last = null;
        int busyRetries = 0;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await SendAsync(uri, ContextFor(_item), range, null, ct).ConfigureAwait(false);
                try { EnsureOk(resp); }
                catch { resp.Dispose(); throw; }
                return resp;
            }
            catch (DownloadException ex) when (!ex.Retryable) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (ex is DownloadException { Kind: FailKind.Rejected } busy && busyRetries < MaxBusyRetries)
                {
                    busyRetries++;
                    attempt--;
                    await Task.Delay(BusyWait(busyRetries, busy.RetryAfter), ct).ConfigureAwait(false);
                    continue;
                }
                if (attempt == MaxRetries) break;
                await Task.Delay(Backoff(attempt, (ex as DownloadException)?.RetryAfter), ct).ConfigureAwait(false);
            }
        }
        throw Friendly(last);
    }

    public static TimeSpan Backoff(int attempt, TimeSpan? retryAfter = null)
        => retryAfter is { } ra && ra > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Min(MaxRetryAfter.TotalSeconds, ra.TotalSeconds))
            : TimeSpan.FromSeconds(Math.Min(20, BaseBackoff.TotalSeconds * (1 << Math.Clamp(attempt, 0, 10))));

    /// <summary>
    /// Smallest piece a connection starts with. Small files gain nothing from 8 connections, and every extra request
    /// counts against rate limiters (Cloudflare challenged AnyDesk's 8.5 MB file at probe + 8 connections at once).
    /// </summary>
    public static long MinInitialPiece { get; set; } = 2 * 1024 * 1024;

    void CreateInitialSegments(long size)
    {
        int max = EffectiveMaxConnections();
        long piece = Math.Max(MinSplit, MinInitialPiece);
        int count = (int)Math.Clamp((size + piece - 1) / piece, 1, max);
        long part = size / count;
        lock (_item.SegLock)
        {
            _item.Segments.Clear();
            for (int i = 0; i < count; i++)
            {
                long start = i * part;
                long end = i == count - 1 ? size - 1 : start + part - 1;
                _item.Segments.Add(new Segment { Start = start, End = end });
            }
        }
    }

    async Task WaitLimitsAsync(int n, CancellationToken ct)
    {
        await _limiter.WaitAsync(n, ct).ConfigureAwait(false);
        await _item.FileLimiter.WaitAsync(n, ct).ConfigureAwait(false);
    }

    static Stream WrapDecompressionStream(Stream raw, HttpResponseMessage resp)
    {
        foreach (var enc in resp.Content.Headers.ContentEncoding)
        {
            var e = enc.Trim().ToLowerInvariant();
            if (e is "gzip" or "x-gzip")
                return new GZipStream(raw, CompressionMode.Decompress, leaveOpen: false);
            if (e == "deflate")
                return new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: false);
            if (e == "br")
                return new BrotliStream(raw, CompressionMode.Decompress, leaveOpen: false);
        }
        return raw;
    }

    async Task RunSingleStreamAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        long maxBytes = _item.TotalSize >= 0 ? _item.TotalSize : MaxUnknownSize;
        EnsureSizeAllowed(_item.TempPath, _item.TotalSize > 0 ? _item.TotalSize : BufferSize);

        lock (_item.SegLock)
        {
            _item.Segments.Clear();
            _item.Segments.Add(new Segment { Start = 0, End = _item.TotalSize > 0 ? _item.TotalSize - 1 : -1, Active = true, Info = Loc.T("core.http.seg_receiving") });
        }
        var seg = _item.Segments[0];
        _item.Status = DownloadStatus.Downloading;
        _item.ActiveConnections = 1;

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var fs = new FileStream(_item.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, BufferSize, FileOptions.Asynchronous);
            var rawStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var stream = WrapDecompressionStream(rawStream, resp);
            while (true)
            {
                int n = await ReadWithTimeoutAsync(stream, buffer, BufferSize, ct, readTimeoutCts).ConfigureAwait(false);
                if (n == 0) break;
                if (_item.Downloaded + n > maxBytes || _item.Downloaded + n > MaxDownloadSize)
                    throw new DownloadException(_item.TotalSize >= 0
                        ? Loc.T("core.http.err_stream_exceeded")
                        : Loc.T("core.http.err_unknown_size_exceeded"));
                await WaitLimitsAsync(n, ct).ConfigureAwait(false);
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                lock (_item.SegLock) seg.Downloaded += n;
                _item.AddDownloaded(n);
            }
            if (_item.TotalSize > 0 && _item.Downloaded < _item.TotalSize)
                throw new DownloadException(Loc.T("core.http.err_incomplete"));
            if (_item.TotalSize < 0) _item.TotalSize = _item.Downloaded;
            seg.Active = false;
            seg.Info = Loc.T("core.http.seg_done");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            MediaDownloader.TryDelete(_item.TempPath);
            throw;
        }
        catch (HttpIOException hio) when (hio.HttpRequestError == HttpRequestError.ResponseEnded)
        {
            MediaDownloader.TryDelete(_item.TempPath);
            throw new DownloadException(Loc.T("core.http.err_incomplete"));
        }
        catch (DownloadException)
        {
            MediaDownloader.TryDelete(_item.TempPath);
            throw;
        }
        catch (Exception ex)
        {
            MediaDownloader.TryDelete(_item.TempPath);
            throw Friendly(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    async Task RunSegmentedAsync(CancellationToken ct)
    {
        _item.Status = DownloadStatus.Downloading;
        int max = EffectiveMaxConnections();
        var workers = new List<Task>();
        using var failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var fileHandle = File.OpenHandle(_item.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        Exception? fatal = null;
        Exception? lastDropped = null;

        List<Segment> initial;
        lock (_item.SegLock)
        {
            foreach (var s in _item.Segments) { s.Active = false; s.Info = s.Remaining <= 0 ? Loc.T("core.http.seg_done") : ""; }
            foreach (var s in _item.Segments.Where(s => s.Remaining > 0).Take(max)) s.Active = true;
            // Opening extra connections at once only for pieces worth it (small files: fewer requests, no rate limiter trips).
            long startPiece = Math.Max(MinSplit, MinInitialPiece);
            while (_item.Segments.Count(s => s.Active) < max)
            {
                if (TakeNextSegment(allowSplit: true, minPiece: startPiece) == null) break;
            }
            initial = _item.Segments.Where(s => s.Active).ToList();
        }
        foreach (var seg in initial) workers.Add(WorkerAsync(seg));

        await Task.WhenAll(workers).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (fatal != null) throw fatal;

        // Some workers gave up on a busy server; finish what is left with whatever still works.
        bool remaining;
        lock (_item.SegLock) remaining = _item.Segments.Any(s => s.Remaining > 0);
        // A browser check (Cloudflare) only gets stricter with every new request: stop here and keep what we have.
        if (remaining && lastDropped is DownloadException { Kind: FailKind.Challenge } challenge) throw challenge;
        if (remaining && lastDropped != null)
        {
            Segment? next;
            lock (_item.SegLock) next = TakeNextSegment(allowSplit: false);
            if (next != null)
            {
                await WorkerAsync(next).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (fatal != null) throw fatal;
            }
        }

        lock (_item.SegLock)
        {
            if (_item.Segments.Any(s => s.Remaining > 0))
                throw lastDropped as DownloadException ?? new DownloadException(Loc.T("core.http.err_incomplete_segments"), FailKind.Retryable);
        }

        async Task WorkerAsync(Segment? seg)
        {
            _item.ActiveConnections = Interlocked.Increment(ref _activeWorkers);
            try
            {
                while (seg != null)
                {
                    try
                    {
                        await DownloadSegmentAsync(seg, fileHandle, failCts.Token).ConfigureAwait(false);
                    }
                    catch (DownloadException ex) when (ex.Kind is FailKind.Rejected or FailKind.Challenge && Volatile.Read(ref _activeWorkers) > 1)
                    {
                        // The server refuses an extra connection: give this range back and run with fewer connections.
                        lastDropped = ex;
                        _serverBusy = true;
                        // A browser check means "too many requests": from now on this site gets one connection.
                        LearnHostLimit(CurrentUri.Host, ex.Kind == FailKind.Challenge ? 1 : Volatile.Read(ref _activeWorkers) - 1);
                        lock (_item.SegLock) { seg.Active = false; seg.Info = Loc.T("core.http.seg_rejected"); }
                        return;
                    }
                    lock (_item.SegLock)
                    {
                        seg.Active = false;
                        seg.Info = Loc.T("core.http.seg_done");
                        // Splitting a running piece costs a new request: only for pieces that still take a while.
                        seg = TakeNextSegment(allowSplit: !_serverBusy, minPiece: Math.Max(MinSplit, MinInitialPiece / 2));
                    }
                }
            }
            catch (OperationCanceledException) when (failCts.IsCancellationRequested)
            {
                // Sibling worker failed or download was cancelled by the caller.
            }
            catch (Exception ex)
            {
                fatal ??= ex;
                failCts.Cancel();
            }
            finally
            {
                _item.ActiveConnections = Math.Max(0, Interlocked.Decrement(ref _activeWorkers));
            }
        }
    }

    /// <summary>Returns an idle unfinished segment, or splits the largest active one in half. Caller holds SegLock.</summary>
    Segment? TakeNextSegment(bool allowSplit = true, long minPiece = MinSplit)
    {
        var idle = _item.Segments.FirstOrDefault(s => !s.Active && s.Remaining > 0);
        if (idle != null) { idle.Active = true; return idle; }
        if (!allowSplit) return null;

        var biggest = _item.Segments.Where(s => s.Active && s.End >= 0).OrderByDescending(s => s.Remaining).FirstOrDefault();
        if (biggest == null || biggest.Remaining < minPiece * 2) return null;

        long mid = biggest.Position + biggest.Remaining / 2;
        var created = new Segment { Start = mid, End = biggest.End, Active = true };
        biggest.End = mid - 1;
        _item.Segments.Insert(_item.Segments.IndexOf(biggest) + 1, created);
        return created;
    }

    async Task ReResolveAsync(CancellationToken ct)
    {
        // Presigned/expiring links: ask the original address again for a fresh target.
        var p = await ProbeAsync(_item, ct).ConfigureAwait(false);
        if (p.Size != _item.TotalSize || (p.ETag.Length > 0 && _item.ETag.Length > 0 && p.ETag != _item.ETag))
            throw new DownloadException(Loc.T("core.http.err_remote_changed"), FailKind.Mismatch);
        _item.ResolvedUrl = p.FinalUri.ToString();
    }

    async Task DownloadSegmentAsync(Segment seg, Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            const int maxSegmentReconnects = 32;
            int reconnects = 0;
            Exception? last = null;
            int busyRetries = 0;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                lock (_item.SegLock)
                {
                    if (seg.Remaining <= 0) return;
                    seg.Info = Loc.T("core.http.seg_connecting");
                }

                var uri = CurrentUri;
                try
                {
                    using var gate = await HostGate.AcquireAsync(uri.Host, Options.ServerLimitFor(uri.Host), ct).ConfigureAwait(false);
                    long from, to;
                    lock (_item.SegLock)
                    {
                        if (seg.Remaining <= 0) return;
                        from = seg.Position;
                        to = seg.End;
                    }
                    using var resp = await SendAsync(uri, ContextFor(_item), new RangeHeaderValue(from, to), IfRangeValue(), ct).ConfigureAwait(false);
                    EnsureOk(resp);
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                    {
                        bool validatorChanged =
                            (_item.ETag.Length > 0 && resp.Headers.ETag is { IsWeak: false } et200 && !string.Equals(et200.Tag, _item.ETag, StringComparison.Ordinal))
                            || (_item.LastModified is { } lm200 && resp.Content.Headers.LastModified is { } rLm200 && rLm200 != lm200);
                        throw new DownloadException(
                            Loc.T(validatorChanged ? "core.http.err_remote_changed" : "core.http.err_no_range"),
                            FailKind.Mismatch);
                    }
                    if (HasContentEncoding(resp))
                        throw new DownloadException(Loc.T("core.http.err_bad_range"), FailKind.Mismatch);
                    var cr = resp.Content.Headers.ContentRange;
                    if (cr is null || !cr.HasRange || cr.From != from || cr.To != to || cr.Length != _item.TotalSize)
                        throw new DownloadException(Loc.T("core.http.err_bad_range"), FailKind.Mismatch);

                    if (_item.ETag.Length > 0)
                    {
                        if (resp.Headers.ETag is { IsWeak: false } et && !string.Equals(et.Tag, _item.ETag, StringComparison.Ordinal))
                            throw new DownloadException(Loc.T("core.http.err_remote_changed"), FailKind.Mismatch);
                        if (resp.Headers.ETag is { IsWeak: true })
                            throw new DownloadException(Loc.T("core.http.err_remote_changed"), FailKind.Mismatch);
                    }
                    if (_item.LastModified is { } expectedLm && resp.Content.Headers.LastModified is { } actualLm && actualLm != expectedLm)
                    {
                        throw new DownloadException(Loc.T("core.http.err_remote_changed"), FailKind.Mismatch);
                    }

                    using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    long writePos = from;
                    long bytesThisAttempt = 0;
                    lock (_item.SegLock) seg.Info = Loc.T("core.http.seg_receiving");

                    while (true)
                    {
                        long allowed;
                        lock (_item.SegLock) allowed = seg.Remaining;
                        if (allowed <= 0) return;

                        int n = await ReadWithTimeoutAsync(stream, buffer, (int)Math.Min(BufferSize, allowed), ct, readTimeoutCts).ConfigureAwait(false);
                        if (n == 0) break;
                        await WaitLimitsAsync(n, ct).ConfigureAwait(false);

                        int use;
                        lock (_item.SegLock) use = (int)Math.Min(n, Math.Max(0, seg.Remaining));
                        if (use > 0)
                        {
                            await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, use), writePos, ct).ConfigureAwait(false);
                            writePos += use;
                            bytesThisAttempt += use;
                            lock (_item.SegLock) seg.Downloaded += use;
                            _item.AddDownloaded(use);
                            if (bytesThisAttempt >= 8192 && reconnects < maxSegmentReconnects)
                            {
                                attempt = 0; // substantial progress resets the retry budget within cap
                                reconnects++;
                                bytesThisAttempt = 0;
                            }
                        }
                        if (use < n) return; // segment was shortened by a split
                    }

                    lock (_item.SegLock) if (seg.Remaining <= 0) return;
                    throw new IOException(Loc.T("core.http.err_disconnected"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (DownloadException ex) when (ex.Kind == FailKind.Expired)
                {
                    Task? waitTask = null;
                    lock (_reResolveLock)
                    {
                        if (_reResolveTask == null)
                        {
                            _reResolvedUrl = uri.ToString();
                            _reResolveTask = ReResolveAsync(ct);
                            waitTask = _reResolveTask;
                        }
                        else if (string.Equals(_reResolvedUrl, uri.ToString(), StringComparison.Ordinal))
                        {
                            waitTask = _reResolveTask;
                        }
                    }
                    if (waitTask == null) throw;
                    last = ex;
                    await waitTask.ConfigureAwait(false);
                }
                catch (DownloadException ex) when (!ex.Retryable || (ex.Kind == FailKind.Rejected && Volatile.Read(ref _activeWorkers) > 1)) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (ex is DownloadException { Kind: FailKind.Rejected } busy && busyRetries < MaxBusyRetries)
                    {
                        // Only this connection is left and the server says "busy": it is rate limiting, so wait and ask again.
                        busyRetries++;
                        attempt--;
                        _serverBusy = true;
                        var wait = BusyWait(busyRetries, busy.RetryAfter);
                        lock (_item.SegLock) seg.Info = Loc.T("core.http.seg_busy_wait", (int)Math.Ceiling(wait.TotalSeconds));
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }
                    lock (_item.SegLock) seg.Info = Loc.T("core.http.seg_retrying");
                    if (attempt == MaxRetries) break;
                    await Task.Delay(Backoff(attempt, (ex as DownloadException)?.RetryAfter), ct).ConfigureAwait(false);
                }
            }
            throw Friendly(last);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, int count, CancellationToken ct, CancellationTokenSource? reusableTimeout = null)
    {
        using var ownedTimeout = reusableTimeout == null ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        var timeout = reusableTimeout ?? ownedTimeout!;
        timeout.CancelAfter(ReadTimeout);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(Loc.T("core.http.err_timeout"));
        }
        finally
        {
            if (reusableTimeout != null && !reusableTimeout.IsCancellationRequested)
                reusableTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    static Exception Friendly(Exception? ex) => ex switch
    {
        DownloadException d => d,
        HttpRequestException h when h.InnerException is System.Net.Sockets.SocketException =>
            new DownloadException(Loc.T("core.http.err_dns"), FailKind.Retryable),
        HttpRequestException h => new DownloadException(Loc.T("core.http.err_conn_failed", h.Message), FailKind.Retryable),
        IOException io when io.HResult == unchecked((int)0x80070070) => new DownloadException(Loc.T("core.http.err_disk_full")),
        IOException io => new DownloadException(Loc.T("core.http.err_disk_io", io.Message), FailKind.Retryable),
        UnauthorizedAccessException => new DownloadException(Loc.T("core.http.err_no_write_access")),
        null => new DownloadException(Loc.T("core.http.err_unexpected")),
        _ => new DownloadException(ex.Message),
    };

    async Task FinishAsync()
    {
        _item.Status = DownloadStatus.Merging;
        MarkOfTheWeb.Write(_item.TempPath, _item.ResolvedUrl ?? _item.Url, _item.Referrer);
        for (int i = 0; ; i++)
        {
            var target = PathRegistry.Reserve(_item.Id, _item.FullPath);
            try
            {
                File.Move(_item.TempPath, target);
                _item.FileName = Path.GetFileName(target);
                _item.ActiveTempPath = "";
                break;
            }
            catch (IOException) when (File.Exists(target) && i < 20)
            {
                // Someone created the file meanwhile: Reserve picks the next free name.
            }
            catch (IOException) when (i < 10)
            {
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
        if (_item.TotalSize <= 0) _item.TotalSize = _item.Downloaded;
        lock (_item.SegLock) _item.Segments.Clear();

        MarkOfTheWeb.Write(_item.FullPath, _item.ResolvedUrl ?? _item.Url, _item.Referrer);
        if (Options.UseServerFileTime && _item.LastModified is { } lm)
        {
            try { File.SetLastWriteTime(_item.FullPath, lm.LocalDateTime); }
            catch (IOException ex) { Log.Write("set file last-write time failed: " + ex.Message); }
        }
        PathRegistry.Release(_item.Id);
    }
}
