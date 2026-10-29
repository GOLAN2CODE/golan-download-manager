using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Golan.Gdm.Core;

public interface IRegistryView
{
    IReadOnlyList<string> GetSubKeyNames(string keyPath);
    object? GetValue(string keyPath, string valueName);
}

/// <summary>Read-only Windows Registry implementation reading under <c>HKEY_CURRENT_USER</c>.</summary>
public sealed class Win32CurrentUserRegistryView : IRegistryView
{
    public static readonly Win32CurrentUserRegistryView Instance = new();

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public object? GetValue(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record IdmCandidate(
    string Url,
    string NormalizedUrl,
    string FileName,
    string? FilePath,
    string Referrer,
    string Cookies,
    string UserAgent,
    string Username,
    string Password,
    string Description,
    long FileSize,
    bool IsCompletedInSource,
    DateTimeOffset? AddedAt)
{
    public override string ToString()
        => $"IdmCandidate {{ Url = {Url}, FileName = {FileName}, FileSize = {FileSize}, Completed = {IsCompletedInSource} }}";
}

public sealed record IdmParseResult(IReadOnlyList<IdmCandidate> Candidates, int SkippedCount);
public sealed record IdmImportSummary(IReadOnlyList<DownloadItem> Imported, int DuplicateSkippedCount, int InvalidSkippedCount);

public static class IdmImport
{
    public const string IdmRegistryRoot = @"Software\DownloadManager";
    const int MaxEf2Bytes = 64 * 1024 * 1024; // 64 MB safety cap

    static IdmImport()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Parses an IDM <c>.ef2</c> file from disk.</summary>
    public static IdmParseResult ParseEf2File(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("File path is required.", nameof(path));
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ParseEf2(fs);
    }

    /// <summary>
    /// Parses an IDM <c>.ef2</c> stream. Supports CRLF and LF, UTF-8 (with or without BOM),
    /// UTF-16 BOM, and Windows-1256 (Arabic ANSI) fallback.
    /// </summary>
    public static IdmParseResult ParseEf2(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        long total = 0;
        while (true)
        {
            int n = stream.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            total += n;
            if (total > MaxEf2Bytes)
                throw new InvalidDataException("EF2 file exceeds maximum allowed size (64 MB).");
            ms.Write(buf, 0, n);
        }

        string text = DecodeEf2Bytes(ms.ToArray());
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        var candidates = new List<IdmCandidate>();
        int skipped = 0;

        bool inBlock = false;
        string? blockUrl = null;
        string referrer = "";
        var cookies = new List<string>();
        string userAgent = "";
        string fileName = "";
        string? filePath = null;
        string username = "";
        string password = "";
        string description = "";

        void ResetBlock()
        {
            blockUrl = null;
            referrer = "";
            cookies.Clear();
            userAgent = "";
            fileName = "";
            filePath = null;
            username = "";
            password = "";
            description = "";
        }

        void CommitBlock()
        {
            if (string.IsNullOrWhiteSpace(blockUrl) || !IsSupportedImportUrl(blockUrl, out var uri) || uri == null)
            {
                skipped++;
                return;
            }

            string cleanUrl = blockUrl.Trim();
            string resolvedName = string.IsNullOrWhiteSpace(fileName)
                ? FileNames.FromUrl(cleanUrl)
                : fileName.Trim();

            candidates.Add(new IdmCandidate(
                Url: cleanUrl,
                NormalizedUrl: NormalizeUrlForDedupe(cleanUrl),
                FileName: resolvedName,
                FilePath: string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim(),
                Referrer: referrer,
                Cookies: string.Join("; ", cookies),
                UserAgent: userAgent,
                Username: username,
                Password: password,
                Description: description,
                FileSize: -1,
                IsCompletedInSource: false,
                AddedAt: null));
        }

        using var reader = new StringReader(text);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            var trimmed = rawLine.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0)
                continue;

            if (trimmed == "<")
            {
                if (inBlock)
                {
                    // Previous block was never closed with '>'
                    skipped++;
                }
                inBlock = true;
                ResetBlock();
                continue;
            }

            if (trimmed == ">")
            {
                if (!inBlock)
                {
                    skipped++;
                    continue;
                }
                CommitBlock();
                inBlock = false;
                ResetBlock();
                continue;
            }

            if (!inBlock)
                continue;

            if (blockUrl == null)
            {
                blockUrl = trimmed;
                continue;
            }

            int colon = rawLine.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = rawLine[..colon].Trim().ToLowerInvariant();
            string val = rawLine[(colon + 1)..].Trim();
            if (val.Length == 0)
                continue;

            switch (key)
            {
                case "referer" or "referrer":
                    referrer = val;
                    break;
                case "cookie" or "cookies":
                    cookies.Add(val);
                    break;
                case "user-agent" or "useragent":
                    userAgent = val;
                    break;
                case "filename" or "localfilename":
                    fileName = val;
                    break;
                case "filepath" or "savepath":
                    filePath = val;
                    break;
                case "username" or "user" or "login":
                    username = val;
                    break;
                case "password" or "pass":
                    password = val;
                    break;
                case "description" or "comment":
                    description = val;
                    break;
                default:
                    // Unknown keys are tolerated and ignored
                    break;
            }
        }

        if (inBlock)
            skipped++;

        return new IdmParseResult(candidates, skipped);
    }

    /// <summary>Reads IDM's download list from <c>HKCU\Software\DownloadManager</c> behind <see cref="IRegistryView"/>.</summary>
    public static IdmParseResult ReadRegistry(IRegistryView registry, string rootKey = IdmRegistryRoot)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (string.IsNullOrWhiteSpace(rootKey))
            rootKey = IdmRegistryRoot;

        var candidates = new List<IdmCandidate>();
        int skipped = 0;

        IReadOnlyList<string> subKeys;
        try { subKeys = registry.GetSubKeyNames(rootKey); }
        catch { return new IdmParseResult([], 0); }

        foreach (var subName in subKeys)
        {
            // Only numeric subkeys ("1", "2", ...) correspond to download entries in IDM
            if (!int.TryParse(subName, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;

            string itemKey = $"{rootKey}\\{subName}";
            try
            {
                string? rawUrl = ReadString(registry, itemKey, "Url0")
                              ?? ReadString(registry, itemKey, "Url");
                if (string.IsNullOrWhiteSpace(rawUrl) || !IsSupportedImportUrl(rawUrl, out _))
                {
                    skipped++;
                    continue;
                }

                string cleanUrl = rawUrl.Trim();
                string? localFile = ReadString(registry, itemKey, "LocalFileName")
                                 ?? ReadString(registry, itemKey, "FileName");
                string? localPath = ReadString(registry, itemKey, "LocalPath")
                                 ?? ReadString(registry, itemKey, "SavePath");

                string resolvedFileName;
                string? resolvedDir = localPath;

                if (!string.IsNullOrWhiteSpace(localFile))
                {
                    string trimmedLocal = localFile.Trim();
                    if (trimmedLocal.Contains('\\') || trimmedLocal.Contains('/'))
                    {
                        resolvedFileName = Path.GetFileName(trimmedLocal);
                        var dir = Path.GetDirectoryName(trimmedLocal);
                        if (!string.IsNullOrWhiteSpace(dir))
                            resolvedDir = dir;
                    }
                    else
                    {
                        resolvedFileName = trimmedLocal;
                    }
                }
                else
                {
                    resolvedFileName = FileNames.FromUrl(cleanUrl);
                }

                if (string.IsNullOrWhiteSpace(resolvedFileName))
                    resolvedFileName = FileNames.FromUrl(cleanUrl);

                long fileSize = ReadFileSize(registry.GetValue(itemKey, "FileSize"));
                bool completed = ReadIsCompleted(
                    registry.GetValue(itemKey, "DownloadStatus") ?? registry.GetValue(itemKey, "Status"));

                string referrer = ReadString(registry, itemKey, "Owr0")
                               ?? ReadString(registry, itemKey, "Referer")
                               ?? "";
                string cookies = ReadString(registry, itemKey, "Cookie")
                              ?? ReadString(registry, itemKey, "Cookies")
                              ?? "";
                string userAgent = ReadString(registry, itemKey, "UserAgent") ?? "";
                string username = ReadString(registry, itemKey, "User")
                               ?? ReadString(registry, itemKey, "Username")
                               ?? "";
                string password = ReadString(registry, itemKey, "Password") ?? "";
                string description = ReadString(registry, itemKey, "Description")
                                  ?? ReadString(registry, itemKey, "Comment")
                                  ?? "";
                DateTimeOffset? addedAt = ReadDate(
                    registry.GetValue(itemKey, "DateAdded") ?? registry.GetValue(itemKey, "LastTryDate"));

                candidates.Add(new IdmCandidate(
                    Url: cleanUrl,
                    NormalizedUrl: NormalizeUrlForDedupe(cleanUrl),
                    FileName: resolvedFileName,
                    FilePath: resolvedDir,
                    Referrer: referrer,
                    Cookies: cookies,
                    UserAgent: userAgent,
                    Username: username,
                    Password: password,
                    Description: description,
                    FileSize: fileSize,
                    IsCompletedInSource: completed,
                    AddedAt: addedAt));
            }
            catch
            {
                skipped++;
            }
        }

        return new IdmParseResult(candidates, skipped);
    }

    /// <summary>Normalizes a URL for deduplication: lowercase scheme and host, default port removed, fragment removed.</summary>
    public static string NormalizeUrlForDedupe(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed.ToLowerInvariant();

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = "",
        };

        if ((builder.Scheme == "http" && builder.Port == 80)
            || (builder.Scheme == "https" && builder.Port == 443)
            || (builder.Scheme == "ftp" && builder.Port == 21))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    /// <summary>Checks that <paramref name="url"/> is an absolute http, https, or ftp URL with a valid host.</summary>
    public static bool IsSupportedImportUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (trimmed.Length > 4096 || trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (trimmed.Any(c => char.IsControl(c)))
            return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            return false;

        var scheme = parsed.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "ftp"))
            return false;
        if (string.IsNullOrWhiteSpace(parsed.Host) || parsed.IsUnc || parsed.IsFile)
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>
    /// Safely checks whether a candidate marked completed in IDM has an existing local (non-UNC, non-traversal) file on disk.
    /// </summary>
    public static bool TryResolveCompletedLocalFile(IdmCandidate cand, out string safeDir, out string safeFileName, out long fileLength)
    {
        safeDir = "";
        fileLength = -1;
        string leafName = ExtractLeafFileName(cand.FileName);
        safeFileName = string.IsNullOrWhiteSpace(leafName)
            ? FileNames.FromUrl(cand.Url)
            : FileNames.Sanitize(leafName);
        if (string.IsNullOrWhiteSpace(safeFileName))
            safeFileName = FileNames.FromUrl(cand.Url);

        if (!cand.IsCompletedInSource || !TryGetSafeLocalDirectory(cand.FilePath, out var candidateDir))
            return false;

        try
        {
            var existingFullPath = Path.Combine(candidateDir, safeFileName);
            if (!File.Exists(existingFullPath))
                return false;
            safeDir = candidateDir;
            var fi = new FileInfo(existingFullPath);
            if (fi.Length > 0) fileLength = fi.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts selected IDM candidates into Golan <see cref="DownloadItem"/> instances, deduplicating against
    /// existing items and within the batch, sanitizing paths, and protecting secrets via DPAPI.
    /// </summary>
    public static IdmImportSummary Materialize(
        IEnumerable<IdmCandidate> selected,
        IEnumerable<DownloadItem> existingItems,
        AppSettings settings,
        Guid? targetQueueId = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(settings);

        var seenNormalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in existingItems)
        {
            if (!string.IsNullOrWhiteSpace(existing.Url))
                seenNormalized.Add(NormalizeUrlForDedupe(existing.Url));
            if (!string.IsNullOrWhiteSpace(existing.ResolvedUrl))
                seenNormalized.Add(NormalizeUrlForDedupe(existing.ResolvedUrl));
        }

        var imported = new List<DownloadItem>();
        int duplicateSkipped = 0;
        int invalidSkipped = 0;

        foreach (var cand in selected)
        {
            if (!IsSupportedImportUrl(cand.Url, out var uri) || uri == null)
            {
                invalidSkipped++;
                continue;
            }

            var norm = NormalizeUrlForDedupe(cand.Url);
            if (!seenNormalized.Add(norm))
            {
                duplicateSkipped++;
                continue;
            }

            bool completedOnDisk = TryResolveCompletedLocalFile(cand, out var completedDir, out var safeFileName, out var diskLen);
            string category = Categories.Detect(safeFileName, cand.Url);
            string saveDir = completedOnDisk ? completedDir : settings.FolderFor(category);
            long finalSize = diskLen > 0 ? diskLen : (cand.FileSize > 0 ? cand.FileSize : -1);

            var item = new DownloadItem
            {
                Url = cand.Url.Trim(),
                FileName = safeFileName,
                SaveDir = saveDir,
                Category = category,
                Referrer = cand.Referrer ?? "",
                UserAgent = cand.UserAgent ?? "",
                Description = cand.Description ?? "",
                TotalSize = finalSize,
                Downloaded = completedOnDisk && finalSize > 0 ? finalSize : 0,
                Status = completedOnDisk ? DownloadStatus.Completed : DownloadStatus.Paused,
                AddedAt = cand.AddedAt?.LocalDateTime ?? DateTime.Now,
                CompletedAt = completedOnDisk ? DateTime.Now : null,
                MaxConnections = settings.MaxConnections,
                QueueId = completedOnDisk ? null : targetQueueId,
                Segments = [],
            };

            if (!string.IsNullOrWhiteSpace(cand.Username))
                item.Login = cand.Username.Trim();
            if (!string.IsNullOrEmpty(cand.Password))
                item.Password = cand.Password; // Encrypted via Secret.Protect into ProtectedPassword
            if (!string.IsNullOrWhiteSpace(cand.Cookies))
            {
                item.Cookies = cand.Cookies.Trim(); // Encrypted via Secret.Protect into ProtectedCookies
                item.CookieHost = uri.Host;
            }

            imported.Add(item);
        }

        return new IdmImportSummary(imported, duplicateSkipped, invalidSkipped);
    }

    static string ExtractLeafFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var normalized = raw.Trim().Replace('/', '\\');
        int lastSep = normalized.LastIndexOf('\\');
        return lastSep >= 0 ? normalized[(lastSep + 1)..] : normalized;
    }

    static bool TryGetSafeLocalDirectory(string? rawDir, out string fullDir)
    {
        fullDir = "";
        if (string.IsNullOrWhiteSpace(rawDir)) return false;
        var trimmed = rawDir.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (trimmed.Contains("..", StringComparison.Ordinal))
            return false;

        try
        {
            var full = Path.GetFullPath(trimmed);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            fullDir = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string DecodeEf2Bytes(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // Try strict UTF-8 first
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Fallback to Windows-1256 (Arabic / Latin ANSI) for legacy IDM exports
            return Encoding.GetEncoding(1256).GetString(bytes);
        }
    }

    static string? ReadString(IRegistryView registry, string keyPath, string valueName)
    {
        var val = registry.GetValue(keyPath, valueName);
        return val switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s.Trim('\0', ' ', '\t', '\r', '\n'),
            _ => null,
        };
    }

    static long ReadFileSize(object? val)
    {
        return val switch
        {
            long l when l >= 0 => l,
            ulong ul when ul <= long.MaxValue => (long)ul,
            int i => i >= 0 ? i : (uint)i,
            uint ui => ui,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 => parsed,
            byte[] b when b.Length == 8 => Math.Max(-1L, BitConverter.ToInt64(b, 0)),
            byte[] b when b.Length == 4 => BitConverter.ToUInt32(b, 0),
            _ => -1L,
        };
    }

    static bool ReadIsCompleted(object? statusVal)
    {
        return statusVal switch
        {
            int i => i is 3 or 100,
            long l => l is 3 or 100,
            string s => s.Trim().Equals("3", StringComparison.OrdinalIgnoreCase)
                     || s.Trim().Equals("100", StringComparison.OrdinalIgnoreCase)
                     || s.Trim().Equals("completed", StringComparison.OrdinalIgnoreCase)
                     || s.Trim().Equals("complete", StringComparison.OrdinalIgnoreCase)
                     || s.Trim().Equals("finished", StringComparison.OrdinalIgnoreCase)
                     || s.Trim().Equals("done", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    static DateTimeOffset? ReadDate(object? dateVal)
    {
        return dateVal switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt),
            string s when DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) => parsed,
            _ => null,
        };
    }
}
