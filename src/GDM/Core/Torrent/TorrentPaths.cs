using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MonoTorrent;

namespace Golan.Gdm.Core.Torrent;

public static class TorrentPaths
{
    public const int MaxFilesPerTorrent = 10_000;
    public const int MaxSegmentLength = 200;

    private static readonly char[] InvalidSegmentChars = ['<', '>', '"', '|', '?', '*', ':', '/', '\\'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
        "CONIN$", "CONOUT$"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".scr",
        ".vbs", ".js", ".hta", ".com", ".pif", ".jar"
    };

    /// <summary>
    /// Validates and canonicalizes a relative path inside a torrent under <paramref name="rootFolder"/>.
    /// Throws <see cref="DownloadException"/> if the path is unsafe or invalid.
    /// </summary>
    public static string Sanitize(string rootFolder, string torrentRelativePath)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", rootFolder ?? ""));

        if (string.IsNullOrWhiteSpace(torrentRelativePath))
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", torrentRelativePath ?? ""));

        foreach (char c in torrentRelativePath)
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                throw new DownloadException(TorrentText.T("torrent.error.unsafePath", torrentRelativePath));
        }

        if (torrentRelativePath[0] is '/' or '\\' ||
            torrentRelativePath.Contains(':') ||
            Path.IsPathRooted(torrentRelativePath))
        {
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", torrentRelativePath));
        }

        string[] rawSegments = torrentRelativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (rawSegments.Length == 0)
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", torrentRelativePath));

        var cleanSegments = new string[rawSegments.Length];
        for (int i = 0; i < rawSegments.Length; i++)
        {
            string seg = rawSegments[i];
            ValidateSegment(seg, torrentRelativePath);
            cleanSegments[i] = seg;
        }

        string fullRoot = Path.GetFullPath(rootFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRootWithSep = fullRoot + Path.DirectorySeparatorChar;

        string combined = fullRoot;
        foreach (string seg in cleanSegments)
        {
            combined = Path.Combine(combined, seg);
        }

        string fullResult = Path.GetFullPath(combined);
        if (!fullResult.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase) ||
            fullResult.Length <= fullRootWithSep.Length)
        {
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", torrentRelativePath));
        }

        EnsureNoReparsePoints(fullRoot, fullResult, torrentRelativePath);
        return fullResult;
    }

    public static bool TrySanitize(string rootFolder, string torrentRelativePath, out string fullPath, out string? error)
    {
        try
        {
            fullPath = Sanitize(rootFolder, torrentRelativePath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            fullPath = "";
            error = ex.Message;
            return false;
        }
    }

    public static void ValidateFileList(string rootFolder, IReadOnlyCollection<string> relativePaths, string? multiFileTorrentName = null)
    {
        if (relativePaths is null || relativePaths.Count == 0)
            throw new DownloadException(TorrentText.T("torrent.error.noFilesSelected"));

        if (relativePaths.Count > MaxFilesPerTorrent)
            throw new DownloadException(TorrentText.T("torrent.error.tooManyFiles"));

        if (relativePaths.Count > 1 && multiFileTorrentName != null)
        {
            ValidateSegment(multiFileTorrentName, multiFileTorrentName);
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relPath in relativePaths)
        {
            string full = Sanitize(rootFolder, relPath);
            if (!seenPaths.Add(full))
                throw new DownloadException(TorrentText.T("torrent.error.unsafePath", relPath));
        }
    }

    public static void ValidateMonoTorrentFiles(string rootFolder, string? torrentName, IList<ITorrentManagerFile> files)
    {
        if (files is null || files.Count == 0)
            throw new DownloadException(TorrentText.T("torrent.error.noFilesSelected"));

        if (files.Count > MaxFilesPerTorrent)
            throw new DownloadException(TorrentText.T("torrent.error.tooManyFiles"));

        string fullRoot = Path.GetFullPath(rootFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRootWithSep = fullRoot + Path.DirectorySeparatorChar;

        if (files.Count > 1)
        {
            ValidateSegment(torrentName ?? "", torrentName ?? "");
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            string sanitized = Sanitize(rootFolder, f.Path);
            if (!seenPaths.Add(sanitized))
                throw new DownloadException(TorrentText.T("torrent.error.unsafePath", f.Path));

            ValidateTargetPathUnderRoot(fullRoot, fullRootWithSep, f.FullPath, f.Path);
            ValidateTargetPathUnderRoot(fullRoot, fullRootWithSep, f.DownloadCompleteFullPath, f.Path);
            ValidateTargetPathUnderRoot(fullRoot, fullRootWithSep, f.DownloadIncompleteFullPath, f.Path);
        }
    }

    private static void ValidateTargetPathUnderRoot(string fullRoot, string fullRootWithSep, string? candidatePath, string relativeReportPath)
    {
        if (string.IsNullOrEmpty(candidatePath)) return;
        string fp = Path.GetFullPath(candidatePath);
        if (!fp.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase) || fp.Length <= fullRootWithSep.Length)
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", relativeReportPath));

        EnsureNoReparsePoints(fullRoot, fp, relativeReportPath);
    }

    internal static void EnsureNoReparsePoints(string fullRoot, string fullTarget, string relativeReportPath)
    {
        if (IsReparsePointPath(fullRoot))
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", relativeReportPath));

        string? current = fullTarget;
        while (!string.IsNullOrEmpty(current) && current.Length > fullRoot.Length)
        {
            if (IsReparsePointPath(current))
                throw new DownloadException(TorrentText.T("torrent.error.unsafePath", relativeReportPath));
            current = Path.GetDirectoryName(current);
        }
    }

    internal static bool IsReparsePointPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsReservedDeviceName(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;

        string trimmed = segment.Trim(' ', '.');
        if (ReservedDeviceNames.Contains(trimmed))
            return true;

        int firstDot = trimmed.IndexOf('.');
        if (firstDot > 0)
        {
            string stem = trimmed[..firstDot].Trim(' ', '.');
            if (ReservedDeviceNames.Contains(stem))
                return true;
        }

        string withoutExt = Path.GetFileNameWithoutExtension(trimmed).Trim(' ', '.');
        return ReservedDeviceNames.Contains(withoutExt);
    }

    public static bool IsExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string trimmed = path.TrimEnd(' ', '.');
        string ext = Path.GetExtension(trimmed);
        return !string.IsNullOrEmpty(ext) && ExecutableExtensions.Contains(ext);
    }

    public static void ValidateSegment(string seg, string fullRelativePath)
    {
        if (string.IsNullOrEmpty(seg) || string.IsNullOrWhiteSpace(seg))
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", fullRelativePath));

        if (seg is "." or "..")
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", fullRelativePath));

        if (seg.Length > MaxSegmentLength)
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", fullRelativePath));

        foreach (char c in seg)
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                throw new DownloadException(TorrentText.T("torrent.error.unsafePath", fullRelativePath));
        }

        // Windows strips trailing spaces and dots, and leading spaces can cause confusion
        if (seg[0] == ' ' || seg[^1] == ' ' || seg[^1] == '.' || (seg[0] == '.' && seg.Trim('.') == ""))
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", fullRelativePath));

        if (seg.IndexOfAny(InvalidSegmentChars) >= 0)
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", fullRelativePath));

        if (IsReservedDeviceName(seg))
            throw new DownloadException(TorrentText.T("torrent.error.unsafePath", fullRelativePath));
    }
}
