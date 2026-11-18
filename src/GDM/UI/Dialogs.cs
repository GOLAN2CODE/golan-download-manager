using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

/// <summary>Small styled dialogs built in code: confirmation and single text input.</summary>
public static class Confirm
{
    public static bool Ask(Window? owner, string title, string text, string yes, bool danger = false)
    {
        var w = Shell(owner, title);
        bool result = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], MaxWidth = 420 });
        var buttons = Buttons(out var ok, out var cancel, yes, Loc.T("main.dialog.cancel"));
        if (danger) ok.Style = (Style)Application.Current.Resources["DangerButton"];
        ok.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(buttons);
        w.Content = panel;
        w.ShowDialog();
        return result;
    }

    public static void Info(Window? owner, string title, string text, string? okText = null)
    {
        var w = Shell(owner, title);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], MaxWidth = 420 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Name = "OkBtn", Content = okText ?? Loc.T("main.dialog.ok"), Style = (Style)Application.Current.Resources["Primary"], IsDefault = true, IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => w.Close();
        row.Children.Add(ok);
        panel.Children.Add(row);
        w.Content = panel;
        w.ShowDialog();
    }

    internal static Window Shell(Window? owner, string title)
    {
        var visibleOwner = owner is { IsVisible: true } ? owner : null;
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = visibleOwner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = visibleOwner == null,
            Style = (Style)Application.Current.Resources["GdmWindow"],
        };
        w.SetResourceReference(Control.BackgroundProperty, "Surface");
        if (visibleOwner != null) w.Owner = visibleOwner;
        return w;
    }

    internal static StackPanel Buttons(out Button ok, out Button cancel, string okText, string cancelText)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) };
        ok = new Button { Name = "OkBtn", Content = okText, Style = (Style)Application.Current.Resources["Primary"], IsDefault = true, MinWidth = 90 };
        cancel = new Button { Name = "CancelBtn", Content = cancelText, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        return row;
    }
}

public static class InputDialog
{
    public static FlowDirection GetInputFlowDirection(string? text, string? initial)
    {
        if (!string.IsNullOrEmpty(text))
            return TextDir.IsRtl(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (!string.IsNullOrEmpty(initial))
            return TextDir.IsRtl(initial) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return Loc.FlowDirection;
    }

    public static string? Ask(Window? owner, string title, string text, string initial)
    {
        var w = Confirm.Shell(owner, title);
        string? result = null;
        var panel = new StackPanel { Margin = new Thickness(24), Width = 460 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 0, 0, 12) });
        var box = new TextBox { Name = "InputBox", Text = initial, FlowDirection = GetInputFlowDirection(initial, initial) };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        box.TextChanged += (_, _) => box.FlowDirection = GetInputFlowDirection(box.Text, initial);
        panel.Children.Add(box);
        var buttons = Confirm.Buttons(out var ok, out var cancel, Loc.T("main.dialog.ok"), Loc.T("main.dialog.cancel"));
        ok.Click += (_, _) => { result = box.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }
}

/// <summary>IDM "Confirm deletion of downloads": option to delete completed files too, and a separate warning for unfinished ones.</summary>
public static class DeleteConfirm
{
    public static (bool DeleteFiles, bool DontAskAgain)? Ask(Window owner, IReadOnlyList<Golan.Gdm.Core.DownloadItem> items, bool deleteFiles)
    {
        var w = Confirm.Shell(owner, Loc.T("main.delete.title"));
        (bool, bool)? result = null;
        var panel = new StackPanel { Margin = new Thickness(24), Width = 440 };
        bool unfinished = items.Any(i => i.Status != Golan.Gdm.Core.DownloadStatus.Completed);
        var what = items.Count == 1 ? (Loc.IsRtl ? $"«{items[0].FileName}»" : $"\"{items[0].FileName}\"") : Loc.T("main.delete.itemsCount", items.Count);
        panel.Children.Add(new TextBlock { Text = Loc.T("main.delete.heading"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock
        {
            Text = unfinished ? Loc.T("main.delete.unfinishedBody", what) : Loc.T("main.delete.body", what),
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"],
        });
        var files = new CheckBox { Content = Loc.T("main.delete.alsoDisk"), IsChecked = deleteFiles, Margin = new Thickness(0, 12, 0, 0) };
        var dont = new CheckBox { Content = Loc.T("main.delete.dontAsk"), Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(files);
        panel.Children.Add(dont);
        var buttons = Confirm.Buttons(out var ok, out var cancel, Loc.T("main.delete.confirmBtn"), Loc.T("main.delete.noBtn"));
        ok.Style = (Style)Application.Current.Resources["DangerButton"];
        ok.Click += (_, _) => { result = (files.IsChecked == true, dont.IsChecked == true); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Loaded += (_, _) => { if (deleteFiles) cancel.Focus(); };
        w.ShowDialog();
        return result;
    }
}

/// <summary>Light / dark palette: recolours the shared brushes in place so every open window follows.</summary>
public static class Theme
{
    static ResourceDictionary? _brandDict;
    static ResourceDictionary? _lightDict;
    static ResourceDictionary? _darkDict;

    public static void Apply(bool dark)
    {
        if (Application.Current == null) return;
        _brandDict ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Theme/Brand.xaml", UriKind.Absolute) };
        _lightDict ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Theme/Light.xaml", UriKind.Absolute) };
        _darkDict ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Theme/Dark.xaml", UriKind.Absolute) };

        ApplyDictionary(_brandDict);
        ApplyDictionary(dark ? _darkDict : _lightDict);

        SetSystemBrush(SystemColors.WindowBrushKey, "Surface");
        SetSystemBrush(SystemColors.WindowTextBrushKey, "Ink");
        SetSystemBrush(SystemColors.ControlBrushKey, "SurfaceAlt");
        SetSystemBrush(SystemColors.ControlTextBrushKey, "Ink");
        SetSystemBrush(SystemColors.HighlightBrushKey, "AccentSoft");
        SetSystemBrush(SystemColors.HighlightTextBrushKey, "Ink");
        SetSystemBrush(SystemColors.InactiveSelectionHighlightBrushKey, "SurfaceAlt");
        SetSystemBrush(SystemColors.InactiveSelectionHighlightTextBrushKey, "Ink");
        SetSystemBrush(SystemColors.MenuBrushKey, "Surface");
        SetSystemBrush(SystemColors.MenuTextBrushKey, "Ink");
        SetSystemBrush(SystemColors.InfoBrushKey, "Surface");
        SetSystemBrush(SystemColors.InfoTextBrushKey, "Ink");
        SetSystemBrush(SystemColors.GrayTextBrushKey, "Muted");
    }

    static void ApplyDictionary(ResourceDictionary dict)
    {
        foreach (var key in dict.Keys)
        {
            var val = dict[key];
            if (val is SolidColorBrush srcBrush)
            {
                if (Application.Current.Resources[key] is SolidColorBrush existing && !existing.IsFrozen)
                    existing.Color = srcBrush.Color;
                else
                    Application.Current.Resources[key] = new SolidColorBrush(srcBrush.Color);
            }
            else
            {
                Application.Current.Resources[key] = val;
            }
        }
    }

    static void SetSystemBrush(ResourceKey key, string tokenKey)
    {
        if (Application.Current.Resources[tokenKey] is not SolidColorBrush tokenBrush)
            return;
        var color = tokenBrush.Color;
        if (Application.Current.Resources[key] is SolidColorBrush b && !b.IsFrozen)
            b.Color = color;
        else
            Application.Current.Resources[key] = new SolidColorBrush(color);
    }
}
