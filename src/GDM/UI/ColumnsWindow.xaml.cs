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

    void Refresh(int preserveIndex = 0)
    {
        _updating = true;
        ColumnsList.Items.Clear();
        foreach (var c in _working)
        {
            var label = _titles.TryGetValue(c.Key, out var t) ? t : c.Key;
            var box = new CheckBox
            {
                Content = label,
                IsChecked = c.Visible,
                Tag = c,
                IsEnabled = c.Key != "name"
            };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            box.PreviewMouseLeftButtonDown += (_, _) => ColumnsList.SelectedItem = box;
            box.GotFocus += (_, _) => ColumnsList.SelectedItem = box;
            box.Checked += CheckChanged;
            box.Unchecked += CheckChanged;
            ColumnsList.Items.Add(box);
        }
        _updating = false;
        if (ColumnsList.Items.Count > 0)
        {
            ColumnsList.SelectedIndex = System.Math.Clamp(preserveIndex, 0, ColumnsList.Items.Count - 1);
        }
        SyncButtons();
    }

    void SyncButtons()
    {
        int idx = ColumnsList.SelectedIndex;
        var sel = Selected;
        if (UpBtn != null) UpBtn.IsEnabled = idx > 0;
        if (DownBtn != null) DownBtn.IsEnabled = idx >= 0 && idx < _working.Count - 1;
        if (ShowBtn != null) ShowBtn.IsEnabled = sel is { Visible: false };
        if (HideBtn != null) HideBtn.IsEnabled = sel is { Visible: true, Key: not "name" };
    }

    void CheckChanged(object s, RoutedEventArgs e)
    {
        if (!_updating && s is CheckBox b && b.Tag is ColumnPref c)
        {
            c.Visible = b.IsChecked == true;
            SyncButtons();
        }
    }

    void Selection_Changed(object s, SelectionChangedEventArgs e)
    {
        if (Selected is { } c)
        {
            _updating = true;
            WidthBox.ClearValue(Control.BorderBrushProperty);
            WidthBox.Text = c.Width > 0 ? c.Width.ToString("0") : "";
            _updating = false;
        }
        SyncButtons();
    }

    public static double? ParseColumnWidth(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        if (double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
            && !double.IsNaN(w) && !double.IsInfinity(w))
        {
            if (w < 0) return null;
            return w == 0 ? 0 : System.Math.Clamp(w, 30, 2000);
        }
        return null;
    }

    void Width_Changed(object s, TextChangedEventArgs e)
    {
        if (_updating || Selected is not { } c) return;
        if (ParseColumnWidth(WidthBox.Text) is { } w)
        {
            WidthBox.ClearValue(Control.BorderBrushProperty);
            c.Width = w;
        }
        else if (TryFindResource("Danger") is System.Windows.Media.Brush danger)
        {
            WidthBox.BorderBrush = danger;
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
        Refresh(j);
    }

    void Up_Click(object s, RoutedEventArgs e) => Move(-1);

    void Down_Click(object s, RoutedEventArgs e) => Move(1);

    void Show_Click(object s, RoutedEventArgs e)
    {
        var idx = ColumnsList.SelectedIndex;
        if (Selected is { } c)
        {
            c.Visible = true;
        }
        Refresh(idx);
    }

    void Hide_Click(object s, RoutedEventArgs e)
    {
        var idx = ColumnsList.SelectedIndex;
        if (Selected is { Key: not "name" } c)
        {
            c.Visible = false;
        }
        Refresh(idx);
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
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = true;
        else
            Close();
    }

    void Cancel_Click(object s, RoutedEventArgs e) => Close();
}
