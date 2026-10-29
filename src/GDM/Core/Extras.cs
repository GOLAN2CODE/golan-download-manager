using System.Buffers.Binary;
using System.Diagnostics;
using System.Media;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Golan.Gdm.Core;

/// <summary>Export / import of the download list (Golan file = JSON, or plain text with one address per line).</summary>
public static class Transfer
{
    public static void Export(string path, IEnumerable<DownloadItem> items, bool text)
    {
        if (text)
        {
            File.WriteAllLines(path, items.Select(i => i.Url), new UTF8Encoding(false));
            return;
        }
        var arr = new JsonArray();
        foreach (var i in items)
            arr.Add(new JsonObject
            {
                ["url"] = i.Url,
                ["fileName"] = i.FileName,
                ["saveDir"] = i.SaveDir,
                ["referrer"] = i.Referrer,
                ["description"] = i.Description,
                ["category"] = i.Category,
                ["kind"] = i.Kind.ToString(),
                ["mediaQuality"] = i.MediaQuality,
                ["login"] = i.Login,
            });
        var root = new JsonObject { ["format"] = "gdm-export", ["version"] = 1, ["items"] = arr };
        File.WriteAllText(path, root.ToJsonString(Json.Options), new UTF8Encoding(false));
    }

    /// <summary>Reads a Golan export file or a text file (any http/https addresses in it).</summary>
    public static List<DownloadItem> Import(string path, DownloadManager manager)
    {
        var content = File.ReadAllText(path);
        var result = new List<DownloadItem>();
        if (content.TrimStart().StartsWith('{'))
        {
            JsonObject? root;
            try { root = JsonNode.Parse(content) as JsonObject; }
            catch (JsonException ex) { Log.Write("import json parse failed: " + ex.Message); return result; }
            if (root != null && root.TryGetPropertyValue("format", out var fmtNode) && fmtNode != null)
            {
                if (fmtNode is not JsonValue fmtVal
                    || !fmtVal.TryGetValue<string>(out var fmtStr)
                    || !string.Equals(fmtStr, "gdm-export", StringComparison.Ordinal))
                {
                    return result;
                }
            }
            foreach (var o in (root?["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var url = o["url"]?.GetValue<string>() ?? "";
                bool isMagnet = Torrent.MagnetValidator.IsMagnetUri(url);
                if (!FileNames.IsHttpUrl(url) && !isMagnet) continue;
                var explicitName = o["fileName"]?.GetValue<string>() ?? "";
                Torrent.MagnetInfo? parsedMagnet = null;
                if (isMagnet && !Torrent.MagnetValidator.TryParse(url, out parsedMagnet, out _)) continue;
                var item = isMagnet
                    ? new DownloadItem
                    {
                        Url = url.Trim(),
                        FileName = !string.IsNullOrWhiteSpace(explicitName)
                            ? FileNames.Sanitize(Path.GetFileName(explicitName))
                            : (!string.IsNullOrWhiteSpace(parsedMagnet!.DisplayName) ? parsedMagnet.DisplayName : $"magnet-{parsedMagnet.InfoHashHex[..8]}"),
                        Category = "general",
                        SaveDir = manager.Settings.FolderFor("general"),
                        Kind = DownloadKind.Torrent,
                        TorrentInfoHash = parsedMagnet!.InfoHashHex,
                        MaxConnections = manager.Settings.MaxConnections,
                    }
                    : manager.CreateHttp(url,
                    o["referrer"]?.GetValue<string>() ?? "",
                    cookies: "",
                    userAgent: o["userAgent"]?.GetValue<string>() ?? "",
                    fileName: explicitName);
                if (!string.IsNullOrWhiteSpace(explicitName)) item.NameLocked = true;
                if (o["category"]?.GetValue<string>() is { Length: > 0 } cat)
                {
                    item.Category = Categories.Get(cat).Key;
                    item.SaveDir = manager.Settings.FolderFor(item.Category);
                }
                if (o["saveDir"]?.GetValue<string>() is { Length: > 0 } dir && IsSafeImportFolder(dir, manager.Settings, out var safeDir))
                    item.SaveDir = safeDir;
                item.Description = o["description"]?.GetValue<string>() ?? "";
                if (o["kind"]?.GetValue<string>() == "Media")
                {
                    item.Kind = DownloadKind.Media;
                    var mq = o["mediaQuality"]?.GetValue<string>() ?? "best";
                    item.MediaQuality = mq is "best" or "2160" or "1440" or "1080" or "720" or "480" or "360" or "mp3" or "m4a" or "audio" ? mq : "best";
                }
                item.Login = o["login"]?.GetValue<string>() ?? "";
                result.Add(item);
            }
            return result;
        }
        try
        {
            var rx = new System.Text.RegularExpressions.Regex(@"https?://[^\s""'<>]+",
                System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(content))
            {
                var url = m.Value.TrimEnd('.', ',', ';');
                while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '('))
                    url = url[..^1];
                result.Add(manager.CreateHttp(url));
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            Log.Write("import regex timeout: " + ex.Message);
        }
        return result;
    }

    public static bool IsSafeImportFolder(string dir, AppSettings settings, out string normalized)
    {
        normalized = "";
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || dir.StartsWith(@"\\") || dir.StartsWith("//")
                || dir.Contains('~') || dir.Contains('%') || !Path.IsPathFullyQualified(dir))
                return false;
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            static bool Under(string candidate, string root)
            {
                if (string.IsNullOrWhiteSpace(root)) return false;
                var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return candidate.Equals(r, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            var forbidden = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            bool isConfiguredTempDownloadRoot = !string.IsNullOrWhiteSpace(settings.DownloadRoot)
                && Under(settings.DownloadRoot, Path.GetTempPath())
                && Under(full, settings.DownloadRoot)
                && !Under(full, AppPaths.DataDir)
                && !Under(full, AppPaths.LocalDir);
            if (!isConfiguredTempDownloadRoot && forbidden.Any(f => Under(full, f))) return false;

            var allowed = new List<string>
            {
                settings.DownloadRoot,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            allowed.AddRange(settings.CategoryDefs.Select(c => settings.FolderFor(c.Key)));
            if (!allowed.Any(a => Under(full, a))) return false;
            normalized = full;
            return true;
        }
        catch { /* invalid import folder */ return false; }
    }

    /// <summary>Expands "http://site/img*.jpg" style batch patterns (IDM "Batch download").</summary>
    public static List<string> ExpandBatch(string pattern, bool letters, string from, string to, int width)
    {
        var list = new List<string>();
        if (!pattern.Contains('*')) return list;
        if (letters)
        {
            if (from.Length != 1 || to.Length != 1) return list;
            for (char c = from[0]; c <= to[0]; c++) list.Add(pattern.Replace("*", c.ToString()));
        }
        else if (long.TryParse(from, out var a) && long.TryParse(to, out var b) && b >= a && b - a < 100000)
        {
            for (long n = a; n <= b; n++) list.Add(pattern.Replace("*", n.ToString().PadLeft(Math.Max(0, width), '0')));
        }
        return list;
    }
}

public sealed record ZipEntryInfo(string Name, long Size, long CompressedSize, DateTime Modified, bool IsFolder);

/// <summary>Lists a remote .zip without downloading it: reads the end-of-central-directory and the directory with range requests.</summary>
public static class ZipPreview
{
    static ZipPreview() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<List<ZipEntryInfo>> ListAsync(DownloadItem item, CancellationToken ct)
    {
        var probe = await HttpDownloader.ProbeAsync(item, ct);
        if (!probe.Resumable || probe.Size < 22) throw new DownloadException(Loc.T("core.zip.notSupported"));
        long size = probe.Size;

        long tailLen = Math.Min(size, 66 * 1024);
        var tail = await RangeAsync(item, probe.FinalUri, size - tailLen, size - 1, ct);
        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new DownloadException(Loc.T("core.zip.notZip"));

        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));

        // ZIP64: the real values live in the zip64 end-of-central-directory record.
        if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF || count == 0xFFFF)
        {
            int loc = eocd - 20;
            if (loc >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(loc)) == 0x07064b50)
            {
                long z64Off = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(loc + 8));
                if (z64Off < 0 || z64Off > size - 56) throw new DownloadException(Loc.T("core.zip.corruptDir"));
                var z64 = await RangeAsync(item, probe.FinalUri, z64Off, z64Off + 55, ct);
                if (z64.Length < 56 || BinaryPrimitives.ReadUInt32LittleEndian(z64) != 0x06064b50)
                    throw new DownloadException(Loc.T("core.zip.corruptDir"));
                ulong rawCount = BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(32));
                ulong rawSize = BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(40));
                ulong rawOff = BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(48));
                if (rawCount > int.MaxValue || rawSize > long.MaxValue || rawOff > long.MaxValue)
                    throw new DownloadException(Loc.T("core.zip.corruptDir"));
                count = (int)rawCount;
                cdSize = (long)rawSize;
                cdOffset = (long)rawOff;
            }
        }
        if (cdOffset < 0 || cdSize <= 0 || cdSize > 64L * 1024 * 1024 || cdOffset > size - cdSize)
            throw new DownloadException(Loc.T("core.zip.corruptDir"));

        var cd = await RangeAsync(item, probe.FinalUri, cdOffset, cdOffset + cdSize - 1, ct);
        var list = new List<ZipEntryInfo>();
        int p = 0;
        while (p + 46 <= cd.Length && BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) == 0x02014b50)
        {
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 8));
            ushort time = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 12));
            ushort date = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 14));
            long comp = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 20));
            long uncomp = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 24));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 28));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 30));
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 32));
            if (p + 46L + nameLen + extraLen + commentLen > cd.Length) break;
            var enc = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(437, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            var name = enc.GetString(cd, p + 46, nameLen);
            // zip64 sizes in the extra field
            int e = p + 46 + nameLen, eEnd = e + extraLen;
            while (e + 4 <= eEnd)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(e));
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(e + 2));
                if (e + 4L + len > eEnd) break;
                if (id == 1)
                {
                    int q = e + 4, qEnd = e + 4 + len;
                    if (uncomp == 0xFFFFFFFF && q + 8 <= qEnd) { uncomp = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(q)); q += 8; }
                    if (comp == 0xFFFFFFFF && q + 8 <= qEnd) comp = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(q));
                }
                e += 4 + len;
            }
            DateTime modified;
            try { modified = new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 15), Math.Max(1, date & 31), time >> 11, (time >> 5) & 63, (time & 31) * 2); }
            catch { modified = DateTime.MinValue; }
            list.Add(new ZipEntryInfo(name, uncomp, comp, modified, name.EndsWith('/')));
            p += 46 + nameLen + extraLen + commentLen;
        }
        return list;
    }

    static async Task<byte[]> RangeAsync(DownloadItem item, Uri uri, long from, long to, CancellationToken ct)
    {
        long expected = to - from + 1;
        if (from < 0 || to < from || expected <= 0 || expected > 64L * 1024 * 1024)
            throw new DownloadException(Loc.T("core.zip.corruptDir"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var slot = await HostGate.AcquireAsync(uri.Host, HttpDownloader.Options.ServerLimitFor(uri.Host), timeout.Token);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Range = new RangeHeaderValue(from, to);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(item.UserAgent) ? HttpDownloader.DefaultUserAgent : item.UserAgent);
        bool sameHostAndSecure = Uri.TryCreate(item.Url, UriKind.Absolute, out var origUri)
            && origUri.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)
            && origUri.Port == uri.Port
            && !(origUri.Scheme == Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttps);
        if (sameHostAndSecure && Uri.TryCreate(item.Referrer, UriKind.Absolute, out var refUri)
            && (refUri.Scheme == Uri.UriSchemeHttp || refUri.Scheme == Uri.UriSchemeHttps)
            && !(refUri.Scheme == Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttps))
        {
            req.Headers.Referrer = new UriBuilder(refUri) { UserName = "", Password = "", Fragment = "" }.Uri;
        }
        var cookies = HttpDownloader.CookiesForRequest(item, uri, sameHostAndSecure);
        if (cookies.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookies);

        var login = !sameHostAndSecure ? default
            : !string.IsNullOrEmpty(item.Login) ? (item.Login, item.Password)
            : HttpDownloader.MatchSiteLogin(uri) is { } sl ? (sl.User, sl.Password)
            : default;
        if (!string.IsNullOrEmpty(login.Item1))
        {
            var raw = Encoding.UTF8.GetBytes($"{login.Item1}:{login.Item2}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }
        using var resp = await HttpDownloader.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new DownloadException(Loc.T("core.zip.rangeFailed"));
        await using var s = await resp.Content.ReadAsStreamAsync(timeout.Token);
        using var ms = new MemoryStream((int)Math.Min(expected, 1024 * 1024));
        var buf = new byte[16384];
        long total = 0;
        while (total < expected)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, expected - total)), timeout.Token);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            total += n;
        }
        return ms.ToArray();
    }
}

/// <summary>Post-download actions: virus check, sounds, "open with".</summary>
public static class AfterDownload
{
    public static IReadOnlyList<string> BuildScannerArguments(string? rawArgs, string file)
    {
        var fullFile = Path.GetFullPath(file);
        if (string.IsNullOrWhiteSpace(rawArgs))
            return [fullFile];

        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < rawArgs.Length; i++)
        {
            char c = rawArgs[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());

        var result = new List<string>(tokens.Count + 1);
        bool replacedPlaceholder = false;
        foreach (var token in tokens)
        {
            if (string.Equals(token, "[FILE]", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(fullFile);
                replacedPlaceholder = true;
            }
            else if (token.Contains("[FILE]", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(token.Replace("[FILE]", fullFile, StringComparison.OrdinalIgnoreCase));
                replacedPlaceholder = true;
            }
            else
            {
                result.Add(token);
            }
        }

        if (!replacedPlaceholder)
            result.Add(fullFile);

        return result;
    }

    /// <summary>Runs the configured scanner; returns null when clean/skipped, or a localized warning.</summary>
    public static async Task<string?> ScanAsync(AppSettings s, string file)
    {
        if (!s.VirusScanEnabled || !File.Exists(file) || !File.Exists(s.VirusScanner)) return null;
        var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
        if (!Wildcard.SplitList(s.VirusExtensions).Any(e => e.TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase))) return null;
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(s.VirusScanner) { CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in BuildScannerArguments(s.VirusArgs, file))
                psi.ArgumentList.Add(arg);
            p = Process.Start(psi)!;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            // Windows Defender returns 2 when it found a threat; other scanners: any non-zero.
            return p.ExitCode == 0 ? null : Loc.T("core.virus.warning", p.ExitCode);
        }
        catch (TimeoutException)
        {
            try { p?.Kill(true); }
            catch (InvalidOperationException) { /* scanner process already exited */ }
            Log.Write("virus scan timed out");
            return Loc.T("core.virus.timeout");
        }
        catch (Exception ex)
        {
            try { if (p is { HasExited: false }) p.Kill(true); }
            catch (InvalidOperationException) { /* scanner process already exited */ }
            Log.Write("virus scan failed: " + ex.Message);
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    public static void PlaySound(AppSettings s, string evt)
    {
        if (!s.SoundsEnabled) return;
        try
        {
            if (s.Sounds.TryGetValue(evt, out var wav) && File.Exists(wav)) new SoundPlayer(wav).Play();
            else (evt == "failed" ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        }
        catch (Exception ex) { Log.Write("sound playback failed: " + ex.Message); }
    }

    public static void OpenWith(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
        try
        {
            var psi = new ProcessStartInfo("rundll32.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
            psi.ArgumentList.Add(Path.GetFullPath(file));
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Write("open-with dialog failed: " + ex.Message); }
    }

    public static Task<ExtractionResult> VerifyAndExtractAsync(
        DownloadItem item,
        AppSettings s,
        Func<string?>? askPassword = null,
        IProgress<double>? hashProgress = null,
        CancellationToken ct = default)
        => Extractor.RunPostDownloadAsync(item, s, askPassword, hashProgress, ct);

    public static bool CanForceBypassException(string? url, string? referrer, string? pageUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri))
            return false;

        static bool SameSiteOrHost(Uri a, Uri b)
        {
            var hostA = a.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
            var hostB = b.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(hostA) || string.IsNullOrEmpty(hostB)) return false;
            if (hostA == hostB || hostA.EndsWith("." + hostB, StringComparison.Ordinal) || hostB.EndsWith("." + hostA, StringComparison.Ordinal))
                return true;
            var partsA = hostA.Split('.');
            var partsB = hostB.Split('.');
            if (partsA.Length >= 2 && partsB.Length >= 2)
                return string.Equals($"{partsA[^2]}.{partsA[^1]}", $"{partsB[^2]}.{partsB[^1]}", StringComparison.Ordinal);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(referrer) && Uri.TryCreate(referrer, UriKind.Absolute, out var refUri))
        {
            if (!SameSiteOrHost(targetUri, refUri)) return false;
        }
        if (!string.IsNullOrWhiteSpace(pageUrl) && Uri.TryCreate(pageUrl, UriKind.Absolute, out var pgUri))
        {
            if (!SameSiteOrHost(targetUri, pgUri)) return false;
        }
        return true;
    }

    public static ProcessStartInfo? BuildOpenWhenDoneStartInfo(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        var trimmed = folderPath.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return null;
        try
        {
            if (!Path.IsPathFullyQualified(trimmed)) return null;
            var full = Path.GetFullPath(trimmed);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)
                || (Uri.TryCreate(full, UriKind.Absolute, out var u) && u.IsUnc))
                return null;
            if (File.Exists(full) || !Directory.Exists(full))
                return null;
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(full);
            return psi;
        }
        catch { /* invalid OpenWhenDone directory path */ return null; }
    }
}
