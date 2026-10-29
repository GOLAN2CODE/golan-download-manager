using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;

namespace Golan.Gdm.UI;

public partial class DropTargetWindow : Window
{
    Point _start;
    bool _moving;

    public DropTargetWindow()
    {
        InitializeComponent();
        var s = App.Instance.Settings;
        Left = s.DropTargetLeft >= 0 ? s.DropTargetLeft : SystemParameters.WorkArea.Right - Width - 20;
        Top = s.DropTargetTop >= 0 ? s.DropTargetTop : SystemParameters.WorkArea.Bottom - Height - 20;
        var menu = new ContextMenu();
        var hide = new MenuItem { Header = Loc.T("win.droptarget.hide") };
        hide.Click += (_, _) => App.Instance.ToggleDropTarget(false);
        var open = new MenuItem { Header = Loc.T("win.droptarget.open") };
        open.Click += (_, _) => App.Instance.ShowMain();
        menu.Items.Add(hide);
        menu.Items.Add(open);
        ContextMenu = menu;
    }

    void MoveStart(object s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _moving = true;
        CaptureMouse();
    }

    void MoveWindow(object s, MouseEventArgs e)
    {
        if (_moving && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(null);
            Left = p.X - _start.X;
            Top = p.Y - _start.Y;
        }
    }

    void MoveEnd(object s, MouseButtonEventArgs e)
    {
        if (!_moving)
        {
            return;
        }
        _moving = false;
        ReleaseMouseCapture();
        App.Instance.Settings.DropTargetLeft = Left;
        App.Instance.Settings.DropTargetTop = Top;
        App.Instance.Settings.Save();
    }

    void OnDragOver(object s, DragEventArgs e)
    {
        var hasTorrents = MainWindow.DroppedTorrentFiles(e.Data).Count > 0;
        var url = MainWindow.DroppedUrl(e.Data);
        var hasUrl = FileNames.IsHttpUrl(url) || MagnetValidator.IsMagnetUri(url) ||
                     e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent("UniformResourceLocatorW");
        e.Effects = (hasTorrents || hasUrl) ? DragDropEffects.Link : DragDropEffects.None;
        Tile.Background = (Brush)FindResource("AccentSoft");
        e.Handled = true;
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
        if (FileNames.IsHttpUrl(url) || MagnetValidator.IsMagnetUri(url))
        {
            App.Instance.ShowAddUrl(url);
        }
        e.Handled = true;
    }
}
