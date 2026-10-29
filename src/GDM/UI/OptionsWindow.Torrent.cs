using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class OptionsWindow
{
    StackPanel TorrentPage()
    {
        var p = Page(Loc.T($"win.options.torrent.title"), Loc.T($"win.options.torrent.note"));
        p.Children.Add(Card(
            Label(Loc.T($"win.options.torrent.network_card")),
            Label(Loc.T($"win.options.torrent.listen_port")),
            Number(_working.TorrentListenPort, v => _working.TorrentListenPort = v, 0, 65535),
            Label(Loc.T($"win.options.torrent.max_connections")),
            Number(_working.TorrentMaxConnections, v => _working.TorrentMaxConnections = v, 10, 2000),
            Check(Loc.T($"win.options.torrent.dht"), () => _working.TorrentDhtEnabled, v => _working.TorrentDhtEnabled = v),
            Check(Loc.T($"win.options.torrent.pex"), () => _working.TorrentPexEnabled, v => _working.TorrentPexEnabled = v),
            Check(Loc.T($"win.options.torrent.lsd"), () => _working.TorrentLsdEnabled, v => _working.TorrentLsdEnabled = v),
            Check(Loc.T($"win.options.torrent.encryption"), () => _working.TorrentEncryptionPreferred, v => _working.TorrentEncryptionPreferred = v),
            Check(Loc.T($"win.options.torrent.upnp"), () => _working.TorrentPortForwarding, v => _working.TorrentPortForwarding = v)));

        var ratioBox = Text(_working.StopSeedingAtRatio.ToString(System.Globalization.CultureInfo.InvariantCulture), s =>
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                && !double.IsNaN(d) && !double.IsInfinity(d) && d is >= 0 and <= 1000)
            {
                _working.StopSeedingAtRatio = d;
            }
        }, true, 110);

        p.Children.Add(Card(
            Label(Loc.T($"win.options.torrent.seeding_card")),
            Label(Loc.T($"win.options.torrent.stop_ratio")),
            ratioBox,
            Label(Loc.T($"win.options.torrent.stop_minutes")),
            Number(_working.StopSeedingAfterMinutes, v => _working.StopSeedingAfterMinutes = v, 0, 525600),
            Label(Loc.T($"win.options.torrent.magnet_timeout")),
            Number(_working.MagnetMetadataTimeoutMinutes, v => _working.MagnetMetadataTimeoutMinutes = v, 1, 60),
            Check(Loc.T($"win.options.torrent.register_magnet"), () => _working.RegisterMagnetProtocol, v =>
            {
                if (v && !_working.RegisterMagnetProtocol && !AppPaths.IsIsolated)
                {
                    if (!Confirm.Ask(this, Loc.T($"vb.magnet.consent.title"), Loc.T($"vb.magnet.consent.body"), Loc.T($"vb.magnet.consent.yes")))
                        return;
                }
                _working.RegisterMagnetProtocol = v;
            })));
        return p;
    }
}
