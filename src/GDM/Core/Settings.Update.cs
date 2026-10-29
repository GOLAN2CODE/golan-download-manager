namespace Golan.Gdm.Core;

public sealed partial class AppSettings
{
    /// <summary>Auto-update JSON feed URL (HTTPS only; empty = disabled).</summary>
    public string UpdateFeedUrl { get; set; } = "";

    /// <summary>Timestamp of the last update check (at most once every 24h).</summary>
    public DateTime? LastUpdateCheck { get; set; }
}
