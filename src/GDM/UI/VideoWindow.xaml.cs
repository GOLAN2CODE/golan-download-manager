using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class VideoWindow : Window
{
    readonly DownloadManager _manager;
    readonly string _referrer;
    readonly string _userAgent;
    readonly JsonArray? _cookies;
    readonly IReadOnlyList<DetectedMedia> _media;
    readonly string _initialUrl;
    readonly string _initialTitle;
    readonly bool _drmDetected;
    readonly CancellationTokenSource _windowCts = new();
    CancellationTokenSource? _fetchCts;
    string _cookieFile = "";
    MediaInfo? _info;
    string? _resolvedStreamUrl;
    bool _resolvedIsDirectFile;
    readonly List<SubtitleRow> _subtitleRows = [];
    bool _loaded;
    bool _closing;
    bool _folderEdited;
    bool _updatingFolder;

    bool IsInitialUrl(string url)
        => _openedWithUrl && string.Equals(url, _initialUrl, StringComparison.OrdinalIgnoreCase);

    IReadOnlyList<DetectedMedia> ActiveMedia(string url) => IsInitialUrl(url) ? _media : [];
    string ActiveInitialTitle(string url) => IsInitialUrl(url) ? _initialTitle : "";

    public VideoWindow(DownloadManager manager, string url, string referrer, string userAgent,
        JsonArray? cookies, IReadOnlyList<DetectedMedia> media, string title, bool drmDetected = false)
    {
        InitializeComponent();
        _manager = manager;
        _referrer = referrer;
        _userAgent = userAgent;
        _cookies = cookies;
        _media = media;
        _initialUrl = url.Trim();
        _initialTitle = title;
        _drmDetected = drmDetected;
        UrlBox.Text = url;
        _openedWithUrl = !string.IsNullOrWhiteSpace(_initialUrl);
        SmartModeBox.IsChecked = manager.Settings.VideoSmartMode;
        SetTitle(title);
        LoadExtraDefaults();
    }

    void LoadExtraDefaults()
    {
        var s = _manager.Settings;
        WriteSubsBox.IsChecked = s.DefaultWriteSubs;
        EmbedSubsBox.IsChecked = s.DefaultEmbedSubs;
        IncludeAutoSubsBox.IsChecked = s.DefaultIncludeAutoSubs;
        ConvertSubsToSrtBox.IsChecked = s.DefaultConvertSubsToSrt;
        WriteThumbnailBox.IsChecked = s.DefaultWriteThumbnail;
        EmbedThumbnailBox.IsChecked = s.DefaultEmbedThumbnail;
        EmbedChaptersBox.IsChecked = s.DefaultEmbedChapters;
        SplitChaptersBox.IsChecked = s.DefaultSplitChapters;
        EmbedMetadataBox.IsChecked = s.DefaultEmbedMetadata;
        Mp3BitrateBox.SelectedIndex = s.DefaultMp3Bitrate switch
        {
            128 => 0,
            320 => 2,
            _ => 1,
        };
    }

    void SetTitle(string title)
    {
        TitleText.Text = title;
        TitleText.FlowDirection = TextDir.IsRtl(title) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TitleText.HorizontalAlignment = HorizontalAlignment.Left;
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (!string.IsNullOrWhiteSpace(UrlBox.Text)) await FetchAsync();
        else UrlBox.Focus();
    }

    async void Fetch_Click(object sender, RoutedEventArgs e) => await FetchAsync();

    readonly bool _openedWithUrl;

    async Task FetchAsync()
    {
        var url = UrlBox.Text.Trim();
        if (!FileNames.IsHttpUrl(url))
        {
            ShowError(Loc.T("win.video.err_invalid_url"));
            UrlBox.Focus();
            return;
        }

        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;
        _info = null;
        _resolvedStreamUrl = null;
        _resolvedIsDirectFile = false;
        ResultCard.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Collapsed;
        DirectExpander.Visibility = Visibility.Collapsed;
        ToolCard.Visibility = Visibility.Collapsed;
        DownloadButton.IsEnabled = QueueButton.IsEnabled = false;
        LoadingCard.Visibility = Visibility.Collapsed; // a fetch cancelled just above must not leave its spinner next to an error

        if (DrmDetector.IsKnownDrmPlatform(url)
            || (_drmDetected && (IsInitialUrl(url) || (!string.IsNullOrWhiteSpace(_referrer) && string.Equals(url, _referrer, StringComparison.OrdinalIgnoreCase)))))
        {
            ShowError(Loc.T("core.media.err_drm"), suppressDirectFallback: true);
            return;
        }

        var activeMedia = ActiveMedia(url);
        SetBusy(true);
        ErrorCard.Visibility = Visibility.Collapsed;
        UpdateToolButton.Visibility = Visibility.Collapsed;
        DirectHint.Visibility = Visibility.Collapsed;
        LoadingCard.Visibility = Visibility.Collapsed;
        try
        {
            MediaDownloader.TryDelete(_cookieFile);
            _cookieFile = MediaDownloader.WriteCookieFile(FilterCookiesForUrl(url));

            bool preferSniffedMedia = !MediaDownloader.LooksLikeMediaPage(url) && activeMedia.Count > 0;
            if (preferSniffedMedia && await TryInspectSniffedMediaAsync(url, ct) is { } sniffedFirst)
            {
                _info = sniffedFirst;
                ShowInfo();
                TrySmartStart();
                return;
            }

            if (StreamDownloader.IsDirectStreamUrl(url))
            {
                LoadingCard.Visibility = Visibility.Visible;
                _info = await StreamDownloader.InspectAsync(url, _userAgent, EffectiveReferrer(url), _cookieFile, ct);
                ShowInfo();
                TrySmartStart();
                return;
            }

            if (!MediaTools.YtDlpReady)
            {
                // Only the small reader tool is needed to list the qualities; the merge tool keeps preparing in the background.
                ToolCard.Visibility = Visibility.Visible;
                ToolProgress.IsIndeterminate = true;
                void OnProgress(string text, double percent) => Dispatcher.BeginInvoke(() =>
                {
                    if (text.Length > 0) ToolStatus.Text = text;
                    ToolProgress.IsIndeterminate = percent < 0;
                    if (percent >= 0) ToolProgress.Value = percent;
                });
                MediaTools.ProgressChanged += OnProgress;
                try { await MediaTools.EnsureYtDlpAsync(null, ct); }
                finally { MediaTools.ProgressChanged -= OnProgress; }
            }

            ToolCard.Visibility = Visibility.Collapsed;
            LoadingCard.Visibility = Visibility.Visible;
            var effectiveRef = EffectiveReferrer(url);
            try
            {
                _info = await MediaDownloader.FetchInfoAsync(url, _userAgent, effectiveRef, _cookieFile, ct);
            }
            catch (DownloadException ex) when (ex.Message != Loc.T("core.media.err_drm") && activeMedia.Count > 0)
            {
                if (await TryInspectSniffedMediaAsync(url, ct) is { } fallbackInfo)
                    _info = fallbackInfo;
                else
                    throw;
            }
            ShowInfo();
            MediaTools.WarmUpFfmpeg();
            TrySmartStart();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Media info fetch cancelled by user or newer URL.
        }
        catch (DownloadException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("video window fetch failed: " + ex);
            ShowError(Loc.T("win.video.err_fetch_failed"));
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                ToolCard.Visibility = Visibility.Collapsed;
                LoadingCard.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
        }
    }

    void ShowInfo()
    {
        if (_info == null) return;
        var currentUrl = UrlBox.Text.Trim();
        var activeMedia = ActiveMedia(currentUrl);
        var fallbackTitle = ActiveInitialTitle(currentUrl);
        var title = string.IsNullOrWhiteSpace(_info.Title) ? fallbackTitle : _info.Title;
        SetTitle(title);
        MetaText.Text = string.Join("  ·  ", new[] { _info.Uploader, Fmt.Duration(_info.Duration) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        MetaText.ToolTip = MetaText.Text;
        ResultCard.Visibility = Visibility.Visible;
        ContentPanel.Visibility = Visibility.Visible;

        QualityList.ItemsSource = _info.Qualities;
        var preferred = _info.Qualities.FirstOrDefault(q => q.Key == _manager.Settings.DefaultVideoQuality);
        QualityList.SelectedItem = preferred ?? _info.Qualities.FirstOrDefault();

        PlaylistCard.Visibility = _info.IsPlaylist ? Visibility.Visible : Visibility.Collapsed;
        PlaylistList.ItemsSource = _info.Entries;
        UpdatePlaylistCount();
        RefreshSubtitleList();
        DirectExpander.Visibility = activeMedia.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DirectHeader.Text = Loc.T("win.video.direct_header_format", activeMedia.Count);
        DirectList.ItemsSource = activeMedia.Select(m => new DirectMediaRow(m)).ToList();
        if (activeMedia.Count > 0 && ErrorCard.Visibility == Visibility.Visible) DirectExpander.IsExpanded = true;
        UpdateFolder(false);
        _ = ApplyThumbnailAsync(_info.Thumbnail);
        DownloadButton.IsEnabled = QueueButton.IsEnabled = _info.Qualities.Count > 0;
    }

    void RefreshSubtitleList()
    {
        _subtitleRows.Clear();
        if (_info != null)
        {
            bool includeAuto = IncludeAutoSubsBox.IsChecked == true;
            var preferredLangs = new HashSet<string>(_manager.Settings.DefaultSubtitleLangs, StringComparer.OrdinalIgnoreCase);
            foreach (var track in _info.Subtitles)
            {
                if (track.IsAuto && !includeAuto) continue;
                _subtitleRows.Add(new SubtitleRow(track, preferredLangs.Contains(track.Lang)));
            }
        }
        SubtitleList.ItemsSource = null;
        SubtitleList.ItemsSource = _subtitleRows;
        SubtitleList.Visibility = _subtitleRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void IncludeAutoSubs_Click(object sender, RoutedEventArgs e) => RefreshSubtitleList();

    async Task ApplyThumbnailAsync(string? source)
    {
        Thumbnail.Visibility = Visibility.Collapsed;
        ThumbnailPlaceholder.Visibility = Visibility.Visible;
        var image = await LoadThumbnailAsync(source);
        if (image != null)
        {
            Thumbnail.Source = image;
            Thumbnail.Visibility = Visibility.Visible;
            ThumbnailPlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    static async Task<BitmapImage?> LoadThumbnailAsync(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || !FileNames.IsHttpUrl(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var resp = await HttpDownloader.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            const int MaxThumbBytes = 10 * 1024 * 1024;
            if (resp.Content.Headers.ContentLength is > MaxThumbBytes) return null;
            await using var netStream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int total = 0;
            while (true)
            {
                int n = await netStream.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                total += n;
                if (total > MaxThumbBytes) return null;
                ms.Write(buf, 0, n);
            }
            ms.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write("thumbnail load failed: " + ex.Message);
            return null;
        }
    }

    void QualityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityList.SelectedItem is not MediaQuality q) return;
        Mp3BitratePanel.Visibility = q.Key == "mp3" ? Visibility.Visible : Visibility.Collapsed;
        UpdateFolder(true);
    }

    void QualityList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src
            && ItemsControl.ContainerFromElement(QualityList, src) is ListBoxItem
            && QualityList.SelectedItem is MediaQuality
            && DownloadButton.IsEnabled)
        {
            AddMedia(startNow: true);
        }
    }

    void UpdateFolder(bool qualityChanged)
    {
        if (QualityList.SelectedItem is not MediaQuality q) return;
        if (!qualityChanged || !_folderEdited)
        {
            _updatingFolder = true;
            FolderBox.Text = _manager.Settings.FolderFor(q.AudioOnly ? "music" : "video");
            _updatingFolder = false;
        }
    }

    void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingFolder && ContentPanel.Visibility == Visibility.Visible) _folderEdited = true;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _info?.Entries ?? []) entry.Selected = true;
        UpdatePlaylistCount();
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _info?.Entries ?? []) entry.Selected = false;
        UpdatePlaylistCount();
    }

    void PlaylistCheck_Click(object sender, RoutedEventArgs e) => UpdatePlaylistCount();

    void PlaylistRange_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_info is not { IsPlaylist: true }) return;
        var fromTxt = PlaylistFromBox.Text.Trim();
        var toTxt = PlaylistToBox.Text.Trim();
        var itemsTxt = PlaylistItemsBox.Text.Trim();
        if (string.IsNullOrEmpty(fromTxt) && string.IsNullOrEmpty(toTxt) && string.IsNullOrEmpty(itemsTxt))
            return;

        try
        {
            int? f = string.IsNullOrEmpty(fromTxt) ? null : int.Parse(fromTxt);
            int? t = string.IsNullOrEmpty(toTxt) ? null : int.Parse(toTxt);
            var resolved = MediaArgs.ResolvePlaylistItems(string.IsNullOrEmpty(itemsTxt) ? null : itemsTxt, f, t);
            var indices = MediaArgs.ParsePlaylistIndices(resolved);
            for (int i = 0; i < _info.Entries.Count; i++)
                _info.Entries[i].Selected = indices.Contains(i + 1);
            UpdatePlaylistCount();
        }
        catch
        {
            // Ignore partial typing errors until submission
        }
    }

    void UpdatePlaylistCount()
    {
        var entries = _info?.Entries ?? [];
        PlaylistCount.Text = Loc.T("win.video.playlist_count_format", entries.Count(e => e.Selected));
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true)
        {
            _folderEdited = true;
            FolderBox.Text = dialog.FolderName;
        }
    }

    void DirectDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DirectMediaRow row } btn) return;
        string? customFolder = ContentPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(FolderBox.Text)
            ? FolderBox.Text.Trim()
            : null;
        if (customFolder != null)
        {
            if (!AddDownloadWindow.IsAllowedFolder(customFolder, _manager.Settings))
            {
                ShowError(Loc.T("main.add.invalidFolder"));
                return;
            }
            try { Directory.CreateDirectory(customFolder); }
            catch { ShowError(Loc.T("main.add.createFolderFailed")); return; }
        }
        var effectiveRef = EffectiveReferrer(UrlBox.Text.Trim());
        var isStream = StreamDownloader.IsStreamMime(row.Media.Mime)
            || StreamDownloader.IsDirectStreamUrl(row.Media.Url)
            || row.Media.Mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || row.Media.Mime.Contains("dash", StringComparison.OrdinalIgnoreCase);
        if (isStream)
        {
            var titleGuess = Path.GetFileNameWithoutExtension(FileNames.FromUrl(row.Media.Url)) is { Length: > 0 } n
                && n is not ("index" or "master" or "playlist" or "manifest")
                ? n
                : (_info?.Title ?? TitleText.Text);
            var item = CreateMediaItem(row.Media.Url, titleGuess, "best", folder: customFolder);
            _manager.Add(item, true);
        }
        else
        {
            var item = _manager.CreateHttp(row.Media.Url, effectiveRef, CookieHeader(row.Media.Url), _userAgent, FileNames.FromUrl(row.Media.Url));
            item.ParentPage = UrlBox.Text.Trim();
            if (customFolder != null) item.SaveDir = customFolder;
            if (row.Media.Size > 0) item.TotalSize = row.Media.Size;
            _manager.Add(item, true);
        }
        btn.IsEnabled = false;
    }

    /// <summary>Smart mode: a video sent from the browser starts right away with the remembered quality and folder.</summary>
    void TrySmartStart()
    {
        if (!IsInitialUrl(UrlBox.Text.Trim()) || SmartModeBox.IsChecked != true || _info == null || _info.IsPlaylist) return;
        if (QualityList.SelectedItem is not MediaQuality q || q.Key != _manager.Settings.DefaultVideoQuality) return;
        var title = _info.Title;
        var label = q.Label;
        if (AddMedia(startNow: true))
            (Application.Current as App)?.Notify(Loc.T("video.smart.started_title"), Loc.T("video.smart.started_body", title, label));
    }

    void Download_Click(object sender, RoutedEventArgs e) => _ = AddMedia(startNow: true);
    void Queue_Click(object sender, RoutedEventArgs e) => _ = AddMedia(startNow: false);

    MediaOptions BuildMediaOptionsFromUi(string quality)
    {
        int bitrate = 192;
        if (Mp3BitrateBox.SelectedItem is ComboBoxItem { Tag: string tagStr } && int.TryParse(tagStr, out int parsedRate))
            bitrate = parsedRate;

        int? plFrom = null;
        if (!string.IsNullOrWhiteSpace(PlaylistFromBox.Text))
        {
            if (!int.TryParse(PlaylistFromBox.Text.Trim(), out int f))
                throw new ArgumentException(Loc.T("video.err_bad_playlist_range"));
            plFrom = f;
        }

        int? plTo = null;
        if (!string.IsNullOrWhiteSpace(PlaylistToBox.Text))
        {
            if (!int.TryParse(PlaylistToBox.Text.Trim(), out int t))
                throw new ArgumentException(Loc.T("video.err_bad_playlist_range"));
            plTo = t;
        }

        string? plItems = string.IsNullOrWhiteSpace(PlaylistItemsBox.Text) ? null : PlaylistItemsBox.Text.Trim();

        var selectedLangs = _subtitleRows
            .Where(r => r.Selected && MediaArgs.IsValidLangCode(r.Track.Lang))
            .Select(r => r.Track.Lang)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedLangs.Count > 10)
            throw new ArgumentException(Loc.T("video.err_too_many_subs"));

        return new MediaOptions
        {
            Quality = quality,
            SubtitleLangs = selectedLangs,
            IncludeAutoSubs = IncludeAutoSubsBox.IsChecked == true,
            WriteSubs = WriteSubsBox.IsChecked == true,
            EmbedSubs = EmbedSubsBox.IsChecked == true,
            ConvertSubsToSrt = ConvertSubsToSrtBox.IsChecked == true,
            PlaylistItems = plItems,
            PlaylistFrom = plFrom,
            PlaylistTo = plTo,
            WriteThumbnail = WriteThumbnailBox.IsChecked == true,
            EmbedThumbnail = EmbedThumbnailBox.IsChecked == true,
            EmbedChapters = EmbedChaptersBox.IsChecked == true,
            SplitChapters = SplitChaptersBox.IsChecked == true,
            Mp3BitrateKbps = bitrate,
            EmbedMetadata = EmbedMetadataBox.IsChecked == true,
        };
    }

    void PersistExtraDefaults(MediaOptions options)
    {
        var s = _manager.Settings;
        s.DefaultSubtitleLangs = options.SubtitleLangs.ToList();
        s.DefaultIncludeAutoSubs = options.IncludeAutoSubs;
        s.DefaultWriteSubs = options.WriteSubs;
        s.DefaultEmbedSubs = options.EmbedSubs;
        s.DefaultConvertSubsToSrt = options.ConvertSubsToSrt;
        s.DefaultWriteThumbnail = options.WriteThumbnail;
        s.DefaultEmbedThumbnail = options.EmbedThumbnail;
        s.DefaultEmbedChapters = options.EmbedChapters;
        s.DefaultSplitChapters = options.SplitChapters;
        s.DefaultMp3Bitrate = options.Mp3BitrateKbps;
        s.DefaultEmbedMetadata = options.EmbedMetadata;
        s.Save();
    }

    bool AddMedia(bool startNow)
    {
        if (_info == null || QualityList.SelectedItem is not MediaQuality q) return false;
        var folder = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) folder = _manager.Settings.FolderFor(q.AudioOnly ? "music" : "video");
        if (!AddDownloadWindow.IsAllowedFolder(folder, _manager.Settings))
        {
            ShowError(Loc.T("main.add.invalidFolder"));
            return false;
        }
        try { Directory.CreateDirectory(folder); }
        catch
        {
            ShowError(Loc.T("main.add.createFolderFailed"));
            return false;
        }

        if (startNow)
        {
            string effectiveTemp = DownloadItem.TempRoot;
            long tempFree = SpaceCheck.AreDifferentDrives(folder, effectiveTemp) ? SpaceCheck.FreeBytes(effectiveTemp) : -1;
            var verdict = SpaceCheck.Evaluate(SpaceCheck.FreeBytes(folder), q.Size ?? -1, 0, isMedia: !_resolvedIsDirectFile, tempFree);
            if (verdict.IsInsufficient)
            {
                ShowError(SpaceCheck.FormatInsufficientMessage(verdict));
                return false;
            }
            if (verdict.IsWarn && Application.Current != null &&
                !Confirm.Ask(this, Loc.T("main.title"), Loc.T("space.warn_unknown", Fmt.Size(verdict.AvailableBytes)), Loc.T("main.dialog.ok")))
                return false;
        }

        MediaOptions mediaOptions;
        string? resolvedPlaylistSpec;
        try
        {
            mediaOptions = BuildMediaOptionsFromUi(q.Key);
            _ = MediaArgs.Build(mediaOptions);
            resolvedPlaylistSpec = MediaArgs.ResolvePlaylistItems(mediaOptions.PlaylistItems, mediaOptions.PlaylistFrom, mediaOptions.PlaylistTo);
        }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message);
            return false;
        }

        PersistExtraDefaults(mediaOptions);
        // Remember the choice (as popular downloaders do) and the smart-mode preference.
        _manager.Settings.DefaultVideoQuality = q.Key;
        _manager.Settings.VideoSmartMode = SmartModeBox.IsChecked == true;
        _manager.Settings.Save();

        if (_info.IsPlaylist && resolvedPlaylistSpec != null)
        {
            var indices = MediaArgs.ParsePlaylistIndices(resolvedPlaylistSpec);
            for (int i = 0; i < _info.Entries.Count; i++)
                _info.Entries[i].Selected = indices.Contains(i + 1);
            UpdatePlaylistCount();
        }

        List<PlaylistEntry> selected = _info.IsPlaylist ? _info.Entries.Where(x => x.Selected).ToList() : [];
        if (_info.IsPlaylist && selected.Count == 0)
        {
            ShowError(Loc.T("win.video.err_no_playlist_item"));
            return false;
        }

        Guid? targetQueue = null;
        bool startPickedQueue = false;
        if (!startNow)
        {
            if (Application.Current is App app)
            {
                var pick = app.PickQueue(this, _info.IsPlaylist ? selected.Count : 1);
                if (pick == null) return false;
                targetQueue = pick.Value.Queue;
                startPickedQueue = pick.Value.Start;
            }
            else
            {
                targetQueue = QueueDef.MainId;
            }
        }

        if (_info.IsPlaylist)
        {
            var perItemOptions = mediaOptions with { PlaylistItems = null, PlaylistFrom = null, PlaylistTo = null };
            var queueId = targetQueue ?? QueueDef.MainId;
            foreach (var entry in selected)
                _manager.Add(CreateMediaItem(entry.Url, entry.Title, q.Key, folder, _info.Title, q.Size, perItemOptions), false, queueId);
            if (startNow || startPickedQueue) _manager.StartQueue(queueId);
        }
        else if (_resolvedStreamUrl != null && _resolvedIsDirectFile)
        {
            var ext = Path.GetExtension(FileNames.FromUrl(_resolvedStreamUrl));
            if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
            var fileName = FileNames.Sanitize(_info.Title) + ext;
            var item = _manager.CreateHttp(_resolvedStreamUrl, EffectiveReferrer(UrlBox.Text.Trim()), CookieHeader(_resolvedStreamUrl), _userAgent, fileName);
            item.ParentPage = UrlBox.Text.Trim();
            item.SaveDir = folder;
            if (q.Size is > 0) item.TotalSize = q.Size.Value;
            _manager.Add(item, startNow, targetQueue);
            if (startPickedQueue && targetQueue.HasValue) _manager.StartQueue(targetQueue.Value);
        }
        else
        {
            var targetUrl = _resolvedStreamUrl ?? UrlBox.Text.Trim();
            _manager.Add(CreateMediaItem(targetUrl, _info.Title, q.Key, folder, _info.Title, q.Size, mediaOptions), startNow, targetQueue);
            if (startPickedQueue && targetQueue.HasValue) _manager.StartQueue(targetQueue.Value);
        }
        Close();
        return true;
    }

    string EffectiveReferrer(string pageUrl)
        => !string.IsNullOrWhiteSpace(_referrer) && IsInitialUrl(pageUrl) ? _referrer : pageUrl;

    async Task<MediaInfo?> TryInspectSniffedMediaAsync(string pageUrl, CancellationToken ct)
    {
        var activeMedia = ActiveMedia(pageUrl);
        if (activeMedia.Count == 0) return null;
        var initialTitle = ActiveInitialTitle(pageUrl);
        var effectiveRef = EffectiveReferrer(pageUrl);
        foreach (var candidate in activeMedia)
        {
            bool isStream = StreamDownloader.IsStreamMime(candidate.Mime) || StreamDownloader.IsDirectStreamUrl(candidate.Url);
            if (!isStream) continue;

            var streamCookies = MediaDownloader.WriteCookieFile(FilterCookiesForUrl(candidate.Url));
            try
            {
                var info = await StreamDownloader.InspectAsync(candidate.Url, _userAgent, effectiveRef, streamCookies, ct);
                if (!string.IsNullOrWhiteSpace(initialTitle))
                    info.Title = initialTitle;
                _resolvedStreamUrl = candidate.Url;
                _resolvedIsDirectFile = false;
                return info;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadException ex) when (ex.Message == Loc.T("core.media.err_drm"))
            {
                throw;
            }
            catch
            {
                /* try next sniffed stream candidate */
            }
            finally
            {
                MediaDownloader.TryDelete(streamCookies);
            }
        }

        var directProg = activeMedia
            .Where(m => !StreamDownloader.IsStreamMime(m.Mime) && !StreamDownloader.IsDirectStreamUrl(m.Url))
            .OrderByDescending(m => m.Size)
            .FirstOrDefault();
        if (directProg != null)
        {
            _resolvedStreamUrl = directProg.Url;
            _resolvedIsDirectFile = true;
            var title = !string.IsNullOrWhiteSpace(initialTitle) ? initialTitle : Path.GetFileNameWithoutExtension(FileNames.FromUrl(directProg.Url));
            var info = new MediaInfo { Title = string.IsNullOrWhiteSpace(title) ? "video" : title };
            info.Qualities.Add(new MediaQuality
            {
                Key = "best",
                Label = Loc.T("core.media.best_quality"),
                Size = directProg.Size > 0 ? directProg.Size : null,
            });
            return info;
        }

        return null;
    }

    async void UpdateTool_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        UpdateToolButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        UpdateToolButton.Content = Loc.T("win.video.updating_tool");
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token, timeoutCts.Token);
            var msg = await MediaTools.UpdateYtDlpAsync(linkedCts.Token);
            if (_closing) return;
            Log.Write("yt-dlp update from video window: " + msg);
            UpdateToolButton.Content = Loc.T("win.video.update_tool");
            await FetchAsync();
        }
        catch (OperationCanceledException) when (_closing)
        {
            // Window closed during tool update.
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                UpdateToolButton.Content = Loc.T("win.video.update_tool");
                ShowError(ex is DownloadException ? ex.Message : Loc.T("core.media.tool_update_failed"));
            }
        }
        finally
        {
            if (!_closing)
            {
                UpdateToolButton.IsEnabled = true;
                RetryButton.IsEnabled = true;
            }
        }
    }

    DownloadItem CreateMediaItem(string url, string title, string quality, string? folder = null,
        string? description = null, long? size = null, MediaOptions? mediaOptions = null)
    {
        var extension = quality is "mp3" or "audio" ? ".mp3" : quality == "m4a" ? ".m4a" : ".mp4";
        var audioOnly = quality is "mp3" or "m4a" or "audio";
        var pageUrl = UrlBox.Text.Trim();
        return new DownloadItem
        {
            Kind = DownloadKind.Media,
            Url = url,
            Referrer = EffectiveReferrer(pageUrl),
            ParentPage = pageUrl,
            UserAgent = _userAgent,
            MediaQuality = quality,
            MediaOptions = mediaOptions,
            MediaCookieFile = MediaDownloader.WriteCookieFile(FilterCookiesForUrl(url)),
            FileName = FileNames.Sanitize(title) + extension,
            Category = audioOnly ? "music" : "video",
            SaveDir = folder ?? _manager.Settings.FolderFor(audioOnly ? "music" : "video"),
            TotalSize = size ?? -1,
            Description = description ?? title,
            MaxConnections = 1,
        };
    }

    static bool CookieMatchesUri(JsonObject c, Uri u)
    {
        var rawDomain = c["domain"]?.GetValue<string>() ?? "";
        var domain = rawDomain.TrimStart('.');
        if (domain.Length == 0) return false;

        bool hostOnly = c["hostOnly"] is JsonValue hv && hv.TryGetValue<bool>(out var ho)
            ? ho
            : !rawDomain.StartsWith('.');
        bool hostMatch = hostOnly
            ? u.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            : u.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
              || u.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        if (!hostMatch) return false;

        var cookiePath = c["path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(cookiePath)) cookiePath = "/";
        var reqPath = string.IsNullOrEmpty(u.AbsolutePath) ? "/" : u.AbsolutePath;
        bool pathMatch = reqPath.Equals(cookiePath, StringComparison.Ordinal)
            || (reqPath.StartsWith(cookiePath, StringComparison.Ordinal)
                && (cookiePath.EndsWith('/') || reqPath[cookiePath.Length] == '/'));
        if (!pathMatch) return false;

        bool secure = c["secure"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s;
        return !secure || u.Scheme == Uri.UriSchemeHttps;
    }

    JsonArray? FilterCookiesForUrl(string url)
    {
        if (_cookies == null || _cookies.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var filtered = new JsonArray();
        foreach (var c in _cookies.OfType<JsonObject>())
        {
            if (CookieMatchesUri(c, u))
                filtered.Add(c.DeepClone());
        }
        return filtered.Count > 0 ? filtered : null;
    }

    /// <summary>Cookie header for one URL: only cookies whose domain, path and secure flag match it.</summary>
    string CookieHeader(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        return string.Join("; ", (_cookies ?? []).OfType<JsonObject>()
            .Where(c => CookieMatchesUri(c, u))
            .Select(c => (Name: c["name"]?.GetValue<string>() ?? "", Value: c["value"]?.GetValue<string>() ?? ""))
            .Where(c => c.Name.Length > 0)
            .Select(c => $"{c.Name}={c.Value}"));
    }

    void SetBusy(bool busy)
    {
        LoadingCard.Visibility = busy && MediaTools.Ready ? Visibility.Visible : LoadingCard.Visibility;
        DownloadButton.IsEnabled = QueueButton.IsEnabled = !busy && _info?.Qualities.Count > 0;
    }

    void ShowError(string message, bool suppressDirectFallback = false)
    {
        bool isDrm = suppressDirectFallback || message == Loc.T("core.media.err_drm");
        var activeMedia = ActiveMedia(UrlBox.Text.Trim());
        ErrorText.Text = message;
        ErrorCard.Visibility = Visibility.Visible;
        UpdateToolButton.Visibility = !isDrm && MediaDownloader.IsToolUpdateRecommended(message)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!isDrm && activeMedia.Count > 0)
        {
            DirectHint.Visibility = Visibility.Visible;
            DirectExpander.Visibility = Visibility.Visible;
            DirectHeader.Text = Loc.T("win.video.direct_header_format", activeMedia.Count);
            DirectList.ItemsSource = activeMedia.Select(m => new DirectMediaRow(m)).ToList();
            DirectExpander.IsExpanded = true;
        }
        else
        {
            DirectHint.Visibility = Visibility.Collapsed;
            DirectExpander.Visibility = Visibility.Collapsed;
        }
    }

    void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = FetchAsync(); }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _windowCts.Cancel();
        _windowCts.Dispose();
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        MediaDownloader.TryDelete(_cookieFile);
        _cookieFile = "";
    }

    public sealed class DirectMediaRow
    {
        public DetectedMedia Media { get; }
        public string FileName => FileNames.FromUrl(Media.Url);
        public string Details => string.Join("  ·  ", new[] { Media.Mime, Media.Size > 0 ? Fmt.Size(Media.Size) : "" }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        public DirectMediaRow(DetectedMedia media) => Media = media;
    }

    public sealed class SubtitleRow(SubtitleTrack track, bool selected = false)
    {
        public SubtitleTrack Track { get; } = track;
        public bool Selected { get; set; } = selected;
        public string DisplayLabel => Track.IsAuto
            ? $"{Track.Name} ({Track.Lang}) [{Loc.T("video.sub_auto_badge")}]"
            : $"{Track.Name} ({Track.Lang})";
    }
}
