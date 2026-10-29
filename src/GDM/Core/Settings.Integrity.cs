namespace Golan.Gdm.Core;

public enum ExtractTargetMode
{
    Subfolder,
    SameFolder,
}

public enum ExistingFileRule
{
    Rename,
    Skip,
    Overwrite,
}

public sealed partial class AppSettings
{
    // ---- Integrity check ----

    /// <summary>Automatically look for `.sha256` / `SHA256SUMS` in the same remote folder when <see cref="DownloadItem.ExpectedHash"/> is empty (default off).</summary>
    public bool AutoDiscoverHash { get; set; }

    // ---- Automatic archive extraction ----

    /// <summary>Automatically extract supported archives when download completes.</summary>
    public bool AutoExtractEnabled { get; set; }

    /// <summary>Destination folder mode used when automatically extracting archives.</summary>
    public ExtractTargetMode AutoExtractTargetMode { get; set; } = ExtractTargetMode.Subfolder;

    /// <summary>Conflict resolution rule when an extracted file already exists on disk.</summary>
    public ExistingFileRule AutoExtractExistingRule { get; set; } = ExistingFileRule.Rename;

    /// <summary>Delete the source archive file after extraction succeeds without errors.</summary>
    public bool AutoExtractDeleteArchive { get; set; }

    /// <summary>Custom path to `7z.exe`; empty searches bundled tools, PATH, and standard Program Files locations.</summary>
    public string Custom7ZipPath { get; set; } = "";

    // ---- Time-of-day speed rules ----

    /// <summary>Ordered list of time-of-day speed rules; first matching enabled rule wins.</summary>
    public List<SpeedRule> SpeedRules { get; set; } = [];

    // ---- Speed graph & statistics ----

    /// <summary>Show the live speed graph panel at the bottom of the main window.</summary>
    public bool ShowSpeedGraph { get; set; } = true;
}
