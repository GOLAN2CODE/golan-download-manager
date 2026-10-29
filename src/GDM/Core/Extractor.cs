using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

public sealed record ExtractionPlan(
    string ArchivePath,
    string TargetDir,
    ExtractTargetMode TargetMode,
    ExistingFileRule ExistingRule,
    bool Uses7Zip);

public sealed record SevenZipEntry(string Path, long Size, bool IsFolder, bool IsEncrypted, bool IsLink = false);

public sealed record SevenZipListing(List<SevenZipEntry> Entries, bool IsEncrypted, long TotalUncompressedSize);

public sealed record MultiPartInfo(
    bool IsMultiPart,
    bool IsComplete,
    string FirstPartPath,
    IReadOnlyList<string> AllPartPaths,
    IReadOnlyList<string> MissingOrIncompleteParts);

public sealed record ExtractionResult(ArchiveExtractState State, string TargetDir, string? ErrorMessage = null);

public static class Extractor
{
    public const long DefaultMaxUncompressedBytes = 50L * 1024 * 1024 * 1024; // 50 GB
    public const int DefaultMaxEntries = 100_000;
    public const int MaxMultiPartVolumes = 1000;

    static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    static readonly string[] SevenZipExtensions =
    [
        ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".zst",
    ];

    static readonly Regex PartRarRegex = new(
        @"^(?<stem>.+)\.part(?<num>\d+)\.rar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex SplitNumericRegex = new(
        @"^(?<stem>.+\.(?:7z|zip|tar))\.(?<num>\d{3,})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex SplitZipZRegex = new(
        @"^(?<stem>.+)\.z(?<num>\d{2,})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex SplitRarRRegex = new(
        @"^(?<stem>.+)\.r(?<num>\d{2,})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsZipArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (IsZipArchive(name)) return true;
        if (SevenZipExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) return true;
        if (SplitNumericRegex.IsMatch(name) || SplitZipZRegex.IsMatch(name) || SplitRarRRegex.IsMatch(name)) return true;
        return false;
    }

    public static string StripArchiveExtension(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "archive";
        var name = Path.GetFileName(fileName.Trim());

        var mRar = PartRarRegex.Match(name);
        if (mRar.Success)
            return FileNames.Sanitize(mRar.Groups["stem"].Value);

        var mSplit = SplitNumericRegex.Match(name);
        if (mSplit.Success)
            name = mSplit.Groups["stem"].Value;

        string[] compound = [".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst"];
        foreach (var c in compound)
        {
            if (name.EndsWith(c, StringComparison.OrdinalIgnoreCase) && name.Length > c.Length)
                return FileNames.Sanitize(name[..^c.Length]);
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        return FileNames.Sanitize(string.IsNullOrWhiteSpace(stem) ? "archive" : stem);
    }

    public static ExtractionPlan Plan(
        string archivePath,
        ExtractTargetMode targetMode = ExtractTargetMode.Subfolder,
        ExistingFileRule existingRule = ExistingFileRule.Rename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullArchive = Path.GetFullPath(archivePath);
        var parentDir = Path.GetDirectoryName(fullArchive) ?? AppContext.BaseDirectory;
        bool uses7Zip = !IsZipArchive(fullArchive) || CheckMultiPart(fullArchive).IsMultiPart;

        if (targetMode == ExtractTargetMode.SameFolder)
            return new ExtractionPlan(fullArchive, parentDir, targetMode, existingRule, uses7Zip);

        var folderName = StripArchiveExtension(Path.GetFileName(fullArchive));
        var candidate = Path.Combine(parentDir, folderName);
        if (Directory.Exists(candidate) || File.Exists(candidate))
        {
            for (int i = 1; ; i++)
            {
                var next = Path.Combine(parentDir, $"{folderName} ({i})");
                if (!Directory.Exists(next) && !File.Exists(next))
                {
                    candidate = next;
                    break;
                }
            }
        }
        return new ExtractionPlan(fullArchive, candidate, targetMode, existingRule, uses7Zip);
    }

    /// <summary>Validates an archive entry relative path against zip-slip, rooted paths, NTFS ADS, and Windows reserved device names.</summary>
    public static string? ValidateEntryPath(string targetDir, string entryName)
    {
        if (string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(entryName))
            return null;

        // Reject null chars and colons (drive letters C:, drive-relative paths, NTFS Alternate Data Streams)
        if (entryName.Contains('\0') || entryName.Contains(':'))
            return null;

        // Reject rooted / UNC paths
        if (entryName.StartsWith('/') || entryName.StartsWith('\\') || Path.IsPathRooted(entryName))
            return null;

        var rawSegments = entryName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
            return null;

        var cleanSegments = new string[rawSegments.Length];
        var invalidChars = Path.GetInvalidFileNameChars();
        for (int i = 0; i < rawSegments.Length; i++)
        {
            var seg = rawSegments[i];
            if (seg is "." or "..") return null;

            var trimmed = seg.TrimEnd(' ', '.');
            if (trimmed.Length == 0) return null;

            if (seg.IndexOfAny(invalidChars) >= 0) return null;

            int dotIdx = trimmed.IndexOf('.');
            var stem = dotIdx >= 0 ? trimmed[..dotIdx].TrimEnd(' ', '.') : trimmed;
            if (ReservedDeviceNames.Contains(stem))
                return null;

            cleanSegments[i] = trimmed;
        }

        string rootFull;
        string combinedFull;
        try
        {
            rootFull = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rel = string.Join(Path.DirectorySeparatorChar, cleanSegments);
            combinedFull = Path.GetFullPath(Path.Combine(rootFull, rel));
        }
        catch
        {
            return null;
        }

        var requiredPrefix = rootFull + Path.DirectorySeparatorChar;
        if (!combinedFull.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return combinedFull;
    }

    public static long MaxAllowedUncompressedBytes(long archiveSize, long hardCap = DefaultMaxUncompressedBytes)
    {
        long safeArchive = Math.Max(0, archiveSize);
        long dynamicCap;
        try
        {
            dynamicCap = checked(100L * safeArchive + 100L * 1024 * 1024);
        }
        catch (OverflowException)
        {
            dynamicCap = hardCap;
        }
        return Math.Min(hardCap, dynamicCap);
    }

    /// <summary>Finds a trusted 7z.exe in Program Files, Program Files (x86), or PATH (never from the download folder or CWD).</summary>
    public static string? Find7Zip(string? customPath = null, string? forbiddenDir = null, IEnumerable<string>? searchDirs = null)
    {
        string? normalizedForbidden = null;
        if (!string.IsNullOrWhiteSpace(forbiddenDir))
        {
            try { normalizedForbidden = Path.GetFullPath(forbiddenDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (ArgumentException) { /* ignore invalid forbiddenDir path */ }
        }

        var cwd = Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        bool IsSafeExecutable(string? candidate, bool blockCwd)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            try
            {
                if (!Path.IsPathFullyQualified(candidate)) return false;
                var full = Path.GetFullPath(candidate);
                if (!File.Exists(full)) return false;
                var dir = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
                if (normalizedForbidden != null &&
                    (dir.Equals(normalizedForbidden, StringComparison.OrdinalIgnoreCase) ||
                     dir.StartsWith(normalizedForbidden + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    return false;
                if (blockCwd &&
                    (dir.Equals(cwd, StringComparison.OrdinalIgnoreCase) ||
                     dir.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        if (IsSafeExecutable(customPath, blockCwd: true))
            return Path.GetFullPath(customPath!);

        if (searchDirs == null)
        {
            string[] standardLocations =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
            ];
            foreach (var loc in standardLocations)
            {
                if (IsSafeExecutable(loc, blockCwd: true))
                    return Path.GetFullPath(loc);
            }
        }

        var dirs = searchDirs ?? (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawDir in dirs)
        {
            var d = rawDir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(d) || d == "." || !Path.IsPathFullyQualified(d)) continue;
            try
            {
                var fullDir = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = Path.Combine(fullDir, "7z.exe");
                if (IsSafeExecutable(candidate, blockCwd: searchDirs == null))
                    return candidate;
            }
            catch (ArgumentException) { /* ignore malformed PATH entry */ }
        }

        return null;
    }

    public static SevenZipListing Parse7zListing(string sltOutput)
    {
        var entries = new List<SevenZipEntry>();
        if (string.IsNullOrWhiteSpace(sltOutput))
            return new SevenZipListing(entries, false, 0);

        var text = sltOutput;
        int sepIdx = text.IndexOf("----------", StringComparison.Ordinal);
        if (sepIdx >= 0)
            text = text[(sepIdx + 10)..];

        string? curPath = null;
        long curSize = 0;
        bool curFolder = false;
        bool curEncrypted = false;
        bool curIsLink = false;
        bool anyEncrypted = false;
        long totalSize = 0;

        void CommitCurrent()
        {
            if (!string.IsNullOrEmpty(curPath))
            {
                entries.Add(new SevenZipEntry(curPath, curSize, curFolder, curEncrypted, curIsLink));
                if (curEncrypted) anyEncrypted = true;
                if (!curFolder && curSize > 0)
                    totalSize = checked(totalSize + curSize);
            }
            else if (!curFolder && curSize > 0)
            {
                totalSize = checked(totalSize + curSize);
            }
            curPath = null;
            curSize = 0;
            curFolder = false;
            curEncrypted = false;
            curIsLink = false;
        }

        var lines = text.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                CommitCurrent();
                continue;
            }

            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 3)..].Trim();

            if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                if (curPath != null) CommitCurrent();
                curPath = val;
            }
            else if (key.Equals("Size", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(val, out var sz) && sz > 0)
                    curSize = sz;
            }
            else if (key.Equals("Folder", StringComparison.OrdinalIgnoreCase))
            {
                curFolder = val == "+";
            }
            else if (key.Equals("Encrypted", StringComparison.OrdinalIgnoreCase))
            {
                if (val == "+") curEncrypted = true;
            }
            else if (key.Equals("Method", StringComparison.OrdinalIgnoreCase))
            {
                if (val.Contains("AES", StringComparison.OrdinalIgnoreCase) ||
                    val.Contains("ZipCrypto", StringComparison.OrdinalIgnoreCase))
                    curEncrypted = true;
            }
            else if (key.Equals("Symbolic Link", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Hard Link", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(val))
                    curIsLink = true;
            }
            else if (key.Equals("Attributes", StringComparison.OrdinalIgnoreCase))
            {
                if (val.StartsWith('D') || val.Contains('D'))
                    curFolder = true;
                if (val.Contains('l') || val.Contains('L') || val.Contains("Reparse", StringComparison.OrdinalIgnoreCase))
                    curIsLink = true;
            }
        }
        CommitCurrent();

        return new SevenZipListing(entries, anyEncrypted, totalSize);
    }

    public static void Validate7zListing(
        SevenZipListing listing,
        string targetDir,
        long archiveSize,
        long maxTotalBytes = DefaultMaxUncompressedBytes,
        int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (listing.Entries.Count > maxEntries)
            throw new InvalidDataException(Loc.T($"extract.err.bomb_entries"));

        long limit = MaxAllowedUncompressedBytes(archiveSize, maxTotalBytes);
        if (listing.TotalUncompressedSize > limit)
            throw new InvalidDataException(Loc.T($"extract.err.bomb_size"));

        foreach (var entry in listing.Entries)
        {
            if (entry.IsLink || ValidateEntryPath(targetDir, entry.Path) == null)
                throw new InvalidDataException(Loc.T($"extract.err.zip_slip", entry.Path));
        }
    }

    public static List<string> Build7zExtractArgs(
        string archivePath,
        string targetDir,
        ExistingFileRule existingRule,
        string? password = null)
    {
        var overwriteSwitch = existingRule switch
        {
            ExistingFileRule.Rename => "-aou",
            ExistingFileRule.Skip => "-aos",
            _ => "-aoa",
        };
        return
        [
            "x",
            Path.GetFullPath(archivePath),
            $"-o{Path.GetFullPath(targetDir)}",
            "-y",
            overwriteSwitch,
            string.IsNullOrEmpty(password) ? "-p-" : $"-p{password}",
        ];
    }

    /// <summary>Detects multi-part sets (.partN.rar, .7z.001, .zip+.z01, .rar+.r00) and checks if all parts are present and finished.</summary>
    public static MultiPartInfo CheckMultiPart(string filePath, IEnumerable<string>? existingFilesInDir = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new MultiPartInfo(false, true, filePath, [], []);

        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        var fileName = Path.GetFileName(fullPath);

        List<string> dirFiles;
        if (existingFilesInDir != null)
            dirFiles = existingFilesInDir.Select(Path.GetFullPath).ToList();
        else if (Directory.Exists(dir))
            dirFiles = Directory.GetFiles(dir).Select(Path.GetFullPath).ToList();
        else
            dirFiles = [];

        var fileSet = new HashSet<string>(dirFiles, StringComparer.OrdinalIgnoreCase);
        bool IsPartReady(string partFullPath)
            => fileSet.Contains(partFullPath) &&
               !fileSet.Contains(partFullPath + ".gdm") &&
               !fileSet.Contains(partFullPath + ".part");

        static string StripTempExt(string name)
        {
            if (name.EndsWith(".gdm", StringComparison.OrdinalIgnoreCase)) return name[..^4];
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return name[..^5];
            return name;
        }

        // 1. .partN.rar
        var mRar = PartRarRegex.Match(fileName);
        if (mRar.Success &&
            int.TryParse(mRar.Groups["num"].Value, out int rarSelfNum) &&
            rarSelfNum > 0 && rarSelfNum <= MaxMultiPartVolumes)
        {
            var stem = mRar.Groups["stem"].Value;
            var numStr = mRar.Groups["num"].Value;
            int padWidth = numStr.Length;
            int maxNum = Math.Max(2, rarSelfNum);
            foreach (var f in dirFiles)
            {
                var n = StripTempExt(Path.GetFileName(f));
                var m = PartRarRegex.Match(n);
                if (m.Success && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(m.Groups["num"].Value, out var k) && k > 0 && k <= MaxMultiPartVolumes)
                {
                    if (k > maxNum) maxNum = k;
                    if (m.Groups["num"].Value.Length > padWidth) padWidth = m.Groups["num"].Value.Length;
                }
            }

            var allParts = new List<string>(maxNum);
            var missing = new List<string>();
            for (int i = 1; i <= maxNum; i++)
            {
                var p1 = Path.Combine(dir, $"{stem}.part{i.ToString().PadLeft(padWidth, '0')}.rar");
                var pUnpadded = Path.Combine(dir, $"{stem}.part{i}.rar");
                var chosen = fileSet.Contains(p1) ? p1 : fileSet.Contains(pUnpadded) ? pUnpadded : p1;
                allParts.Add(chosen);
                if (!IsPartReady(chosen))
                    missing.Add(chosen);
            }
            return new MultiPartInfo(true, missing.Count == 0, allParts[0], allParts, missing);
        }

        // 2. .7z.001 / .zip.001 / .tar.001
        var mSplit = SplitNumericRegex.Match(fileName);
        if (mSplit.Success &&
            int.TryParse(mSplit.Groups["num"].Value, out int splitSelfNum) &&
            splitSelfNum > 0 && splitSelfNum <= MaxMultiPartVolumes)
        {
            var stem = mSplit.Groups["stem"].Value;
            var numStr = mSplit.Groups["num"].Value;
            int padWidth = Math.Max(3, numStr.Length);
            int maxNum = Math.Max(2, splitSelfNum);

            foreach (var f in dirFiles)
            {
                var n = StripTempExt(Path.GetFileName(f));
                var m = SplitNumericRegex.Match(n);
                if (m.Success && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(m.Groups["num"].Value, out var k) && k > maxNum && k <= MaxMultiPartVolumes)
                {
                    maxNum = k;
                }
            }

            var allParts = new List<string>(maxNum);
            var missing = new List<string>();
            for (int i = 1; i <= maxNum; i++)
            {
                var p = Path.Combine(dir, $"{stem}.{i.ToString().PadLeft(padWidth, '0')}");
                allParts.Add(p);
                if (!IsPartReady(p))
                    missing.Add(p);
            }
            return new MultiPartInfo(true, missing.Count == 0, allParts[0], allParts, missing);
        }

        // 3. .zip + .z01/.z02
        var mZipZ = SplitZipZRegex.Match(fileName);
        int zipZSelfNum = 1;
        bool validZipZ = (!mZipZ.Success || (int.TryParse(mZipZ.Groups["num"].Value, out zipZSelfNum) && zipZSelfNum > 0 && zipZSelfNum <= MaxMultiPartVolumes));
        if (validZipZ && (mZipZ.Success || (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            dirFiles.Any(f => Path.GetFileName(f).StartsWith(Path.GetFileNameWithoutExtension(fileName) + ".z01", StringComparison.OrdinalIgnoreCase)))))
        {
            var stem = mZipZ.Success ? mZipZ.Groups["stem"].Value : Path.GetFileNameWithoutExtension(fileName);
            int maxNum = mZipZ.Success ? Math.Max(1, zipZSelfNum) : 1;
            foreach (var f in dirFiles)
            {
                var n = StripTempExt(Path.GetFileName(f));
                var m = SplitZipZRegex.Match(n);
                if (m.Success && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(m.Groups["num"].Value, out var k) && k > maxNum && k <= MaxMultiPartVolumes)
                    maxNum = k;
            }

            var mainZip = Path.Combine(dir, $"{stem}.zip");
            var allParts = new List<string> { mainZip };
            var missing = new List<string>();
            if (!IsPartReady(mainZip)) missing.Add(mainZip);
            for (int i = 1; i <= maxNum; i++)
            {
                var zp = Path.Combine(dir, $"{stem}.z{i:D2}");
                allParts.Add(zp);
                if (!IsPartReady(zp)) missing.Add(zp);
            }
            return new MultiPartInfo(true, missing.Count == 0, mainZip, allParts, missing);
        }

        // 4. .rar + .r00/.r01
        var mRarR = SplitRarRRegex.Match(fileName);
        int rarRSelfNum = 0;
        bool validRarR = (!mRarR.Success || (int.TryParse(mRarR.Groups["num"].Value, out rarRSelfNum) && rarRSelfNum >= 0 && rarRSelfNum <= MaxMultiPartVolumes));
        if (validRarR && (mRarR.Success || (fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) &&
            dirFiles.Any(f => Path.GetFileName(f).StartsWith(Path.GetFileNameWithoutExtension(fileName) + ".r00", StringComparison.OrdinalIgnoreCase)))))
        {
            var stem = mRarR.Success ? mRarR.Groups["stem"].Value : Path.GetFileNameWithoutExtension(fileName);
            int maxNum = mRarR.Success ? Math.Max(0, rarRSelfNum) : 0;
            foreach (var f in dirFiles)
            {
                var n = StripTempExt(Path.GetFileName(f));
                var m = SplitRarRRegex.Match(n);
                if (m.Success && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(m.Groups["num"].Value, out var k) && k > maxNum && k <= MaxMultiPartVolumes)
                    maxNum = k;
            }

            var mainRar = Path.Combine(dir, $"{stem}.rar");
            var allParts = new List<string> { mainRar };
            var missing = new List<string>();
            if (!IsPartReady(mainRar)) missing.Add(mainRar);
            for (int i = 0; i <= maxNum; i++)
            {
                var rp = Path.Combine(dir, $"{stem}.r{i:D2}");
                allParts.Add(rp);
                if (!IsPartReady(rp)) missing.Add(rp);
            }
            return new MultiPartInfo(true, missing.Count == 0, mainRar, allParts, missing);
        }

        return new MultiPartInfo(false, IsPartReady(fullPath), fullPath, [fullPath], IsPartReady(fullPath) ? [] : [fullPath]);
    }

    public static Task<ExtractionResult> ExtractZipAsync(
        string archivePath,
        string targetDir,
        ExistingFileRule existingRule = ExistingFileRule.Rename,
        long maxTotalBytes = DefaultMaxUncompressedBytes,
        int maxEntries = DefaultMaxEntries,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(archivePath))
                    return new ExtractionResult(ArchiveExtractState.Failed, targetDir, "Archive not found");

                long archiveSize = new FileInfo(archivePath).Length;
                long effectiveMaxBytes = MaxAllowedUncompressedBytes(archiveSize, maxTotalBytes);
                string zonePayload = MarkOfTheWeb.ReadZonePayloadOrDefault(archivePath);

                await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var za = new ZipArchive(archiveStream, ZipArchiveMode.Read);

                if (za.Entries.Count > maxEntries)
                    return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.bomb_entries"));

                long declaredTotal = 0;
                var plannedEntries = new List<(ZipArchiveEntry Entry, string FullDest, bool IsDir)>(za.Entries.Count);

                foreach (var entry in za.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    bool isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || (entry.Name.Length == 0 && entry.Length == 0);
                    var trimmedName = entry.FullName.TrimEnd('/', '\\');
                    if (trimmedName.Length == 0) continue;

                    var validDest = ValidateEntryPath(targetDir, trimmedName);
                    if (validDest == null)
                        return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.zip_slip", entry.FullName));

                    if (!isDir && entry.Length > 0)
                    {
                        declaredTotal = checked(declaredTotal + entry.Length);
                        if (declaredTotal > effectiveMaxBytes)
                            return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.bomb_size"));
                    }

                    plannedEntries.Add((entry, validDest, isDir));
                }

                Directory.CreateDirectory(targetDir);
                long totalStreamed = 0;
                var buffer = new byte[65536];

                foreach (var (entry, destPath, isDir) in plannedEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (isDir)
                    {
                        Directory.CreateDirectory(destPath);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    var finalDest = destPath;
                    if (File.Exists(finalDest))
                    {
                        if (existingRule == ExistingFileRule.Skip)
                            continue;
                        if (existingRule == ExistingFileRule.Rename)
                            finalDest = FileNames.Unique(finalDest);
                    }

                    string? createdFile = finalDest;
                    try
                    {
                        await using (var inStream = entry.Open())
                        await using (var outStream = new FileStream(finalDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        {
                            while (true)
                            {
                                ct.ThrowIfCancellationRequested();
                                int n = await inStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                                if (n <= 0) break;

                                totalStreamed += n;
                                if (totalStreamed > effectiveMaxBytes)
                                    throw new InvalidDataException(Loc.T($"extract.err.bomb_size"));

                                await outStream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            }
                        }
                        MarkOfTheWeb.WriteRawPayload(finalDest, zonePayload);
                    }
                    catch
                    {
                        try { if (createdFile != null && File.Exists(createdFile)) File.Delete(createdFile); }
                        catch (IOException) { /* best-effort partial file cleanup on zip bomb or I/O abort */ }
                        throw;
                    }
                }

                return new ExtractionResult(ArchiveExtractState.Extracted, targetDir);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ExtractionResult(ArchiveExtractState.Failed, targetDir, ex.Message);
            }
        }, ct);
    }

    public static async Task<ExtractionResult> ExtractAsync(
        string archivePath,
        ExtractTargetMode targetMode = ExtractTargetMode.Subfolder,
        ExistingFileRule existingRule = ExistingFileRule.Rename,
        bool deleteAfterSuccess = false,
        Func<string?>? askPassword = null,
        string? custom7ZipPath = null,
        long maxTotalBytes = DefaultMaxUncompressedBytes,
        int maxEntries = DefaultMaxEntries,
        CancellationToken ct = default)
    {
        var mp = CheckMultiPart(archivePath);
        if (mp.IsMultiPart && !mp.IsComplete)
            return new ExtractionResult(ArchiveExtractState.WaitingForParts, "");

        var effectiveArchive = mp.IsMultiPart ? mp.FirstPartPath : Path.GetFullPath(archivePath);
        var plan = Plan(effectiveArchive, targetMode, existingRule);

        ExtractionResult result;
        if (!plan.Uses7Zip)
        {
            result = await ExtractZipAsync(effectiveArchive, plan.TargetDir, existingRule, maxTotalBytes, maxEntries, ct).ConfigureAwait(false);
        }
        else
        {
            var archiveDir = Path.GetDirectoryName(effectiveArchive);
            var sevenZipExe = Find7Zip(custom7ZipPath, forbiddenDir: archiveDir);
            if (sevenZipExe == null)
                return new ExtractionResult(ArchiveExtractState.SkippedNo7Zip, plan.TargetDir, Loc.T($"extract.err.no_7z"));

            result = await ExtractWith7ZipAsync(
                sevenZipExe,
                effectiveArchive,
                plan.TargetDir,
                existingRule,
                askPassword,
                maxTotalBytes,
                maxEntries,
                ct).ConfigureAwait(false);
        }

        if (result.State == ArchiveExtractState.Extracted && deleteAfterSuccess)
        {
            foreach (var part in mp.AllPartPaths)
                MediaDownloader.TryDelete(part);
        }

        return result;
    }

    static async Task<ExtractionResult> ExtractWith7ZipAsync(
        string sevenZipExe,
        string archivePath,
        string targetDir,
        ExistingFileRule existingRule,
        Func<string?>? askPassword,
        long maxTotalBytes,
        int maxEntries,
        CancellationToken ct)
    {
        try
        {
            long archiveSize = new FileInfo(archivePath).Length;
            string zonePayload = MarkOfTheWeb.ReadZonePayloadOrDefault(archivePath);
            string? password = null;
            bool askedPassword = false;

            // Step 1: List entries with 7z l -slt -ba -p-
            var (listExit, listOut, listErr) = await Run7ZipProcessAsync(
                sevenZipExe,
                ["l", "-slt", "-ba", "-p-", archivePath],
                ct).ConfigureAwait(false);

            if (listExit != 0 && Is7ZipPasswordError(listOut + "\n" + listErr))
            {
                if (askPassword == null)
                    return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.password_cancelled"));
                askedPassword = true;
                password = askPassword();
                if (string.IsNullOrEmpty(password))
                    return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.password_cancelled"));

                (listExit, listOut, listErr) = await Run7ZipProcessAsync(
                    sevenZipExe,
                    ["l", "-slt", "-ba", $"-p{password}", archivePath],
                    ct).ConfigureAwait(false);
            }

            if (listExit != 0)
                return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.7z_failed", listExit));

            var listing = Parse7zListing(listOut);
            Validate7zListing(listing, targetDir, archiveSize, maxTotalBytes, maxEntries);

            if (listing.IsEncrypted && !askedPassword)
            {
                if (askPassword == null)
                    return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.password_cancelled"));
                askedPassword = true;
                password = askPassword();
                if (string.IsNullOrEmpty(password))
                    return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.password_cancelled"));
            }

            Directory.CreateDirectory(targetDir);
            var preExisting = new HashSet<string>(
                Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            var extractArgs = Build7zExtractArgs(archivePath, targetDir, existingRule, password);
            var (extExit, _, _) = await Run7ZipProcessAsync(sevenZipExe, extractArgs, ct).ConfigureAwait(false);
            if (extExit != 0)
                return new ExtractionResult(ArchiveExtractState.Failed, targetDir, Loc.T($"extract.err.7z_failed", extExit));

            ApplyMarkOfTheWebToExtractedDirectory(targetDir, zonePayload, preExisting);
            return new ExtractionResult(ArchiveExtractState.Extracted, targetDir);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExtractionResult(ArchiveExtractState.Failed, targetDir, ex.Message);
        }
    }

    internal static void ApplyMarkOfTheWebToExtractedDirectory(
        string targetDir,
        string? zonePayload,
        IReadOnlySet<string>? preExistingFiles = null)
    {
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir)) return;
        foreach (var file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                string full = Path.GetFullPath(file);
                if (preExistingFiles != null && preExistingFiles.Contains(full)) continue;
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
                MarkOfTheWeb.WriteRawPayload(full, zonePayload);
            }
            catch (Exception ex)
            {
                Log.Write("extracted file MOTW write failed: " + ex.Message);
            }
        }
    }

    static bool Is7ZipPasswordError(string output)
        => output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Enter password", StringComparison.OrdinalIgnoreCase);

    static async Task<(int ExitCode, string StdOut, string StdErr)> Run7ZipProcessAsync(
        string exePath,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeoutCts.Token;

        var psi = new ProcessStartInfo(exePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        try { proc.StandardInput.Close(); }
        catch (InvalidOperationException) { /* process already exited */ }
        using var reg = token.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* process already exited */ }
        });

        try
        {
            var outTask = proc.StandardOutput.ReadToEndAsync(token);
            var errTask = proc.StandardError.ReadToEndAsync(token);
            await proc.WaitForExitAsync(token).ConfigureAwait(false);
            return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* process already exited */ }
        }
    }

    /// <summary>Runs F8 integrity verification first (when ExpectedHash or AutoDiscoverHash is set), then F9 auto-extraction.</summary>
    public static async Task<ExtractionResult> RunPostDownloadAsync(
        DownloadItem item,
        AppSettings settings,
        Func<string?>? askPassword = null,
        IProgress<double>? hashProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);

        bool hashOk = await Integrity.VerifyItemAsync(item, settings, hashProgress, ct).ConfigureAwait(false);
        if (!hashOk)
        {
            item.ExtractState = ArchiveExtractState.None;
            return new ExtractionResult(ArchiveExtractState.None, "");
        }

        if (File.Exists(item.FullPath) && !File.Exists(item.FullPath + ":Zone.Identifier"))
        {
            string hostUrl = !string.IsNullOrWhiteSpace(item.ResolvedUrl) ? item.ResolvedUrl : item.Url;
            MarkOfTheWeb.Write(item.FullPath, hostUrl, item.Referrer);
        }

        bool shouldExtract = item.AutoExtract ?? settings.AutoExtractEnabled;
        if (!shouldExtract || !IsSupportedArchive(item.FullPath))
        {
            item.ExtractState = ArchiveExtractState.None;
            return new ExtractionResult(ArchiveExtractState.None, "");
        }

        item.ExtractState = ArchiveExtractState.Extracting;
        var res = await ExtractAsync(
            item.FullPath,
            settings.AutoExtractTargetMode,
            settings.AutoExtractExistingRule,
            settings.AutoExtractDeleteArchive,
            askPassword,
            settings.Custom7ZipPath,
            ct: ct).ConfigureAwait(false);

        item.ExtractState = res.State;
        item.ExtractedPath = res.TargetDir;
        item.ExtractError = res.ErrorMessage ?? "";
        return res;
    }
}
