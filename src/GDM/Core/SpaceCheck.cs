using System.Runtime.InteropServices;

namespace Golan.Gdm.Core;

public enum SpaceVerdictKind
{
    Ok,
    Warn,
    Insufficient,
}

public readonly record struct SpaceVerdict(SpaceVerdictKind Kind, long NeedBytes, long AvailableBytes = -1)
{
    public static readonly SpaceVerdictKind Ok = SpaceVerdictKind.Ok;
    public static readonly SpaceVerdictKind Warn = SpaceVerdictKind.Warn;
    public static readonly SpaceVerdictKind Insufficient = SpaceVerdictKind.Insufficient;

    public SpaceVerdictKind Status => Kind;
    public bool IsOk => Kind == SpaceVerdictKind.Ok;
    public bool IsWarn => Kind == SpaceVerdictKind.Warn;
    public bool IsInsufficient => Kind == SpaceVerdictKind.Insufficient;

    public static implicit operator SpaceVerdictKind(SpaceVerdict v) => v.Kind;
}

/// <summary>F3: Free disk space check before downloading or pre-allocating files.</summary>
public static class SpaceCheck
{
    public const long SafetyBufferBytes = 64L * 1024 * 1024;
    public const long UnknownSizeWarnThresholdBytes = 512L * 1024 * 1024;
    public const double MediaMultiplier = 2.2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    /// <summary>
    /// Pure space evaluation:
    /// - Unknown total (&lt; 0): Warn when free &gt;= 0 and &lt; 512 MB (or tempFree &gt;= 0 and &lt; 512 MB), else Ok.
    /// - Known total (&gt;= 0): need = max(0, total - downloaded) + 64 MB (media: ceil(2.2 * remaining) + 64 MB).
    /// - Temp on another drive (tempFree &gt;= 0): remaining + safety must fit on temp drive AND final size + safety on target drive.
    /// </summary>
    public static SpaceVerdict Evaluate(long free, long total, long downloaded = 0, bool isMedia = false, long tempFree = -1)
    {
        if (total < 0)
        {
            if (tempFree >= 0 && tempFree < UnknownSizeWarnThresholdBytes)
                return new SpaceVerdict(SpaceVerdictKind.Warn, UnknownSizeWarnThresholdBytes, tempFree);
            if (free >= 0 && free < UnknownSizeWarnThresholdBytes)
                return new SpaceVerdict(SpaceVerdictKind.Warn, UnknownSizeWarnThresholdBytes, free);
            return new SpaceVerdict(SpaceVerdictKind.Ok, SafetyBufferBytes, free);
        }

        long safeDownloaded = Math.Max(0L, downloaded);
        long remaining = Math.Max(0L, total - safeDownloaded);
        long remainingNeed = ComputeNeed(remaining, isMedia);

        if (tempFree >= 0)
        {
            if (tempFree < remainingNeed)
                return new SpaceVerdict(SpaceVerdictKind.Insufficient, remainingNeed, tempFree);

            long finalTargetNeed = AddSaturating(total, SafetyBufferBytes);
            if (free >= 0 && free < finalTargetNeed)
                return new SpaceVerdict(SpaceVerdictKind.Insufficient, finalTargetNeed, free);

            return new SpaceVerdict(SpaceVerdictKind.Ok, Math.Max(remainingNeed, finalTargetNeed), free);
        }

        if (free >= 0 && free < remainingNeed)
            return new SpaceVerdict(SpaceVerdictKind.Insufficient, remainingNeed, free);

        return new SpaceVerdict(SpaceVerdictKind.Ok, remainingNeed, free);
    }

    static long ComputeNeed(long remaining, bool isMedia)
    {
        if (!isMedia)
            return AddSaturating(remaining, SafetyBufferBytes);

        double scaled = Math.Ceiling(remaining * MediaMultiplier);
        if (scaled >= long.MaxValue - SafetyBufferBytes)
            return long.MaxValue;
        return AddSaturating((long)scaled, SafetyBufferBytes);
    }

    static long AddSaturating(long a, long b)
    {
        if (a > long.MaxValue - b) return long.MaxValue;
        return a + b;
    }

    /// <summary>Queries free bytes available to the caller via GetDiskFreeSpaceExW, walking up to the nearest existing parent folder.</summary>
    public static long FreeBytes(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return -1;
        try
        {
            var trimmed = folder.Trim();
            if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || trimmed.Contains('?') || trimmed.Contains('*'))
                return -1;

            var full = Path.GetFullPath(trimmed);
            var current = full;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }

            if (string.IsNullOrEmpty(current) || !Directory.Exists(current))
                return -1;

            if (!GetDiskFreeSpaceExW(current, out ulong freeAvailable, out _, out _))
                return -1;

            return freeAvailable > (ulong)long.MaxValue ? long.MaxValue : (long)freeAvailable;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return -1;
        }
    }

    public static bool AreDifferentDrives(string? targetFolder, string? tempFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder) || string.IsNullOrWhiteSpace(tempFolder))
            return false;
        try
        {
            var rootA = Path.GetPathRoot(Path.GetFullPath(targetFolder.Trim()));
            var rootB = Path.GetPathRoot(Path.GetFullPath(tempFolder.Trim()));
            if (string.IsNullOrEmpty(rootA) || string.IsNullOrEmpty(rootB))
                return false;
            return !string.Equals(
                rootA.TrimEnd('\\', '/'),
                rootB.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static SpaceVerdict CheckItem(DownloadItem item, string? tempDir = null)
    {
        long free = FreeBytes(item.SaveDir);
        string effectiveTemp = !string.IsNullOrWhiteSpace(tempDir) ? tempDir : DownloadItem.TempRoot;
        long tempFree = AreDifferentDrives(item.SaveDir, effectiveTemp) ? FreeBytes(effectiveTemp) : -1;
        return Evaluate(free, item.TotalSize, item.Downloaded, item.Kind == DownloadKind.Media, tempFree);
    }

    public static string FormatInsufficientMessage(SpaceVerdict verdict)
        => Loc.T("space.insufficient", Fmt.Size(verdict.NeedBytes), Fmt.Size(verdict.AvailableBytes));
}
