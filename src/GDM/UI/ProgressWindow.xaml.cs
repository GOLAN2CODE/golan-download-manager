using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public sealed class SegmentBar : FrameworkElement
{
    public DownloadItem? Item { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var trackBrush = Application.Current?.TryFindResource("ProgressTrack") as Brush ?? Brushes.Transparent;
        var doneBrush = Application.Current?.TryFindResource("Accent") as Brush ?? Brushes.Transparent;
        var edgeBrush = Application.Current?.TryFindResource("AccentHover") as Brush ?? Brushes.Transparent;
        var dividerBrush = Application.Current?.TryFindResource("Surface") as Brush ?? Brushes.Transparent;

        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(rect, 4, 4));
        dc.DrawRectangle(trackBrush, null, rect);
        if (Item is { TotalSize: > 0 } item)
        {
            List<(long Start, long Len, long Done, bool Active)> segs;
            lock (item.SegLock)
            {
                segs = item.Segments.Select(s => (s.Start, s.Length, s.Downloaded, s.Active)).ToList();
            }
            double scale = ActualWidth / item.TotalSize;
            foreach (var s in segs)
            {
                double x = Math.Clamp(s.Start * scale, 0, ActualWidth);
                double dw = Math.Max(0, s.Done * scale);
                double w = Math.Max(0, Math.Min(dw, ActualWidth - x));
                if (w > 0)
                    dc.DrawRectangle(doneBrush, null, new Rect(x, 0, w, ActualHeight));
                if (s.Active && s.Len > s.Done && ActualWidth >= 2)
                {
                    dc.DrawRectangle(edgeBrush, null, new Rect(Math.Clamp(x + w, 0, ActualWidth - 2), 0, 2, ActualHeight));
                }
                if (s.Start > 0 && x < ActualWidth)
                {
                    dc.DrawRectangle(dividerBrush, null, new Rect(x, 0, Math.Min(1, ActualWidth - x), ActualHeight));
                }
            }
        }
        dc.Pop();
    }
}

public sealed class SegmentRow : ObservableObject
{
    int _number;
    public int Number { get => _number; set => Set(ref _number, value); }

    string _downloaded = "";
    public string Downloaded { get => _downloaded; set => Set(ref _downloaded, value); }

    string _info = "";
    public string Info { get => _info; set => Set(ref _info, value); }
}

public sealed class TorrentProgressFileRow : ObservableObject
{
    string _path;
    public string Path { get => _path; set => Set(ref _path, value); }

    string _sizeText;
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    string _progressText;
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public TorrentProgressFileRow(string path, string sizeText, string progressText)
    {
        _path = path;
        _sizeText = sizeText;
        _progressText = progressText;
    }
}

public partial class ProgressWindow : Window
{
    readonly DownloadItem _item;
    readonly ObservableCollection<SegmentRow> _segmentRows = [];
    readonly ObservableCollection<TorrentProgressFileRow> _torrentFileRows = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string? _cachedTorrentPath;
    MonoTorrent.Torrent? _cachedDiskTorrent;
    bool _loading = true;
    bool _completed;
    bool _details = true;

    public ProgressWindow(DownloadItem item)
    {
        InitializeComponent();
        _item = item;
        Segments.Item = item;
        ConnectionsGrid.ItemsSource = _segmentRows;
        TorrentFilesGrid.ItemsSource = _torrentFileRows;
        Glyph.Text = item.Glyph;
        var displayUrl = FileNames.FormatDisplayUrl(item.Url);
        UrlText.Text = displayUrl;
        UrlText.ToolTip = displayUrl;
        SegmentsCard.Visibility = item.Kind is DownloadKind.Media or DownloadKind.Torrent ? Visibility.Collapsed : Visibility.Visible;
        TorrentCard.Visibility = item.Kind == DownloadKind.Torrent ? Visibility.Visible : Visibility.Collapsed;
        var appSettings = (Application.Current as App)?.Settings;
        SpeedLimitEnabled.IsChecked = item.SpeedLimitKB > 0;
        int initialKb = item.SpeedLimitKB > 0 ? item.SpeedLimitKB : Math.Max(64, appSettings?.SpeedLimitKB ?? 64);
        SpeedLimitBox.Text = initialKb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SpeedLimitBox.IsEnabled = SpeedLimitEnabled.IsChecked == true;
        RememberSpeedLimit.IsChecked = item.RememberSpeedLimit;
        OnDoneShowDialog.IsChecked = item.OnDoneShowDialog ?? (appSettings?.ShowCompleteDialog ?? true);
        OnDoneExit.IsChecked = item.OnDoneExit;
        OnDoneShutdown.IsChecked = item.OnDoneShutdown;
        OnDoneForce.IsChecked = item.OnDoneForce;
        OnDoneForce.IsEnabled = OnDoneShutdown.IsChecked == true;
        _loading = false;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    public void EnsureActive()
    {
        // Downloading again in a window that already showed "completed": let it finish a second time.
        if (_item.Status != DownloadStatus.Completed) _completed = false;
        if (!_timer.IsEnabled) _timer.Start();
        Refresh();
    }

    void Refresh()
    {
        var displayUrl = FileNames.FormatDisplayUrl(_item.Url);
        if (!string.Equals(UrlText.Text, displayUrl, StringComparison.Ordinal))
        {
            UrlText.Text = displayUrl;
            UrlText.ToolTip = displayUrl;
        }
        NameText.Text = _item.FileName;
        NameText.FlowDirection = TextDir.IsRtl(_item.FileName) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        NameText.HorizontalAlignment = HorizontalAlignment.Left;
        NameText.ToolTip = _item.FullPath;
        SaveToText.Text = Loc.IsRtl
            ? Loc.T("main.progress.savedTo", $"\u2066{_item.FullPath}\u2069")
            : Loc.T("main.progress.savedTo", _item.FullPath);
        SaveToText.ToolTip = _item.FullPath;
        bool done = _item.Status == DownloadStatus.Completed;
        Title = _item.Status == DownloadStatus.Downloading && _item.TotalSize > 0
            ? $"{Fmt.Percent(_item.Progress, "0")} · {_item.FileName}"
            : $"{Loc.T("main.brand.name")} · {_item.FileName}";
        if (done)
        {
            var badge = Loc.T("main.progress.completedBadge");
            PercentText.Text = badge;
            PercentText.FlowDirection = TextDir.IsRtl(badge) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            PercentText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            Bar.SetResourceReference(Control.ForegroundProperty, "Success");
        }
        else
        {
            _completed = false;
            PercentText.Text = _item.TotalSize > 0 ? Fmt.Percent(_item.Progress) : "";
            PercentText.FlowDirection = FlowDirection.LeftToRight;
            PercentText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            Bar.SetResourceReference(Control.ForegroundProperty, "Accent");
        }
        Bar.Value = _item.Progress;
        Bar.IsIndeterminate = _item.Status is DownloadStatus.Connecting or DownloadStatus.Merging
            || (_item.IsActive && _item.TotalSize <= 0);
        // The big badge already says "complete": do not repeat it next to it.
        StatusText.Text = _item.IsSeeding && !string.IsNullOrEmpty(_item.Stage)
            ? $"{_item.StatusText} · {_item.Stage}"
            : (done ? "" : _item.StatusText);
        // A failure must stand out as much as success does.
        bool failed = _item.Status == DownloadStatus.Error;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, failed ? "Danger" : "Muted");
        StatusText.FontWeight = failed ? FontWeights.SemiBold : FontWeights.Normal;
        StatusText.FontSize = failed ? 14 : 12;
        SizeText.Text = _item.TotalSize > 0 ? Fmt.Size(_item.TotalSize) : Loc.T("main.progress.unknownSize");
        DoneText.Text = _item.TotalSize > 0
            ? Loc.T("main.progress.doneOf", Fmt.Size(_item.Downloaded), Fmt.Size(_item.TotalSize))
            : Fmt.Size(_item.Downloaded);
        SpeedText.Text = _item.IsActive ? (Fmt.Speed(_item.Speed) is { Length: > 0 } s ? s : "—") : "—";
        EtaText.Text = _item.IsActive ? (Fmt.Eta(_item.Eta) is { Length: > 0 } t ? t : "—") : "—";
        if (_item.Kind == DownloadKind.Torrent)
        {
            TorrentSeedsText.Text = Fmt.Int(_item.TorrentSeeds);
            TorrentPeersText.Text = Fmt.Int(_item.TorrentPeers);
            TorrentRatioText.Text = _item.TorrentRatioText;
            TorrentUploadText.Text = _item.TorrentUploadSpeed > 0 ? _item.TorrentUploadSpeedText : "—";
            RefreshTorrentFiles();
        }
        ConnText.Text = _item.IsActive ? Loc.T("main.progress.activeConnections", _item.ActiveConnections) : Loc.T("main.progress.idleConnections");
        ResumeText.Text = _item.Resumable
            ? Loc.T("main.progress.resumableYes")
            : (_item.TotalSize > 0 || _item.Downloaded > 0 ? Loc.T("main.progress.resumableNo") : "");
        ResumeText.SetResourceReference(TextBlock.ForegroundProperty, _item.Resumable ? "Success" : "Warning");
        List<(int Number, string Downloaded, string Info)> segSnap;
        lock (_item.SegLock)
        {
            segSnap = _item.Segments.Select((s, n) => (n + 1, Fmt.Size(s.Downloaded), s.Info)).ToList();
        }
        while (_segmentRows.Count > segSnap.Count) _segmentRows.RemoveAt(_segmentRows.Count - 1);
        for (int i = 0; i < segSnap.Count; i++)
        {
            var (num, dl, info) = segSnap[i];
            if (i < _segmentRows.Count)
            {
                _segmentRows[i].Number = num;
                _segmentRows[i].Downloaded = dl;
                _segmentRows[i].Info = info;
            }
            else
            {
                _segmentRows.Add(new SegmentRow { Number = num, Downloaded = dl, Info = info });
            }
        }
        Segments.InvalidateVisual();
        ErrorText.Visibility = _item.Status == DownloadStatus.Error ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = _item.ErrorMessage;
        PauseBtn.Content = done
            ? Loc.T("main.progress.openFile")
            : _item.IsActive
                ? Loc.T("main.progress.pause")
                : (_item.Status == DownloadStatus.Error ? Loc.T("main.progress.retry") : Loc.T("main.progress.resume"));
        // Never the Enter key's button once it means "open the file": this window comes to the front by itself,
        // and an Enter meant for another program would run whatever was just downloaded.
        PauseBtn.IsDefault = false;
        OpenBtn.Visibility = Visibility.Collapsed;
        bool canRefreshExpired = _item.Status == DownloadStatus.Error
            && _item.LastFailKind is FailKind.Expired or FailKind.Challenge
            && AddressRefresh.CanRefresh(_item);
        RefreshUrlBtn.Visibility = canRefreshExpired ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        if (done && !_completed)
        {
            OnCompleted();
        }
    }

    void RefreshTorrentFiles()
    {
        try
        {
            var rows = new List<(string Path, string SizeText, string ProgressText)>();
            if (Golan.Gdm.Core.Torrent.TorrentService.TryGetActiveManager(_item.Id, out var mgr) && mgr?.Files is { Count: > 0 } liveFiles)
            {
                foreach (var f in liveFiles)
                {
                    double pct = f.BitField != null ? f.BitField.PercentComplete : _item.Progress;
                    rows.Add((f.Path.Replace('\\', '/'), Fmt.Size(f.Length), Fmt.Percent(pct)));
                }
            }
            else
            {
                string? path = !string.IsNullOrWhiteSpace(_item.TorrentFilePath) && System.IO.File.Exists(_item.TorrentFilePath)
                    ? _item.TorrentFilePath
                    : (!string.IsNullOrWhiteSpace(_item.TorrentSource) && System.IO.File.Exists(_item.TorrentSource) ? _item.TorrentSource : null);
                if (path != null && !string.Equals(path, _cachedTorrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    _cachedTorrentPath = path;
                    MonoTorrent.Torrent.TryLoad(path, out _cachedDiskTorrent);
                }
                if (_cachedDiskTorrent?.Files is { Count: > 0 } diskFiles)
                {
                    foreach (var f in diskFiles)
                        rows.Add((f.Path.Replace('\\', '/'), Fmt.Size(f.Length), Fmt.Percent(_item.Progress)));
                }
                else if (!string.IsNullOrWhiteSpace(_item.FileName))
                {
                    rows.Add((_item.FileName, _item.TotalSize > 0 ? Fmt.Size(_item.TotalSize) : "—", Fmt.Percent(_item.Progress)));
                }
            }

            while (_torrentFileRows.Count > rows.Count) _torrentFileRows.RemoveAt(_torrentFileRows.Count - 1);
            for (int i = 0; i < rows.Count; i++)
            {
                var (p, sz, prog) = rows[i];
                if (i < _torrentFileRows.Count)
                {
                    _torrentFileRows[i].Path = p;
                    _torrentFileRows[i].SizeText = sz;
                    _torrentFileRows[i].ProgressText = prog;
                }
                else
                {
                    _torrentFileRows.Add(new TorrentProgressFileRow(p, sz, prog));
                }
            }
        }
        catch { /* ignore transient torrent file read errors during UI refresh */ }
    }

    public void OnCompleted()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        Refresh();
        if (_item.Kind != DownloadKind.Torrent || !_item.IsSeeding)
            _timer.Stop();
        if (AutoClose.IsChecked == true)
        {
            Close();
            return;
        }
        if (WindowState != WindowState.Minimized)
        {
            Topmost = true;
            Activate();
            Topmost = false;
        }
    }

    void Details_Click(object sender, RoutedEventArgs e)
    {
        _details = !_details;
        ConnectionsGrid.Visibility = _details ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = _details ? Loc.T("main.progress.hideDetails") : Loc.T("main.progress.showDetails");
    }

    void SpeedLimitChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized)
        {
            return;
        }
        bool on = SpeedLimitEnabled.IsChecked == true;
        SpeedLimitBox.IsEnabled = on;
        if (!on)
        {
            _item.SpeedLimitKB = 0;
            (Application.Current as App)?.Manager.Save();
            return;
        }
        if (int.TryParse(SpeedLimitBox.Text, out var kb) && kb > 0)
        {
            _item.SpeedLimitKB = kb;
            (Application.Current as App)?.Manager.Save();
        }
    }

    void SpeedLimitTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => SpeedLimitChanged(sender, e);

    void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized)
        {
            return;
        }
        OnDoneForce.IsEnabled = OnDoneShutdown.IsChecked == true;
        _item.RememberSpeedLimit = RememberSpeedLimit.IsChecked == true;
        _item.OnDoneShowDialog = OnDoneShowDialog.IsChecked;
        _item.OnDoneExit = OnDoneExit.IsChecked == true;
        _item.OnDoneShutdown = OnDoneShutdown.IsChecked == true;
        _item.OnDoneForce = OnDoneForce.IsChecked == true;
        (Application.Current as App)?.Manager.Save();
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_item.Status == DownloadStatus.Completed)
        {
            MainWindow.OpenFile(_item);
        }
        else if (_item.IsActive)
        {
            App.Instance.Manager.Pause(_item);
        }
        else
        {
            App.Instance.Manager.Start(_item);
        }
        Refresh();
    }

    void Open_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFile(_item);

    void RefreshUrl_Click(object sender, RoutedEventArgs e) => App.Instance.BeginAddressRefresh(_item, App.Instance.MainView);

    void Folder_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(_item);

    void Hide_Click(object sender, RoutedEventArgs e) => Close();

    async void TorrentFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MonoTorrent.Torrent? torrent = null;
            if (Golan.Gdm.Core.Torrent.TorrentService.TryGetActiveManager(_item.Id, out var mgr) && mgr?.Torrent != null)
            {
                torrent = mgr.Torrent;
            }
            else
            {
                string? path = !string.IsNullOrWhiteSpace(_item.TorrentFilePath) && System.IO.File.Exists(_item.TorrentFilePath)
                    ? _item.TorrentFilePath
                    : (!string.IsNullOrWhiteSpace(_item.TorrentSource) && System.IO.File.Exists(_item.TorrentSource) ? _item.TorrentSource : null);
                if (path != null)
                    torrent = await MonoTorrent.Torrent.LoadAsync(path);
            }

            if (torrent == null) return;
            var dlg = TorrentFilesWindow.FromTorrent(torrent, _item);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
                Refresh();
        }
        catch (Exception ex)
        {
            Log.Write("progress window torrent files dialog failed: " + ex.Message);
            Confirm.Info(this, Loc.T("main.title"), FriendlyError.Format(ex));
        }
    }

    async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm.Ask(this, Loc.T("main.progress.cancelTitle"), Loc.T("main.progress.cancelBody"), Loc.T("main.progress.cancelDownload"), danger: true))
        {
            return;
        }
        // deleteFile stays false: RemoveAsync already removes this download's partial files, and until the server
        // answers, FullPath is still the plain name, which can be a file the user already had in that folder.
        await App.Instance.Manager.RemoveAsync(_item, deleteFile: false);
        Close();
    }
}
