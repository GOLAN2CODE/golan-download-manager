using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using Golan.Gdm.Integration;

namespace Golan.Gdm.UI;

public partial class MainWindow
{
    // ---------- Tasks menu / toolbar ----------

    void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = _settings.ClipboardMonitor ? ClipboardUrl() : null;
        if (url != null && MagnetValidator.IsMagnetUri(url)) { App.Instance.ShowAddUrl(url); return; }
        if (url != null && MediaDownloader.LooksLikeMediaPage(url)) { App.Instance.ShowVideo(url, "", "", null, []); return; }
        if (url != null) App.Instance.ShowAddDownload(_manager.CreateHttp(url));
        else App.Instance.ShowAddUrl();
    }

    async void AddTorrentOrMagnet_Click(object sender, RoutedEventArgs e)
    {
        var clip = _settings.ClipboardMonitor ? ClipboardUrl() : null;
        if (clip != null && MagnetValidator.IsMagnetUri(clip))
        {
            App.Instance.ShowAddUrl(clip);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T($"vb.add_torrent_or_magnet.title"),
            Filter = Loc.T($"vb.torrent.filter")
        };
        if (dlg.ShowDialog(this) == true)
        {
            await App.Instance.OpenTorrentFileAsync(dlg.FileName, this);
            return;
        }

        var magnet = InputDialog.Ask(this, Loc.T($"vb.add_torrent_or_magnet.title"), Loc.T($"vb.add_torrent_or_magnet.prompt"), "magnet:?xt=urn:btih:");
        if (!string.IsNullOrWhiteSpace(magnet) && MagnetValidator.IsMagnetUri(magnet.Trim()))
            App.Instance.ShowAddUrl(magnet.Trim());
    }

    void ImportIdm_Click(object sender, RoutedEventArgs e)
    {
        new IdmImportWindow(_manager) { Owner = this }.ShowDialog();
        UpdateCounts();
    }

    void Video_Click(object sender, RoutedEventArgs e) => App.Instance.ShowVideo((_settings.ClipboardMonitor ? ClipboardUrl() : null) ?? "", "", "", null, []);
    void Batch_Click(object sender, RoutedEventArgs e) => App.Instance.ShowBatch([], "", "");
    void BatchPattern_Click(object sender, RoutedEventArgs e) => App.Instance.ShowBatchPattern();

    void BatchClipboard_Click(object sender, RoutedEventArgs e)
    {
        string text = "";
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (ExternalException) { /* clipboard locked by another process */ }
        var rx = new System.Text.RegularExpressions.Regex(@"https?://[^\s""'<>]+",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));
        List<BatchLink> links = [];
        try
        {
            links = rx.Matches(text)
                .Select(m => new BatchLink(m.Value.TrimEnd('.', ',', ')', ';'), "")).DistinctBy(l => l.Url).ToList();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            Log.Write("batch clipboard regex timeout: " + ex.Message);
        }
        if (links.Count == 0) { Confirm.Info(this, Loc.T("main.clipboard.title"), Loc.T("main.clipboard.empty")); return; }
        App.Instance.ShowBatch(links, "", "");
    }

    void Grabber_Click(object sender, RoutedEventArgs e) => App.Instance.ShowGrabber(_settings.ClipboardMonitor ? ClipboardUrl() : null);

    void DropTarget_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ToggleDropTarget(DropTargetMenu.IsChecked);
        App.Instance.RefreshTrayMenu();
    }

    void ExportGdm_Click(object sender, RoutedEventArgs e) => Export(text: false);
    void ExportText_Click(object sender, RoutedEventArgs e) => Export(text: true);

    void Export(bool text)
    {
        var w = new ExportWindow(Selected.Count, _manager.QueueItems(QueueDef.MainId).Count()) { Owner = this };
        if (w.ShowDialog() != true) return;
        var items = w.Scope switch
        {
            "selected" => Selected,
            "queue" => _manager.QueueItems(QueueDef.MainId).ToList(),
            _ => _manager.Items.ToList(),
        };
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("main.export.dialogTitle"),
            FileName = text ? "golan-links.txt" : "golan-downloads.gdm",
            Filter = text ? Loc.T("main.export.filterText") : Loc.T("main.export.filterGdm"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Transfer.Export(dlg.FileName, items, text);
            App.Instance.Notify(Loc.T("main.export.doneTitle"), Loc.T("main.export.doneBody", items.Count));
        }
        catch (Exception ex) { Confirm.Info(this, Loc.T("main.export.errorTitle"), Loc.T("main.export.errorBody", ex.Message)); }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("main.import.dialogTitle"), Filter = Loc.T("main.import.filter") };
        if (dlg.ShowDialog(this) != true) return;
        List<DownloadItem> items;
        try { items = Transfer.Import(dlg.FileName, _manager); }
        catch (Exception ex) { Confirm.Info(this, Loc.T("main.import.errorTitle"), Loc.T("main.import.errorBody", ex.Message)); return; }
        items = items.Where(i => _manager.FindDuplicate(i.Url) == null).ToList();
        if (items.Count == 0) { Confirm.Info(this, Loc.T("main.import.errorTitle"), Loc.T("main.import.emptyBody")); return; }
        var pick = App.Instance.PickQueue(this, items.Count);
        if (pick == null) return;
        foreach (var i in items) _manager.Add(i, startNow: false, pick.Value.Queue);
        if (pick.Value.Start) _manager.StartQueue(pick.Value.Queue);
    }

    void Exit_Click(object sender, RoutedEventArgs e) => App.Instance.ExitApp();

    // ---------- File menu ----------

    void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var item = Selected.FirstOrDefault();
        bool completedFile = item != null && item.Status == DownloadStatus.Completed && File.Exists(item.FullPath);
        FileStop.IsEnabled = item is { IsActive: true } || item?.Status == DownloadStatus.Queued;
        FileStart.IsEnabled = item != null && !item.IsActive && item.Status != DownloadStatus.Completed;
        FileRemove.IsEnabled = FileRedownload.IsEnabled = FileProps.IsEnabled = item != null;
        FileHash.IsEnabled = FileVerify.IsEnabled = completedFile;
        FileExtract.IsEnabled = completedFile && Extractor.IsSupportedArchive(item!.FullPath);
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected.Where(i => !i.IsActive && i.Status != DownloadStatus.Completed)) _manager.Start(i);
        UpdateSelectionButtons();
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected) _manager.Pause(i);
        UpdateSelectionButtons();
    }

    async void Redownload_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected) await _manager.RedownloadAsync(i);
    }

    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } i) App.Instance.ShowProperties(i, this);
    }

    void HashItem_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } item && File.Exists(item.FullPath))
            new HashWindow(item) { Owner = this }.ShowDialog();
    }

    async void VerifyItem_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } item || !File.Exists(item.FullPath)) return;
        if (string.IsNullOrWhiteSpace(item.ExpectedHash) && !_settings.AutoDiscoverHash)
        {
            new HashWindow(item) { Owner = this }.ShowDialog();
            return;
        }
        bool ok = await Integrity.VerifyItemAsync(item, _settings);
        _manager.Save();
        if (ok && item.HashState == HashVerifyState.Verified)
            App.Instance.Notify(Loc.T($"hash.status.verified"), Loc.T($"hash.notify.verified", item.FileName));
        else if (item.HashState == HashVerifyState.Mismatch)
            App.Instance.Notify(Loc.T($"hash.status.mismatch"), Loc.T($"hash.notify.mismatch", item.FileName), error: true);
    }

    async void ExtractItem_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } item || !File.Exists(item.FullPath) || !Extractor.IsSupportedArchive(item.FullPath))
            return;
        item.ExtractState = ArchiveExtractState.Extracting;
        item.LastResult = Loc.T($"extract.status.extracting");
        var res = await Extractor.ExtractAsync(
            item.FullPath,
            _settings.AutoExtractTargetMode,
            _settings.AutoExtractExistingRule,
            _settings.AutoExtractDeleteArchive,
            _manager.AskArchivePassword != null ? () => _manager.AskArchivePassword(item) : null,
            _settings.Custom7ZipPath);
        item.ExtractState = res.State;
        item.ExtractedPath = res.TargetDir;
        item.ExtractError = res.ErrorMessage ?? "";
        item.LastResult = res.State switch
        {
            ArchiveExtractState.Extracted => Loc.T($"extract.status.done"),
            ArchiveExtractState.SkippedNo7Zip => Loc.T($"extract.status.skipped_no_7z"),
            ArchiveExtractState.WaitingForParts => Loc.T($"extract.status.waiting_parts"),
            ArchiveExtractState.Failed => Loc.T($"extract.status.failed", res.ErrorMessage ?? ""),
            _ => item.LastResult
        };
        _manager.Save();
        if (res.State == ArchiveExtractState.Extracted)
            App.Instance.Notify(Loc.T($"extract.status.done"), item.FileName);
        else if (res.State == ArchiveExtractState.Failed)
            App.Instance.Notify(Loc.T("main.notify.failed"), $"{item.FileName}\n{item.LastResult}", error: true);
    }

    // ---------- Downloads menu ----------

    void DownloadsMenu_Opened(object sender, RoutedEventArgs e)
    {
        DownloadsRefreshUrlMenu.IsEnabled = Selected.FirstOrDefault() is { Status: not DownloadStatus.Completed };
        StartQueueMenu.Items.Clear();
        StopQueueMenu.Items.Clear();
        foreach (var q in _settings.Queues)
        {
            var id = q.Id;
            var s = new MenuItem { Header = q.Name, IsEnabled = !_manager.IsQueueRunning(id) };
            s.Click += (_, _) => { _manager.StartQueue(id); RefreshToolbarState(); App.Instance.RefreshTrayMenu(); };
            StartQueueMenu.Items.Add(s);
            var t = new MenuItem { Header = q.Name, IsEnabled = _manager.IsQueueRunning(id) };
            t.Click += (_, _) => { _manager.StopQueue(id); RefreshToolbarState(); App.Instance.RefreshTrayMenu(); };
            StopQueueMenu.Items.Add(t);
        }
        LimiterOnMenu.IsEnabled = !_settings.SpeedLimitEnabled;
        LimiterOffMenu.IsEnabled = _settings.SpeedLimitEnabled;
    }

    void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var q in _settings.Queues)
        {
            if (_manager.QueueItems(q.Id).Any(i => i.Status == DownloadStatus.Queued))
                _manager.StartQueue(q.Id);
        }
        int slots = Math.Max(0, _settings.MainQueue.MaxSimultaneous - _manager.ActiveCount);
        foreach (var i in _manager.Items.Where(i => !i.IsActive && i.Status is DownloadStatus.Paused or DownloadStatus.Error).ToList())
        {
            if (slots > 0)
            {
                _manager.Start(i);
                slots--;
            }
            else
            {
                _manager.AddToQueue(i, _settings.MainQueue.Id);
            }
        }
        if (_manager.QueueItems(_settings.MainQueue.Id).Any(i => i.Status == DownloadStatus.Queued) && !_manager.IsQueueRunning(_settings.MainQueue.Id))
            _manager.StartQueue(_settings.MainQueue.Id);
        RefreshToolbarState();
    }

    void PauseAll_Click(object sender, RoutedEventArgs e) { _manager.PauseAll(); RefreshToolbarState(); App.Instance.RefreshTrayMenu(); }

    void StopAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var q in _settings.Queues) _manager.StopQueue(q.Id);
        _manager.PauseAll();
        RefreshToolbarState();
        App.Instance.RefreshTrayMenu();
    }

    async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync(false);
    async void DeleteWithFile_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync(true);

    async Task DeleteSelectedAsync(bool withFile)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        if (withFile || _settings.ConfirmDelete)
        {
            var result = DeleteConfirm.Ask(this, sel, withFile);
            if (result == null) return;
            withFile = result.Value.DeleteFiles;
            if (result.Value.DontAskAgain) { _settings.ConfirmDelete = false; _settings.Save(); }
        }
        foreach (var i in sel) await _manager.RemoveAsync(i, withFile);
        UpdateCounts();
    }

    void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        int n = _manager.Items.Count(i => i.Status == DownloadStatus.Completed);
        if (n == 0) return;
        if (!Confirm.Ask(this, Loc.T("main.deleteCompleted.title"), Loc.T("main.deleteCompleted.body", n), Loc.T("main.deleteCompleted.yes"))) return;
        _manager.RemoveCompleted();
        UpdateCounts();
    }

    void Scheduler_Click(object sender, RoutedEventArgs e) => App.Instance.ShowScheduler(_current.QueueId);

    void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (_manager.IsQueueRunning(QueueDef.MainId)) _manager.StopQueue(QueueDef.MainId);
        else
        {
            if (!_manager.QueueItems(QueueDef.MainId).Any(i => i.Status != DownloadStatus.Completed))
            {
                // Nothing queued yet: queue every unfinished, idle download.
                foreach (var i in _manager.Items.Where(i => !i.IsActive && i.QueueId == null && i.Status is DownloadStatus.Paused or DownloadStatus.Error))
                    _manager.AddToQueue(i, QueueDef.MainId);
            }
            _manager.StartQueue(QueueDef.MainId);
        }
        RefreshToolbarState();
        App.Instance.RefreshTrayMenu();
    }

    void Limiter_Click(object sender, RoutedEventArgs e) => App.Instance.SetSpeedLimit(LimiterToggle.IsChecked == true);
    void LimiterOn_Click(object sender, RoutedEventArgs e) => App.Instance.SetSpeedLimit(true);
    void LimiterOff_Click(object sender, RoutedEventArgs e) => App.Instance.SetSpeedLimit(false);
    void LimiterSettings_Click(object sender, RoutedEventArgs e) => App.Instance.ShowSpeedLimiterSettings();

    void SpeedRules_Click(object sender, RoutedEventArgs e)
    {
        if (new SpeedRulesWindow(_settings, App.Instance.SpeedRuleController) { Owner = this }.ShowDialog() == true)
            RefreshToolbarState();
    }

    void RemoteControl_Click(object sender, RoutedEventArgs e)
    {
        new RemoteWindow(_manager) { Owner = this }.ShowDialog();
    }

    void Options_Click(object sender, RoutedEventArgs e) => App.Instance.ShowOptions(this);

    public void ShowShutdownBanner() => ShutdownBanner.Visibility = Visibility.Visible;

    void CancelShutdown_Click(object sender, RoutedEventArgs e)
    {
        App.CancelShutdown();
        ShutdownBanner.Visibility = Visibility.Collapsed;
    }

    // ---------- View menu ----------

    void ViewMenu_Opened(object sender, RoutedEventArgs e)
    {
        HideCategoriesMenu.IsChecked = _settings.HideCategories;
        SpeedGraphMenu.IsChecked = _settings.ShowSpeedGraph;
        LargeButtonsMenu.IsChecked = !_settings.SmallToolbar;
        SmallButtonsMenu.IsChecked = _settings.SmallToolbar;
        DarkModeMenu.IsChecked = _settings.DarkMode;
        var lang = (_settings.Language ?? "auto").ToLowerInvariant();
        LangAutoMenu.IsChecked = lang is not ("ar" or "en");
        LangArMenu.IsChecked = lang == "ar";
        LangEnMenu.IsChecked = lang == "en";
    }

    void ToggleSpeedGraph_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowSpeedGraph = SpeedGraphMenu.IsChecked;
        _settings.Save();
        UpdateSpeedGraphVisibility();
    }

    void Stats_Click(object sender, RoutedEventArgs e)
    {
        _manager.Stats.Flush();
        new StatsWindow(_manager.Stats) { Owner = this }.ShowDialog();
    }

    void HideCategories_Click(object sender, RoutedEventArgs e)
    {
        _settings.HideCategories = HideCategoriesMenu.IsChecked;
        _settings.Save();
        CategoryColumn.Width = _settings.HideCategories ? new GridLength(0) : new GridLength(250);
    }

    void LargeButtons_Click(object sender, RoutedEventArgs e) { _settings.SmallToolbar = false; _settings.Save(); ApplyToolbarSize(); }
    void SmallButtons_Click(object sender, RoutedEventArgs e) { _settings.SmallToolbar = true; _settings.Save(); ApplyToolbarSize(); }

    void ApplyToolbarSize()
    {
        foreach (var (b, caption) in _toolCaptions)
        {
            b.Content = _settings.SmallToolbar ? "" : caption;
            b.ToolTip ??= caption;
        }
    }

    void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        _settings.DarkMode = DarkModeMenu.IsChecked;
        _settings.Save();
        Theme.Apply(_settings.DarkMode);
    }

    void LangAuto_Click(object sender, RoutedEventArgs e) => SetLanguage("auto");
    void LangAr_Click(object sender, RoutedEventArgs e) => SetLanguage("ar");
    void LangEn_Click(object sender, RoutedEventArgs e) => SetLanguage("en");

    void SetLanguage(string lang)
    {
        _settings.Language = lang;
        _settings.Save();
        if (Confirm.Ask(this, Loc.T("main.lang.restartTitle"), Loc.T("main.lang.restartPrompt"), Loc.T("main.lang.restartYes")))
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 >nul & start \"\" \"%GDM_RESTART_EXE%\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                psi.Environment["GDM_RESTART_EXE"] = exe;
                Process.Start(psi);
            }
            App.Instance.ExitApp();
        }
    }

    // ---------- Help menu ----------

    void HelpMenu_Opened(object sender, RoutedEventArgs e)
    {
        var targetExe = Installer.GetTargetExePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        bool alreadyInstalled = File.Exists(targetExe) || string.Equals(AppPaths.ExePath, targetExe, StringComparison.OrdinalIgnoreCase);
        InstallGdmMenu.Visibility = (AppPaths.IsPortable || alreadyInstalled) ? Visibility.Collapsed : Visibility.Visible;
        InstallGdmMenu.IsEnabled = !AppPaths.IsPortable && !alreadyInstalled;
        UnregisterBrowserMenu.Visibility = AppPaths.IsPortable ? Visibility.Visible : Visibility.Collapsed;
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await App.Instance.CheckForUpdatesAsync(interactive: true, owner: this);
    }

    void BuildPackages_Click(object sender, RoutedEventArgs e)
    {
        var f = new Microsoft.Win32.OpenFolderDialog();
        if (f.ShowDialog() != true) return;
        try
        {
            var extDir = Program.FindExtensionSourceDir();
            if (extDir == null)
                throw new DirectoryNotFoundException("Extension source directory not found.");
            var res = ExtensionPackager.Build(extDir, f.FolderName);
            Confirm.Info(this,
                Loc.T($"vb.packager.ok.title"),
                Loc.T($"vb.packager.ok.body", res.Version, res.ChromeEdgeZipPath, res.FirefoxXpiPath));
        }
        catch (Exception ex)
        {
            Confirm.Info(this, Loc.T($"vb.packager.ok.title"), Loc.T($"vb.packager.err", ex.Message));
        }
    }

    void InstallGdm_Click(object sender, RoutedEventArgs e) => App.Instance.RunUserInstall(this);

    void UnregisterBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new WindowsInstallHost().UnregisterBrowserHosts();
            Confirm.Info(this, Loc.T("main.title"), Loc.T($"vb.browser.unregistered"));
            OnTick();
        }
        catch (Exception ex)
        {
            Confirm.Info(this, Loc.T("main.title"), ex.Message);
        }
    }

    void HelpExtension_Click(object sender, RoutedEventArgs e) => App.Instance.ShowBrowserGuide(this);

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", $"\"{AppPaths.LocalDir}\""); }
        catch (Exception ex) { Log.Write("open log folder failed: " + ex.Message); }
    }

    /// <summary>Status-bar note while the video tools are being prepared for the first time (hidden when done).</summary>
    public void ShowToolsStatus(string text, double percent)
    {
        bool busy = text.Length > 0 && percent is >= 0 and < 100 && !MediaTools.Ready;
        StatusTools.Text = busy ? Loc.T("core.media.prefetch_status", Fmt.Percent(percent, "0")) : "";
        StatusTools.ToolTip = busy ? text : null;
    }

    void Activation_Click(object sender, RoutedEventArgs e) => App.Instance.ShowActivation();

    void StatusLicense_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => App.Instance.ShowActivation();

    DateTime _licenseShownAt;

    /// <summary>Status-bar note while on the trial (days left) or when not activated; hidden once licensed.</summary>
    public void RefreshLicenseStatus()
    {
        _licenseShownAt = DateTime.UtcNow;
        var s = Core.Licensing.LicenseGate.Status;
        if (s.State == Core.Licensing.LicenseState.Licensed) { StatusLicense.Text = ""; return; }
        if (s.CanDownload)
        {
            StatusLicense.Text = Loc.T("license.statusbar.trial", s.DaysLeft);
            StatusLicense.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, s.DaysLeft <= 5 ? "Warning" : "Muted");
        }
        else
        {
            StatusLicense.Text = Loc.T("license.statusbar.blocked");
            StatusLicense.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Danger");
        }
    }

    void About_Click(object sender, RoutedEventArgs e)
        => Confirm.Info(this, Loc.T("main.about.title"), Loc.T("main.about.body", App.Version));

    // ---------- context menu ----------

    void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        var item = sel.FirstOrDefault();
        bool completedFile = item != null && item.Status == DownloadStatus.Completed && File.Exists(item.FullPath);
        MenuOpen.IsEnabled = MenuOpenWith.IsEnabled = item?.Status == DownloadStatus.Completed;
        MenuZip.Visibility = item != null && item.Status != DownloadStatus.Completed && item.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        MenuHash.IsEnabled = MenuVerify.IsEnabled = completedFile;
        MenuExtract.IsEnabled = completedFile && Extractor.IsSupportedArchive(item!.FullPath);
        MenuUnqueue.IsEnabled = sel.Any(i => i.QueueId != null);
        MenuRefreshUrl.IsEnabled = item != null && item.Status != DownloadStatus.Completed;
        MenuUp.IsEnabled = MenuDown.IsEnabled = item?.QueueId != null && _current.QueueId != null;

        MenuQueue.Items.Clear();
        foreach (var q in _settings.Queues)
        {
            var id = q.Id;
            var mi = new MenuItem { Header = q.Name };
            mi.Click += (_, _) =>
            {
                foreach (var i in Selected.Where(i => i.Status != DownloadStatus.Completed || q.IsSync)) _manager.AddToQueue(i, id);
                UpdateCounts();
            };
            MenuQueue.Items.Add(mi);
        }
    }

    void OpenFile_Click(object sender, RoutedEventArgs e) { if (Selected.FirstOrDefault() is { } i) OpenFile(i); }
    void OpenWith_Click(object sender, RoutedEventArgs e) { if (Selected.FirstOrDefault() is { } i) AfterDownload.OpenWith(i.FullPath); }
    void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Selected.FirstOrDefault() is { } i) OpenFolder(i); }
    void ShowProgress_Click(object sender, RoutedEventArgs e) { if (Selected.FirstOrDefault() is { } i) App.Instance.ShowProgress(i); }
    void ZipPreview_Click(object sender, RoutedEventArgs e) { if (Selected.FirstOrDefault() is { } i) App.Instance.ShowZipPreview(i); }

    void Unqueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected.Where(i => i.QueueId != null)) _manager.RemoveFromQueue(i);
        UpdateCounts();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    void Move(int delta)
    {
        if (Selected.FirstOrDefault() is not { } item) return;
        _manager.MoveInQueue(item, delta);
        _view.Refresh();
        Grid.SelectedItem = item;
    }

    void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } i) return;
        SuppressClipboard();
        _lastClipboard = i.Url;
        try { Clipboard.SetText(i.Url); }
        catch (ExternalException ex) { Log.Write("copy url to clipboard failed: " + ex.Message); }
    }

    void ChangeUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } item || item.Status == DownloadStatus.Completed) return;
        if (AddressRefresh.CanRefresh(item)) { App.Instance.BeginAddressRefresh(item, this); return; }
        AskNewAddress(this, item);
    }

    /// <summary>Manual fallback: paste a new link for the same file.</summary>
    public static void AskNewAddress(Window owner, DownloadItem item)
    {
        var _manager = App.Instance.Manager;
        var url = InputDialog.Ask(owner, Loc.T("main.changeUrl.title"), Loc.T("main.changeUrl.body"), item.Url);
        if (url == null || (!FileNames.IsHttpUrl(url) && !MagnetValidator.IsMagnetUri(url))) return;
        _manager.Pause(item);
        if (AddressRefresh.CanRefresh(item) && FileNames.IsHttpUrl(url))
        {
            AddressRefresh.Apply(item, url, "", "", "");
            _manager.Save();
            return;
        }
        item.Url = url.Trim();
        item.SanitizeMegaUrlIfNeeded();
        item.ErrorMessage = "";
        if (item.Status == DownloadStatus.Error) item.Status = DownloadStatus.Paused;
        _manager.Save();
    }

    public static void OpenFile(DownloadItem item)
    {
        if (!File.Exists(item.FullPath)) { OpenFolder(item); return; }
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open file failed: " + ex.Message); }
    }

    public static void OpenFolder(DownloadItem item)
    {
        try
        {
            if (File.Exists(item.FullPath)) Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
            else if (Directory.Exists(item.SaveDir)) Process.Start("explorer.exe", $"\"{item.SaveDir}\"");
        }
        catch (Exception ex)
        {
            Log.Write("open folder failed: " + ex.Message);
        }
    }
}
