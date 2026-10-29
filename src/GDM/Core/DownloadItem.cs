using System.Text.Json.Serialization;

namespace Golan.Gdm.Core;

public enum DownloadStatus { Queued, Connecting, Downloading, Merging, Paused, Completed, Error }

public enum DownloadKind { Http, Media, Torrent }

/// <summary>A byte range of the file handled by one connection. End is inclusive (-1 = unknown).</summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; } = -1;
    public long Downloaded { get; set; }

    [JsonIgnore] public bool Active { get; set; }
    /// <summary>Live status line for the connections list ("receiving data", "connecting", ...).</summary>
    [JsonIgnore] public string Info { get; set; } = "";
    [JsonIgnore] public long Position => Start + Downloaded;
    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : End - Start + 1 - Downloaded;
    [JsonIgnore] public long Length => End < 0 ? -1 : End - Start + 1;
}

public sealed partial class DownloadItem : ObservableObject
{
    /// <summary>Folder for partial files when the user picked one in Options (empty = next to the final file).</summary>
    public static string TempRoot { get; set; } = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    string _url = "";
    /// <summary>The address the user (or the browser) gave. Redirect targets are never stored here.</summary>
    public string Url { get => _url; set { if (Set(ref _url, value)) ResolvedUrl = null; } }

    /// <summary>Where <see cref="Url"/> redirected to in this session; expiring links are re-resolved from Url.</summary>
    [JsonIgnore] public string? ResolvedUrl { get; set; }

    public string Referrer { get; set; } = "";
    /// <summary>Page the download was started from (IDM "parent web page").</summary>
    public string ParentPage { get; set; } = "";
    public string UserAgent { get; set; } = "";

    string _description = "";
    public string Description { get => _description; set => Set(ref _description, value); }

    // ---- cookies: kept encrypted on disk, sent only to the host they belong to ----
    public string ProtectedCookies { get; set; } = "";
    /// <summary>Host the cookies were read for; they are never sent to another host.</summary>
    public string CookieHost { get; set; } = "";

    [JsonIgnore]
    public string Cookies
    {
        get => Secret.Unprotect(ProtectedCookies);
        set => ProtectedCookies = Secret.Protect(value);
    }

    // ---- per-download login ----
    public string Login { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    [JsonIgnore] public string Password { get => Secret.Unprotect(ProtectedPassword); set => ProtectedPassword = Secret.Protect(value); }

    string _fileName = "";
    public string FileName
    {
        get => _fileName;
        set { if (Set(ref _fileName, value)) { OnPropertyChanged(nameof(FullPath)); OnPropertyChanged(nameof(Extension)); } }
    }

    /// <summary>True when the user typed the name, so server names never override it.</summary>
    public bool NameLocked { get; set; }

    string _saveDir = "";
    public string SaveDir
    {
        get => _saveDir;
        set { if (Set(ref _saveDir, value)) OnPropertyChanged(nameof(FullPath)); }
    }

    string _category = "general";
    public string Category { get => _category; set { if (Set(ref _category, value)) OnPropertyChanged(nameof(Glyph)); } }

    long _totalSize = -1;
    public long TotalSize
    {
        get => _totalSize;
        set { if (Set(ref _totalSize, value)) { OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(Progress)); } }
    }

    long _downloaded;
    public long Downloaded
    {
        get => Interlocked.Read(ref _downloaded);
        set => Interlocked.Exchange(ref _downloaded, value);
    }

    public void AddDownloaded(long n) => Interlocked.Add(ref _downloaded, n);

    DownloadStatus _status = DownloadStatus.Queued;
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            if (value != DownloadStatus.Downloading && value != DownloadStatus.Merging) { Speed = 0; Eta = null; ActiveConnections = 0; }
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsFinished));
        }
    }

    string _error = "";
    public string ErrorMessage { get => _error; set { if (Set(ref _error, value)) OnPropertyChanged(nameof(StatusText)); } }

    /// <summary>Result of the last attempt as shown in Properties ("اكتمل", "خطأ: …", "ما في تغيير").</summary>
    public string LastResult { get; set; } = "";

    public DateTime AddedAt { get; set; } = DateTime.Now;
    DateTime? _completedAt;
    public DateTime? CompletedAt { get => _completedAt; set => Set(ref _completedAt, value); }
    DateTime? _lastTry;
    public DateTime? LastTry { get => _lastTry; set => Set(ref _lastTry, value); }

    public int MaxConnections { get; set; } = 8;

    bool _resumable;
    public bool Resumable { get => _resumable; set => Set(ref _resumable, value); }

    /// <summary>Server validators used for If-Range on resume and for the synchronization queue.</summary>
    public string ETag { get; set; } = "";
    public DateTimeOffset? LastModified { get; set; }

    public List<Segment> Segments { get; set; } = [];

    public DownloadKind Kind { get; set; } = DownloadKind.Http;
    /// <summary>yt-dlp quality key (best, 1080, mp3, ...) for media downloads.</summary>
    public string MediaQuality { get; set; } = "best";
    public string MediaCookieFile { get; set; } = "";

    // ---- queue ----
    Guid? _queueId;
    public Guid? QueueId
    {
        get => _queueId;
        set { if (Set(ref _queueId, value)) OnPropertyChanged(nameof(InQueue)); }
    }

    /// <summary>Order inside the queue (lower runs first).</summary>
    public long QueuePosition { get; set; }
    public int RetryCount { get; set; }

    /// <summary>Why the last attempt failed in this session (Expired = the link needs a fresh address).</summary>
    [JsonIgnore] public FailKind LastFailKind { get; set; }

    /// <summary>The address was refreshed by hand: before resuming, the new server must prove it serves the same bytes.</summary>
    public bool AddressRefreshed { get; set; }

    [JsonIgnore]
    public bool InQueue
    {
        get => QueueId != null;
        set => QueueId = value ? QueueId ?? QueueDef.MainId : null;
    }

    /// <summary>Reads the v1 "InQueue" flag.</summary>
    [JsonPropertyName("InQueue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyInQueue { get => null; set { if (value == true && QueueId == null) QueueId = QueueDef.MainId; } }

    // ---- per-file speed limit and completion options (IDM progress dialog tabs) ----
    int _speedLimitKB;
    /// <summary>Per-file speed limit in KB/s, 0 = none.</summary>
    public int SpeedLimitKB { get => _speedLimitKB; set { if (Set(ref _speedLimitKB, value)) FileLimiter.BytesPerSecond = Math.Max(1, value) * 1024L; FileLimiter.Enabled = value > 0; } }
    public bool RememberSpeedLimit { get; set; }
    public bool OnDoneExit { get; set; }
    public bool OnDoneShutdown { get; set; }
    public bool OnDoneForce { get; set; }
    /// <summary>null = follow the global "show download complete dialog" option.</summary>
    public bool? OnDoneShowDialog { get; set; }

    // ---- live (not persisted) ----
    [JsonIgnore] public readonly object SegLock = new();
    [JsonIgnore] public SpeedLimiter FileLimiter { get; } = new();
    /// <summary>Set when the user (or the scheduler) stopped the download, so it does not count as a finished queue item.</summary>
    [JsonIgnore] public bool StoppedByUser { get; set; }
    /// <summary>Set when a stopped queue item should go back to Queued.</summary>
    [JsonIgnore] public bool RequeueOnStop { get; set; }

    double _speed;
    [JsonIgnore] public double Speed { get => _speed; set { if (Set(ref _speed, value)) OnPropertyChanged(nameof(SpeedText)); } }

    TimeSpan? _eta;
    [JsonIgnore] public TimeSpan? Eta { get => _eta; set { if (Set(ref _eta, value)) OnPropertyChanged(nameof(EtaText)); } }

    int _activeConnections;
    [JsonIgnore] public int ActiveConnections { get => _activeConnections; set => Set(ref _activeConnections, value); }

    string _stage = "";
    /// <summary>Extra state text for media downloads (e.g. "الجزء 2").</summary>
    [JsonIgnore] public string Stage { get => _stage; set { if (Set(ref _stage, value)) OnPropertyChanged(nameof(StatusText)); } }

    public string ActiveTempPath { get; set; } = "";

    [JsonIgnore] public string FullPath => Path.Combine(SaveDir, FileName);

    [JsonIgnore]
    public string TempPath
    {
        get
        {
            var configured = string.IsNullOrWhiteSpace(TempRoot) ? FullPath + ".gdm" : Path.Combine(TempRoot, $"{Id:N}.gdm");
            if (File.Exists(configured)) return ActiveTempPath = configured;
            if (!string.IsNullOrWhiteSpace(ActiveTempPath) && File.Exists(ActiveTempPath)) return ActiveTempPath;
            var adjacent = FullPath + ".gdm";
            if (File.Exists(adjacent)) return ActiveTempPath = adjacent;
            return ActiveTempPath = configured;
        }
    }

    [JsonIgnore] public string Extension => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
    [JsonIgnore] public string Glyph => Categories.Get(Category).Glyph;
    [JsonIgnore] public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : "";
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Merging;
    [JsonIgnore] public bool IsFinished => Status == DownloadStatus.Completed;

    [JsonIgnore]
    public double Progress => Status == DownloadStatus.Completed ? 100
        : TotalSize > 0 ? Math.Min(100, Downloaded * 100.0 / TotalSize) : 0;

    [JsonIgnore]
    public string SizeText => TotalSize > 0 ? Fmt.Size(TotalSize)
        : Downloaded > 0 ? Fmt.Size(Downloaded) : "";

    [JsonIgnore] public string SpeedText => IsActive ? Fmt.Speed(Speed) : "";
    [JsonIgnore] public string EtaText => IsActive ? Fmt.Eta(Eta) : "";

    [JsonIgnore]
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => Loc.T("core.status.queued"),
        DownloadStatus.Connecting => Loc.T("core.status.connecting"),
        DownloadStatus.Downloading => TotalSize > 0 ? $"{Fmt.Percent(Progress)}{(Stage.Length > 0 ? " · " + Stage : "")}" : (Stage.Length > 0 ? Stage : Loc.T("core.status.downloading")),
        DownloadStatus.Merging => Loc.T("core.status.merging"),
        DownloadStatus.Paused => TotalSize > 0 && Downloaded > 0 ? Loc.T("core.status.paused_pct", Progress.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) : Loc.T("core.status.paused"),
        DownloadStatus.Completed => Loc.T("core.status.completed"),
        DownloadStatus.Error => Loc.T("core.status.error", ErrorMessage),
        _ => "",
    };

    /// <summary>Raises change notifications for the values that move while downloading.</summary>
    public void RefreshLive()
    {
        OnPropertyChanged(nameof(Downloaded));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
    }

    public void ResetProgress()
    {
        lock (SegLock) Segments.Clear();
        Downloaded = 0;
        TotalSize = -1;
        Resumable = false;
        ETag = "";
        LastModified = null;
        ActiveTempPath = "";
        AddressRefreshed = false;
    }

    /// <summary>Cookie header for a request to <paramref name="uri"/>: only for the exact host the cookies were read for, without HTTPS-to-HTTP downgrade.</summary>
    public string CookiesFor(Uri uri) => HttpDownloader.CookiesForRequest(this, uri);
}
