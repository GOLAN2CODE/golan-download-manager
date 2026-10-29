using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

/// <summary>A download category (IDM "Save to" categories): extensions and sites decide membership, Folder is where files go.</summary>
public sealed class CategoryDef
{
    string _name = "";

    public string Key { get; set; } = "";
    public string Name
    {
        get => (BuiltIn && !string.IsNullOrEmpty(Key)) || _name.StartsWith("core.category.", StringComparison.Ordinal)
            ? Loc.T(!string.IsNullOrEmpty(Key) ? "core.category." + Key : _name)
            : _name;
        set => _name = value;
    }
    public string Glyph { get; set; } = "";
    /// <summary>Space separated extensions, e.g. "mp4 mkv avi".</summary>
    public string Extensions { get; set; } = "";
    /// <summary>Space separated site patterns (wildcards allowed). Empty = any site.</summary>
    public string Sites { get; set; } = "";
    /// <summary>Absolute folder; empty = DownloadRoot\Name.</summary>
    public string Folder { get; set; } = "";
    public bool RememberLastPath { get; set; }
    public string LastPath { get; set; } = "";
    public bool BuiltIn { get; set; }

    [JsonIgnore] public string[] ExtensionArray => Split(Extensions);
    [JsonIgnore] public string[] SiteArray => Split(Sites);
    static string[] Split(string s) => s.Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToArray();
}

/// <summary>A download queue with its own scheduler (IDM "Scheduler" dialog).</summary>
public sealed class QueueDef
{
    public static readonly Guid MainId = new("a0a0a0a0-0000-4000-8000-000000000001");
    public static readonly Guid SyncId = new("a0a0a0a0-0000-4000-8000-000000000002");

    string _name = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name
    {
        get => IsMain ? Loc.T("core.queue.main")
             : IsSync ? Loc.T("core.queue.sync")
             : _name.StartsWith("core.queue.", StringComparison.Ordinal) ? Loc.T(_name)
             : _name;
        set => _name = value;
    }
    public int MaxSimultaneous { get; set; } = 3;

    // Schedule
    public bool ScheduleEnabled { get; set; }
    /// <summary>Periodic synchronization: re-check finished files and download the changed ones.</summary>
    public bool Periodic { get; set; }
    public string StartTime { get; set; } = "02:00";
    /// <summary>true = daily on <see cref="Days"/>, false = once on <see cref="OnceDate"/>.</summary>
    public bool Daily { get; set; } = true;
    public DateTime? OnceDate { get; set; }
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    public bool RepeatEnabled { get; set; }
    public int RepeatHours { get; set; } = 2;
    public int RepeatMinutes { get; set; }
    public bool StopEnabled { get; set; }
    public string StopTime { get; set; } = "08:00";
    public bool StartOnStartup { get; set; }

    // Processing
    public int Retries { get; set; } = 10;
    public string OpenWhenDone { get; set; } = "";
    public bool ExitWhenDone { get; set; }
    public bool ShutdownWhenDone { get; set; }
    public bool ForceProcesses { get; set; }

    public DateTime? LastStart { get; set; }
    public DateTime? LastStop { get; set; }

    [JsonIgnore] public bool IsMain => Id == MainId;
    [JsonIgnore] public bool IsSync => Id == SyncId;
    [JsonIgnore] public bool BuiltIn => IsMain || IsSync;
}

/// <summary>Saved login for a server or a server path (IDM "Sites Logins"). The password is protected with DPAPI.</summary>
public sealed class SiteLogin
{
    public string Pattern { get; set; } = "";
    public string User { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(ProtectedPassword);
        set => ProtectedPassword = Secret.Protect(value);
    }
}

/// <summary>Per-server connection cap (IDM "Max. connections number for a server").</summary>
public sealed class ServerLimit
{
    public string Pattern { get; set; } = "";
    public int MaxConnections { get; set; } = 2;
}

public sealed class ColumnPref
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double Width { get; set; }
}

/// <summary>Windows DPAPI helpers: secrets are readable only by the current Windows user.</summary>
public static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Golan.GDM.v1");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static string Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

/// <summary>Case-insensitive "*" wildcard matching used for sites, servers and addresses.</summary>
public static class Wildcard
{
    public static bool Match(string pattern, string text)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var rx = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try
        {
            return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Host match: "example.com" also matches "www.example.com" and "dl.example.com".</summary>
    public static bool MatchHost(string pattern, string host)
    {
        pattern = pattern.Trim().ToLowerInvariant();
        if (pattern.Contains("://") && Uri.TryCreate(pattern, UriKind.Absolute, out var u)) pattern = u.Host;
        host = host.ToLowerInvariant();
        if (pattern.Contains('*')) return Match(pattern, host);
        return host == pattern || host.EndsWith("." + pattern);
    }

    public static IEnumerable<string> SplitList(string? s)
        => (s ?? "").Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
}

public sealed partial class AppSettings
{
    /// <summary>Settings file schema version (`2` for Golan v2).</summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>Default root directory for downloaded files.</summary>
    public string DownloadRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Golan");

    /// <summary>Place downloads into per-category subfolders under <see cref="DownloadRoot"/> when no explicit category folder is set.</summary>
    public bool UseCategoryFolders { get; set; } = true;

    /// <summary>Default number of parallel HTTP segment connections per download (`1..32`).</summary>
    public int MaxConnections { get; set; } = 8;

    // ---- Downloads tab ----
    /// <summary>Open the per-download progress window when a download starts.</summary>
    public bool ShowProgressWindow { get; set; } = true;
    /// <summary>Show the "Download Complete" dialog when an interactive download finishes.</summary>
    public bool ShowCompleteDialog { get; set; } = true;
    /// <summary>Show the "Add Download" confirmation dialog when a download is captured from a browser.</summary>
    public bool ShowAddDialogFromBrowser { get; set; } = true;
    /// <summary>Start downloading while the "Download File Info" dialog is still open.</summary>
    public bool StartImmediatelyWhileDialog { get; set; }
    /// <summary>Show queue selection options when clicking "Download Later".</summary>
    public bool ShowQueuePanelOnLater { get; set; } = true;
    /// <summary>ask | number | overwrite | resume</summary>
    public string DuplicateAction { get; set; } = "ask";
    /// <summary>Custom HTTP `User-Agent` header; empty uses the default browser User-Agent.</summary>
    public string ManualUserAgent { get; set; } = "";
    /// <summary>Prompt for confirmation before deleting items from the download list.</summary>
    public bool ConfirmDelete { get; set; } = true;

    // ---- Virus checking ----
    /// <summary>Run an external antivirus scanner on completed files matching <see cref="VirusExtensions"/>.</summary>
    public bool VirusScanEnabled { get; set; }
    /// <summary>Executable path of the antivirus command-line scanner.</summary>
    public string VirusScanner { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
    /// <summary>Command-line arguments passed to <see cref="VirusScanner"/> (`[FILE]` is replaced with the downloaded file path).</summary>
    public string VirusArgs { get; set; } = "-Scan -ScanType 3 -File \"[FILE]\" -DisableRemediation";
    /// <summary>Space-separated file extensions that trigger automatic virus scanning on completion.</summary>
    public string VirusExtensions { get; set; } = "exe msi msu msix appx zip rar 7z cab apk bat cmd ps1 hta js vbs scr com jar iso docm xlsm pptm";

    // ---- Browser / file types ----
    /// <summary>Intercept matching file downloads initiated in supported browsers.</summary>
    public bool InterceptBrowser { get; set; } = true;
    /// <summary>Space-separated file extensions automatically captured from browsers.</summary>
    public string CatchExtensions { get; set; } =
        "3gp 7z aac ace aif apk arj asf avi bin bz2 exe gz gzip img iso lzh m4a m4v mkv mov mp3 mp4 " +
        "mpa mpe mpeg mpg msi msu ogg ogv pdf pps ppt pptx qt ra rar rm rmvb sea sit sitx tar tif tiff " +
        "wav webm wma wmv xz z zip zst flac dmg deb rpm cab msix appx docx xlsx epub torrent";
    /// <summary>Catch any browser download at least this big (MB). 0 = only by extension.</summary>
    public int MinSizeMB { get; set; } = 0;
    /// <summary>Don't capture downloads from these sites (wildcards).</summary>
    public string ExceptionSites { get; set; } = "";
    /// <summary>Don't capture downloads from these addresses (wildcards).</summary>
    public string ExceptionAddresses { get; set; } = "";
    /// <summary>Offer to add a host to <see cref="ExceptionSites"/> when the user cancels two downloads from the same host.</summary>
    public bool TwiceCancelPrompt { get; set; } = true;
    /// <summary>Extensions that start downloading without showing the "Download File Info" dialog.</summary>
    public string AutoStartExtensions { get; set; } = "";

    // ---- Media panel (browser extension) ----
    /// <summary>Show the floating media download button over web videos in the browser extension.</summary>
    public bool MediaPanelEnabled { get; set; } = true;
    /// <summary>Minimum detected media size in KB required before showing the browser media panel (`0` = any size).</summary>
    public int MediaPanelMinSizeKB { get; set; } = 0;
    /// <summary>Wildcard host patterns where the browser media panel is suppressed.</summary>
    public string MediaPanelExceptions { get; set; } = "";
    /// <summary>Context menu items shown by the extension: link, all, video, image.</summary>
    public List<string> ExtensionMenuItems { get; set; } = ["link", "all", "video", "image"];

    // ---- General ----
    /// <summary>Monitor the Windows clipboard for copied download URLs matching configured extensions.</summary>
    public bool ClipboardMonitor { get; set; } = true;
    /// <summary>Launch Golan in the system tray when Windows starts.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>Minimize Golan to the system tray instead of exiting when closing the main window.</summary>
    public bool CloseToTray { get; set; } = true;

    // ---- Speed limiter & quota ----
    /// <summary>Enable the global download speed limiter.</summary>
    public bool SpeedLimitEnabled { get; set; }
    /// <summary>Global download speed limit in KB/s when <see cref="SpeedLimitEnabled"/> is true.</summary>
    public int SpeedLimitKB { get; set; } = 500;
    /// <summary>Keep <see cref="SpeedLimitEnabled"/> active across application restarts.</summary>
    public bool SpeedLimitAlwaysOnStartup { get; set; }
    /// <summary>Enable the periodic download volume quota.</summary>
    public bool QuotaEnabled { get; set; }
    /// <summary>Maximum download volume in MB allowed per quota window.</summary>
    public int QuotaMB { get; set; } = 1024;
    /// <summary>Duration of the quota window in hours.</summary>
    public int QuotaHours { get; set; } = 1;
    /// <summary>Notify the user when the download quota is reached.</summary>
    public bool QuotaWarn { get; set; } = true;

    // ---- Connection ----
    /// <summary>Per-host connection caps (`Pattern` -> `MaxConnections`).</summary>
    public List<ServerLimit> ServerLimits { get; set; } = [];

    // ---- Proxy ----
    /// <summary>none | system | manual</summary>
    public string ProxyMode { get; set; } = "system";
    /// <summary>http | socks5 | socks4</summary>
    public string ProxyType { get; set; } = "http";
    /// <summary>Manual proxy server hostname or IP address.</summary>
    public string ProxyHost { get; set; } = "";
    /// <summary>Manual proxy server TCP port.</summary>
    public int ProxyPort { get; set; } = 8080;
    /// <summary>Optional username for manual proxy authentication.</summary>
    public string ProxyUser { get; set; } = "";
    /// <summary>DPAPI-protected password for manual proxy authentication.</summary>
    public string ProxyProtectedPassword { get; set; } = "";
    /// <summary>Space/semicolon-separated host patterns that bypass the manual proxy.</summary>
    public string ProxyBypass { get; set; } = "localhost 127.0.0.1";

    /// <summary>Plain-text manual proxy password (protected via DPAPI in <see cref="ProxyProtectedPassword"/> when serialized).</summary>
    [JsonIgnore]
    public string ProxyPassword { get => Secret.Unprotect(ProxyProtectedPassword); set => ProxyProtectedPassword = Secret.Protect(value); }

    // ---- Logins ----
    /// <summary>Saved HTTP Basic authentication credentials per host/path pattern.</summary>
    public List<SiteLogin> Logins { get; set; } = [];

    // ---- Save to ----
    /// <summary>User-configurable download categories and extension/site matching rules.</summary>
    public List<CategoryDef> CategoryDefs { get; set; } = [];
    /// <summary>Folder for partial files; empty = next to the final file.</summary>
    public string TempDir { get; set; } = "";
    /// <summary>Apply the HTTP `Last-Modified` server timestamp to completed files.</summary>
    public bool UseServerFileTime { get; set; }

    // ---- Queues ----
    /// <summary>Configured download queues (always includes `MainQueue` and `SyncQueue` after <see cref="Normalize"/>).</summary>
    public List<QueueDef> Queues { get; set; } = [];
    /// <summary>none | shutdown | sleep | exit — legacy single-queue action, kept for the main queue.</summary>
    public string AfterQueue { get; set; } = "none";

    // ---- Sounds ----
    /// <summary>Play custom or system `.wav` sounds on download/queue events.</summary>
    public bool SoundsEnabled { get; set; }
    /// <summary>Event key (complete, failed, queueStart, queueDone) → .wav path. Empty path = Windows default sound.</summary>
    public Dictionary<string, string> Sounds { get; set; } = new() { ["complete"] = "", ["failed"] = "", ["queueStart"] = "", ["queueDone"] = "" };

    // ---- Video ----
    /// <summary>best | 2160 | 1440 | 1080 | 720 | 480 | 360 | mp3 | m4a</summary>
    public string DefaultVideoQuality { get; set; } = "best";

    // ---- View ----
    /// <summary>Saved main-list column visibility, order, and pixel widths.</summary>
    public List<ColumnPref> Columns { get; set; } = [];
    /// <summary>Property name by which the main download list is sorted.</summary>
    public string SortBy { get; set; } = "AddedAt";
    /// <summary>Sort the main download list in descending order when true.</summary>
    public bool SortDescending { get; set; } = true;
    /// <summary>Hide the left-hand categories/queues sidebar in the main window.</summary>
    public bool HideCategories { get; set; }
    /// <summary>Use compact toolbar buttons in the main window.</summary>
    public bool SmallToolbar { get; set; }
    /// <summary>Use the dark color theme when true, or the light theme when false.</summary>
    public bool DarkMode { get; set; }
    /// <summary>Show the floating always-on-top drag-and-drop target window.</summary>
    public bool ShowDropTarget { get; set; }
    /// <summary>Saved horizontal screen position of the floating drop target (`-1` = default).</summary>
    public double DropTargetLeft { get; set; } = -1;
    /// <summary>Saved vertical screen position of the floating drop target (`-1` = default).</summary>
    public double DropTargetTop { get; set; } = -1;

    /// <summary>auto | ar | en — applied at startup.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>True until the first-run welcome dialog has been shown.</summary>
    public bool FirstRun { get; set; } = true;

    // ---- legacy v1 fields (read once, then migrated) ----
    /// <summary>Legacy v1 simultaneous download limit; migrated into <see cref="MainQueue"/> by <see cref="Normalize"/>.</summary>
    public int? MaxSimultaneous { get; set; }
    /// <summary>Legacy v1 scheduler flag; migrated into <see cref="MainQueue"/> by <see cref="Normalize"/>.</summary>
    public bool? SchedulerEnabled { get; set; }
    /// <summary>Legacy v1 schedule start time; migrated into <see cref="MainQueue"/> by <see cref="Normalize"/>.</summary>
    public string? ScheduleStart { get; set; }
    /// <summary>Legacy v1 schedule stop time; migrated into <see cref="MainQueue"/> by <see cref="Normalize"/>.</summary>
    public string? ScheduleStop { get; set; }

    [JsonIgnore]
    public IEnumerable<string> ExtensionList =>
        CatchExtensions.Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Distinct();

    [JsonIgnore] public QueueDef MainQueue => Queues.First(q => q.IsMain);
    [JsonIgnore] public QueueDef SyncQueue => Queues.First(q => q.IsSync);

    public QueueDef? FindQueue(Guid? id) => id == null ? null : Queues.FirstOrDefault(q => q.Id == id);

    public string FolderFor(string category)
    {
        var def = Categories.Get(category);
        if (def.RememberLastPath && Directory.Exists(def.LastPath)) return def.LastPath;
        if (!string.IsNullOrWhiteSpace(def.Folder)) return def.Folder;
        return UseCategoryFolders ? Path.Combine(DownloadRoot, def.Name) : DownloadRoot;
    }

    public SiteLogin? LoginFor(Uri uri) => HttpDownloader.MatchSiteLogin(uri, Logins);

    public int? ServerLimitFor(string host)
        => ServerLimits.FirstOrDefault(s => Wildcard.MatchHost(s.Pattern, host))?.MaxConnections;

    public bool IsException(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (Wildcard.SplitList(ExceptionSites).Any(p => Wildcard.MatchHost(p, u.Host))) return true;
        return Wildcard.SplitList(ExceptionAddresses).Any(p => Wildcard.Match(p, url));
    }

    public bool AutoStarts(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext.Length > 0 && Wildcard.SplitList(AutoStartExtensions).Any(e => e.TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fills defaults and migrates v1 files.</summary>
    public void Normalize()
    {
        if (CategoryDefs.Count == 0) CategoryDefs = Categories.Defaults();
        foreach (var d in Categories.Defaults())
            if (CategoryDefs.All(c => c.Key != d.Key)) CategoryDefs.Add(d);
        // "general" stays last so specific categories win.
        var general = CategoryDefs.First(c => c.Key == "general");
        CategoryDefs.Remove(general);
        CategoryDefs.Add(general);

        if (Queues.All(q => !q.IsMain))
        {
            var main = new QueueDef { Id = QueueDef.MainId, Name = Loc.T("core.queue.main"), MaxSimultaneous = MaxSimultaneous ?? 3 };
            if (SchedulerEnabled == true)
            {
                main.ScheduleEnabled = true;
                main.StartTime = ScheduleStart ?? "02:00";
                main.StopEnabled = !string.IsNullOrWhiteSpace(ScheduleStop);
                main.StopTime = string.IsNullOrWhiteSpace(ScheduleStop) ? "08:00" : ScheduleStop!;
            }
            main.ShutdownWhenDone = AfterQueue == "shutdown";
            main.ExitWhenDone = AfterQueue == "exit";
            Queues.Insert(0, main);
        }
        if (Queues.All(q => !q.IsSync))
            Queues.Insert(1, new QueueDef { Id = QueueDef.SyncId, Name = Loc.T("core.queue.sync"), Periodic = true, Daily = true, RepeatEnabled = true, RepeatHours = 6 });
        MaxSimultaneous = null;
        SchedulerEnabled = null;
        ScheduleStart = null;
        ScheduleStop = null;
        SchemaVersion = 2;

        foreach (var k in new[] { "complete", "failed", "queueStart", "queueDone" })
            Sounds.TryAdd(k, "");
        Categories.Use(CategoryDefs);
    }

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json.Options) ?? new()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
            s = new AppSettings();
        }
        s.Normalize();
        return s;
    }

    public void Save()
    {
        try { Json.WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json.Options)); }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }

    public AppSettings Clone()
    {
        var c = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
        c.Normalize();
        Categories.Use(CategoryDefs); // Normalize on the clone must not steal the live list
        return c;
    }
}

/// <summary>Facade over the user's editable categories (kept in <see cref="AppSettings.CategoryDefs"/>).</summary>
public static class Categories
{
    static List<CategoryDef> _defs = Defaults();

    public static IReadOnlyList<CategoryDef> All => _defs;

    public static void Use(List<CategoryDef> defs) => _defs = defs;

    public static List<CategoryDef> Defaults() =>
    [
        new() { Key = "video", Name = Loc.T("core.category.video"), Glyph = "", BuiltIn = true,
            Extensions = "mp4 mkv avi mov wmv flv webm m4v 3gp mpg mpeg ts ogv rm rmvb asf m3u8" },
        new() { Key = "music", Name = Loc.T("core.category.music"), Glyph = "", BuiltIn = true,
            Extensions = "mp3 wav wma aac m4a flac ogg opus aif mpa ra" },
        new() { Key = "compressed", Name = Loc.T("core.category.compressed"), Glyph = "", BuiltIn = true,
            Extensions = "zip rar 7z tar gz gzip bz2 xz zst iso img cab arj lzh ace z sit sitx sea" },
        new() { Key = "documents", Name = Loc.T("core.category.documents"), Glyph = "", BuiltIn = true,
            Extensions = "pdf doc docx xls xlsx ppt pptx pps txt rtf epub odt csv tif tiff" },
        new() { Key = "programs", Name = Loc.T("core.category.programs"), Glyph = "", BuiltIn = true,
            Extensions = "exe msi msu apk dmg msix appx deb rpm bin bat jar" },
        new() { Key = "general", Name = Loc.T("core.category.general"), Glyph = "", BuiltIn = true },
    ];

    public static CategoryDef Get(string? key) => _defs.FirstOrDefault(c => c.Key == key) ?? _defs.First(c => c.Key == "general");

    /// <summary>Category for a file: a matching site rule wins, then the extension.</summary>
    public static string Detect(string fileName, string? url = null)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        string? host = null;
        if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var u)) host = u.Host;

        foreach (var c in _defs)
        {
            var sites = c.SiteArray;
            if (sites.Length == 0 || host == null) continue;
            if (sites.Any(s => Wildcard.MatchHost(s, host)) && (c.ExtensionArray.Length == 0 || c.ExtensionArray.Contains(ext)))
                return c.Key;
        }
        foreach (var c in _defs)
        {
            if (c.SiteArray.Length > 0) continue;
            if (c.ExtensionArray.Contains(ext)) return c.Key;
        }
        return "general";
    }

    public static string NewKey() => "cat-" + Guid.NewGuid().ToString("N")[..8];
}
