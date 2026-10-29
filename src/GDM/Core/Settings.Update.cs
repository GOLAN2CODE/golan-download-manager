namespace Golan.Gdm.Core;

public sealed partial class AppSettings
{
    /// <summary>Check for new versions once a day (on by default) from the official releases page.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Optional custom update feed (HTTPS JSON); empty = the official feed.</summary>
    public string UpdateFeedUrl { get; set; } = "";

    /// <summary>Timestamp of the last update check (at most once every 24h).</summary>
    public DateTime? LastUpdateCheck { get; set; }
}
