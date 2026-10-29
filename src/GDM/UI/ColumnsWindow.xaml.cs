using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class ColumnsWindow : Window
{
    readonly List<ColumnPref> _target;
    readonly List<ColumnPref> _working;
    readonly IReadOnlyDictionary<string, string> _titles;
    bool _updating;

    public ColumnsWindow(List<ColumnPref> columns, IReadOnlyDictionary<string, string> titles)
    {
        InitializeComponent();
        _target = columns;
        _titles = titles;
        _working = columns.Select(Clone).ToList();
        Refresh();
    }

    static ColumnPref Clone(ColumnPref p) => new() { Key = p.Key, Visible = p.Visible, Width = p.Width };

    ColumnPref? Selected => (ColumnsList.SelectedItem as CheckBox)?.Tag as ColumnPref;

    void Refresh()
    {
        _updating = true;
        ColumnsList.Items.Clear();
        foreach (var c in _working)
        {
            var box = new CheckBox
            {
                Content = _titles.TryGetValue(c.Key, out var t) ? t : c.Key,
                IsChecked = c.Visible,
                Tag = c,
                IsEnabled = c.Key != "name"
            };
            box.Checked += CheckChanged;
            box.Unchecked += CheckChanged;
            ColumnsList.Items.Add(box);
        }
        _updating = false;
        if (ColumnsList.Items.Count > 0)
        {
            ColumnsList.SelectedIndex = 0;
        }
    }

    void CheckChanged(object s, RoutedEventArgs e)
    {
        if (!_updating && s is CheckBox b && b.Tag is ColumnPref c)
        {
            c.Visible = b.IsChecked == true;
        }
    }

    void Selection_Changed(object s, SelectionChangedEventArgs e)
    {
        if (Selected is { } c)
        {
            _updating = true;
            WidthBox.Text = c.Width > 0 ? c.Width.ToString("0") : "";
            _updating = false;
        }
    }

    void Width_Changed(object s, TextChangedEventArgs e)
    {
        if (!_updating && Selected is { } c && double.TryParse(WidthBox.Text, out var w))
        {
            c.Width = w;
        }
    }

    void Move(int d)
    {
        var i = ColumnsList.SelectedIndex;
        var j = i + d;
        if (i < 0 || j < 0 || j >= _working.Count)
        {
            return;
        }
        (_working[i], _working[j]) = (_working[j], _working[i]);
        Refresh();
        ColumnsList.SelectedIndex = j;
    }

    void Up_Click(object s, RoutedEventArgs e) => Move(-1);

    void Down_Click(object s, RoutedEventArgs e) => Move(1);

    void Show_Click(object s, RoutedEventArgs e)
    {
        if (Selected is { } c)
        {
            c.Visible = true;
        }
        Refresh();
    }

    void Hide_Click(object s, RoutedEventArgs e)
    {
        if (Selected is { Key: not "name" } c)
        {
            c.Visible = false;
        }
        Refresh();
    }

    static readonly string[] DefaultColumnOrder = ["name", "size", "status", "speed", "eta", "lastTry", "description", "saveTo", "added", "referrer", "parentPage"];

    void Default_Click(object s, RoutedEventArgs e)
    {
        var ordered = _working
            .OrderBy(c =>
            {
                var idx = System.Array.IndexOf(DefaultColumnOrder, c.Key);
                return idx >= 0 ? idx : int.MaxValue;
            })
            .ToList();
        _working.Clear();
        foreach (var c in ordered)
        {
            c.Visible = c.Key is not ("lastTry" or "description" or "saveTo" or "referrer" or "parentPage");
            c.Width = 0;
            _working.Add(c);
        }
        Refresh();
    }

    void Ok_Click(object s, RoutedEventArgs e)
    {
        _target.Clear();
        _target.AddRange(_working.Select(Clone));
        DialogResult = true;
    }
}
