using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class MainWindow
{
    /// <summary>Column keys in the order they are declared in MainWindow.xaml.</summary>
    static readonly string[] ColumnKeys = ["name", "size", "status", "speed", "eta", "lastTry", "description", "saveTo", "added", "referrer", "parentPage"];

    static (string Key, string Title)[] SortChoices =>
    [
        ("AddedAt", Loc.T("main.sort.addedAt")),
        ("FileName", Loc.T("main.sort.fileName")),
        ("TotalSize", Loc.T("main.sort.totalSize")),
        ("Status", Loc.T("main.sort.status")),
        ("Eta", Loc.T("main.sort.eta")),
        ("Speed", Loc.T("main.sort.speed")),
        ("LastTry", Loc.T("main.sort.lastTry")),
        ("Description", Loc.T("main.sort.description")),
        ("SaveDir", Loc.T("main.sort.saveDir")),
        ("Referrer", Loc.T("main.sort.referrer")),
        ("ParentPage", Loc.T("main.sort.parentPage")),
    ];

    void ApplySort()
    {
        _view.SortDescriptions.Clear();
        // Inside a queue the order is the queue order (move up / down).
        if (_current.QueueId != null)
        {
            _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.QueuePosition), ListSortDirection.Ascending));
            return;
        }
        _view.SortDescriptions.Add(new SortDescription(_settings.SortBy, _settings.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    void BuildArrangeMenu()
    {
        ArrangeMenu.Items.Clear();
        foreach (var (key, title) in SortChoices)
        {
            var mi = new MenuItem { Header = title, IsCheckable = true, IsChecked = _settings.SortBy == key };
            mi.Click += (_, _) =>
            {
                if (_settings.SortBy == key) _settings.SortDescending = !_settings.SortDescending;
                else { _settings.SortBy = key; _settings.SortDescending = key is "AddedAt" or "LastTry" or "TotalSize"; }
                _settings.Save();
                ApplySort();
                BuildArrangeMenu();
            };
            ArrangeMenu.Items.Add(mi);
        }
    }

    Dictionary<string, DataGridColumn> ColumnMap()
        => ColumnKeys.Zip(Grid.Columns).ToDictionary(p => p.First, p => p.Second);

    static readonly Dictionary<string, bool> DefaultVisible = new()
    {
        ["name"] = true, ["size"] = true, ["status"] = true, ["speed"] = true, ["eta"] = true,
        ["lastTry"] = false, ["description"] = false, ["saveTo"] = false, ["added"] = true,
        ["referrer"] = false, ["parentPage"] = false,
    };

    static readonly Dictionary<string, DataGridLength> DefaultWidths = new()
    {
        ["name"] = new DataGridLength(3, DataGridLengthUnitType.Star),
        ["size"] = new DataGridLength(95),
        ["status"] = new DataGridLength(2, DataGridLengthUnitType.Star),
        ["speed"] = new DataGridLength(100),
        ["eta"] = new DataGridLength(95),
        ["lastTry"] = new DataGridLength(135),
        ["description"] = new DataGridLength(160),
        ["saveTo"] = new DataGridLength(180),
        ["added"] = new DataGridLength(135),
        ["referrer"] = new DataGridLength(160),
        ["parentPage"] = new DataGridLength(160),
    };

    void ApplyColumns()
    {
        var map = ColumnMap();
        bool wasResetToDefault = _settings.Columns.Count > 0 && _settings.Columns.All(c => c.Width == 0);
        if (wasResetToDefault || _settings.Columns.Count == 0 || _settings.Columns.Any(c => !map.ContainsKey(c.Key)) || map.Keys.Any(k => _settings.Columns.All(c => c.Key != k)))
            _settings.Columns = ColumnKeys.Select(k => new ColumnPref { Key = k, Visible = DefaultVisible[k] }).ToList();
        int index = 0;
        foreach (var pref in _settings.Columns)
        {
            var col = map[pref.Key];
            col.Visibility = pref.Visible || pref.Key == "name" ? Visibility.Visible : Visibility.Collapsed;
            col.DisplayIndex = index++;
            if (pref.Key is "added" or "lastTry" && pref.Width > 20 && pref.Width < 135) pref.Width = 135;
            if (pref.Width > 20) col.Width = new DataGridLength(pref.Width);
            else if (DefaultWidths.TryGetValue(pref.Key, out var defWidth)) col.Width = defWidth;
        }
    }

    void Columns_Click(object sender, RoutedEventArgs e)
    {
        var map = ColumnMap();
        foreach (var pref in _settings.Columns) pref.Width = map[pref.Key].ActualWidth;
        var titles = map.ToDictionary(p => p.Key, p => p.Value.Header?.ToString() ?? p.Key);
        if (new ColumnsWindow(_settings.Columns, titles) { Owner = this }.ShowDialog() == true)
        {
            ApplyColumns();
            _settings.Save();
        }
    }
}
