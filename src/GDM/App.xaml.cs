using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using Golan.Gdm.Integration;
using Golan.Gdm.UI;
using WinForms = System.Windows.Forms;

namespace Golan.Gdm;

public partial class App : Application
{
    public static App Instance => (App)Current;
    public const string Version = "2.5.0";

    public bool StartHidden { get; init; }
    public string? StartUrl { get; init; }
    public string? StartTorrentPath { get; init; }

    public AppSettings Settings { get; private set; } = null!;
    public DownloadManager Manager { get; private set; } = null!;
    public QueueScheduler Scheduler { get; private set; } = null!;
    public PowerGuard PowerGuard { get; private set; } = null!;
    public SpeedRuleController SpeedRuleController { get; private set; } = null!;
    public DownloadManagerCliTarget CliTarget { get; private set; } = null!;
    public MainWindow? MainView { get; private set; }

    PipeServer? _pipe;
    WinForms.NotifyIcon? _tray;
    DispatcherTimer? _schedulerTimer;
    readonly Dictionary<Guid, ProgressWindow> _progressWindows = [];
    readonly Dictionary<string, int> _declinesByHost = new(StringComparer.OrdinalIgnoreCase);
    SchedulerWindow? _schedulerWindow;
    GrabberWindow? _grabberWindow;
    DropTargetWindow? _dropTarget;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("UI error: " + ex.Exception);
            ex.Handled = true;
        };

        Settings = AppSettings.Load();
        Manager = new DownloadManager(Settings);
        Scheduler = new QueueScheduler(Manager);
        PowerGuard = new PowerGuard
        {
            KeepSystemAwake = Settings.KeepSystemAwake,
            KeepDisplayOn = Settings.KeepDisplayOn,
        };
        SpeedRuleController = new SpeedRuleController(Settings, Manager.Limiter, startTimer: true);
        CliTarget = new DownloadManagerCliTarget(Manager);

        Manager.AskArchivePassword = item =>
        {
            if (Dispatcher.CheckAccess())
                return InputDialog.Ask(MainView, Loc.T($"extract.password.title"), Loc.T($"extract.password.prompt", item.FileName), "");
            return Dispatcher.Invoke(() =>
                InputDialog.Ask(MainView, Loc.T($"extract.password.title"), Loc.T($"extract.password.prompt", item.FileName), ""));
        };

        Core.Torrent.TorrentService.MetadataReadyPromptAsync = (item, torrent) =>
            Dispatcher.InvokeAsync(() =>
            {
                var w = TorrentFilesWindow.FromTorrent(torrent, item);
                w.Owner = MainView is { IsVisible: true } ? MainView : null;
                return w.ShowDialog() == true;
            }).Task;

        Manager.ItemStarted += OnItemStarted;
        Manager.ItemCompleted += OnItemCompleted;
        Manager.ItemFailed += OnItemFailed;
        Manager.QueueStarted += q => { AfterDownload.PlaySound(Settings, "queueStart"); RefreshTrayMenu(); };
        Manager.QueueFinished += OnQueueFinished;
        Manager.Ticked += () =>
        {
            PowerGuard.KeepSystemAwake = Settings.KeepSystemAwake;
            PowerGuard.KeepDisplayOn = Settings.KeepDisplayOn;
            PowerGuard.Update(Manager.ActiveCount > 0 || Manager.QueueRunning);
        };
        Manager.QuotaChanged += blocked =>
        {
            if (blocked && Settings.QuotaWarn) Notify(Loc.T("main.notify.quotaReachedTitle"), Loc.T("main.notify.quotaReachedBody", Settings.QuotaMB, Settings.QuotaHours));
            else if (!blocked) Notify(Loc.T("main.notify.quotaResumedTitle"), Loc.T("main.notify.quotaResumedBody"));
        };

        Manager.LicenseBlocked += () => Dispatcher.BeginInvoke(() => ShowActivation(fromBlockedDownload: true));

        BrowserIntegration.Register();
        BrowserIntegration.ExtractExtension();
        if (!AppPaths.IsPortable && !AppPaths.IsIsolated)
        {
            if (!Settings.StartWithWindows && WindowsStartup.IsEnabled())
            {
                Settings.StartWithWindows = true;
                Settings.Save();
            }
            WindowsStartup.Apply(Settings.StartWithWindows);
            if (Settings.RegisterMagnetProtocol)
                try { new MagnetProtocolRegistrar().ApplySetting(true); }
                catch (Exception ex) { Log.Write("magnet protocol registration failed: " + ex.Message); }
        }

        _pipe = new PipeServer(msg => Dispatcher.InvokeAsync(() => HandleMessageAsync(msg)).Task.Unwrap());
        _pipe.Start();

        CreateTray();
        MainView = new MainWindow();
        // Video tools are prepared quietly in the background so the first video needs no waiting; the status bar shows it.
        MediaTools.ProgressChanged += (text, percent) => Dispatcher.BeginInvoke(() => MainView?.ShowToolsStatus(text, percent));
        MediaTools.StartPrefetch(TimeSpan.FromSeconds(20));
        if (!StartHidden) MainView.Show();
        else MainView.EnsureHandle();
        if (Settings.ShowDropTarget) ToggleDropTarget(true);

        if (Settings.RemoteEnabled)
            _ = RemoteWindow.EnsureServerStateAsync(Manager);

        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _schedulerTimer.Tick += (_, _) => Scheduler.Tick(DateTime.Now);
        _schedulerTimer.Start();

        Scheduler.StartupQueues();
        // Resume the main queue if it was interrupted when Golan closed.
        if (Manager.QueueItems(QueueDef.MainId).Any(i => i.Status == DownloadStatus.Queued) && !Manager.IsQueueRunning(QueueDef.MainId))
            Manager.StartQueue(QueueDef.MainId);

        if (StartUrl != null) ShowAddUrl(StartUrl);
        if (!string.IsNullOrWhiteSpace(StartTorrentPath))
            _ = OpenTorrentFileAsync(StartTorrentPath, MainView);

        if (Updater.ShouldCheckNow(Settings, DateTime.UtcNow, AppPaths.IsPortable))
            _ = CheckForUpdatesAsync(interactive: false, owner: MainView);

        if (!Core.Licensing.LicenseGate.CanDownload && !StartHidden)
            Dispatcher.BeginInvoke(() => ShowActivation(), DispatcherPriority.ApplicationIdle);

        if (Settings.FirstRun)
        {
            Settings.FirstRun = false;
            Settings.Save();
            if (!StartHidden) Dispatcher.BeginInvoke(() => new WelcomeWindow { Owner = MainView }.ShowDialog(), DispatcherPriority.ApplicationIdle);
        }

        OfferInstallOnceIfNeeded();
    }

    // ---------- messages from the browser extension / other instances ----------

    public static JsonObject PingReply(AppSettings s) => new()
    {
        ["ok"] = true,
        ["running"] = true,
        ["version"] = Version,
        ["settings"] = new JsonObject
        {
            ["intercept"] = s.InterceptBrowser && Core.Licensing.LicenseGate.CanDownload,
            ["extensions"] = new JsonArray(s.ExtensionList.Select(x => (JsonNode)x).ToArray()),
            ["minSizeKB"] = s.MinSizeMB > 0 ? s.MinSizeMB * 1024 : 0,
            ["exceptionSites"] = new JsonArray(Wildcard.SplitList(s.ExceptionSites).Select(x => (JsonNode)x).ToArray()),
            ["exceptionAddresses"] = new JsonArray(Wildcard.SplitList(s.ExceptionAddresses).Select(x => (JsonNode)x).ToArray()),
            ["mediaPanel"] = s.MediaPanelEnabled,
            ["mediaPanelMinSizeKB"] = s.MediaPanelMinSizeKB,
            ["mediaPanelExceptions"] = new JsonArray(Wildcard.SplitList(s.MediaPanelExceptions).Select(x => (JsonNode)x).ToArray()),
            ["menuItems"] = new JsonArray(s.ExtensionMenuItems.Select(x => (JsonNode)x).ToArray()),
        },
    };

    static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static long Long(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : -1;
    static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };
    static JsonObject Ok(bool accepted = true) => new() { ["ok"] = true, ["accepted"] = accepted };

    public async Task<JsonObject> HandleMessageAsync(JsonObject msg)
    {
        switch (Str(msg, "action"))
        {
            case "ping":
                ExtensionContact.Record(Str(msg, "browser"));
                return PingReply(Settings);
            case "noop":
                return Ok();
            case "show":
                ShowMain();
                return Ok();
            case "cli":
                return await CliActions.HandleAsync(msg, CliTarget);
            case "torrent":
            {
                var path = Str(msg, "path");
                if (!string.IsNullOrWhiteSpace(path))
                    _ = OpenTorrentFileAsync(path, MainView);
                return Ok();
            }
            case "add":
                ShowAddUrl(Str(msg, "url"));
                return Ok();
            case "download":
                return await HandleBrowserDownloadAsync(msg);
            case "downloadAll":
            {
                var links = (msg["links"] as JsonArray)?.OfType<JsonObject>()
                    .Select(l => new BatchLink(Str(l, "url"), Str(l, "text"), Str(l, "cookies")))
                    .Where(l => FileNames.IsHttpUrl(l.Url)).ToList() ?? [];
                if (links.Count == 0) return Fail(Loc.T("main.ipc.noLinks"));
                ShowBatch(links, Str(msg, "referrer"), Str(msg, "userAgent"));
                return Ok();
            }
            case "media":
            {
                var page = Str(msg, "pageUrl");
                var media = (msg["mediaUrls"] as JsonArray)?.OfType<JsonObject>()
                    .Select(m => new DetectedMedia(Str(m, "url"), Str(m, "mime"), Long(m, "size")))
                    .Where(m => FileNames.IsHttpUrl(m.Url)).ToList() ?? [];
                if (!FileNames.IsHttpUrl(page) && media.Count == 0) return Fail(Loc.T("main.ipc.noVideo"));
                ShowVideo(FileNames.IsHttpUrl(page) ? page : media[0].Url, page, Str(msg, "userAgent"), msg["cookies"] as JsonArray, media, Str(msg, "title"));
                return Ok();
            }
            default:
                return Fail(Loc.T("main.ipc.unknownRequest"));
        }
    }

    /// <summary>A download the extension paused in the browser. Replies accepted=false so the browser keeps it.</summary>
    async Task<JsonObject> HandleBrowserDownloadAsync(JsonObject msg)
    {
        var watch = Stopwatch.StartNew();
        var reply = await DecideBrowserDownloadAsync(msg);
        var host = Uri.TryCreate(Str(msg, "url"), UriKind.Absolute, out var lu) ? lu.Host : "?";
        Log.Write($"browser download from {host} ({Str(msg, "filename")}, {Long(msg, "fileSize")} bytes): " +
                  $"{(reply["accepted"]?.GetValue<bool>() == false ? "left to the browser" : "taken by Golan")} in {watch.ElapsedMilliseconds} ms");
        return reply;
    }

    async Task<JsonObject> DecideBrowserDownloadAsync(JsonObject msg)
    {
        var url = Str(msg, "url");
        var rawUrl = Str(msg, "rawUrl") is { Length: > 0 } ru ? ru : Str(msg, "originalUrl");
        var finalUrl = Str(msg, "finalUrl");
        if (MagnetValidator.IsMagnetUri(url))
        {
            ShowAddUrl(url);
            return Ok(accepted: true);
        }
        if (!FileNames.IsHttpUrl(url)) return Fail(Loc.T("main.ipc.invalidUrl"));
        // Not activated: leave the download to the browser instead of swallowing it.
        if (!Core.Licensing.LicenseGate.CanDownload) return Ok(accepted: false);
        // "Refresh download address": the click the user was asked for lands on the waiting item.
        if (await TryTakeRefreshedAddressAsync(msg, url, finalUrl)) return Ok(accepted: true);
        bool force = msg["force"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb
            && AfterDownload.CanForceBypassException(url, Str(msg, "referrer"), Str(msg, "pageUrl"));
        if (!force && (Settings.IsException(url) ||
                       (FileNames.IsHttpUrl(rawUrl) && Settings.IsException(rawUrl)) ||
                       (FileNames.IsHttpUrl(finalUrl) && Settings.IsException(finalUrl))))
            return Ok(accepted: false);

        var item = Manager.CreateHttp(url, Str(msg, "referrer"), Str(msg, "cookies"), Str(msg, "userAgent"), Str(msg, "filename"));
        item.ParentPage = Str(msg, "pageUrl") is { Length: > 0 } pg ? pg : item.Referrer;
        if (Str(msg, "filename").Length > 0) item.NameLocked = true;
        var size = Long(msg, "fileSize");
        if (size > 0) item.TotalSize = size;

        if (MediaDownloader.LooksLikeMediaPage(url) && !Path.HasExtension(item.FileName))
        {
            ShowVideo(url, Str(msg, "referrer"), Str(msg, "userAgent"), null, []);
            return Ok();
        }

        if (!Settings.ShowAddDialogFromBrowser || Settings.AutoStarts(item.FileName))
        {
            if (!ResolveDuplicate(item, out var existing)) return Ok(accepted: existing);
            if (Uri.TryCreate(url, UriKind.Absolute, out var su) && !string.IsNullOrEmpty(su.Host))
                _declinesByHost.Remove(su.Host);
            Manager.Add(item, startNow: true);
            return Ok();
        }

        // Golan takes the download now (the browser cancels its own copy at once, as with IDM) and asks the
        // details in its own window; "Cancel" there simply drops the download.
        var decision = new TaskCompletionSource<bool>();
        ShowAddDownload(item, decision);
        _ = decision.Task.ContinueWith(t =>
        {
            if (t.Result)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var au) && !string.IsNullOrEmpty(au.Host))
                    _declinesByHost.Remove(au.Host);
            }
            else
            {
                Log.Write("browser download cancelled in Golan's window: " + item.FileName);
                OnBrowserDownloadDeclined(url);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return Ok();
    }

    // ---------- refresh download address (IDM) ----------

    public readonly AddressRefresh AddressRefresh = new();
    public event Action<DownloadItem>? AddressRefreshed;

    /// <summary>Pauses the item, opens its source page and waits for the user to click the download there again.</summary>
    public void BeginAddressRefresh(DownloadItem item, Window? owner = null)
    {
        if (!AddressRefresh.CanRefresh(item)) return;
        if (item.IsActive) Manager.Pause(item);
        AddressRefresh.Begin(item.Id, DateTime.UtcNow);
        var w = new RefreshAddressWindow(item);
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        BringToFront(w);
        w.OpenSourcePage();
    }

    async Task<bool> TryTakeRefreshedAddressAsync(JsonObject msg, string url, string finalUrl)
    {
        if (AddressRefresh.Pending(DateTime.UtcNow) is not { } id) return false;
        var item = Manager.Items.FirstOrDefault(i => i.Id == id);
        if (item == null || !AddressRefresh.CanRefresh(item)) { AddressRefresh.Cancel(id); return false; }
        var name = Str(msg, "filename") is { Length: > 0 } f ? FileNames.Sanitize(f)
                 : FileNames.FromUrl(FileNames.IsHttpUrl(finalUrl) ? finalUrl : url);
        var how = AddressRefresh.Match(item, name, Long(msg, "fileSize"));
        if (how == RefreshMatch.None)
        {
            Log.Write($"refresh address: {name} is not {item.FileName}, treated as a new download");
            return false;
        }
        Log.Write($"refresh address: {item.FileName} takes a new address ({how})");
        AddressRefresh.Cancel(id);
        // If the user resumed it meanwhile, let that attempt stop first so the new address is the one that runs.
        Manager.Pause(item);
        for (int i = 0; i < 100 && Manager.IsRunning(item); i++) await Task.Delay(50);
        AddressRefresh.Apply(item, url, Str(msg, "referrer"), Str(msg, "cookies"), Str(msg, "userAgent"), Str(msg, "pageUrl"));
        Manager.Save();
        Manager.Start(item);
        AddressRefreshed?.Invoke(item);
        Notify(Loc.T("refresh.done.title"), Loc.T("refresh.done.body", item.FileName));
        return true;
    }

    /// <summary>IDM "duplicate download link". Returns false when the new item must not be added; handledExisting tells whether Golan took the download anyway.</summary>
    public bool ResolveDuplicate(DownloadItem incoming, out bool handledExisting, Window? owner = null)
    {
        handledExisting = false;
        var existing = Manager.FindDuplicate(incoming.Url);
        if (existing == null) return true;

        var choice = Settings.DuplicateAction;
        if (choice == "ask")
        {
            var dlg = new DuplicateWindow(existing, incoming) { Owner = owner is { IsVisible: true } ? owner : null };
            if (dlg.ShowDialog() != true) return false;
            choice = dlg.Choice;
            if (dlg.Remember) { Settings.DuplicateAction = choice; Settings.Save(); }
        }
        switch (choice)
        {
            case "overwrite":
                if (!incoming.NameLocked)
                {
                    incoming.FileName = existing.FileName;
                    incoming.SaveDir = existing.SaveDir;
                }
                incoming.NameLocked = true;
                Manager.Pause(existing);
                PathRegistry.Release(existing.Id);
                MediaDownloader.TryDelete(existing.FullPath);
                MediaDownloader.TryDelete(existing.TempPath);
                MediaDownloader.TryDelete(existing.FullPath + ".gdm");
                _ = Manager.RemoveAsync(existing, deleteFile: false);
                return true;
            case "resume":
                handledExisting = true;
                if (existing.Status == DownloadStatus.Completed) BringToFront(new CompleteWindow(existing));
                else Manager.Start(existing);
                return false;
            default: // number: a free "name (1).ext" is picked when the download starts
                return true;
        }
    }

    void OnBrowserDownloadDeclined(string url)
    {
        if (!Settings.TwiceCancelPrompt || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        var n = _declinesByHost[u.Host] = _declinesByHost.GetValueOrDefault(u.Host) + 1;
        if (n < 2) return;
        _declinesByHost[u.Host] = 0;
        if (Confirm.Ask(MainView, Loc.T("main.declined.title"),
                Loc.T("main.declined.body", u.Host),
                Loc.T("main.declined.yes")))
        {
            Settings.ExceptionSites = (Settings.ExceptionSites + " " + u.Host).Trim();
            Settings.Save();
        }
    }

    // ---------- windows ----------

    public static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Topmost = true;
        ForceForeground(w);
        w.Activate();
        w.Topmost = false;
        w.Focus();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// The browser keeps one helper running, which may no longer be allowed to hand the focus over; joining the
    /// input of the window in front lets Golan's window take the focus anyway (the usual Windows way).
    /// </summary>
    static void ForceForeground(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var front = GetForegroundWindow();
            if (front == hwnd) return;
            uint frontThread = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool joined = frontThread != 0 && frontThread != me && AttachThreadInput(me, frontThread, true);
            try { SetForegroundWindow(hwnd); }
            finally { if (joined) AttachThreadInput(me, frontThread, false); }
        }
        catch (Exception ex) { Log.Write("bring to front failed: " + ex.Message); }
    }

    /// <summary>Opens the guided "add Golan to my browser" window, optionally preselecting a browser (exe name).</summary>
    public void ShowBrowserGuide(Window? owner = null, string? browserExe = null)
    {
        var guide = new UI.BrowserGuideWindow(browserExe);
        var o = owner ?? (MainView is { IsVisible: true } ? MainView : null);
        if (o != null) guide.Owner = o;
        else guide.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        guide.ShowDialog();
    }

    UI.ActivationWindow? _activation;
    DateTime _lastLicenseNotice;

    /// <summary>Opens (or focuses) the activation window. Blocked downloads notify at most once a minute.</summary>
    public void ShowActivation(bool fromBlockedDownload = false)
    {
        if (_activation is { IsLoaded: true })
        {
            BringToFront(_activation);
            return;
        }
        if (fromBlockedDownload)
        {
            if (DateTime.UtcNow - _lastLicenseNotice < TimeSpan.FromMinutes(1)) return;
            _lastLicenseNotice = DateTime.UtcNow;
            Notify(Loc.T("license.notify.title"), Loc.T("license.notify.body"), error: true);
        }
        _activation = new UI.ActivationWindow();
        if (MainView is { IsVisible: true }) _activation.Owner = MainView;
        else _activation.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _activation.Closed += (_, _) => _activation = null;
        _activation.Show();
    }

    /// <summary>After a successful activation: refresh the status bar; the extension intercepts again on its next ping.</summary>
    public void OnLicenseChanged()
    {
        Core.Licensing.LicenseGate.Refresh();
        MainView?.RefreshLicenseStatus();
    }

    public void ShowMain()
    {
        if (MainView == null) return;
        BringToFront(MainView);
    }

    public void ShowAddUrl(string? url = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            BringToFront(new AddDownloadWindow(null));
            return;
        }
        if (MagnetValidator.IsMagnetUri(url))
        {
            BringToFront(new AddDownloadWindow(null, initialUrl: url.Trim()));
            return;
        }
        if (MediaDownloader.LooksLikeMediaPage(url)) { ShowVideo(url, "", "", null, []); return; }
        ShowAddDownload(Manager.CreateHttp(url));
    }

    public void ShowAddDownload(DownloadItem item, TaskCompletionSource<bool>? decision = null)
        => BringToFront(new AddDownloadWindow(item, decision));

    public async Task OpenTorrentFileAsync(string torrentPath, Window? owner = null)
    {
        try
        {
            if (!Program.IsTorrentFileArg(torrentPath))
                throw new ArgumentException("Invalid .torrent file path.");
            var fullPath = Path.GetFullPath(torrentPath);
            var dir = Path.GetDirectoryName(fullPath) ?? "";
            if (!AddDownloadWindow.IsAllowedFolder(dir, Settings))
                throw new InvalidOperationException(Loc.T("main.add.invalidFolder"));
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0 || info.Length > Core.Torrent.TorrentService.MaxTorrentFileSizeBytes)
                throw new InvalidOperationException(Core.Torrent.TorrentText.T("torrent.error.invalidTorrentFile", Path.GetFileName(fullPath)));

            var torrent = await MonoTorrent.Torrent.LoadAsync(fullPath);
            var dlg = TorrentFilesWindow.FromTorrent(torrent);
            dlg.Owner = owner ?? (MainView is { IsVisible: true } ? MainView : null);
            if (dlg.ShowDialog() == true)
            {
                await Manager.AddTorrentFileAsync(
                    fullPath,
                    fileSelection: dlg.Entries,
                    sequential: dlg.SequentialDownload,
                    startNow: true);
            }
        }
        catch (Exception ex)
        {
            Confirm.Info(owner ?? MainView, Loc.T($"vb.add_torrent_or_magnet.title"), ex.Message);
        }
    }

    public async Task CheckForUpdatesAsync(bool interactive, Window? owner = null)
    {
        if (interactive && string.IsNullOrWhiteSpace(Settings.UpdateFeedUrl))
        {
            Confirm.Info(owner ?? MainView, Loc.T($"main.menu.checkUpdates"), Loc.T($"vb.update.no_feed"));
            return;
        }

        var feed = await Updater.CheckForUpdateAsync(Settings, force: interactive);
        if (feed == null)
        {
            if (interactive)
                Confirm.Info(owner ?? MainView, Loc.T($"main.menu.checkUpdates"), Loc.T($"vb.update.up_to_date", Version));
            return;
        }

        var notes = Loc.IsRtl
            ? (string.IsNullOrWhiteSpace(feed.NotesAr) ? feed.NotesEn : feed.NotesAr)
            : (string.IsNullOrWhiteSpace(feed.NotesEn) ? feed.NotesAr : feed.NotesEn);

        if (!interactive)
        {
            _ = Dispatcher.BeginInvoke(() => Notify(Loc.T($"update.available_title", feed.Version), notes));
            return;
        }

        if (!Confirm.Ask(owner ?? MainView,
                Loc.T($"update.available_title", feed.Version),
                Loc.T($"vb.update.install_prompt", feed.Version, notes),
                Loc.T($"common.yes")))
            return;

        var (ok, tempPath, err) = await Updater.DownloadAndVerifyAsync(feed, Manager.Limiter);
        if (!ok)
        {
            Confirm.Info(owner ?? MainView, Loc.T($"update.available_title", feed.Version), err);
            return;
        }

        if (Updater.LaunchApplyUpdate(tempPath))
            ExitApp();
        else
            Confirm.Info(owner ?? MainView, Loc.T($"update.available_title", feed.Version), Loc.T($"update.err_apply"));
    }

    void OfferInstallOnceIfNeeded()
    {
        try
        {
            if (AppPaths.IsPortable) return;
            if (AppPaths.IsIsolated) return;

            var host = new WindowsInstallHost();
            var targetExe = Installer.GetTargetExePath(host.LocalAppDataDir);
            if (string.Equals(Path.GetFullPath(AppPaths.ExePath), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(targetExe))
                return;

            var marker = Path.Combine(AppPaths.LocalDir, "install-offered.flag");
            if (File.Exists(marker))
                return;

            Directory.CreateDirectory(AppPaths.LocalDir);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));

            if (Confirm.Ask(MainView, Loc.T($"install.offer_title"), Loc.T($"install.offer_body"), Loc.T($"common.yes")))
                RunUserInstall(MainView);
        }
        catch (Exception ex)
        {
            Log.Write("offer install check failed: " + ex.Message);
        }
    }

    public void RunUserInstall(Window? owner = null)
    {
        if (AppPaths.IsPortable)
        {
            Confirm.Info(owner ?? MainView, Loc.T($"install.offer_title"), Loc.T($"install.portable_blocked"));
            return;
        }

        try
        {
            var host = new WindowsInstallHost();
            var res = Installer.Install(host, createDesktopShortcut: true, startWithWindows: Settings.StartWithWindows);
            if (res == InstallResult.Installed)
            {
                var targetExe = Installer.GetTargetExePath(host.LocalAppDataDir);
                Confirm.Info(owner ?? MainView, Loc.T($"install.offer_title"), Loc.T($"vb.install.done", targetExe));
            }
        }
        catch (Exception ex)
        {
            Confirm.Info(owner ?? MainView, Loc.T($"install.offer_title"), ex.Message);
        }
    }

    public void ShowVideo(string url, string referrer, string userAgent, JsonArray? cookies, IReadOnlyList<DetectedMedia> media, string title = "")
        => BringToFront(new VideoWindow(Manager, url, referrer, userAgent, cookies, media, title));

    public void ShowBatch(IReadOnlyList<BatchLink> links, string referrer, string userAgent)
        => BringToFront(new BatchWindow(Manager, links, referrer, userAgent));

    public void ShowBatchPattern() => new BatchPatternWindow(Manager) { Owner = MainView }.ShowDialog();

    public void ShowOptions(Window? owner = null, string? tab = null)
    {
        var w = new OptionsWindow(Manager, tab) { Owner = owner ?? MainView };
        w.ShowDialog();
        RefreshTrayMenu();
        MainView?.RefreshToolbarState();
    }

    public void ShowScheduler(Guid? queue = null)
    {
        if (_schedulerWindow is { IsLoaded: true }) { BringToFront(_schedulerWindow); return; }
        _schedulerWindow = new SchedulerWindow(Manager, queue);
        _schedulerWindow.Closed += (_, _) => { _schedulerWindow = null; RefreshTrayMenu(); };
        BringToFront(_schedulerWindow);
    }

    public void ShowGrabber(string? startUrl = null)
    {
        if (_grabberWindow is { IsLoaded: true }) { BringToFront(_grabberWindow); return; }
        _grabberWindow = new GrabberWindow(Manager, startUrl);
        _grabberWindow.Closed += (_, _) => _grabberWindow = null;
        BringToFront(_grabberWindow);
    }

    public void ShowSpeedLimiterSettings()
    {
        if (new SpeedLimiterWindow(Settings) { Owner = MainView }.ShowDialog() == true)
        {
            Settings.Save();
            SpeedRuleController?.SetManualOverride(Settings.SpeedLimitEnabled, Settings.SpeedLimitKB);
            Manager.ApplyLimiter();
            RefreshTrayMenu();
            MainView?.RefreshToolbarState();
        }
    }

    /// <summary>Queue-selection panel shown on "Download later". Returns null when cancelled.</summary>
    public (Guid Queue, bool Start)? PickQueue(Window? owner, int count)
    {
        if (!Settings.ShowQueuePanelOnLater) return (QueueDef.MainId, false);
        var w = new QueuePickerWindow(Manager, count) { Owner = owner is { IsVisible: true } ? owner : MainView };
        return w.ShowDialog() == true ? (w.SelectedQueueId, w.StartNow) : null;
    }

    public void ShowProperties(DownloadItem item, Window? owner = null)
        => new PropertiesWindow(item, Manager) { Owner = owner ?? MainView }.ShowDialog();

    public void ShowZipPreview(DownloadItem item) => BringToFront(new ZipPreviewWindow(item));

    public void ShowProgress(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item.Id, out var existing)) { BringToFront(existing); return; }
        var w = new ProgressWindow(item);
        _progressWindows[item.Id] = w;
        w.Closed += (_, _) => _progressWindows.Remove(item.Id);
        w.Show();
    }

    public void ToggleDropTarget(bool show)
    {
        Settings.ShowDropTarget = show;
        Settings.Save();
        if (show)
        {
            _dropTarget ??= new DropTargetWindow();
            _dropTarget.Closed += (_, _) => _dropTarget = null;
            _dropTarget.Show();
        }
        else
        {
            _dropTarget?.Close();
            _dropTarget = null;
        }
    }

    public bool DropTargetVisible => _dropTarget != null;

    // ---------- download events ----------

    void OnItemStarted(DownloadItem item)
    {
        bool fromRunningQueue = item.QueueId is { } q && Manager.IsQueueRunning(q);
        if (Settings.ShowProgressWindow && !_progressWindows.ContainsKey(item.Id) && !fromRunningQueue)
            ShowProgress(item);
    }

    void OnItemCompleted(DownloadItem item)
    {
        AfterDownload.PlaySound(Settings, "complete");
        bool fromQueue = item.QueueId != null || item.RetryCount > 0;
        bool showDialog = item.OnDoneShowDialog ?? Settings.ShowCompleteDialog;

        if (item.HashState == HashVerifyState.Verified)
            Notify(Loc.T($"hash.status.verified"), Loc.T($"hash.notify.verified", item.FileName));
        else if (item.HashState == HashVerifyState.Mismatch)
            Notify(Loc.T($"hash.status.mismatch"), Loc.T($"hash.notify.mismatch", item.FileName), error: true);

        if (item.ExtractState == ArchiveExtractState.Extracted)
            Notify(Loc.T($"extract.status.done"), item.FileName);
        else if (item.ExtractState == ArchiveExtractState.Failed && !string.IsNullOrWhiteSpace(item.LastResult))
            Notify(Loc.T("main.notify.failed"), $"{item.FileName}\n{item.LastResult}", error: true);

        if (_progressWindows.TryGetValue(item.Id, out var w)) w.OnCompleted();
        if (showDialog && !fromQueue) BringToFront(new CompleteWindow(item));
        else if (w == null) Notify(Loc.T("main.notify.completed"), item.FileName);

        if (item.OnDoneShutdown) ShutdownComputer(item.OnDoneForce);
        else if (item.OnDoneExit) ExitApp();
    }

    void OnItemFailed(DownloadItem item)
    {
        AfterDownload.PlaySound(Settings, "failed");
        // An expired link: offer IDM's "refresh download address" (clicking the notification starts it).
        if (item.LastFailKind is FailKind.Expired or FailKind.Challenge && AddressRefresh.CanRefresh(item))
        {
            Notify(Loc.T("refresh.expired.title"), Loc.T("refresh.expired.body", item.FileName), error: true,
                   onClick: () => BeginAddressRefresh(item, MainView));
            return;
        }
        if (!_progressWindows.ContainsKey(item.Id)) Notify(Loc.T("main.notify.failed"), $"{item.FileName}\n{item.ErrorMessage}", error: true);
    }

    void OnQueueFinished(QueueDef q)
    {
        AfterDownload.PlaySound(Settings, "queueDone");
        Notify(Loc.T("main.notify.queueDoneTitle"), Loc.T("main.notify.queueDoneBody", q.Name));
        RefreshTrayMenu();
        if (AfterDownload.BuildOpenWhenDoneStartInfo(q.OpenWhenDone) is { } psi)
            try { Process.Start(psi); }
            catch (Exception ex) { Log.Write("queue OpenWhenDone failed: " + ex.Message); }
        if (q.ShutdownWhenDone) ShutdownComputer(q.ForceProcesses);
        else if (q.ExitWhenDone) ExitApp();
    }

    public void ShutdownComputer(bool force)
    {
        Notify(Loc.T("main.notify.shutdownTitle"), Loc.T("main.notify.shutdownBody"));
        Process.Start(new ProcessStartInfo("shutdown", force ? "/s /f /t 60" : "/s /t 60") { CreateNoWindow = true, UseShellExecute = false });
        MainView?.ShowShutdownBanner();
    }

    public static void CancelShutdown()
        => Process.Start(new ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false });

    // ---------- tray ----------

    void CreateTray()
    {
        System.Drawing.Icon icon;
        try
        {
            using var s = GetResourceStream(new Uri("pack://application:,,,/Assets/gdm.ico"))!.Stream;
            icon = new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
        }
        catch { icon = System.Drawing.SystemIcons.Application; }

        _tray = new WinForms.NotifyIcon { Icon = icon, Text = Loc.T("main.tray.tooltip"), Visible = true };
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            if (action != null) action(); else ShowMain();
        };
        RefreshTrayMenu();
    }

    public void RefreshTrayMenu()
    {
        if (_tray == null) return;
        var menu = new WinForms.ContextMenuStrip { RightToLeft = Loc.IsRtl ? WinForms.RightToLeft.Yes : WinForms.RightToLeft.No };
        menu.Items.Add(Loc.T("main.tray.open"), null, (_, _) => ShowMain());
        menu.Items.Add(Loc.T("main.tray.newUrl"), null, (_, _) => ShowAddUrl());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var start = new WinForms.ToolStripMenuItem(Loc.T("main.tray.startQueue"));
        var stop = new WinForms.ToolStripMenuItem(Loc.T("main.tray.stopQueue"));
        foreach (var q in Settings.Queues)
        {
            var id = q.Id;
            start.DropDownItems.Add(q.Name, null, (_, _) => { Manager.StartQueue(id); RefreshTrayMenu(); }).Enabled = !Manager.IsQueueRunning(id);
            stop.DropDownItems.Add(q.Name, null, (_, _) => { Manager.StopQueue(id); RefreshTrayMenu(); }).Enabled = Manager.IsQueueRunning(id);
        }
        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(Loc.T("main.tray.scheduler"), null, (_, _) => ShowScheduler());

        var limiter = new WinForms.ToolStripMenuItem(Loc.T("main.tray.speedLimiter")) { Checked = Settings.SpeedLimitEnabled };
        limiter.Click += (_, _) => SetSpeedLimit(!Settings.SpeedLimitEnabled);
        menu.Items.Add(limiter);
        menu.Items.Add(Loc.T("main.tray.pauseAll"), null, (_, _) => Manager.PauseAll());
        var drop = new WinForms.ToolStripMenuItem(Loc.T("main.tray.dropTarget")) { Checked = DropTargetVisible };
        drop.Click += (_, _) => ToggleDropTarget(!DropTargetVisible);
        menu.Items.Add(drop);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("main.tray.options"), null, (_, _) => { ShowMain(); ShowOptions(); });
        menu.Items.Add(Loc.T("main.tray.exit"), null, (_, _) => ExitApp());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void SetSpeedLimit(bool enabled)
    {
        Settings.SpeedLimitEnabled = enabled;
        Settings.Save();
        SpeedRuleController?.SetManualOverride(Settings.SpeedLimitEnabled, Settings.SpeedLimitKB);
        Manager.ApplyLimiter();
        RefreshTrayMenu();
        MainView?.RefreshToolbarState();
    }

    Action? _balloonAction;

    public void Notify(string title, string text, bool error = false, Action? onClick = null)
    {
        if (_tray == null) return;
        _balloonAction = onClick;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = string.IsNullOrWhiteSpace(text) ? " " : text;
        _tray.BalloonTipIcon = error ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    public void ExitApp()
    {
        _schedulerTimer?.Stop();
        SpeedRuleController?.Dispose();
        PowerGuard?.Dispose();
        _pipe?.Stop();
        Manager.Shutdown();
        Settings.Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }
}

public sealed record DetectedMedia(string Url, string Mime, long Size);

/// <summary>A link from "download all links": cookies are the ones the browser holds for that link's own site.</summary>
public sealed record BatchLink(string Url, string Text, string Cookies = "");
