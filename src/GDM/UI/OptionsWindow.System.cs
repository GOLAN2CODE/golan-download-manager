using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class OptionsWindow
{
    StackPanel RemotePage()
    {
        var p = Page(Loc.T($"win.options.remote.title"), Loc.T($"win.options.remote.note"));
        var openRemoteBtn = new Button
        {
            Content = Loc.T($"win.options.remote.open_window"),
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
        };
        p.Children.Add(Card(
            Check(Loc.T($"remote.enable"), () => _working.RemoteEnabled, v => _working.RemoteEnabled = v),
            Label(Loc.T($"remote.interface")),
            Text(_working.RemoteBindAddress, v => _working.RemoteBindAddress = v, true),
            Label(Loc.T($"remote.port")),
            Number(_working.RemotePort, v => _working.RemotePort = v, 1024, 65535),
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
            _manager.Settings.UpdateFeedUrl = _working.UpdateFeedUrl.Trim();
            if (App.Instance != null)
                await App.Instance.CheckForUpdatesAsync(interactive: true, owner: this);
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
            Label(Loc.T($"win.options.updates.feed_url")),
            Text(_working.UpdateFeedUrl, v => _working.UpdateFeedUrl = v, true),
            lastCheckLabel,
            checkNowBtn,
            installBtn));
        return p;
    }
}
