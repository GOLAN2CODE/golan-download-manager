namespace Golan.Gdm.Core;

public sealed partial class AppSettings
{
    /// <summary>Prevent Windows from entering sleep while one or more downloads are actively running.</summary>
    public bool KeepSystemAwake { get; set; } = true;

    /// <summary>Keep the display turned on while downloads are active (only applies when <see cref="KeepSystemAwake"/> is enabled).</summary>
    public bool KeepDisplayOn { get; set; }
}
