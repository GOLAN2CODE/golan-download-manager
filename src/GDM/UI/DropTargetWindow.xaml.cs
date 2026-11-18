using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;

namespace Golan.Gdm.UI;

public partial class DropTargetWindow : Window
{
    public DropTargetWindow()
    {
        InitializeComponent();
        var s = App.Instance.Settings;
        var pos = ClampToVirtualScreen(
            s.DropTargetLeft >= 0 ? s.DropTargetLeft : SystemParameters.WorkArea.Right - Width - 20,
            s.DropTargetTop >= 0 ? s.DropTargetTop : SystemParameters.WorkArea.Bottom - Height - 20,
            Width, Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Left = pos.Left;
        Top = pos.Top;
        var menu = new ContextMenu();
        var hide = new MenuItem { Header = Loc.T("win.droptarget.hide") };
        hide.Click += (_, _) => App.Instance.ToggleDropTarget(false);
        var open = new MenuItem { Header = Loc.T("win.droptarget.open") };
        open.Click += (_, _) => App.Instance.ShowMain();
        menu.Items.Add(hide);
        menu.Items.Add(open);
        ContextMenu = menu;
    }

    public static (double Left, double Top) ClampToVirtualScreen(
        double left, double top, double width, double height,
        double vLeft, double vTop, double vWidth, double vHeight)
    {
        if (vWidth <= width || vHeight <= height) return (left, top);
        double clampedLeft = Math.Clamp(left, vLeft, vLeft + vWidth - width);
        double clampedTop = Math.Clamp(top, vTop, vTop + vHeight - height);
        return (clampedLeft, clampedTop);
    }

    void MoveStart(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            App.Instance.ShowMain();
            e.Handled = true;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            var clamped = ClampToVirtualScreen(
                Left, Top, Width, Height,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Left = clamped.Left;
            Top = clamped.Top;
            App.Instance.Settings.DropTargetLeft = Left;
            App.Instance.Settings.DropTargetTop = Top;
            App.Instance.Settings.Save();
        }
    }

    void OnDragOver(object s, DragEventArgs e)
    {
        var hasTorrents = MainWindow.DroppedTorrentFiles(e.Data).Count > 0;
        var url = MainWindow.DroppedUrl(e.Data);
        var hasUrl = FileNames.IsDownloadableUrl(url) || MagnetValidator.IsMagnetUri(url) ||
                     e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent("UniformResourceLocatorW");
        bool valid = hasTorrents || hasUrl;
        e.Effects = valid ? DragDropEffects.Link : DragDropEffects.None;
        Tile.Background = (Brush)FindResource(valid ? "AccentSoft" : "Surface");
        e.Handled = true;
    }

    void OnDragLeave(object s, DragEventArgs e)
    {
        Tile.Background = (Brush)FindResource("Surface");
    }

    void OnDrop(object s, DragEventArgs e)
    {
        Tile.Background = (Brush)FindResource("Surface");
        var torrentFiles = MainWindow.DroppedTorrentFiles(e.Data);
        if (torrentFiles.Count > 0)
        {
            foreach (var path in torrentFiles)
            {
                _ = App.Instance.OpenTorrentFileAsync(path);
            }
            e.Handled = true;
            return;
        }
        var url = MainWindow.DroppedUrl(e.Data);
        if (FileNames.IsDownloadableUrl(url) || MagnetValidator.IsMagnetUri(url))
        {
            App.Instance.ShowAddUrl(url);
        }
        e.Handled = true;
    }
}
