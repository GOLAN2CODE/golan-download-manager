using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using Golan.Gdm.Integration;

namespace Golan.Gdm.UI;

public sealed class CategoryEntry : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required Func<DownloadItem, bool> Match { get; init; }
    public Guid? QueueId { get; init; }

    int _count;
    public int Count { get => _count; set { if (Set(ref _count, value)) OnPropertyChanged(nameof(CountVisibility)); } }
    public Visibility CountVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow : Window
{
    readonly DownloadManager _manager = App.Instance.Manager;
    readonly AppSettings _settings = App.Instance.Settings;
    readonly List<CategoryEntry> _categories = [];
    readonly ListCollectionView _view;
    readonly Dictionary<System.Windows.Controls.Primitives.ButtonBase, object> _toolCaptions = [];
    CategoryEntry _current = null!;
    string _search = "";
    string _lastClipboard = "";
    DateTime _ignoreClipboardUntil = DateTime.MinValue;
    HwndSource? _hwnd;
    FindWindow? _lastFind;

    public MainWindow()
    {
        InitializeComponent();
        Theme.Apply(_settings.DarkMode);

        BuildCategories();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_manager.Items);
        _view.Filter = o => o is DownloadItem d && _current.Match(d) && MatchesSearch(d);
        _view.IsLiveFiltering = true;
        foreach (var p in new[] { nameof(DownloadItem.Status), nameof(DownloadItem.QueueId), nameof(DownloadItem.Category), nameof(DownloadItem.FileName) })
            _view.LiveFilteringProperties.Add(p);
        Grid.ItemsSource = _view;
        CategoryList.SelectedIndex = 0;
        ApplySort();
        ApplyColumns();
        BuildArrangeMenu();
        foreach (var b in Toolbar.Children.OfType<System.Windows.Controls.Primitives.ButtonBase>()) _toolCaptions[b] = b.Content;
        ApplyToolbarSize();
        CategoryColumn.Width = _settings.HideCategories ? new GridLength(0) : new GridLength(250);

        LiveSpeedGraph.History = _manager.SpeedHistory;
        UpdateSpeedGraphVisibility();

        _manager.Ticked += OnTick;
        _manager.Items.CollectionChanged += (_, _) => UpdateCounts();
        AllowDrop = true;
        Drop += Window_Drop;
        DragOver += Window_DragOver;

        InputBindings.Add(new KeyBinding(new RelayCommand(() => AddUrl_Click(this, new RoutedEventArgs())), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Find_Click(this, new RoutedEventArgs())), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => FindNext_Click(this, new RoutedEventArgs())), Key.F3, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(Clipboard_PasteUrl), Key.V, ModifierKeys.Control));

        SourceInitialized += (_, _) => HookClipboard();
        Closing += MainWindow_Closing;
        Loaded += (_, _) => { UpdateCounts(); RefreshToolbarState(); UpdateSelectionButtons(); };
    }

    public void UpdateSpeedGraphVisibility()
    {
        SpeedGraphHost.Visibility = _settings.ShowSpeedGraph ? Visibility.Visible : Visibility.Collapsed;
        SpeedGraphMenu.IsChecked = _settings.ShowSpeedGraph;
        if (_settings.ShowSpeedGraph) LiveSpeedGraph.Refresh(force: true);
    }

    public void EnsureHandle()
    {
        new WindowInteropHelper(this).EnsureHandle();
        HookClipboard();
    }

    // ---------- categories + queues ----------

    void BuildCategories()
    {
        var selectedKey = (CategoryList.SelectedItem as CategoryEntry)?.Key;
        _categories.Clear();
        _categories.Add(new() { Key = "all", Name = Loc.T("main.cat.all"), Glyph = "", Match = _ => true });
        _categories.Add(new() { Key = "active", Name = Loc.T("main.cat.active"), Glyph = "", Match = i => i.Status != DownloadStatus.Completed });
        _categories.Add(new() { Key = "done", Name = Loc.T("main.cat.done"), Glyph = "", Match = i => i.Status == DownloadStatus.Completed });
        foreach (var q in _settings.Queues)
        {
            var id = q.Id;
            _categories.Add(new() { Key = "queue:" + id, Name = q.Name, Glyph = q.IsSync ? "" : "", QueueId = id, Match = i => i.QueueId == id });
        }
        foreach (var c in Categories.All)
        {
            var key = c.Key;
            _categories.Add(new() { Key = key, Name = c.Name, Glyph = c.Glyph, Match = i => i.Category == key });
        }
        CategoryList.ItemsSource = null;
        CategoryList.ItemsSource = _categories;
        CategoryList.SelectedItem = _categories.FirstOrDefault(c => c.Key == selectedKey) ?? _categories[0];
    }

    /// <summary>Call after queues or categories changed.</summary>
    public void RefreshCategories()
    {
        BuildCategories();
        UpdateCounts();
    }

    void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not CategoryEntry c) return;
        _current = c;
        _view?.Refresh();
        if (_view != null) ApplySort();
        UpdateEmptyState();
    }

    void UpdateCounts()
    {
        foreach (var c in _categories) c.Count = _manager.Items.Count(c.Match);
        UpdateEmptyState();
    }

    void UpdateEmptyState()
    {
        if (_view == null) return;
        EmptyState.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (_manager.Items.Count == 0)
        {
            EmptyTitle.Text = Loc.T("main.empty.noneTitle");
            EmptyText.Text = Loc.T("main.empty.noneText");
        }
        else
        {
            EmptyTitle.Text = Loc.T("main.empty.filteredTitle");
            EmptyText.Text = _search.Length > 0 ? Loc.T("main.empty.noSearchMatch") : Loc.T("main.empty.categoryEmpty");
        }
    }

    // ---------- search & find ----------

    bool MatchesSearch(DownloadItem d)
        => _search.Length == 0
           || d.FileName.Contains(_search, StringComparison.OrdinalIgnoreCase)
           || d.Url.Contains(_search, StringComparison.OrdinalIgnoreCase)
           || d.Description.Contains(_search, StringComparison.OrdinalIgnoreCase);

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // A pasted link means "download this", not "search for this".
        var typed = SearchBox.Text.Trim();
        if ((FileNames.IsHttpUrl(typed) || MagnetValidator.IsMagnetUri(typed)) && !typed.Contains(' '))
        {
            SearchBox.Clear();
            if (MediaDownloader.LooksLikeMediaPage(typed)) App.Instance.ShowVideo(typed, "", "", null, []);
            else App.Instance.ShowAddUrl(typed);
            return;
        }
        _search = typed;
        _view.Refresh();
        UpdateEmptyState();
    }

    void Find_Click(object sender, RoutedEventArgs e)
    {
        var w = new FindWindow(_lastFind?.Query ?? "") { Owner = this };
        if (w.ShowDialog() != true || string.IsNullOrWhiteSpace(w.Query)) return;
        _lastFind = w;
        FindNext(fromStart: true);
    }

    void FindNext_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFind == null) { Find_Click(sender, e); return; }
        FindNext(fromStart: false);
    }

    void FindNext(bool fromStart)
    {
        var f = _lastFind!;
        var cmp = f.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool Hit(string s) => f.WholeString ? string.Equals(s, f.Query, cmp) : s.Contains(f.Query, cmp);
        bool Matches(DownloadItem d) =>
            (f.InName && Hit(d.FileName)) || (f.InDescription && Hit(d.Description))
            || (f.InAddress && (Hit(d.Url) || Hit(d.Referrer) || Hit(d.ParentPage) || Hit(d.Host)));

        // Search every download, not only the visible category.
        if (_current.Key != "all") CategoryList.SelectedIndex = 0;
        var list = _view.Cast<DownloadItem>().ToList();
        int start = fromStart || Grid.SelectedItem is not DownloadItem cur ? 0 : list.IndexOf(cur) + 1;
        var found = list.Skip(start).FirstOrDefault(Matches) ?? (start > 0 ? list.Take(start).FirstOrDefault(Matches) : null);
        if (found == null) { Confirm.Info(this, Loc.T("main.find.title"), Loc.T("main.find.notFound", f.Query)); return; }
        Grid.SelectedItem = found;
        Grid.ScrollIntoView(found);
        Grid.Focus();
    }

    // ---------- live status ----------

    int _tick;
    void OnTick()
    {
        var active = _manager.ActiveCount;
        StatusActive.Text = active > 0 ? Loc.T("main.status.downloading", active) : Loc.T("main.status.idle");
        if (DateTime.UtcNow - _licenseShownAt > TimeSpan.FromMinutes(1)) RefreshLicenseStatus();
        StatusSpeed.Text = active > 0 ? Fmt.Speed(_manager.TotalSpeed) : "";
        StatusLimiter.Text = _manager.Limiter.Enabled
            ? Loc.T("main.status.limiter", Fmt.Speed(_manager.Limiter.BytesPerSecond))
            : (_settings.SpeedLimitEnabled ? Loc.T("main.status.limiter", Fmt.Speed(_settings.SpeedLimitKB * 1024.0)) : "");
        var running = _settings.Queues.Where(q => _manager.IsQueueRunning(q.Id)).Select(q => q.Name).ToList();
        var sep = Loc.IsRtl ? "، " : ", ";
        StatusQueue.Text = _manager.QuotaBlocked ? Loc.T("main.status.quotaBlocked")
            : running.Count > 0 ? Loc.T("main.status.runningQueues", string.Join(sep, running)) : "";
        QueueToggle.IsChecked = _manager.IsQueueRunning(QueueDef.MainId);
        if (_settings.ShowSpeedGraph)
            LiveSpeedGraph.Refresh();
        if (++_tick % 4 == 0)
        {
            UpdateCounts();
            UpdateSelectionButtons();
            BrowserStatus.Text = BrowserIntegration.IsRegistered() ? Loc.T("main.status.browserReady") : Loc.T("main.status.browserNotReady");
        }
        if (_tick % 4 == 0)
        {
            var t = _manager.ActiveCount > 0 ? $"{Loc.T("main.brand.name")} · {Fmt.Speed(_manager.TotalSpeed)}" : Loc.T("main.title");
            if (Title != t) Title = t;
        }
    }

    public void RefreshToolbarState()
    {
        LimiterToggle.IsChecked = _settings.SpeedLimitEnabled;
        QueueToggle.IsChecked = _manager.IsQueueRunning(QueueDef.MainId);
        DropTargetMenu.IsChecked = App.Instance.DropTargetVisible;
        UpdateSpeedGraphVisibility();
        OnTick();
    }

    List<DownloadItem> Selected => Grid.SelectedItems.OfType<DownloadItem>().ToList();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionButtons();

    void UpdateSelectionButtons()
    {
        var sel = Selected;
        ResumeBtn.IsEnabled = sel.Any(i => !i.IsActive && i.Status != DownloadStatus.Completed);
        PauseBtn.IsEnabled = sel.Any(i => i.IsActive || i.Status == DownloadStatus.Queued);
        DeleteBtn.IsEnabled = sel.Count > 0;
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (item.Status == DownloadStatus.Completed) OpenFile(item);
        else App.Instance.ShowProgress(item);
    }

    void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Delete_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && Selected.FirstOrDefault() is { } item)
        {
            if (item.Status == DownloadStatus.Completed) OpenFile(item); else App.Instance.ShowProgress(item);
            e.Handled = true;
        }
    }

    // ---------- closing ----------

    void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var map = ColumnMap();
        foreach (var pref in _settings.Columns)
        {
            if (map.TryGetValue(pref.Key, out var c))
                pref.Width = c.Width.IsStar && pref.Key is "name" or "status" ? 0 : c.ActualWidth;
        }
        _settings.Save();
        if (_settings.CloseToTray)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(Hide, System.Windows.Threading.DispatcherPriority.Background);
            if (_manager.ActiveCount > 0) App.Instance.Notify(Loc.T("main.notify.stillRunningTitle"), Loc.T("main.notify.stillRunningBody"));
            return;
        }
        if (_hwnd != null) RemoveClipboardFormatListener(_hwnd.Handle);
        e.Cancel = true;
        App.Instance.ExitApp();
    }
}

public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
