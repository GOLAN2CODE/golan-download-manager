using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class OptionsWindow
{
    StackPanel RemotePage()
    {
        var p = Page(Loc.T("win.options.remote.title"), Loc.T("win.options.remote.note"));
        var enableCheck = Check(Loc.T("remote.enable"), () => _working.RemoteEnabled, v => _working.RemoteEnabled = v);
        var bindBox = Text(_working.RemoteBindAddress, v => _working.RemoteBindAddress = v, true);
        var portBox = Number(_working.RemotePort, v => _working.RemotePort = v, 1024, 65535);
        var openRemoteBtn = new Button
        {
            Content = Loc.T("win.options.remote.open_window"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        openRemoteBtn.Click += (_, _) =>
        {
            new RemoteWindow(_manager) { Owner = this }.ShowDialog();
            _working.RemoteEnabled = _manager.Settings.RemoteEnabled;
            _working.RemoteBindAddress = _manager.Settings.RemoteBindAddress;
            _working.RemotePort = _manager.Settings.RemotePort;
            _working.RemoteDevices = _manager.Settings.RemoteDevices
                .Select(d => new PairedDevice { Id = d.Id, Name = d.Name, TokenHash = d.TokenHash, CreatedAt = d.CreatedAt, LastSeenAt = d.LastSeenAt })
                .ToList();
            enableCheck.IsChecked = _working.RemoteEnabled;
            bindBox.Text = _working.RemoteBindAddress;
            portBox.Text = _working.RemotePort.ToString();
        };
        p.Children.Add(Card(
            enableCheck,
            Label(Loc.T("remote.interface")),
            bindBox,
            Label(Loc.T("remote.port")),
            portBox,
            openRemoteBtn));
        return p;
    }

    StackPanel UpdatesPage()
    {
        var p = Page(Loc.T($"win.options.updates.title"), Loc.T($"win.options.updates.note"));
        string lastCheckStr = _working.LastUpdateCheck is { } dt
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : Loc.T($"win.options.updates.never");
        var lastCheckLabel = new TextBlock
        {
            Text = Loc.T($"win.options.updates.last_check", lastCheckStr),
            Foreground = Brush("Muted"),
            Margin = new Thickness(0, 6, 0, 8)
        };
        var checkNowBtn = new Button
        {
            Content = Loc.T($"main.menu.checkUpdates"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        };
        checkNowBtn.Click += async (_, _) =>
        {
            checkNowBtn.IsEnabled = false;
            var prevFeed = _manager.Settings.UpdateFeedUrl;
            var prevAuto = _manager.Settings.CheckForUpdates;
            try
            {
                _manager.Settings.UpdateFeedUrl = _working.UpdateFeedUrl.Trim();
                _manager.Settings.CheckForUpdates = _working.CheckForUpdates;
                if (App.Instance != null)
                    await App.Instance.CheckForUpdatesAsync(interactive: true, owner: this);
                _working.LastUpdateCheck = _manager.Settings.LastUpdateCheck;
                string updatedStr = _working.LastUpdateCheck is { } updatedDt
                    ? updatedDt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : Loc.T("win.options.updates.never");
                lastCheckLabel.Text = Loc.T("win.options.updates.last_check", updatedStr);
            }
            finally
            {
                // Saved while the check was running: the saved values stand. Putting the old ones back would undo
                // the save in memory, and the next save anywhere would write them to disk.
                if (DialogResult != true)
                {
                    _manager.Settings.UpdateFeedUrl = prevFeed;
                    _manager.Settings.CheckForUpdates = prevAuto;
                }
                checkNowBtn.IsEnabled = true;
            }
        };
        var installBtn = new Button
        {
            Content = Loc.T($"main.menu.installGdm"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            IsEnabled = !AppPaths.IsPortable
        };
        installBtn.Click += (_, _) => App.Instance?.RunUserInstall(this);

        p.Children.Add(Card(
            Check(Loc.T("win.options.updates.auto"), () => _working.CheckForUpdates, v => _working.CheckForUpdates = v),
            Label(Loc.T($"win.options.updates.feed_url")),
            Text(_working.UpdateFeedUrl, v => _working.UpdateFeedUrl = v, true),
            lastCheckLabel,
            checkNowBtn,
            installBtn));
        return p;
    }
}
