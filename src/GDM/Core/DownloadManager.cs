using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace Golan.Gdm.Core;

/// <summary>Owns the download list, running tasks, the queues and persistence. All public members run on the UI thread.</summary>
public sealed partial class DownloadManager
{
    public ObservableCollection<DownloadItem> Items { get; } = [];
    public AppSettings Settings { get; }
    public SpeedLimiter Limiter { get; } = new();
    public SpeedHistory SpeedHistory { get; } = new(600);
    public StatsStore Stats { get; } = new(autoFlushTimer: true);
    public Func<DownloadItem, string?>? AskArchivePassword { get; set; }

    sealed class Running
    {
        public required CancellationTokenSource Cts;
        public Task? Task;
    }

    readonly object _stateLock = new();
    readonly object _saveLock = new();
    readonly Dictionary<Guid, Running> _running = [];
    readonly Dictionary<Guid, (long Bytes, DateTime At)> _samples = [];
    readonly HashSet<Guid> _runningQueues = [];
    readonly List<(DateTime At, long Bytes)> _quotaLog = [];
    readonly HashSet<Guid> _quotaPausedQueues = [];
    readonly Dictionary<Guid, DispatcherTimer> _retryTimers = [];
    readonly DispatcherTimer _timer;
    DateTime _lastSave = DateTime.MinValue;
    int _tickCounter;
    bool _dirty;
    bool _shuttingDown;

    void CancelRetry(Guid id)
    {
        DispatcherTimer? t;
        lock (_stateLock)
        {
            _retryTimers.Remove(id, out t);
        }
        t?.Stop();
    }

    public event Action<DownloadItem>? ItemStarted;
    public event Action<DownloadItem>? ItemCompleted;
    public event Action<DownloadItem>? ItemFailed;
    public event Action<QueueDef>? QueueStarted;
    public event Action<QueueDef>? QueueFinished;
    public event Action<bool>? QuotaChanged;
    public event Action? Ticked;

    public DownloadManager(AppSettings settings)
    {
        Settings = settings;
        if (settings.SpeedLimitAlwaysOnStartup) settings.SpeedLimitEnabled = true;
        HttpDownloader.Configure(settings);
        ApplyLimiter();
        Load();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public int ActiveCount { get { lock (_stateLock) return _running.Count; } }
    public double TotalSpeed => Items.Where(i => i.IsActive).Sum(i => i.Speed);
    public bool QuotaBlocked { get; private set; }

    /// <summary>True when any queue is processing.</summary>
    public bool QueueRunning { get { lock (_stateLock) return _runningQueues.Count > 0; } }
    public bool IsQueueRunning(Guid id) { lock (_stateLock) return _runningQueues.Contains(id); }
    public bool IsRunning(DownloadItem item) { lock (_stateLock) return _running.ContainsKey(item.Id); }

    public void ApplyLimiter()
    {
        Limiter.Enabled = Settings.SpeedLimitEnabled;
        Limiter.BytesPerSecond = Math.Max(1, Settings.SpeedLimitKB) * 1024L;
    }

    /// <summary>Re-applies proxy / temp folder / limiter after the options changed.</summary>
    public void ApplySettings()
    {
        HttpDownloader.Configure(Settings);
        Categories.Use(Settings.CategoryDefs);
        ApplyLimiter();
    }

    // ---------- list operations ----------

    static FailKind FailKindOf(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex is DownloadException d) return d.Kind;
        return FailKind.General;
    }

    public DownloadItem CreateHttp(string url, string referrer = "", string cookies = "", string userAgent = "", string fileName = "")
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? FileNames.FromUrl(url) : FileNames.Sanitize(fileName);
        var cat = Categories.Detect(name, url);
        var item = new DownloadItem
        {
            Url = url,
            Referrer = referrer,
            UserAgent = userAgent,
            FileName = name,
            Category = cat,
            SaveDir = Settings.FolderFor(cat),
            MaxConnections = Settings.MaxConnections,
        };
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            item.Cookies = cookies;
            item.CookieHost = item.Host;
        }
        item.SanitizeMegaUrlIfNeeded();
        return item;
    }

    public void Add(DownloadItem item, bool startNow, Guid? queueId = null)
    {
        item.SanitizeMegaUrlIfNeeded();
        lock (_stateLock)
        {
            Items.Insert(0, item);
        }
        PathRegistry.Claim(item.Id, item.FullPath);
        if (startNow && queueId == null) Start(item);
        else AddToQueue(item, queueId ?? QueueDef.MainId);
        MarkDirty();
    }

    public void AddToQueue(DownloadItem item, Guid queueId)
    {
        CancelRetry(item.Id);
        item.QueueId = queueId;
        lock (_stateLock)
        {
            item.QueuePosition = Items.Where(i => i.QueueId == queueId).Select(i => i.QueuePosition).DefaultIfEmpty(0).Max() + 1;
        }
        item.RetryCount = 0;
        if (!item.IsActive && item.Status != DownloadStatus.Completed) item.Status = DownloadStatus.Queued;
        PumpQueues();
        MarkDirty();
    }

    public void RemoveFromQueue(DownloadItem item)
    {
        CancelRetry(item.Id);
        item.QueueId = null;
        if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        MarkDirty();
    }

    /// <summary>Moves an item up (-1) or down (+1) inside its queue.</summary>
    public void MoveInQueue(DownloadItem item, int delta)
    {
        if (item.QueueId is not { } q) return;
        var list = QueueItems(q).ToList();
        int i = list.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i].QueuePosition, list[j].QueuePosition) = (list[j].QueuePosition, list[i].QueuePosition);
        if (list[i].QueuePosition == list[j].QueuePosition) list[j].QueuePosition += delta;
        MarkDirty();
    }

    public IEnumerable<DownloadItem> QueueItems(Guid queueId)
    {
        lock (_stateLock)
            return Items.Where(i => i.QueueId == queueId).OrderBy(i => i.QueuePosition).ThenBy(i => i.AddedAt).ToList();
    }

    public DownloadItem? FindDuplicate(string url)
    {
        lock (_stateLock)
            return Items.FirstOrDefault(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(i.ResolvedUrl, url, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Raised when a download could not start because the trial ended or the licence expired.</summary>
    public event Action? LicenseBlocked;

    public void Start(DownloadItem item)
    {
        CancelRetry(item.Id);
        if (!Licensing.LicenseGate.CanDownload && item.Status != DownloadStatus.Completed)
        {
            item.LastResult = Loc.T("license.blocked");
            if (item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading) item.Status = DownloadStatus.Paused;
            LicenseBlocked?.Invoke();
            return;
        }
        Running? run = null;
        bool quotaBlocked = false;
        bool insufficientSpace = false;
        lock (_stateLock)
        {
            if (_running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed || _shuttingDown) return;
            if (QuotaBlocked)
            {
                item.Status = item.QueueId != null ? DownloadStatus.Queued : DownloadStatus.Paused;
                quotaBlocked = true;
            }
            else
            {
                var spaceVerdict = SpaceCheck.CheckItem(item, Settings.TempDir);
                if (spaceVerdict.IsInsufficient)
                {
                    item.ErrorMessage = SpaceCheck.FormatInsufficientMessage(spaceVerdict);
                    item.LastResult = Loc.T("core.status.error", item.ErrorMessage);
                    item.Status = DownloadStatus.Error;
                    MarkDirty();
                    insufficientSpace = true;
                }
                else
                {
                    item.Status = DownloadStatus.Connecting;
                    run = new Running { Cts = new CancellationTokenSource() };
                    _running[item.Id] = run;
                }
            }
        }
        if (quotaBlocked)
        {
            Save();
            return;
        }
        if (insufficientSpace)
        {
            Save();
            ItemFailed?.Invoke(item);
            return;
        }
        if (run != null)
            run.Task = RunAsync(item, run);
    }

    /// <summary>Stops a download by the user's wish; it never counts as a finished queue item.</summary>
    public void Pause(DownloadItem item)
    {
        CancelRetry(item.Id);
        Running? run;
        lock (_stateLock)
        {
            _running.TryGetValue(item.Id, out run);
        }
        Log.Write($"pause: {item.FileName} ({item.Status}, {(run != null ? "running" : "not running")})");
        if (run != null)
        {
            item.StoppedByUser = true;
            run.Cts.Cancel();
        }
        else if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        else if (item.Status == DownloadStatus.Paused && item.RetryCount > 0)
        {
            // Waiting to try again by itself (busy server / queue retry): pausing stops that wait for good.
            item.RetryCount = 0;
            item.ErrorMessage = "";
            item.LastResult = Loc.T("core.status.paused");
        }
        MarkDirty();
    }

    public void PauseAll()
    {
        List<Guid> retryIds;
        List<DownloadItem> activeItems;
        lock (_stateLock)
        {
            _runningQueues.Clear();
            _quotaPausedQueues.Clear();
            retryIds = _retryTimers.Keys.ToList();
            activeItems = Items.Where(i => _running.ContainsKey(i.Id)).ToList();
        }
        foreach (var id in retryIds) CancelRetry(id);
        foreach (var item in activeItems) Pause(item);
    }

    /// <summary>Stops and restarts from zero; waits for the running task first.</summary>
    public async Task RedownloadAsync(DownloadItem item)
    {
        CancelRetry(item.Id);
        Running? run;
        lock (_stateLock)
        {
            _running.TryGetValue(item.Id, out run);
        }
        if (run != null)
        {
            item.StoppedByUser = true;
            run.Cts.Cancel();
            if (run.Task != null)
            {
                try { await run.Task; }
                catch (Exception) { /* cancelled or faulted task already logged by runner */ }
            }
        }
        DeleteAllPartialFiles(item);
        item.ActiveTempPath = "";
        if (item.Kind == DownloadKind.Media) MediaDownloader.DeletePartials(item);
        item.ResetProgress();
        item.Status = DownloadStatus.Paused;
        item.CompletedAt = null;
        item.ErrorMessage = "";
        item.RetryCount = 0;
        Start(item);
    }

    static void DeleteAllPartialFiles(DownloadItem item)
    {
        MediaDownloader.TryDelete(item.TempPath);
        if (!string.IsNullOrWhiteSpace(item.FullPath))
        {
            MediaDownloader.TryDelete(item.FullPath + ".gdm");
        }
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
        {
            MediaDownloader.TryDelete(Path.Combine(DownloadItem.TempRoot, $"{item.Id:N}.gdm"));
        }
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFile)
    {
        CancelRetry(item.Id);
        PathRegistry.Release(item.Id);
        Running? run;
        lock (_stateLock)
        {
            _running.TryGetValue(item.Id, out run);
        }
        if (run != null)
        {
            item.StoppedByUser = true;
            run.Cts.Cancel();
            if (run.Task != null)
            {
                try { await run.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { /* cancelled, faulted, or timed out while removing item */ }
            }
        }
        if (item.Kind == DownloadKind.Torrent)
            await CleanupTorrentItemAsync(item, deleteDownloadedFiles: deleteFile);
        bool pathShared;
        bool tempShared;
        lock (_stateLock)
        {
            Items.Remove(item);
            PathRegistry.Release(item.Id);
            pathShared = Items.Any(other => other.Id != item.Id && string.Equals(other.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
            tempShared = Items.Any(other => other.Id != item.Id && string.Equals(other.TempPath, item.TempPath, StringComparison.OrdinalIgnoreCase));
        }
        if (!tempShared && !pathShared) DeleteAllPartialFiles(item);
        else if (!tempShared) MediaDownloader.TryDelete(item.TempPath);
        MediaDownloader.TryDelete(item.MediaCookieFile);
        if (!pathShared && item.Kind == DownloadKind.Media && item.Status != DownloadStatus.Completed) MediaDownloader.DeletePartials(item);
        if (deleteFile && !pathShared) MediaDownloader.TryDelete(item.FullPath);
        MarkDirty();
    }

    void EnsureSafeDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new DownloadException(Loc.T("core.media.err_save_path"));
        string full;
        try { full = Path.GetFullPath(dir.Trim()); }
        catch (Exception ex) { throw new DownloadException(Loc.T("core.media.err_save_path_detail", ex.Message)); }
        bool unc = full.StartsWith(@"\\", StringComparison.Ordinal) || (Uri.TryCreate(full, UriKind.Absolute, out var u) && u.IsUnc);
        if (unc && !ConfiguredRoots().Any(root => IsUnderDir(full, root)))
            throw new DownloadException(Loc.T("core.media.err_unc"));
    }

    IEnumerable<string> ConfiguredRoots()
    {
        yield return Settings.DownloadRoot;
        yield return Settings.TempDir;
        foreach (var c in Settings.CategoryDefs)
        {
            yield return c.Folder;
            yield return c.LastPath;
        }
    }

    static bool IsUnderDir(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var r = Path.GetFullPath(root.Trim()).TrimEnd('\\', '/') + "\\";
            return (path.TrimEnd('\\', '/') + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Updates SaveDir/FileName of an item (including one started early while AddDownloadWindow was open) and moves its partial/completed file.</summary>
    public async Task RelocateAsync(DownloadItem item, string newSaveDir, string newFileName, bool nameLocked, bool resumeIfRunning)
    {
        EnsureSafeDir(newSaveDir);
        if (!string.IsNullOrEmpty(newFileName)) newFileName = FileNames.Sanitize(Path.GetFileName(newFileName));
        var oldFull = item.FullPath;
        var oldTemp = item.TempPath;
        bool changedPath = !string.Equals(oldFull, Path.Combine(newSaveDir, newFileName), StringComparison.OrdinalIgnoreCase);
        bool wasRunning = false;
        Running? run = null;
        if (changedPath || !resumeIfRunning)
        {
            lock (_stateLock)
            {
                _running.TryGetValue(item.Id, out run);
            }
        }
        if (run != null)
        {
            wasRunning = true;
            item.StoppedByUser = true;
            run.Cts.Cancel();
            if (run.Task != null)
            {
                try { await run.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { /* cancelled, faulted, or timed out while relocating item */ }
            }
        }

        Directory.CreateDirectory(newSaveDir);
        item.SaveDir = newSaveDir;
        item.FileName = newFileName;
        item.NameLocked = nameLocked || item.NameLocked;

        if (changedPath)
        {
            var reserved = PathRegistry.Reserve(item.Id, item.FullPath);
            item.FileName = Path.GetFileName(reserved);
            try
            {
                if (item.Status == DownloadStatus.Completed && File.Exists(oldFull) && !string.Equals(oldFull, item.FullPath, StringComparison.OrdinalIgnoreCase))
                    File.Move(oldFull, item.FullPath, overwrite: true);
                else if (File.Exists(oldTemp) && !string.Equals(oldTemp, item.TempPath, StringComparison.OrdinalIgnoreCase))
                    File.Move(oldTemp, item.TempPath, overwrite: true);
            }
            catch (Exception ex) { Log.Write("relocate move failed: " + ex.Message); }
        }
        else
        {
            PathRegistry.Claim(item.Id, item.FullPath);
        }

        MarkDirty();
        if (wasRunning && resumeIfRunning && item.Status != DownloadStatus.Completed)
            Start(item);
    }

    public void RemoveCompleted()
    {
        lock (_stateLock)
        {
            foreach (var i in Items.Where(i => i.Status == DownloadStatus.Completed).ToList())
            {
                Items.Remove(i);
                PathRegistry.Release(i.Id);
            }
        }
        MarkDirty();
    }

    public void ImportItems(IEnumerable<DownloadItem> items, Guid? targetQueueId)
    {
        lock (_stateLock)
        {
            foreach (var item in items)
            {
                Items.Insert(0, item);
                if (item.Status != DownloadStatus.Completed)
                    PathRegistry.Claim(item.Id, item.FullPath);
            }
        }
        if (targetQueueId is { } qid)
        {
            foreach (var item in items)
            {
                if (item.Status != DownloadStatus.Completed)
                    AddToQueue(item, qid);
            }
        }
        Save();
    }

    // ---------- queues ----------

    public void StartQueue(Guid? queueId = null)
    {
        var q = Settings.FindQueue(queueId ?? QueueDef.MainId);
        if (q == null || _shuttingDown) return;
        q.LastStart = DateTime.Now;
        List<DownloadItem> toQueue;
        lock (_stateLock)
        {
            _runningQueues.Add(q.Id);
            if (QuotaBlocked) _quotaPausedQueues.Add(q.Id);
            toQueue = QueueItems(q.Id).Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Error && !_running.ContainsKey(i.Id)).ToList();
        }
        foreach (var i in toQueue)
        {
            CancelRetry(i.Id);
            i.Status = DownloadStatus.Queued;
            i.RetryCount = 0;
        }
        QueueStarted?.Invoke(q);
        if (q.Periodic) _ = SyncCheckAsync(q);
        else
        {
            PumpQueues();
            CheckQueueFinished(q);
        }
    }

    /// <summary>Stops a queue (all queues when null). Interrupted items go back to Queued so the next start resumes them.</summary>
    public void StopQueue(Guid? queueId = null)
    {
        List<Guid> ids;
        lock (_stateLock)
        {
            ids = queueId is { } id ? [id] : _runningQueues.Concat(_quotaPausedQueues).Distinct().ToList();
        }
        foreach (var qid in ids)
        {
            List<DownloadItem> retrying;
            List<DownloadItem> active;
            lock (_stateLock)
            {
                _runningQueues.Remove(qid);
                _quotaPausedQueues.Remove(qid);
                if (Settings.FindQueue(qid) is { } q) q.LastStop = DateTime.Now;
                retrying = Items.Where(i => i.QueueId == qid && _retryTimers.ContainsKey(i.Id)).ToList();
                active = Items.Where(i => i.QueueId == qid && _running.ContainsKey(i.Id)).ToList();
            }
            foreach (var i in retrying)
            {
                CancelRetry(i.Id);
                i.Status = DownloadStatus.Queued;
            }
            foreach (var i in active)
            {
                i.RequeueOnStop = true;
                Pause(i);
            }
        }
        MarkDirty();
    }

    /// <summary>Synchronization queue: re-download completed files that changed on the server.</summary>
    async Task SyncCheckAsync(QueueDef q)
    {
        foreach (var item in QueueItems(q.Id).Where(i => i.Status == DownloadStatus.Completed && i.Kind == DownloadKind.Http).ToList())
        {
            if (!IsQueueRunning(q.Id)) return;
            try
            {
                if (await HttpDownloader.HasChangedAsync(item, CancellationToken.None))
                {
                    MediaDownloader.TryDelete(item.FullPath);
                    MediaDownloader.TryDelete(item.TempPath);
                    item.LastResult = Loc.T("core.sync.changed");
                    item.ResetProgress();
                    item.CompletedAt = null;
                    item.Status = DownloadStatus.Queued;
                    PathRegistry.Claim(item.Id, item.FullPath);
                }
                else item.LastResult = Loc.T("core.sync.unchanged", DateTime.Now.ToString("yyyy/MM/dd HH:mm"));
            }
            catch (Exception ex) { item.LastResult = Loc.T("core.sync.check_failed", ex.Message); }
        }
        PumpQueues();
        CheckQueueFinished(q);
    }

    void PumpQueues()
    {
        if (_shuttingDown || QuotaBlocked) return;
        List<DownloadItem> toStart = [];
        lock (_stateLock)
        {
            if (_shuttingDown || QuotaBlocked) return;
            foreach (var qid in _runningQueues.ToList())
            {
                var q = Settings.FindQueue(qid);
                if (q == null) { _runningQueues.Remove(qid); continue; }
                int max = Math.Max(1, q.MaxSimultaneous);
                int active = Items.Count(i => i.QueueId == qid && (_running.ContainsKey(i.Id) || toStart.Contains(i)));
                foreach (var item in QueueItems(qid).Where(i => i.Status == DownloadStatus.Queued && !_running.ContainsKey(i.Id) && !toStart.Contains(i)).ToList())
                {
                    if (active >= max) break;
                    toStart.Add(item);
                    active++;
                }
            }
        }
        foreach (var item in toStart)
            Start(item);
    }

    void CheckQueueFinished(QueueDef q)
    {
        bool finishedNow = false;
        lock (_stateLock)
        {
            if (_shuttingDown || !_runningQueues.Contains(q.Id)) return;
            if (Items.Any(i => i.QueueId == q.Id && (i.Status == DownloadStatus.Queued || _running.ContainsKey(i.Id) || _retryTimers.ContainsKey(i.Id)))) return;
            _runningQueues.Remove(q.Id);
            _quotaPausedQueues.Remove(q.Id);
            finishedNow = true;
        }
        if (finishedNow) QueueFinished?.Invoke(q);
    }

    // ---------- running ----------

    public async Task<bool> TryResolveSharingItemAsync(DownloadItem item, HttpMessageInvoker? invoker = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.Url) || !Uri.TryCreate(item.Url, UriKind.Absolute, out var srcUri))
            return false;

        if (Resolvers.MegaResolver.TryParseLink(srcUri, out _, out var inlineMegaKey))
        {
            item.NeedsMegaDecrypt = true;
            item.MegaKey = inlineMegaKey;
            item.Url = Resolvers.MegaResolver.StripSecretFragment(item.Url);
        }
        else if (item.NeedsMegaDecrypt && item.MegaKey is { Length: 32 } storedKey && string.IsNullOrEmpty(srcUri.Fragment))
        {
            string b64Url = Convert.ToBase64String(storedKey).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Uri.TryCreate(item.Url.TrimEnd('/') + "#" + b64Url, UriKind.Absolute, out srcUri!);
        }

        if (!Resolvers.HostResolvers.All.Any(r => r.CanHandle(srcUri)))
            return false;

        var resolved = await Resolvers.HostResolvers.TryResolveAsync(srcUri, invoker, ct);
        if (resolved == null)
        {
            var warn = Loc.T("resolver.fallback_warning");
            item.Stage = warn;
            Log.Write($"resolver fallback for {item.Url}: {warn}");
            return false;
        }

        item.ResolvedUrl = resolved.Direct.AbsoluteUri;
        if (!item.NameLocked && !string.IsNullOrWhiteSpace(resolved.FileName))
        {
            item.FileName = FileNames.Sanitize(resolved.FileName);
            item.Category = Categories.Detect(item.FileName, item.Url);
            PathRegistry.Claim(item.Id, item.FullPath);
        }
        if (resolved.Size > 0 && item.TotalSize <= 0)
            item.TotalSize = resolved.Size;
        if (resolved.NeedsMegaDecrypt && resolved.MegaKey is { Length: 32 })
        {
            item.NeedsMegaDecrypt = true;
            item.MegaKey = resolved.MegaKey;
            item.Url = Resolvers.MegaResolver.StripSecretFragment(item.Url);
        }
        return true;
    }

    async Task RunAsync(DownloadItem item, Running run)
    {
        var cts = run.Cts;
        item.ErrorMessage = "";
        item.Stage = "";
        item.StoppedByUser = false;
        item.RequeueOnStop = false;
        PathRegistry.Claim(item.Id, item.FullPath);
        lock (_stateLock) _samples[item.Id] = (item.Downloaded, DateTime.Now);
        ItemStarted?.Invoke(item);
        bool finished = false;
        Guid? completedQueueId = null;
        try
        {
            if (item.Kind == DownloadKind.Media)
            {
                await MediaDownloader.RunAsync(item, Limiter, cts.Token);
            }
            else if (item.Kind == DownloadKind.Torrent)
            {
                await RunTorrentAsync(item, cts.Token);
            }
            else
            {
                await TryResolveSharingItemAsync(item, null, cts.Token);
                if (item.NeedsMegaDecrypt && item.MegaKey is { Length: 32 } megaKey)
                    await Task.Run(() => new Resolvers.MegaDownloader(item, megaKey, Limiter).RunAsync(cts.Token));
                else
                    await Task.Run(() => new HttpDownloader(item, Limiter).RunAsync(cts.Token));
            }

            if (File.Exists(item.FullPath))
            {
                Func<string?>? askPw = AskArchivePassword != null ? () => AskArchivePassword(item) : null;
                await VerifyVirusScanAndExtractAsync(item, Settings, askPw, cts.Token);
            }

            item.Status = DownloadStatus.Completed;
            item.CompletedAt = DateTime.Now;
            item.LastResult = Loc.T("core.status.completed");
            if (!item.RememberSpeedLimit) item.SpeedLimitKB = 0;
            item.Cookies = ""; // never keep session cookies after the file is done
            if (!string.IsNullOrEmpty(item.MediaCookieFile)) { MediaDownloader.TryDelete(item.MediaCookieFile); item.MediaCookieFile = ""; }
            item.RefreshLive();
            finished = true;
            completedQueueId = item.QueueId;
            Stats.RecordFileCompleted(item.Host, item.Category);
            ItemCompleted?.Invoke(item);
            item.RetryCount = 0;
            if (item.QueueId is { } qid)
            {
                var qDef = Settings.FindQueue(qid);
                if (qDef is not { IsSync: true } && qDef is not { Periodic: true })
                    item.QueueId = null;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            item.Status = item.RequeueOnStop ? DownloadStatus.Queued : DownloadStatus.Paused;
            item.LastResult = Loc.T("core.status.paused");
            Log.Write($"paused: {item.FileName} at {item.Downloaded} of {item.TotalSize} bytes");
        }
        catch (Exception ex)
        {
            Log.Write($"download failed {item.Url}: {ex}");
            item.LastFailKind = FailKindOf(ex);
            item.ErrorMessage = ex is DownloadException ? ex.Message : Loc.T("core.error.generic", ex.Message);
            item.LastResult = Loc.T("core.status.error", item.ErrorMessage);

            var q = Settings.FindQueue(item.QueueId);
            bool retry = q != null && IsQueueRunning(q.Id) && q.Retries > 0 && item.RetryCount < q.Retries
                         && ex is not DownloadException { Retryable: false };
            // A busy server (429/503) is not a broken download: like IDM, try again later by itself.
            // A browser check (Cloudflare) clears after a couple of minutes; then one connection is welcome again.
            bool busyRetry = !retry && item.LastFailKind is FailKind.Rejected or FailKind.Challenge && !item.StoppedByUser && item.RetryCount < MaxBusyAutoRetries;
            if (retry)
            {
                item.RetryCount++;
                item.Status = DownloadStatus.Paused;
                item.ErrorMessage = Loc.T("core.error.retry", item.ErrorMessage, item.RetryCount, q!.Retries);
                ScheduleRetry(item);
            }
            else if (busyRetry)
            {
                item.RetryCount++;
                bool challenged = item.LastFailKind == FailKind.Challenge;
                var wait = challenged ? ChallengeAutoRetryDelay(item.RetryCount) : BusyAutoRetryDelay(item.RetryCount);
                item.Status = DownloadStatus.Paused;
                item.ErrorMessage = Loc.T(challenged ? "core.error.challenge_retry" : "core.error.busy_retry", (int)wait.TotalSeconds, item.RetryCount, MaxBusyAutoRetries);
                item.LastResult = item.ErrorMessage;
                Log.Write($"{(challenged ? "browser check" : "server busy")}: {item.FileName} tries again in {(int)wait.TotalSeconds}s ({item.RetryCount}/{MaxBusyAutoRetries}), kept {item.Downloaded} bytes");
                ScheduleRetry(item, wait);
            }
            else
            {
                if (!string.IsNullOrEmpty(item.MediaCookieFile)) { MediaDownloader.TryDelete(item.MediaCookieFile); item.MediaCookieFile = ""; }
                item.Status = DownloadStatus.Error;
                finished = true;
                ItemFailed?.Invoke(item);
            }
        }
        finally
        {
            long finalDelta = 0;
            lock (_stateLock)
            {
                _running.Remove(item.Id);
                if (_samples.Remove(item.Id, out var prev))
                    finalDelta = Math.Max(0, item.Downloaded - prev.Bytes);
                else if (item.Downloaded > 0)
                    finalDelta = item.Downloaded;
            }
            if (finalDelta > 0)
            {
                Stats.Record(finalDelta, item.Host, item.Category);
                CheckQuota(DateTime.Now, finalDelta);
            }
            cts.Dispose();
            item.RefreshLive();
            MarkDirty();
            if (!_shuttingDown)
            {
                PumpQueues();
                bool anyRunning;
                List<Guid> rqs;
                lock (_stateLock)
                {
                    anyRunning = _running.Count > 0;
                    rqs = _runningQueues.ToList();
                }
                if (!anyRunning || (DateTime.Now - _lastSave).TotalMilliseconds >= 500)
                    Save();
                // Only a completed or failed item can finish a queue; a user stop never does.
                if (finished && Settings.FindQueue(item.QueueId ?? completedQueueId) is { } q2) CheckQueueFinished(q2);
                foreach (var qid in rqs)
                    if (Settings.FindQueue(qid) is { } rq && finished) CheckQueueFinished(rq);
            }
        }
    }

    public static async Task VerifyVirusScanAndExtractAsync(
        DownloadItem item,
        AppSettings settings,
        Func<string?>? askPw = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(item.FullPath))
            return;

        var virusWarning = await AfterDownload.ScanAsync(settings, item.FullPath);
        if (!string.IsNullOrEmpty(virusWarning))
        {
            item.ExtractState = ArchiveExtractState.None;
            throw new DownloadException(virusWarning, FailKind.Mismatch);
        }

        await Extractor.RunPostDownloadAsync(item, settings, askPw, ct: ct);
    }

    /// <summary>Automatic retries after "server busy" for downloads outside a queue.</summary>
    public const int MaxBusyAutoRetries = 3;

    public static TimeSpan BusyAutoRetryDelay(int retry) => TimeSpan.FromSeconds(Math.Min(120, 30 * Math.Max(1, retry)));

    /// <summary>Measured on Cloudflare (AnyDesk): the check lifts after about two minutes.</summary>
    public static TimeSpan ChallengeAutoRetryDelay(int retry) => TimeSpan.FromSeconds(retry <= 1 ? 120 : 180);

    void ScheduleRetry(DownloadItem item, TimeSpan? delay = null)
    {
        CancelRetry(item.Id);
        var t = new DispatcherTimer(DispatcherPriority.Background, _timer.Dispatcher) { Interval = delay ?? TimeSpan.FromSeconds(Math.Min(60, 5 * item.RetryCount)) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            lock (_stateLock) _retryTimers.Remove(item.Id);
            if (item.Status != DownloadStatus.Paused || _shuttingDown) return;
            if (item.QueueId != null)
            {
                item.Status = DownloadStatus.Queued;
                PumpQueues();
            }
            else
            {
                Start(item); // busy server: the waiting time is over
            }
        };
        lock (_stateLock) _retryTimers[item.Id] = t;
        t.Start();
    }

    public void SampleSpeedAndStats()
    {
        SpeedHistory.Add((long)TotalSpeed);
        if (ActiveCount > 0)
            Stats.RecordActiveSecond();
    }

    void Tick()
    {
        var now = DateTime.Now;
        long tickBytes = 0;
        List<DownloadItem> activeItems;
        lock (_stateLock)
        {
            activeItems = Items.Where(i => _running.ContainsKey(i.Id)).ToList();
        }
        foreach (var item in activeItems)
        {
            var bytes = item.Downloaded;
            (long Bytes, DateTime At) prev;
            bool hadPrev;
            lock (_stateLock)
            {
                hadPrev = _samples.TryGetValue(item.Id, out prev);
                _samples[item.Id] = (bytes, now);
            }
            if (hadPrev)
            {
                var delta = Math.Max(0, bytes - prev.Bytes);
                tickBytes += delta;
                if (delta > 0)
                    Stats.Record(delta, item.Host, item.Category);
                if (item.Kind == DownloadKind.Http && item.Status == DownloadStatus.Downloading)
                {
                    var dt = (now - prev.At).TotalSeconds;
                    if (dt > 0)
                    {
                        var instant = delta / dt;
                        item.Speed = item.Speed <= 0 ? instant : item.Speed * 0.7 + instant * 0.3;
                    }
                }
            }
            if (item.Kind == DownloadKind.Http)
                item.Eta = item.Speed > 1 && item.TotalSize > 0 ? TimeSpan.FromSeconds((item.TotalSize - bytes) / item.Speed) : null;
            item.RefreshLive();
        }

        _tickCounter++;
        if ((_tickCounter & 1) == 0)
            SampleSpeedAndStats();

        CheckQuota(now, tickBytes);
        if (ActiveCount > 0) _dirty = true;
        if (_dirty && (now - _lastSave).TotalSeconds >= 3) Save();
        Ticked?.Invoke();
    }

    /// <summary>"Download no more than X MB every Y hours": stops everything when the window is full and resumes later.</summary>
    void CheckQuota(DateTime now, long tickBytes)
    {
        var window = TimeSpan.FromHours(Math.Max(1, Settings.QuotaHours));
        long used;
        lock (_stateLock)
        {
            int removed = _quotaLog.RemoveAll(e => now - e.At > window);
            if (removed > 0) _dirty = true;
            if (!Settings.QuotaEnabled)
            {
                if (!QuotaBlocked) return;
                QuotaBlocked = false;
                foreach (var q in _quotaPausedQueues) _runningQueues.Add(q);
                _quotaPausedQueues.Clear();
                used = -1;
            }
            else
            {
                if (tickBytes > 0)
                {
                    _quotaLog.Add((now, tickBytes));
                    _dirty = true;
                }
                used = _quotaLog.Sum(e => e.Bytes);
            }
        }
        if (used == -1)
        {
            QuotaChanged?.Invoke(false);
            PumpQueues();
            return;
        }
        long limit = Settings.QuotaMB * 1024L * 1024L;
        if (!QuotaBlocked && used >= limit)
        {
            List<DownloadItem> activeItems;
            lock (_stateLock)
            {
                QuotaBlocked = true;
                _quotaPausedQueues.Clear();
                foreach (var q in _runningQueues) _quotaPausedQueues.Add(q);
                activeItems = Items.Where(i => _running.ContainsKey(i.Id)).ToList();
            }
            foreach (var item in activeItems)
            {
                item.RequeueOnStop = item.QueueId != null;
                Pause(item);
            }
            QuotaChanged?.Invoke(true);
        }
        else if (QuotaBlocked && used < limit * 0.95)
        {
            lock (_stateLock)
            {
                QuotaBlocked = false;
                foreach (var q in _quotaPausedQueues) _runningQueues.Add(q);
                _quotaPausedQueues.Clear();
            }
            QuotaChanged?.Invoke(false);
            PumpQueues();
        }
    }

    public long QuotaUsedBytes { get { lock (_stateLock) return _quotaLog.Sum(e => e.Bytes); } }

    // ---------- persistence ----------

    void MarkDirty() => _dirty = true;

    static string QuotaFile => Path.Combine(AppPaths.DataDir, "quota.json");

    sealed record QuotaRecord(DateTime At, long Bytes);

    void Load()
    {
        try
        {
            if (File.Exists(QuotaFile))
            {
                var window = TimeSpan.FromHours(Math.Max(1, Settings.QuotaHours));
                var now = DateTime.Now;
                var qlist = JsonSerializer.Deserialize<List<QuotaRecord>>(File.ReadAllText(QuotaFile), Json.Options) ?? [];
                foreach (var q in qlist.Where(e => now - e.At <= window && e.Bytes > 0))
                    _quotaLog.Add((q.At, q.Bytes));
            }
        }
        catch (Exception ex) { Log.Write("quota load failed: " + ex.Message); }

        try
        {
            if (File.Exists(AppPaths.DownloadsFile))
            {
                var list = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(AppPaths.DownloadsFile), Json.Options) ?? [];
                foreach (var item in list)
                {
                    var baseName = Path.GetFileName((item.FileName ?? "").Replace('/', Path.DirectorySeparatorChar));
                    item.FileName = FileNames.Sanitize(string.IsNullOrWhiteSpace(baseName) ? "download" : baseName);
                    if (!IsSafeLoadedSaveDir(item.SaveDir))
                        item.SaveDir = Settings.FolderFor(item.Category);
                    if (!string.IsNullOrEmpty(item.ActiveTempPath) && !IsSafeLoadedTempPath(item.ActiveTempPath, item.SaveDir, Settings))
                        item.ActiveTempPath = "";
                    if (!string.IsNullOrEmpty(item.MediaCookieFile) && !MediaDownloader.IsUnderCookiesDir(item.MediaCookieFile))
                        item.MediaCookieFile = "";
                    if (item.IsActive) item.Status = item.InQueue ? DownloadStatus.Queued : DownloadStatus.Paused;
                    if (item.Status == DownloadStatus.Completed) item.Downloaded = Math.Max(item.Downloaded, item.TotalSize);
                    if (item.QueueId is { } q && Settings.FindQueue(q) == null) item.QueueId = QueueDef.MainId;
                    if (item.SpeedLimitKB > 0) item.FileLimiter.Enabled = true;
                    if (item.Status != DownloadStatus.Completed) PathRegistry.Claim(item.Id, item.FullPath);
                    Items.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("downloads load failed: " + ex.Message);
            try { File.Copy(AppPaths.DownloadsFile, AppPaths.DownloadsFile + ".bad", true); }
            catch (IOException) { /* best-effort corrupt downloads.json backup */ }
        }
        CheckQuota(DateTime.Now, 0);
        PurgeOrphanCookieFiles();
    }

    static bool IsSafeLoadedSaveDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal) || dir.StartsWith("//", StringComparison.Ordinal))
            return false;
        try
        {
            if (!Path.IsPathFullyQualified(dir)) return false;
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var forbidden = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            };
            foreach (var f in forbidden)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                var rf = Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Equals(rf, StringComparison.OrdinalIgnoreCase) || full.StartsWith(rf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch { /* invalid save path */ return false; }
    }

    static bool IsSafeLoadedTempPath(string? tempPath, string saveDir, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(tempPath) || tempPath.StartsWith(@"\\", StringComparison.Ordinal) || tempPath.StartsWith("//", StringComparison.Ordinal))
            return false;
        try
        {
            var full = Path.GetFullPath(tempPath);
            string[] roots = [DownloadItem.TempRoot, settings.TempDir, saveDir, Path.GetTempPath()];
            foreach (var r in roots)
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                var fullRoot = Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { /* invalid temp path */ return false; }
    }

    void PurgeOrphanCookieFiles()
    {
        try
        {
            if (!Directory.Exists(AppPaths.CookiesDir)) return;
            var used = Items.Select(i => i.MediaCookieFile).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(AppPaths.CookiesDir))
                if (!used.Contains(f)) MediaDownloader.TryDelete(f);
        }
        catch (IOException ex) { Log.Write("orphan cookie purge failed: " + ex.Message); }
    }

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                List<DownloadItem> snapshot;
                List<QuotaRecord> qlist;
                lock (_stateLock)
                {
                    snapshot = Items.ToList();
                    qlist = _quotaLog.Select(e => new QuotaRecord(e.At, e.Bytes)).ToList();
                }
                var array = new JsonArray();
                foreach (var item in snapshot)
                {
                    item.SanitizeMegaUrlIfNeeded();
                    JsonNode? node;
                    lock (item.SegLock) node = JsonSerializer.SerializeToNode(item, Json.Options);
                    array.Add(node);
                }
                Json.WriteAtomic(AppPaths.DownloadsFile, array.ToJsonString(Json.Options));
                try
                {
                    Json.WriteAtomic(QuotaFile, JsonSerializer.Serialize(qlist, Json.Options));
                }
                catch (IOException ex) { Log.Write("quota save failed: " + ex.Message); }
                Settings.Save(); // queue LastStart/LastStop and remembered paths
                _dirty = false;
                _lastSave = DateTime.Now;
            }
            catch (Exception ex) { Log.Write("downloads save failed: " + ex.Message); }
        }
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _timer.Stop();
        List<Guid> retryIds;
        List<Guid> queued;
        Task[] tasks;
        List<(DownloadItem Item, CancellationTokenSource Cts)> activeToCancel;
        lock (_stateLock)
        {
            retryIds = _retryTimers.Keys.ToList();
            queued = _runningQueues.ToList();
            _runningQueues.Clear();
            _quotaPausedQueues.Clear();
            tasks = _running.Values.Select(r => r.Task).OfType<Task>().ToArray();
            activeToCancel = Items.Where(i => _running.ContainsKey(i.Id)).Select(i => (i, _running[i.Id].Cts)).ToList();
        }
        foreach (var id in retryIds) CancelRetry(id);
        foreach (var (item, cts) in activeToCancel)
        {
            item.StoppedByUser = true;
            item.RequeueOnStop = item.QueueId != null && queued.Contains(item.QueueId.Value);
            cts.Cancel();
        }
        // Give connections a moment to flush their last chunk before saving.
        if (tasks.Length > 0)
        {
            var all = Task.WhenAll(tasks);
            var sw = Stopwatch.StartNew();
            while (!all.IsCompleted && sw.ElapsedMilliseconds < 5000)
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                if (!all.IsCompleted) Thread.Sleep(15);
            }
        }
        List<DownloadItem> snapshot;
        lock (_stateLock) snapshot = Items.Where(i => i.IsActive).ToList();
        foreach (var item in snapshot) item.Status = item.RequeueOnStop ? DownloadStatus.Queued : DownloadStatus.Paused;
        Save();
        try { Stats.Dispose(); }
        catch (Exception ex) { Log.Write("stats dispose failed: " + ex.Message); }
    }
}
