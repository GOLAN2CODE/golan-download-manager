using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;

namespace Golan.Gdm.UI;

/// <summary>IDM "Download File Info" dialog.</summary>
public partial class AddDownloadWindow : Window
{
    readonly DownloadManager _manager = App.Instance.Manager;
    readonly DownloadItem? _incoming;
    readonly TaskCompletionSource<bool>? _decision;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    CancellationTokenSource? _probeCts;
    ProbeResult? _probe;
    string? _rawDataUrl;
    bool _nameEdited, _folderEdited, _settingUrl, _settingName, _settingFolder, _startedEarly, _submitted, _submitting;

    string EffectiveUrl => _rawDataUrl ?? UrlBox.Text.Trim();

    void SetInitialUrl(string u)
    {
        _settingUrl = true;
        if (FileNames.IsDataUrl(u) && u.Length > 4096)
        {
            _rawDataUrl = u;
            UrlBox.Text = FileNames.FormatDisplayUrl(u);
        }
        else
        {
            _rawDataUrl = null;
            UrlBox.Text = u;
        }
        _settingUrl = false;
    }

    /// <param name="decision">Completed with true when the user accepts (start or later), false when cancelled. Used by the browser extension.</param>
    public AddDownloadWindow(DownloadItem? item, TaskCompletionSource<bool>? decision = null, string? initialUrl = null)
    {
        InitializeComponent();
        _incoming = item;
        _decision = decision;
        CategoryBox.ItemsSource = Categories.All;
        ConnText.Text = Loc.T("main.add.connections", _manager.Settings.MaxConnections);
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await ProbeAsync(); };

        if (item != null)
        {
            if (decision == null) item.AddedManually = true;
            _nameEdited = item.NameLocked;
            SetInitialUrl(item.Url);
            VideoHint.Visibility = MediaDownloader.LooksLikeMediaPage(item.Url) ? Visibility.Visible : Visibility.Collapsed;
            SetName(item.FileName);
            CategoryBox.SelectedItem = Categories.Get(item.Category);
            SetFolder(item.SaveDir);
            DescriptionBox.Text = item.Description;
            ExpectedHashBox.Text = item.ExpectedHash;
            AutoExtractCheck.IsChecked = item.AutoExtract;
            if (item.TotalSize > 0) InfoText.Text = Loc.T("main.add.sizeValue", Fmt.Size(item.TotalSize));
            if (!string.IsNullOrEmpty(item.Referrer)) SubTitle.Text = Loc.T("main.add.fromHost", TryHost(item.Referrer));
        }
        else
        {
            CategoryBox.SelectedItem = Categories.Get("general");
            if (!string.IsNullOrWhiteSpace(initialUrl))
            {
                SetInitialUrl(initialUrl);
                HandleUrlChanged(EffectiveUrl, scheduleProbe: false);
            }
        }
        UpdateSpaceHint();

        Loaded += (_, _) =>
        {
            if (FileNames.IsDownloadableUrl(EffectiveUrl) || MagnetValidator.IsMagnetUri(EffectiveUrl))
            {
                _ = ProbeAsync();
                NameBox.Focus();
                SelectNameStem(NameBox);
            }
            else UrlBox.Focus();
            if (_incoming != null && _manager.Settings.StartImmediatelyWhileDialog) StartEarly();
        };
        Closed += (_, _) =>
        {
            _probeCts?.Cancel();
            _probeCts?.Dispose();
            _probeCts = null;
            _debounce.Stop();
            if (!_submitted && _startedEarly && _incoming != null) _ = _manager.RemoveAsync(_incoming, deleteFile: false);
            _decision?.TrySetResult(_submitted);
        };
    }

    static string TryHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    internal static (int Start, int Length) GetFileNameStemRange(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return (0, 0);
        int dot = fileName.LastIndexOf('.');
        if (dot <= 0) return (0, fileName.Length);
        return (0, dot);
    }

    static void SelectNameStem(TextBox box)
    {
        var (start, len) = GetFileNameStemRange(box.Text);
        if (len > 0) box.Select(start, len);
    }

    void SetName(string name)
    {
        _settingName = true;
        NameBox.Text = name;
        NameBox.FlowDirection = InputDialog.GetInputFlowDirection(name, name);
        _settingName = false;
    }
    void SetFolder(string dir) { _settingFolder = true; FolderBox.Text = dir; _settingFolder = false; UpdateSpaceHint(); }

    void UpdateSpaceHint()
    {
        if (DiskSpaceText == null) return;
        var folder = FolderBox.Text.Trim();
        var free = SpaceCheck.FreeBytes(folder);
        if (free < 0)
        {
            DiskSpaceText.Text = "";
            DiskSpaceText.Visibility = Visibility.Collapsed;
            return;
        }
        long size = _probe?.Size ?? _incoming?.TotalSize ?? -1;
        bool isMedia = _incoming?.Kind == DownloadKind.Media || MediaDownloader.LooksLikeMediaPage(EffectiveUrl);
        string effectiveTemp = DownloadItem.TempRoot;
        long tempFree = SpaceCheck.AreDifferentDrives(folder, effectiveTemp) ? SpaceCheck.FreeBytes(effectiveTemp) : -1;
        var verdict = SpaceCheck.Evaluate(free, size, _incoming?.Downloaded ?? 0, isMedia, tempFree);
        if (verdict.IsInsufficient)
        {
            DiskSpaceText.Text = SpaceCheck.FormatInsufficientMessage(verdict);
            DiskSpaceText.Foreground = (Brush)FindResource("Danger");
        }
        else if (verdict.IsWarn)
        {
            DiskSpaceText.Text = Loc.T("space.warn_unknown", Fmt.Size(verdict.AvailableBytes));
            DiskSpaceText.Foreground = (Brush)FindResource("Danger");
        }
        else
        {
            DiskSpaceText.Text = Loc.T($"vb.space.free", Fmt.Size(free));
            DiskSpaceText.Foreground = (Brush)FindResource("Muted");
        }
        DiskSpaceText.Visibility = Visibility.Visible;
    }

    /// <summary>"Start downloading immediately while displaying the dialog".</summary>
    void StartEarly()
    {
        if (!App.Instance.ResolveDuplicate(_incoming!, out var handled, this))
        {
            _submitted = handled;
            Close();
            return;
        }
        _manager.Add(_incoming!, startNow: true);
        _startedEarly = true;
        UrlBox.IsReadOnly = true;
        SubTitle.Text = Loc.T("main.add.startedEarlyHint");
        StartBtn.Content = Loc.T("main.dialog.ok");
    }

    void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingUrl) return;
        _rawDataUrl = null;
        HandleUrlChanged(EffectiveUrl, scheduleProbe: true);
    }

    void HandleUrlChanged(string url, bool scheduleProbe)
    {
        VideoHint.Visibility = MediaDownloader.LooksLikeMediaPage(url) ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        _probe = null;
        PreviewBtn.Visibility = Visibility.Collapsed;
        if (MagnetValidator.IsMagnetUri(url))
        {
            if (MagnetValidator.TryParse(url, out var mag, out _) && mag != null && !_nameEdited && !string.IsNullOrWhiteSpace(mag.DisplayName))
                ApplyName(FileNames.Sanitize(mag.DisplayName));
            InfoText.Text = Loc.T($"vb.add.magnet_hint");
            return;
        }
        if (!FileNames.IsDownloadableUrl(url)) { InfoText.Text = Loc.T("main.add.sizeDash"); return; }
        if (!_nameEdited) ApplyName(FileNames.FromUrl(url));
        if (scheduleProbe)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingName) return;
        _nameEdited = true;
        NameBox.FlowDirection = InputDialog.GetInputFlowDirection(NameBox.Text, NameBox.Text);
        var cat = Categories.Detect(NameBox.Text, EffectiveUrl);
        if (cat != "general") CategoryBox.SelectedItem = Categories.Get(cat);
    }

    void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingFolder) _folderEdited = true;
        UpdateSpaceHint();
    }

    void ApplyName(string name)
    {
        SetName(name);
        CategoryBox.SelectedItem = Categories.Get(Categories.Detect(name, EffectiveUrl));
    }

    void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is not CategoryDef c) return;
        KindGlyph.Text = c.Glyph;
        RememberPathBox.Content = Loc.T("main.add.rememberPath", c.Name);
        RememberPathBox.IsChecked = c.RememberLastPath;
        if (!_folderEdited) SetFolder(_manager.Settings.FolderFor(c.Key));
    }

    void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, Loc.T("main.add.newCategoryTitle"), Loc.T("main.add.newCategoryPrompt"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var ext = Path.GetExtension(NameBox.Text).TrimStart('.').ToLowerInvariant();
        var def = new CategoryDef { Key = Categories.NewKey(), Name = name.Trim(), Glyph = "", Extensions = ext };
        var s = _manager.Settings;
        s.CategoryDefs.Insert(Math.Max(0, s.CategoryDefs.Count - 1), def); // before "general"
        s.Save();
        _manager.ApplySettings();
        App.Instance.MainView?.RefreshCategories();
        CategoryBox.ItemsSource = null;
        CategoryBox.ItemsSource = Categories.All;
        CategoryBox.SelectedItem = def;
    }

    async void Check_Click(object sender, RoutedEventArgs e) => await ProbeAsync();

    DownloadItem ProbeItem(string url)
    {
        var p = new DownloadItem { Url = url, Referrer = _incoming?.Referrer ?? "", UserAgent = _incoming?.UserAgent ?? "" };
        if (_incoming != null && url == _incoming.Url)
        {
            p.ProtectedCookies = _incoming.ProtectedCookies;
            p.CookieHost = _incoming.CookieHost;
            p.Login = _incoming.Login;
            p.ProtectedPassword = _incoming.ProtectedPassword;
        }
        return p;
    }

    async Task ProbeAsync()
    {
        _debounce.Stop();
        var url = EffectiveUrl;
        if (MagnetValidator.IsMagnetUri(url))
        {
            if (MagnetValidator.TryParse(url, out var mag, out var magErr) && mag != null)
            {
                if (!_nameEdited && !string.IsNullOrWhiteSpace(mag.DisplayName))
                    ApplyName(FileNames.Sanitize(mag.DisplayName));
                InfoText.Text = mag.DisplayName ?? mag.InfoHashHex;
            }
            else
            {
                ShowError(magErr ?? Loc.T("main.add.invalidUrl"));
            }
            return;
        }
        if (FileNames.IsDataUrl(url))
        {
            try
            {
                var p = DataUrlDownloader.Probe(url);
                _probe = p;
                if (!_nameEdited && p.FileName != null && !_startedEarly) ApplyName(p.FileName);
                InfoText.Text = Loc.T("main.add.sizeValue", Fmt.Size(p.Size));
                UpdateSpaceHint();
            }
            catch (Exception ex)
            {
                InfoText.Text = Loc.T("main.add.sizeDash");
                ShowError(ex is DownloadException ? ex.Message : Loc.T("main.add.invalidUrl"));
            }
            return;
        }
        if (!FileNames.IsHttpUrl(url) && !FileNames.IsFtpUrl(url)) { ShowError(Loc.T("main.add.invalidUrl")); return; }
        var prevCts = _probeCts;
        _probeCts = null;
        prevCts?.Cancel();
        prevCts?.Dispose();
        var cts = _probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        ProbeBar.Visibility = Visibility.Visible;
        InfoText.Text = Loc.T("main.add.connecting");
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var p = await ProbeNetworkUrlAsync(url, cts.Token);
            if (cts.IsCancellationRequested) return;
            _probe = p;
            if (!_nameEdited && p.FileName != null && !_startedEarly) ApplyName(p.FileName);
            var parts = new List<string> { p.Size > 0 ? Loc.T("main.add.sizeValue", Fmt.Size(p.Size)) : Loc.T("main.add.unknownSize") };
            parts.Add(p.Resumable ? Loc.T("main.add.resumable") : Loc.T("main.add.notResumable"));
            if (p.Mime == "text/html") parts.Add(Loc.T("main.add.htmlWarning"));
            InfoText.Text = string.Join("  ·  ", parts);
            // A link that answers with a web page is the most common mistake: make the warning visible.
            if (p.Mime == "text/html") InfoText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
            else InfoText.ClearValue(TextBlock.ForegroundProperty);
            // Do not promise 8 connections for a server that only allows one.
            ConnText.Text = p.Resumable ? Loc.T("main.add.connections", _manager.Settings.MaxConnections) : Loc.T("main.add.connectionOne");
            PreviewBtn.Visibility = p.Resumable && Path.GetExtension(p.FileName ?? NameBox.Text).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateSpaceHint();
        }
        catch (OperationCanceledException) when (_probeCts != cts)
        {
            // Superseded by a newer URL probe.
        }
        catch (OperationCanceledException) { InfoText.Text = Loc.T("main.add.sizeDash"); ShowError(Loc.T("main.add.probeTimeout")); }
        catch (Exception ex)
        {
            InfoText.Text = Loc.T("main.add.sizeDash");
            ShowError(ex is DownloadException ? ex.Message : Loc.T("main.add.probeFailed", FriendlyError.Format(ex)));
        }
        finally
        {
            if (_probeCts == cts) ProbeBar.Visibility = Visibility.Collapsed;
        }
    }

    void Preview_Click(object sender, RoutedEventArgs e) => App.Instance.ShowZipPreview(ProbeItem(EffectiveUrl));

    async Task<ProbeResult> ProbeNetworkUrlAsync(string url, CancellationToken ct)
    {
        if (FileNames.IsFtpUrl(url))
            return await FtpDownloader.ProbeAsync(ProbeItem(url), ct);

        if (Uri.TryCreate(url, UriKind.Absolute, out var srcUri)
            && Core.Resolvers.HostResolvers.All.Any(r => r.CanHandle(srcUri)))
        {
            var resolved = await Core.Resolvers.HostResolvers.TryResolveAsync(srcUri, null, ct);
            if (resolved != null)
            {
                if (resolved.NeedsMegaDecrypt)
                    return new ProbeResult(resolved.Direct, resolved.Size, resolved.Size > 0, resolved.FileName, "application/octet-stream", "", null);

                var resolvedItem = ProbeItem(resolved.Direct.AbsoluteUri);
                if (resolved.Headers.TryGetValue("Cookie", out var rc) && !string.IsNullOrWhiteSpace(rc) && string.IsNullOrEmpty(resolvedItem.Cookies))
                {
                    resolvedItem.Cookies = rc;
                    resolvedItem.CookieHost = resolved.Direct.Host;
                }
                try
                {
                    var dp = await HttpDownloader.ProbeAsync(resolvedItem, ct);
                    var bestName = !string.IsNullOrWhiteSpace(resolved.FileName)
                        && (string.IsNullOrWhiteSpace(dp.FileName) || dp.FileName == FileNames.FromUrl(dp.FinalUri.ToString()))
                        ? resolved.FileName
                        : dp.FileName;
                    var bestSize = dp.Size > 0 ? dp.Size : resolved.Size;
                    return dp with { FileName = bestName, Size = bestSize };
                }
                catch (Exception) when (!ct.IsCancellationRequested && (resolved.Size > 0 || !string.IsNullOrWhiteSpace(resolved.FileName)))
                {
                    return new ProbeResult(resolved.Direct, resolved.Size, false, resolved.FileName, null, "", null);
                }
            }
        }

        return await HttpDownloader.ProbeAsync(ProbeItem(url), ct);
    }

    void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("main.add.browseTitle") };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true)
        {
            SetFolder(dlg.FolderName);
            _folderEdited = true;
        }
    }

    void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_startedEarly && _incoming != null)
        {
            _startedEarly = false;
            // Partials go with the item; the final name may still be a file the user already had (see ProgressWindow cancel).
            _ = _manager.RemoveAsync(_incoming, deleteFile: false);
        }
        App.Instance.ShowVideo(EffectiveUrl, _incoming?.Referrer ?? "", _incoming?.UserAgent ?? "", null, []);
        _submitted = true; // the video window takes over
        Close();
    }

    async void Start_Click(object sender, RoutedEventArgs e) => await SubmitAsync(later: false);
    async void Later_Click(object sender, RoutedEventArgs e) => await SubmitAsync(later: true);
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    internal static bool IsAllowedFolder(string folder, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) return false;
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (ArgumentException) { return false; }

        bool isUnc = full.StartsWith(@"\\", StringComparison.Ordinal) ||
                     (Uri.TryCreate(full, UriKind.Absolute, out var u) && u.IsUnc);
        if (!isUnc) return true;

        IEnumerable<string> roots = [
            settings.DownloadRoot,
            settings.TempDir,
            .. settings.CategoryDefs.SelectMany(c => new[] { c.Folder, c.LastPath })
        ];
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (ArgumentException)
            {
                // Ignore invalid configured root path.
            }
        }
        return false;
    }

    async Task SubmitAsync(bool later)
    {
        if (_submitting || _submitted) return;
        _submitting = true;
        StartBtn.IsEnabled = LaterBtn.IsEnabled = false;
        try
        {
            await SubmitCoreAsync(later);
        }
        finally
        {
            if (!_submitted)
            {
                _submitting = false;
                StartBtn.IsEnabled = LaterBtn.IsEnabled = true;
            }
        }
    }

    async Task SubmitCoreAsync(bool later)
    {
        var name = FileNames.Sanitize(NameBox.Text);
        var folder = FolderBox.Text.Trim();
        if (!IsAllowedFolder(folder, _manager.Settings)) { ShowError(Loc.T("main.add.invalidFolder")); return; }
        try { Directory.CreateDirectory(folder); }
        catch { ShowError(Loc.T("main.add.createFolderFailed")); return; }

        long required = _probe?.Size ?? _incoming?.TotalSize ?? -1;
        if (!later)
        {
            bool isMedia = _incoming?.Kind == DownloadKind.Media || MediaDownloader.LooksLikeMediaPage(EffectiveUrl);
            string effectiveTemp = DownloadItem.TempRoot;
            long tempFree = SpaceCheck.AreDifferentDrives(folder, effectiveTemp) ? SpaceCheck.FreeBytes(effectiveTemp) : -1;
            var verdict = SpaceCheck.Evaluate(SpaceCheck.FreeBytes(folder), required, _incoming?.Downloaded ?? 0, isMedia, tempFree);
            if (verdict.IsInsufficient)
            {
                ShowError(SpaceCheck.FormatInsufficientMessage(verdict));
                return;
            }
            if (verdict.IsWarn && !Confirm.Ask(this, Loc.T("main.title"), Loc.T("space.warn_unknown", Fmt.Size(verdict.AvailableBytes)), Loc.T("main.dialog.ok")))
                return;
        }

        if (_startedEarly && _incoming != null)
        {
            _incoming.Category = (CategoryBox.SelectedItem as CategoryDef)?.Key ?? Categories.Detect(name, _incoming.Url);
            _incoming.Description = DescriptionBox.Text.Trim();
            _incoming.ExpectedHash = ExpectedHashBox.Text.Trim();
            _incoming.AutoExtract = AutoExtractCheck.IsChecked;
            var earlyCat = Categories.Get(_incoming.Category);
            earlyCat.RememberLastPath = RememberPathBox.IsChecked == true;
            if (earlyCat.RememberLastPath) earlyCat.LastPath = folder;
            _manager.Settings.Save();

            if (later)
            {
                var pick = App.Instance.PickQueue(this, 1);
                if (pick == null) return;
                await _manager.RelocateAsync(_incoming, folder, name, _nameEdited, resumeIfRunning: false);
                _incoming.RequeueOnStop = true;
                _manager.AddToQueue(_incoming, pick.Value.Queue);
                if (pick.Value.Start) _manager.StartQueue(pick.Value.Queue);
            }
            else
            {
                await _manager.RelocateAsync(_incoming, folder, name, _nameEdited, resumeIfRunning: true);
            }
            _submitted = true;
            Close();
            return;
        }

        var url = EffectiveUrl;
        if (MagnetValidator.IsMagnetUri(url))
        {
            try
            {
                Guid? qId = null;
                bool startQueue = false;
                if (later)
                {
                    var pick = App.Instance.PickQueue(this, 1);
                    if (pick == null) return;
                    qId = pick.Value.Queue;
                    startQueue = pick.Value.Start;
                }
                var magItem = _manager.AddMagnet(url, saveDir: folder, startNow: false);
                if (_nameEdited && !string.IsNullOrWhiteSpace(name))
                {
                    magItem.FileName = name;
                    magItem.NameLocked = true;
                }
                if (CategoryBox.SelectedItem is CategoryDef selCat)
                {
                    magItem.Category = selCat.Key;
                    selCat.RememberLastPath = RememberPathBox.IsChecked == true;
                    if (selCat.RememberLastPath) selCat.LastPath = folder;
                    _manager.Settings.Save();
                }
                magItem.Description = DescriptionBox.Text.Trim();
                magItem.ExpectedHash = ExpectedHashBox.Text.Trim();
                magItem.AutoExtract = AutoExtractCheck.IsChecked;
                if (qId.HasValue)
                    magItem.QueueId = qId;
                _manager.Save();
                if (!later)
                    _manager.Start(magItem);
                else if (startQueue && qId.HasValue)
                    _manager.StartQueue(qId.Value);
                _submitted = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.Format(ex));
            }
            return;
        }

        if (!FileNames.IsDownloadableUrl(url)) { ShowError(Loc.T("main.add.invalidUrl")); UrlBox.Focus(); return; }

        var item = _incoming ?? _manager.CreateHttp(url);
        if (_decision == null) item.AddedManually = true;
        if (item.Url != url) item.Url = url;
        item.FileName = name;
        item.NameLocked = _nameEdited || item.NameLocked;
        item.SaveDir = folder;
        item.Description = DescriptionBox.Text.Trim();
        item.ExpectedHash = ExpectedHashBox.Text.Trim();
        item.AutoExtract = AutoExtractCheck.IsChecked;
        item.Category = (CategoryBox.SelectedItem as CategoryDef)?.Key ?? Categories.Detect(name, url);
        if (_probe != null && (_probe.Size > 0 || item.TotalSize <= 0 || item.Url != _incoming?.Url))
            item.TotalSize = _probe.Size;

        var cat = Categories.Get(item.Category);
        cat.RememberLastPath = RememberPathBox.IsChecked == true;
        if (cat.RememberLastPath) cat.LastPath = folder;
        _manager.Settings.Save();

        if (!App.Instance.ResolveDuplicate(item, out var handled, this))
        {
            _submitted = handled;
            Close();
            return;
        }

        if (later)
        {
            var pick = App.Instance.PickQueue(this, 1);
            if (pick == null) return;
            _manager.Add(item, startNow: false, pick.Value.Queue);
            if (pick.Value.Start) _manager.StartQueue(pick.Value.Queue);
        }
        else _manager.Add(item, startNow: true);
        _submitted = true;
        Close();
    }
}
