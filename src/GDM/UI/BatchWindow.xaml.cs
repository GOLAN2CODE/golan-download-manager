using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.UI;

public partial class BatchWindow : Window
{
    readonly DownloadManager _manager;
    readonly ObservableCollection<BatchLinkRow> _rows = [];
    readonly ICollectionView _view;
    string _referrer;
    string _userAgent;
    bool _pasteMode;
    string _customFolder = "";
    static readonly HashSet<string> WebExtensions = new(StringComparer.OrdinalIgnoreCase) { "html", "htm", "php", "asp", "aspx", "jsp" };
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico" };

    public BatchWindow(DownloadManager manager, IReadOnlyList<BatchLink> links, string referrer, string userAgent)
    {
        InitializeComponent();
        _manager = manager;
        _referrer = referrer;
        _userAgent = userAgent;

        LinksGrid.ItemsSource = _rows;
        CategoryCombo.ItemsSource = Categories.All;
        CategoryCombo.SelectedItem = Categories.Get("general");
        CategoryFolderRadio.Checked += SaveModeChanged;
        OneCategoryRadio.Checked += SaveModeChanged;
        CustomFolderRadio.Checked += SaveModeChanged;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterPredicate;

        _pasteMode = links.Count == 0;
        PasteHeader.Visibility = _pasteMode ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = _pasteMode ? Visibility.Collapsed : Visibility.Visible;
        SelectButtons.Visibility = _pasteMode ? Visibility.Collapsed : Visibility.Visible;

        if (!_pasteMode)
        {
            foreach (var l in links)
                AddRow(l.Url, l.Text, l.Cookies);
            ApplyDefaultSelection();
        }

        Loaded += BatchWindow_Loaded;
    }

    void BatchWindow_Loaded(object sender, RoutedEventArgs e)
    {
        BuildFilterChips();
        UpdateCounter();
    }

    // ---------- Row model ----------

    public sealed class BatchLinkRow : ObservableObject
    {
        string _url = "";
        public string Url { get => _url; set => Set(ref _url, value); }

        string _linkText = "";
        public string LinkText { get => _linkText; set => Set(ref _linkText, value); }

        string _fileName = "";
        public string FileName { get => _fileName; set => Set(ref _fileName, value); }

        string _category = "general";
        public string Category { get => _category; set => Set(ref _category, value); }

        public string Cookies { get; set; } = "";
        public string CookieHost { get; set; } = "";

        bool _selected;
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
    }

    // ---------- Link parsing ----------

    internal static (string Cookies, string CookieHost) ResolveCookiesForLink(string? url, string? referrer, string? cookies)
    {
        if (string.IsNullOrEmpty(cookies) || !Uri.TryCreate(url, UriKind.Absolute, out var linkUri)
            || (linkUri.Scheme != Uri.UriSchemeHttp && linkUri.Scheme != Uri.UriSchemeHttps))
            return ("", "");
        if (Uri.TryCreate(referrer, UriKind.Absolute, out var refUri)
            && linkUri.Host.Equals(refUri.Host, StringComparison.OrdinalIgnoreCase)
            && refUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !linkUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return ("", "");
        return (cookies, linkUri.Host);
    }

    void AddRow(string url, string text, string cookies = "")
    {
        var fileName = FileNames.FromUrl(url);
        var cat = Categories.Detect(fileName, url);
        var (safeCookies, cookieHost) = ResolveCookiesForLink(url, _referrer, cookies);
        _rows.Add(new BatchLinkRow { Url = url, LinkText = text, FileName = fileName, Category = cat, Cookies = safeCookies, CookieHost = cookieHost });
    }

    public const int MaxBatchExpandedLinks = 10_000;
    static readonly Regex UrlRegex = new(@"(?:https?|ftps?)://[^\s\)\]\}\>\""\']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex RangeRegex = new(@"^(.+)\[(\d+)-(\d+)\](.+)$", RegexOptions.Compiled);

    public static List<(string Url, string Text)> ParseBatchInput(string? text, int maxItems = MaxBatchExpandedLinks)
    {
        var urls = new List<(string Url, string Text)>();
        if (string.IsNullOrWhiteSpace(text) || maxItems <= 0)
            return urls;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (urls.Count >= maxItems) break;
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Check for range pattern: https://site/file[01-20].zip
            var rangeMatch = RangeRegex.Match(trimmed);
            if (rangeMatch.Success
                && int.TryParse(rangeMatch.Groups[2].Value, out int start)
                && int.TryParse(rangeMatch.Groups[3].Value, out int end)
                && start <= end)
            {
                var prefix = rangeMatch.Groups[1].Value;
                var suffix = rangeMatch.Groups[4].Value;
                var pad = Math.Min(16, rangeMatch.Groups[2].Value.Length);
                var sample = prefix + start.ToString().PadLeft(pad, '0') + suffix;
                if (FileNames.IsNetworkDownloadUrl(sample))
                {
                    for (long i = start; i <= end && urls.Count < maxItems; i++)
                    {
                        var num = i.ToString().PadLeft(pad, '0');
                        var expanded = prefix + num + suffix;
                        if (FileNames.IsNetworkDownloadUrl(expanded))
                            urls.Add((expanded, ""));
                    }
                    continue;
                }
            }

            // Extract all URLs from the line
            bool matchedAny = false;
            foreach (Match m in UrlRegex.Matches(trimmed))
            {
                if (urls.Count >= maxItems) break;
                var candidate = m.Value.TrimEnd('.', ',', ';');
                if (FileNames.IsNetworkDownloadUrl(candidate))
                {
                    urls.Add((candidate, trimmed));
                    matchedAny = true;
                }
            }
            if (!matchedAny && urls.Count < maxItems && FileNames.IsNetworkDownloadUrl(trimmed))
            {
                urls.Add((trimmed, ""));
            }
        }
        return urls;
    }

    void ParseLinks_Click(object sender, RoutedEventArgs e)
    {
        var text = PasteBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var urls = ParseBatchInput(text);
        if (urls.Count == 0)
        {
            App.Instance?.Notify(Loc.T("win.batch.no_links_title"), Loc.T("win.batch.no_links_msg"), error: true);
            return;
        }

        _rows.Clear();
        foreach (var (url, linkText) in urls)
            AddRow(url, linkText);

        _pasteMode = false;
        PasteHeader.Visibility = Visibility.Collapsed;
        FilterBar.Visibility = Visibility.Visible;
        SelectButtons.Visibility = Visibility.Visible;
        ApplyDefaultSelection(selectAllByDefault: true);
        BuildFilterChips();
        UpdateCounter();
        if (urls.Count >= MaxBatchExpandedLinks)
            App.Instance?.Notify(Loc.T("win.batch.limit_title"), Loc.T("win.batch.limit_msg", MaxBatchExpandedLinks));
    }

    void PasteFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                PasteBox.Text = Clipboard.GetText();
            }
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Write("batch window clipboard paste failed: " + ex.Message);
        }
    }

    // ---------- Filtering ----------

    void BuildFilterChips()
    {
        FilterChips.Children.Clear();
        var categories = new[]
        {
            Loc.T("win.batch.chip.all"),
            Loc.T("win.batch.chip.video"),
            Loc.T("win.batch.chip.audio"),
            Loc.T("win.batch.chip.compressed"),
            Loc.T("win.batch.chip.documents"),
            Loc.T("win.batch.chip.programs"),
            Loc.T("win.batch.chip.images")
        };
        var catKeys = new[] { "all", "video", "music", "compressed", "documents", "programs", "images" };

        for (int i = 0; i < categories.Length; i++)
        {
            var idx = i;
            var btn = new ToggleButton
            {
                Content = categories[i],
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("Chip"),
                Tag = catKeys[i],
                IsChecked = i == 0
            };
            btn.Checked += FilterChip_Checked;
            btn.Unchecked += FilterChip_Unchecked;
            FilterChips.Children.Add(btn);
        }
    }

    string _activeFilterCategory = "all";
    string _activeTextFilter = "";

    void FilterChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is string key)
        {
            // Uncheck others
            foreach (var child in FilterChips.Children)
            {
                if (child is ToggleButton other && other != btn)
                    other.IsChecked = false;
            }
            _activeFilterCategory = key;
            _view.Refresh();
            UpdateEmptyState();
        }
    }

    void FilterChip_Unchecked(object sender, RoutedEventArgs e)
    {
        // If all unchecked, check "all"
        if (FilterChips.Children.OfType<ToggleButton>().All(b => !b.IsChecked.GetValueOrDefault()))
        {
            var allBtn = FilterChips.Children.OfType<ToggleButton>().FirstOrDefault(b => (string)b.Tag == "all");
            if (allBtn != null) allBtn.IsChecked = true;
        }
    }

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _activeTextFilter = FilterBox.Text?.Trim().ToLowerInvariant() ?? "";
        _view.Refresh();
        UpdateEmptyState();
    }

    void HideFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        foreach (var row in _rows)
        {
            var ext = Path.GetExtension(row.FileName).TrimStart('.').ToLowerInvariant();
            if (HideWebBox?.IsChecked == true && WebExtensions.Contains(ext)) row.Selected = false;
            if (HideImagesBox?.IsChecked == true && ImageExtensions.Contains(ext)) row.Selected = false;
            if (HideDuplicatesBox?.IsChecked == true && _manager.FindDuplicate(row.Url) != null) row.Selected = false;
        }
        _view.Refresh();
        UpdateCounter();
    }

    bool FilterPredicate(object obj)
    {
        if (obj is not BatchLinkRow row) return false;

        var ext = Path.GetExtension(row.FileName).TrimStart('.').ToLowerInvariant();
        if (HideWebBox?.IsChecked == true && WebExtensions.Contains(ext)) return false;
        if (HideImagesBox?.IsChecked == true && ImageExtensions.Contains(ext)) return false;
        if (HideDuplicatesBox?.IsChecked == true && _manager.FindDuplicate(row.Url) != null) return false;

        // Category filter
        if (_activeFilterCategory != "all")
        {
            var catKey = _activeFilterCategory;
            if (catKey == "images")
            {
                if (!ImageExtensions.Contains(ext))
                    return false;
            }
            else if (row.Category != catKey)
            {
                return false;
            }
        }

        // Text filter
        if (!string.IsNullOrEmpty(_activeTextFilter))
        {
            var haystack = (row.Url + " " + row.FileName + " " + row.LinkText + " " + row.Category).ToLowerInvariant();
            // Support space-separated extensions like "zip mp4"
            var terms = _activeTextFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!terms.Any(t => haystack.Contains(t)))
                return false;
        }

        return true;
    }

    // ---------- Selection ----------

    void ApplyDefaultSelection(bool selectAllByDefault = false)
    {
        var extList = _manager.Settings.ExtensionList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var ext = Path.GetExtension(row.FileName).TrimStart('.').ToLowerInvariant();
            row.Selected = selectAllByDefault || _pasteMode || extList.Contains(ext);
            if (HideWebBox?.IsChecked == true && WebExtensions.Contains(ext)) row.Selected = false;
            if (HideImagesBox?.IsChecked == true && ImageExtensions.Contains(ext)) row.Selected = false;
            if (HideDuplicatesBox?.IsChecked == true && _manager.FindDuplicate(row.Url) != null) row.Selected = false;
        }
        UpdateCounter();
    }

    void RowCheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateCounter();
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(r => FilterPredicate(r))) row.Selected = true;
        UpdateCounter();
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Selected = false;
        UpdateCounter();
    }

    void UpdateCounter()
    {
        int selected = _rows.Count(r => r.Selected);
        CounterText.Text = Loc.T("win.batch.counter_format", selected, _rows.Count);
        UpdateEmptyState();
    }

    void UpdateEmptyState()
    {
        if (EmptyLinksText == null) return;
        if (_rows.Count == 0)
        {
            EmptyLinksText.Text = Loc.T("win.batch.empty_paste");
            EmptyLinksText.Visibility = Visibility.Visible;
            return;
        }
        bool anyVisible = _rows.Any(FilterPredicate);
        EmptyLinksText.Text = Loc.T("win.batch.empty_filter");
        EmptyLinksText.Visibility = anyVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    public static string FormatBatchRename(string pattern, string originalFileName, int oneBasedIndex)
    {
        var pat = pattern.Trim();
        var stem = pat.Contains('*') ? pat.Replace("*", oneBasedIndex.ToString()) : $"{pat}_{oneBasedIndex}";
        if (string.IsNullOrEmpty(Path.GetExtension(pat)))
        {
            var origExt = Path.GetExtension(originalFileName);
            if (!string.IsNullOrEmpty(origExt))
                stem += origExt;
        }
        return FileNames.Sanitize(stem);
    }

    // ---------- Save folder ----------

    void SaveModeChanged(object sender, RoutedEventArgs e)
    {
        CategoryCombo.Visibility = OneCategoryRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CustomFolderPanel.Visibility = CustomFolderRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void BrowseCustom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = Loc.T("win.batch.browse_title")
        };
        if (dlg.ShowDialog(this) == true)
        {
            _customFolder = dlg.FolderName;
            CustomFolderBox.Text = dlg.FolderName;
        }
    }

    // ---------- Download ----------

    void DownloadSelected_Click(object sender, RoutedEventArgs e) => ProcessSelected(startQueue: true);

    void AddToQueue_Click(object sender, RoutedEventArgs e) => ProcessSelected(startQueue: false);

    void ProcessSelected(bool startQueue)
    {
        if (_pasteMode && _rows.Count == 0 && !string.IsNullOrWhiteSpace(PasteBox.Text))
        {
            ParseLinks_Click(this, new RoutedEventArgs());
            if (_rows.Count == 0)
                return;
        }
        var selectedRows = _rows.Where(r => r.Selected).ToList();
        if (selectedRows.Count == 0)
        {
            App.Instance?.Notify(Loc.T("win.batch.no_selection_title"), Loc.T("win.batch.no_selection_msg"), error: true);
            return;
        }
        var customFolder = CustomFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(customFolder)) customFolder = _customFolder;
        if (CustomFolderRadio.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(customFolder) || !AddDownloadWindow.IsAllowedFolder(customFolder, _manager.Settings))
            {
                Confirm.Info(this, Loc.T("win.batch.title"), Loc.T("win.batch.err_invalid_folder"));
                return;
            }
        }
        var pick = App.Instance?.PickQueue(this, selectedRows.Count);
        if (pick == null) return;

        DownloadSelectedBtn.IsEnabled = false;
        AddToQueueBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;

        int added = 0;
        int skipped = 0;

        foreach (var row in selectedRows)
        {
            try
            {
                var item = _manager.CreateHttp(row.Url, _referrer, row.Cookies, _userAgent, row.FileName);
                if (!string.IsNullOrEmpty(row.CookieHost)) item.CookieHost = row.CookieHost;
                bool hasExplicitExt = !string.IsNullOrEmpty(Path.GetExtension(row.FileName));
                if (hasExplicitExt) item.NameLocked = true;
                if (DescriptionBox.IsChecked == true) item.Description = row.LinkText;
                if (HideDuplicatesBox.IsChecked == true && _manager.FindDuplicate(item.Url) != null) { skipped++; continue; }
                if (RenameBox.IsChecked == true && !string.IsNullOrWhiteSpace(RenamePatternBox.Text))
                {
                    item.FileName = FormatBatchRename(RenamePatternBox.Text, row.FileName, added + 1);
                    item.NameLocked = true;
                }

                if (CustomFolderRadio.IsChecked == true && !string.IsNullOrWhiteSpace(customFolder)) item.SaveDir = customFolder;
                else if (OneCategoryRadio.IsChecked == true && CategoryCombo.SelectedItem is CategoryDef category)
                {
                    item.Category = category.Key;
                    item.SaveDir = _manager.Settings.FolderFor(category.Key);
                }

                _manager.Add(item, startNow: false, pick.Value.Queue);
                added++;
            }
            catch (Exception ex)
            {
                Log.Write($"batch add failed for {row.Url}: {ex.Message}");
            }
        }

        if (added > 0 && (startQueue || pick.Value.Start))
        {
            _manager.StartQueue(pick.Value.Queue);
        }

        App.Instance?.Notify(added > 0 ? Loc.T("win.batch.done_title") : Loc.T("win.batch.none_added_title"),
            added > 0
                ? Loc.T("win.batch.added_msg", added) + (skipped > 0 ? Loc.T("win.batch.skipped_suffix", skipped) : "")
                : Loc.T("win.batch.all_duplicates_msg", skipped));

        if (added > 0 || skipped > 0)
        {
            Close();
        }
        else
        {
            DownloadSelectedBtn.IsEnabled = true;
            AddToQueueBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

// ---------- Converters ----------

public sealed class CategoryGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is string cat ? Categories.Get(cat).Glyph : Categories.Get("general").Glyph;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class CategoryNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var cat = value as string ?? "general";
        return cat switch
        {
            "video" => Loc.T("win.batch.cat.video"),
            "music" => Loc.T("win.batch.cat.music"),
            "compressed" => Loc.T("win.batch.cat.compressed"),
            "documents" => Loc.T("win.batch.cat.documents"),
            "programs" => Loc.T("win.batch.cat.programs"),
            "general" => Loc.T("win.batch.cat.general"),
            _ => Categories.Get(cat).Name
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}
