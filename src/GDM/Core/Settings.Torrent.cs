namespace Golan.Gdm.Core;

public sealed partial class AppSettings
{
    /// <summary>Incoming TCP/UDP listen port for BitTorrent peers (`0` = random `49152–65535`).</summary>
    public int TorrentListenPort { get; set; } = 0;

    /// <summary>Enables the mainline BitTorrent DHT network for trackerless peer discovery.</summary>
    public bool TorrentDhtEnabled { get; set; } = true;

    /// <summary>Enables Peer Exchange (PEX) between connected peers.</summary>
    public bool TorrentPexEnabled { get; set; } = true;

    /// <summary>Enables Local Peer Discovery (LSD) on the local network.</summary>
    public bool TorrentLsdEnabled { get; set; } = true;

    /// <summary>Prefer encrypted BitTorrent peer connections.</summary>
    public bool TorrentEncryptionPreferred { get; set; } = true;

    /// <summary>Enable UPnP / NAT-PMP automatic port forwarding on the router (off by default).</summary>
    public bool TorrentPortForwarding { get; set; } = false;

    /// <summary>Maximum total peer connections across active torrents.</summary>
    public int TorrentMaxConnections { get; set; } = 200;

    /// <summary>Target share ratio (`Uploaded / Downloaded`) at which seeding stops (`0` = don't seed).</summary>
    public double StopSeedingAtRatio { get; set; } = 1.0;

    /// <summary>Maximum seeding duration in minutes after download completion (`0` = don't seed).</summary>
    public int StopSeedingAfterMinutes { get; set; } = 60;

    /// <summary>Timeout in minutes when resolving `magnet:` metadata before failing.</summary>
    public int MagnetMetadataTimeoutMinutes { get; set; } = 5;

    /// <summary>Register Golan as the current user's handler for `magnet:` links.</summary>
    public bool RegisterMagnetProtocol { get; set; } = false;
}
