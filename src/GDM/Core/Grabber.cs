using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

/// <summary>A Site Grabber project (IDM wizard steps 1-4). Saved as JSON in %APPDATA%\Golan\GDM\grabber.</summary>
public sealed class GrabberProject
{
    public string Name { get; set; } = "";
    public string StartUrl { get; set; } = "";
    public string Login { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Password { get => Secret.Unprotect(ProtectedPassword); set => ProtectedPassword = Secret.Protect(value); }

    // Step 2: which pages to explore
    public bool ExploreWholeSite { get; set; }
    public int LevelsBase { get; set; } = 2;
    public int LevelsOther { get; set; }
    public bool DontExploreParent { get; set; } = true;
    public bool ExploreMainDomain { get; set; }
    /// <summary>Semicolon separated path/domain patterns.</summary>
    public string ExploreOnly { get; set; } = "";
    public string ExploreSkip { get; set; } = "";
    public int MaxPages { get; set; } = 2000;

    // Step 3: which files
    /// <summary>Comma separated masks, e.g. "*.jpg,*.png". Empty = every non-page file.</summary>
    public string FileMasks { get; set; } = "*.jpg,*.jpeg,*.png,*.gif,*.webp";
    public string FileSkipMasks { get; set; } = "";
    public bool SearchThisSiteOnly { get; set; }
    public bool HideDuplicates { get; set; } = true;
    public long MinSizeKB { get; set; }
    public long MaxSizeKB { get; set; }
    public string FilesOnly { get; set; } = "";
    public string FilesSkip { get; set; } = "";
    public bool DownloadAllAtOnce { get; set; }

    // Step 4: where to save
    /// <summary>category | onecategory | folder</summary>
    public string SaveMode { get; set; } = "category";
    public string SaveCategory { get; set; } = "general";
    public string SaveFolder { get; set; } = "";
    public bool KeepRelativeFolders { get; set; }
    public bool OverwriteExisting { get; set; }
    public bool LinkTextAsDescription { get; set; } = true;

    // Settings
    public int ExploreAtOnce { get; set; } = 4;
    public int DownloadAtOnce { get; set; } = 4;

    public static string Folder => Path.Combine(AppPaths.DataDir, "grabber");

    public void Save()
    {
        var name = FileNames.Sanitize(string.IsNullOrWhiteSpace(Name) ? Loc.T("core.grabber.defaultName") : Name);
        Json.WriteAtomic(Path.Combine(Folder, name + ".json"), JsonSerializer.Serialize(this, Json.Options));
    }

    public static List<string> List()
        => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n).ToList() : [];

    public static int ClampCrawlDepth(int depth) => Math.Clamp(depth, 0, 10);

    public static GrabberProject? Load(string name)
    {
        var p = Path.Combine(Folder, FileNames.Sanitize(name) + ".json");
        if (!File.Exists(p)) return null;
        var loaded = JsonSerializer.Deserialize<GrabberProject>(File.ReadAllText(p), Json.Options);
        if (loaded != null)
        {
            loaded.LevelsBase = ClampCrawlDepth(loaded.LevelsBase);
            loaded.LevelsOther = ClampCrawlDepth(loaded.LevelsOther);
        }
        return loaded;
    }

    /// <summary>Ready-made filters (IDM "Filters" list).</summary>
    public static (string Name, string Mask)[] Presets =>
    [
        (Loc.T("core.grabber.preset.images"), "*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.svg"),
        (Loc.T("core.grabber.preset.video"), "*.mp4,*.mkv,*.avi,*.mov,*.wmv,*.flv,*.webm,*.m4v"),
        (Loc.T("core.grabber.preset.audio"), "*.mp3,*.wav,*.m4a,*.aac,*.flac,*.ogg,*.wma"),
        (Loc.T("core.grabber.preset.docs"), "*.pdf,*.doc,*.docx,*.xls,*.xlsx,*.ppt,*.pptx,*.txt,*.epub"),
        (Loc.T("core.grabber.preset.compressed"), "*.zip,*.rar,*.7z,*.tar,*.gz,*.iso"),
        (Loc.T("core.grabber.preset.programs"), "*.exe,*.msi,*.apk,*.dmg"),
        (Loc.T("core.grabber.preset.all"), "*.*"),
    ];
}

public sealed class GrabbedFile : ObservableObject
{
    public required string Url { get; init; }
    public required string FoundOn { get; init; }
    public string LinkText { get; init; } = "";
    public int Depth { get; init; }
    public string Name => FileNames.FromUrl(Url);
    public string Extension => Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string Glyph => Categories.Get(Categories.Detect(Name, Url)).Glyph;

    long _size = -1;
    public long Size { get => _size; set { if (Set(ref _size, value)) OnPropertyChanged(nameof(SizeText)); } }
    public string SizeText => _size > 0 ? Fmt.Size(_size) : "";

    bool _checked = true;
    public bool Checked { get => _checked; set => Set(ref _checked, value); }

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
}

public sealed class GrabberStats : ObservableObject
{
    int _pages, _explored, _matched, _downloaded;
    string _status = "";
    public int PagesProcessed { get => Volatile.Read(ref _pages); set { Volatile.Write(ref _pages, value); OnPropertyChanged(); } }
    public int Explored { get => Volatile.Read(ref _explored); set { Volatile.Write(ref _explored, value); OnPropertyChanged(); } }
    public int Matched { get => Volatile.Read(ref _matched); set { Volatile.Write(ref _matched, value); OnPropertyChanged(); } }
    public int Downloaded { get => Volatile.Read(ref _downloaded); set { Volatile.Write(ref _downloaded, value); OnPropertyChanged(); } }
    public string Status { get => _status; set => Set(ref _status, value); }

    public void IncPagesProcessed() { Interlocked.Increment(ref _pages); OnPropertyChanged(nameof(PagesProcessed)); }
    public void IncExplored() { Interlocked.Increment(ref _explored); OnPropertyChanged(nameof(Explored)); }
    public void IncMatched() { Interlocked.Increment(ref _matched); OnPropertyChanged(nameof(Matched)); }
    public void IncDownloaded() { Interlocked.Increment(ref _downloaded); OnPropertyChanged(nameof(Downloaded)); }
}

/// <summary>Breadth-first crawler. Raises <see cref="FileFound"/> for every matching file (on a worker thread).</summary>
public sealed class SiteGrabber(GrabberProject p)
{
    static readonly Regex LinkRx = new(@"(?:href|src|data-src|data-href)\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>]+))(?:[^>]*>([^<]{0,200}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    static readonly HashSet<string> PageExt = new(StringComparer.OrdinalIgnoreCase) { "", "html", "htm", "php", "asp", "aspx", "jsp", "shtml", "cfm", "cgi", "pl" };

    public GrabberStats Stats { get; } = new();
    public event Action<GrabbedFile>? FileFound;

    readonly ConcurrentDictionary<string, byte> _seenPages = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _seenFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _seenNames = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken ct)
    {
        var start = new Uri(p.StartUrl);
        var baseDir = start.GetLeftPart(UriPartial.Path);
        baseDir = baseDir[..(baseDir.LastIndexOf('/') + 1)];
        var mainDomain = MainDomain(start.Host);

        var frontier = new List<(Uri Uri, int BaseDepth, int OtherDepth)> { (start, 0, 0) };
        _seenPages[Key(start)] = 1;
        var gate = new SemaphoreSlim(Math.Clamp(p.ExploreAtOnce, 1, 10));
        int baseLimit = p.ExploreWholeSite ? 10 : GrabberProject.ClampCrawlDepth(p.LevelsBase);

        while (frontier.Count > 0 && !ct.IsCancellationRequested)
        {
            var next = new ConcurrentBag<(Uri, int, int)>();
            var tasks = frontier.Select(async page =>
            {
                await gate.WaitAsync(ct);
                try { await ProcessPageAsync(page, next, start, baseDir, mainDomain, baseLimit, ct); }
                finally { gate.Release(); }
            }).ToList();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { break; }
            frontier = next.Take(Math.Max(0, p.MaxPages - Stats.PagesProcessed)).ToList();
        }
        Stats.Status = ct.IsCancellationRequested ? Loc.T("core.grabber.stopped") : Loc.T("core.grabber.done");
    }

    async Task ProcessPageAsync((Uri Uri, int BaseDepth, int OtherDepth) page, ConcurrentBag<(Uri, int, int)> next,
        Uri start, string baseDir, string mainDomain, int baseLimit, CancellationToken ct)
    {
        Stats.Status = Loc.T("core.grabber.opening", page.Uri);
        string html;
        try
        {
            html = await FetchPageAsync(page.Uri, start, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write("grabber page failed: " + page.Uri + " " + ex.Message);
            return;
        }
        finally
        {
            Stats.IncPagesProcessed();
        }

        MatchCollection matches;
        try { matches = LinkRx.Matches(html); }
        catch (RegexMatchTimeoutException) { return; }

        foreach (Match m in matches)
        {
            var raw = WebUtility.HtmlDecode((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim());
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(page.Uri, raw, out var link) || (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps)) continue;
            link = new UriBuilder(link) { Fragment = "" }.Uri;
            var linkText = WebUtility.HtmlDecode(m.Groups[4].Value).Trim();
            bool sameSite = link.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase)
                            || (p.ExploreMainDomain && MainDomain(link.Host) == mainDomain);
            var ext = Path.GetExtension(link.AbsolutePath).TrimStart('.');

            if (PageExt.Contains(ext) && !IsFileMask(ext))
            {
                Stats.IncExplored();
                int baseDepth = page.BaseDepth + (sameSite ? 1 : 0);
                int otherDepth = page.OtherDepth + (sameSite ? 0 : 1);
                if (sameSite ? baseDepth > baseLimit : otherDepth > GrabberProject.ClampCrawlDepth(p.LevelsOther)) continue;
                if (p.DontExploreParent && sameSite && !link.AbsoluteUri.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PathAllowed(link, p.ExploreOnly, p.ExploreSkip)) continue;
                if (_seenPages.TryAdd(Key(link), 1)) next.Add((link, baseDepth, otherDepth));
            }
            else
            {
                if (p.SearchThisSiteOnly && !sameSite) continue;
                if (!MatchesMasks(link, p.FileMasks, p.FileSkipMasks) || !PathAllowed(link, p.FilesOnly, p.FilesSkip)) continue;
                if (!_seenFiles.TryAdd(link.AbsoluteUri, 1)) continue;
                if (p.HideDuplicates && !_seenNames.TryAdd(FileNames.FromUrl(link.AbsoluteUri), 1)) continue;

                var file = new GrabbedFile { Url = link.AbsoluteUri, FoundOn = page.Uri.AbsoluteUri, LinkText = linkText, Depth = page.BaseDepth };
                if (p.MinSizeKB > 0 || p.MaxSizeKB > 0)
                {
                    try
                    {
                        bool allowAuth = !string.IsNullOrWhiteSpace(p.Login)
                            && link.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase)
                            && !(start.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !link.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
                        var probeItem = new DownloadItem
                        {
                            Url = file.Url,
                            Referrer = file.FoundOn,
                            Login = allowAuth ? p.Login : "",
                            Password = allowAuth ? p.Password : "",
                        };
                        var probe = await HttpDownloader.ProbeAsync(probeItem, ct);
                        file.Size = probe.Size;
                        if (p.MinSizeKB > 0 && (probe.Size <= 0 || probe.Size < p.MinSizeKB * 1024L)) continue;
                        if (p.MaxSizeKB > 0 && probe.Size > 0 && probe.Size > p.MaxSizeKB * 1024L) continue;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        if (p.MinSizeKB > 0) continue;
                    }
                }
                Stats.IncMatched();
                FileFound?.Invoke(file);
            }
        }
    }

    bool IsFileMask(string ext)
        => ext.Length > 0 && Masks(p.FileMasks).Any(m => !m.EndsWith(".*") && Wildcard.Match(m, "x." + ext));

    async Task<string> FetchPageAsync(Uri uri, Uri startUri, CancellationToken ct, int hop = 0)
    {
        if (hop > 15) return "";
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", HttpDownloader.DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
        if (!string.IsNullOrWhiteSpace(p.Login)
            && uri.Host.Equals(startUri.Host, StringComparison.OrdinalIgnoreCase)
            && !(startUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{p.Login}:{p.Password}")));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await HttpDownloader.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
        {
            var nextUri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
            if (!HttpDownloader.IsAllowedRedirectTarget(uri, nextUri) || !HttpDownloader.IsAllowedRedirectTarget(startUri, nextUri)) return "";
            return await FetchPageAsync(nextUri, startUri, ct, hop + 1);
        }
        resp.EnsureSuccessStatusCode();
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("html") && !type.Contains("xml")) return "";
        const int maxBytes = 8 * 1024 * 1024;
        if (resp.Content.Headers.ContentLength > maxBytes) return "";
        await using var s = await resp.Content.ReadAsStreamAsync(timeout.Token);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int total = 0;
        while (true)
        {
            int n = await s.ReadAsync(buf, timeout.Token);
            if (n <= 0) break;
            total += n;
            if (total > maxBytes) return "";
            ms.Write(buf, 0, n);
        }
        var charset = resp.Content.Headers.ContentType?.CharSet;
        var enc = System.Text.Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { enc = System.Text.Encoding.GetEncoding(charset.Trim('"', '\'')); }
            catch (ArgumentException) { /* unsupported charset header; fall back to UTF-8 */ }
        }
        return enc.GetString(ms.ToArray());
    }

    static IEnumerable<string> Masks(string s) => s.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim());

    static bool MatchesMasks(Uri link, string include, string exclude)
    {
        var name = FileNames.FromUrl(link.AbsoluteUri);
        if (!Path.HasExtension(name)) return false;
        var inc = Masks(include).ToList();
        if (inc.Count > 0 && !inc.Any(m => Wildcard.Match(m, name))) return false;
        return !Masks(exclude).Any(m => Wildcard.Match(m, name));
    }

    static bool PathAllowed(Uri link, string only, string skip)
    {
        var s = link.Host + link.AbsolutePath;
        var o = only.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (o.Length > 0 && !o.Any(x => Wildcard.Match("*" + x.Replace("http://", "").Replace("https://", "") + "*", s))) return false;
        return !skip.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => Wildcard.Match("*" + x.Replace("http://", "").Replace("https://", "") + "*", s));
    }

    static string Key(Uri u) => u.GetLeftPart(UriPartial.Query).TrimEnd('/');

    static string MainDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length >= 3 && parts[^1].Length == 2
            && parts[^2] is "co" or "org" or "ac" or "gov" or "net" or "edu" or "com")
            return string.Join('.', parts[^3..]);
        return parts.Length <= 2 ? host : string.Join('.', parts[^2..]);
    }
}
