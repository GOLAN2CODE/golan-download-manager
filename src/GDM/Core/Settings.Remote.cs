namespace Golan.Gdm.Core;

/// <summary>Device paired with Golan's LAN remote-control web UI (token kept only as a SHA-256 hash).</summary>
public sealed class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed partial class AppSettings
{
    /// <summary>Enables the LAN remote-control web server (disabled by default).</summary>
    public bool RemoteEnabled { get; set; }

    /// <summary>Local IPv4 bind address (`127.0.0.1` or a private LAN IPv4 address).</summary>
    public string RemoteBindAddress { get; set; } = "127.0.0.1";

    /// <summary>TCP port for the remote-control HTTP listener (default `8765`).</summary>
    public int RemotePort { get; set; } = 8765;

    /// <summary>Paired devices authorized to access the remote-control API.</summary>
    public List<PairedDevice> RemoteDevices { get; set; } = [];
}
