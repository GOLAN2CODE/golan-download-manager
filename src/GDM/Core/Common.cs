using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Well-known folders used by Golan Download Manager (GDM).</summary>
public static class AppPaths
{
    /// <summary>
    /// Tests (and portable runs) set GOLAN_HOME or GDM_HOME so they never touch the user's real settings and download list.
    /// </summary>
    public static string? GetIsolationHomeEnv()
    {
        var golan = Environment.GetEnvironmentVariable("GOLAN_HOME");
        if (!string.IsNullOrWhiteSpace(golan)) return golan.Trim();
        var gdm = Environment.GetEnvironmentVariable("GDM_HOME");
        if (!string.IsNullOrWhiteSpace(gdm)) return gdm.Trim();
        return null;
    }

    public static bool IsIsolated => !string.IsNullOrWhiteSpace(GetIsolationHomeEnv());

    static readonly string? Home = ResolveHome(GetIsolationHomeEnv(), AppContext.BaseDirectory, File.Exists);

    public static bool IsPortable { get; } = IsPortableMode(GetIsolationHomeEnv(), AppContext.BaseDirectory, File.Exists);

    public static string? ResolveHome(string? envHome, string exeDir, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrWhiteSpace(envHome))
            return envHome.Trim();
        if (IsPortableMode(envHome, exeDir, fileExists))
            return Path.Combine(exeDir.Trim(), "Data");
        return null;
    }

    public static bool IsPortableMode(string? envHome, string exeDir, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrWhiteSpace(envHome) || string.IsNullOrWhiteSpace(exeDir))
            return false;
        var baseDir = exeDir.Trim();
        return fileExists(Path.Combine(baseDir, "portable.txt"))
            || fileExists(Path.Combine(baseDir, "GDM.portable"))
            || fileExists(Path.Combine(baseDir, "Golan.portable"));
    }

    public static string DataDir { get; } = Home != null ? Path.Combine(Home, "Roaming") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Golan", "GDM");

    public static string LocalDir { get; } = Home != null ? Path.Combine(Home, "Local") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Golan", "GDM");

    public static string ToolsDir => Path.Combine(LocalDir, "tools");
    public static string CookiesDir => Path.Combine(LocalDir, "cookies");
    public static string ExtensionDir => Path.Combine(LocalDir, "extension");
    public static string NativeManifest => Path.Combine(LocalDir, "native", "com.golan.gdm.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DownloadsFile => Path.Combine(DataDir, "downloads.json");
    public static string LogFile => Path.Combine(LocalDir, "gdm.log");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GDM.exe");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="flushToDisk">For the files that must survive a power cut (the download list, the settings).
    /// Left off for files rewritten many times a minute, where forcing the disk each time would only slow the download.</param>
    public static void WriteAtomic(string path, string content, bool flushToDisk = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A unique temp name per write: two saves (or a reader such as a CLI instance or antivirus scan) never collide.
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        // Flushed to the disk before the swap: after a power cut the new name must never point at an empty file.
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk);
        }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                // The target is briefly open elsewhere; try again shortly.
                Thread.Sleep(50 * (attempt + 1));
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { /* best effort */ }
                throw;
            }
        }
    }
}

public static class Log
{
    static readonly object Gate = new();
    static readonly Regex UserInfoPrefixRx = new(
        @"^([a-zA-Z][a-zA-Z0-9+.-]*://)[^/?#]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Strips embedded credentials (<c>user:password@</c> or <c>user@</c>) from a URL before logging.</summary>
    public static string StripUserInfo(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var trimmed = url.Trim();
        if (!trimmed.Contains('@', StringComparison.Ordinal)) return trimmed;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var u))
        {
            if (!string.IsNullOrEmpty(u.UserInfo))
                return new UriBuilder(u) { UserName = "", Password = "" }.Uri.AbsoluteUri;
            return UserInfoPrefixRx.Replace(trimmed, "$1");
        }
        return UserInfoPrefixRx.Replace(trimmed, "$1");
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LocalDir);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}

/// <summary>Localized formatting helpers for sizes, speeds and times.</summary>
public static class Fmt
{
    static string[] Units => [
        Loc.T("core.unit.b"),
        Loc.T("core.unit.kb"),
        Loc.T("core.unit.mb"),
        Loc.T("core.unit.gb"),
        Loc.T("core.unit.tb"),
    ];

    public static string Size(long bytes)
    {
        if (bytes < 0) return Loc.T("core.unit.unknown");
        var units = Units;
        if (bytes < 1024) return $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[0]}";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {units[i]}";
    }

    public static string Speed(double bytesPerSecond)
        => bytesPerSecond <= 0 ? "" : Size((long)bytesPerSecond) + Loc.T("core.unit.per_sec");

    public static string Percent(double pct, string format = "0.0")
        => $"{pct.ToString(format, CultureInfo.InvariantCulture)}%";

    public static string Ratio(double ratio)
        => ratio.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Number(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Int(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    public static string Eta(TimeSpan? t)
    {
        if (t is null || t.Value.TotalSeconds < 0 || t.Value.TotalDays > 30) return "";
        var s = t.Value;
        if (s.TotalHours >= 1) return Loc.T("core.unit.eta_hm", (int)s.TotalHours, s.Minutes);
        if (s.TotalMinutes >= 1) return Loc.T("core.unit.eta_ms", s.Minutes, s.Seconds);
        return Loc.T("core.unit.eta_s", Math.Max(1, s.Seconds));
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

public static class TextDir
{
    /// <summary>True when the first letter is Arabic/Hebrew, so the text should flow right-to-left.</summary>
    public static bool IsRtl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Loc.IsRtl;
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ') return true;
            if (char.IsLetter(c)) return false;
        }
        return Loc.IsRtl;
    }
}

public static class FileNames
{
    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    static readonly HashSet<string> GenericBrowserPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "download", "downloads", "unknown", "unnamed", "untitled",
        "file", "files", "content", "uc", "open", "view", "export",
        "attachment", "blob", "index", "default",
    };

    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";
        var raw = name.Trim().Trim('"', '\'').Trim();
        if (raw.Length >= 2 && char.IsAsciiLetter(raw[0]) && raw[1] == ':')
            raw = raw[2..];
        if (raw.Contains("..", StringComparison.Ordinal) || raw.Contains('/') || raw.Contains('\\'))
        {
            var leaf = raw.TrimEnd('/', '\\');
            int lastSep = leaf.LastIndexOfAny(['/', '\\']);
            raw = lastSep >= 0 ? leaf[(lastSep + 1)..] : leaf;
            if (raw is "." or "..") raw = "";
        }
        if (string.IsNullOrWhiteSpace(raw)) return "download";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
        {
            bool bad = invalid.Contains(c)
                       || char.IsControl(c)
                       || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;
            sb.Append(bad ? '_' : c);
        }
        var result = sb.ToString().Trim(' ', '.');
        if (result.Length > 180)
        {
            var ext = Path.GetExtension(result);
            if (ext.Length > 40) ext = ext[..40].TrimEnd(' ', '.');
            var basePart = result[..(180 - ext.Length)].TrimEnd(' ', '.');
            if (basePart.Length == 0) basePart = "download";
            result = basePart + ext;
        }
        var stem = Path.GetFileNameWithoutExtension(result).TrimEnd(' ', '.');
        var finalExt = Path.GetExtension(result);
        if (stem.Length > 0 && finalExt.Length > 0)
            result = stem + finalExt;
        var firstDotToken = stem.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem) || ReservedNames.Contains(firstDotToken))
            result = "_" + result;
        return result.Length == 0 ? "download" : result;
    }

    public static string UnescapePercentEncoded(string value, Encoding? encoding = null)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('%', StringComparison.Ordinal))
            return value;

        var enc = encoding ?? Encoding.UTF8;
        var sb = new StringBuilder(value.Length);
        var bytes = new List<byte>(16);
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] == '%' && i + 2 < value.Length && TryHexVal(value[i + 1], out int hi) && TryHexVal(value[i + 2], out int lo))
            {
                bytes.Clear();
                bytes.Add((byte)((hi << 4) | lo));
                i += 3;
                while (i + 2 < value.Length && value[i] == '%' && TryHexVal(value[i + 1], out hi) && TryHexVal(value[i + 2], out lo))
                {
                    bytes.Add((byte)((hi << 4) | lo));
                    i += 3;
                }
                sb.Append(enc.GetString(bytes.ToArray()));
            }
            else
            {
                sb.Append(value[i++]);
            }
        }
        return sb.ToString();
    }

    static bool TryHexVal(char c, out int val)
    {
        if (c is >= '0' and <= '9') { val = c - '0'; return true; }
        if (c is >= 'A' and <= 'F') { val = c - 'A' + 10; return true; }
        if (c is >= 'a' and <= 'f') { val = c - 'a' + 10; return true; }
        val = 0;
        return false;
    }

    public static string ExtensionForMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return ".txt";
        var m = mime.Split(';')[0].Trim().ToLowerInvariant();
        return m switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "video/mp2t" => ".ts",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            "application/pdf" => ".pdf",
            "application/zip" or "application/x-zip-compressed" => ".zip",
            "application/json" => ".json",
            "application/xml" or "text/xml" => ".xml",
            "text/html" => ".html",
            "text/csv" => ".csv",
            "text/plain" => ".txt",
            "application/octet-stream" => ".bin",
            _ => ".bin",
        };
    }

    public static string FromUrl(string url)
    {
        if (IsDataUrl(url))
        {
            var trimmed = url.Trim();
            int comma = trimmed.IndexOf(',');
            var header = comma > 5 ? trimmed[5..comma] : "";
            var mime = header.Split(';')[0].Trim();
            if (mime.Contains('=')) mime = "";
            return Sanitize("data" + (string.IsNullOrEmpty(mime) ? ".txt" : ExtensionForMime(mime)));
        }
        try
        {
            var uri = new Uri(url);
            var rawSeg = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";
            var last = UnescapePercentEncoded(rawSeg, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(last)) last = uri.Host;
            return Sanitize(last);
        }
        catch (Exception)
        {
            /* fallback for malformed URL strings */
            return "download";
        }
    }

    /// <summary>True when a browser-supplied filename is specific enough to lock over server Content-Disposition.</summary>
    public static bool ShouldLockBrowserFileName(string? rawFileName, string? url = null)
    {
        if (string.IsNullOrWhiteSpace(rawFileName)) return false;
        var leaf = Path.GetFileName(rawFileName.Replace('/', '\\').Trim()).Trim('"', '\'', ' ');
        if (string.IsNullOrWhiteSpace(leaf)) return false;
        var ext = Path.GetExtension(leaf);
        if (ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".part", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".opdownload", StringComparison.OrdinalIgnoreCase))
            return false;
        if (ext.Length > 1) return true;

        var stem = StripBrowserCopySuffix(leaf);
        if (stem.Length == 0 || GenericBrowserPlaceholders.Contains(stem)) return false;
        if (!string.IsNullOrWhiteSpace(url) && string.Equals(stem, FromUrl(url), StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    static string StripBrowserCopySuffix(string name)
    {
        var s = name.Trim();
        if (!s.EndsWith(')')) return s;
        int open = s.LastIndexOf('(');
        if (open <= 0 || open >= s.Length - 2) return s;
        for (int i = open + 1; i < s.Length - 1; i++)
            if (!char.IsAsciiDigit(s[i])) return s;
        return s[..open].TrimEnd();
    }

    /// <summary>Returns a path that does not exist yet: "name (1).ext", "name (2).ext"...</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".gdm")) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".gdm")) return candidate;
        }
    }

    public static bool IsHttpUrl(string? s)
        => Uri.TryCreate(s?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public static bool IsFtpUrl(string? s)
        => Uri.TryCreate(s?.Trim(), UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeFtp || string.Equals(u.Scheme, "ftps", StringComparison.OrdinalIgnoreCase))
           && !string.IsNullOrWhiteSpace(u.Host);

    public static bool IsDataUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        return t.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && t.IndexOf(',') >= 5;
    }

    public static bool IsDownloadableUrl(string? s)
        => IsHttpUrl(s) || IsFtpUrl(s) || IsDataUrl(s);

    /// <summary>True for network download schemes accepted on the command line (http, https, ftp, ftps — never data:).</summary>
    public static bool IsNetworkDownloadUrl(string? s)
        => IsHttpUrl(s) || IsFtpUrl(s);

    /// <summary>Safe single-line display form of a URL so multi-megabyte data: URLs never freeze WPF layout or tooltips.</summary>
    public static string FormatDisplayUrl(string? url, int maxChars = 240)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var s = url.Trim();
        if (IsDataUrl(s))
        {
            int comma = s.IndexOf(',');
            var header = comma > 0 ? s[..Math.Min(comma, 80)] : "data:";
            return s.Length <= maxChars ? s : $"{header},… ({Fmt.Size(Math.Max(0, s.Length - comma - 1))})";
        }
        return s.Length <= maxChars ? s : s[..maxChars] + "…";
    }

    /// <summary>True when a clipboard string is a downloadable URL/magnet/host/stream that clipboard monitoring should capture.</summary>
    public static bool ShouldCaptureClipboardUrl(string? text, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Contains('\n') || t.Contains('\r')) return false;
        if (Torrent.MagnetValidator.IsMagnetUri(t)) return true;

        if (!IsHttpUrl(t) && !IsFtpUrl(t)) return false;
        if (settings.IsException(t)) return false;

        var u = new Uri(t);
        if (StreamDownloader.IsDirectStreamUrl(t)) return true;
        if (Resolvers.HostResolvers.All.Any(r => r.CanHandle(u))) return true;

        var ext = Path.GetExtension(u.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return ext.Length > 0 && settings.ExtensionList.Contains(ext);
    }
}

/// <summary>Maps raw .NET / OS / network exceptions into plain-word localized messages telling the user what happened and what to do next.</summary>
public static class FriendlyError
{
    public static string Format(Exception? ex)
    {
        if (ex == null) return Loc.T("core.err.generic");
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            ex = agg.Flatten().InnerExceptions[0];

        if (ex is DownloadException dlEx && !string.IsNullOrWhiteSpace(dlEx.Message))
            return dlEx.Message;

        if (ex is TimeoutException or OperationCanceledException)
            return Loc.T("core.err.timeout");

        if (FindInner<System.Net.Sockets.SocketException>(ex) is { } sockEx)
        {
            if (sockEx.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                return Loc.T("core.err.port_in_use");
            if (sockEx.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
                return Loc.T("core.err.access_denied");
            return Loc.T("core.err.network");
        }

        if (ex is System.Net.Http.HttpRequestException || FindInner<System.Net.Http.HttpRequestException>(ex) != null)
            return Loc.T("core.err.network");

        if (ex is UnauthorizedAccessException or System.Security.SecurityException)
            return Loc.T("core.err.access_denied");

        if (ex is PathTooLongException or DirectoryNotFoundException or FileNotFoundException or DriveNotFoundException or NotSupportedException or ArgumentException)
            return Loc.T("core.err.invalid_path");

        if (ex is IOException ioEx)
        {
            int win32 = ioEx.HResult & 0xFFFF;
            if (win32 is 0x70 or 0x27) // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
                return Loc.T("core.err.disk_full");
            if (win32 is 0x20 or 0x21) // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
                return Loc.T("core.err.file_in_use");
            if (ioEx.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || ioEx.Message.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase))
                return Loc.T("core.err.port_in_use");
            return Loc.T("core.err.invalid_path");
        }

        var typeName = ex.GetType().FullName ?? "";
        if (typeName.StartsWith("MonoTorrent.", StringComparison.Ordinal))
            return Loc.T("core.err.bad_torrent");

        if (ex is InvalidOperationException inv && !string.IsNullOrWhiteSpace(inv.Message))
        {
            // Keep app-generated localized messages while filtering raw framework English diagnostics.
            if (Loc.IsRtl ? TextDir.IsRtl(inv.Message) : !LooksLikeFrameworkException(inv.Message))
                return inv.Message;
        }

        return Loc.T("core.err.generic");
    }

    static T? FindInner<T>(Exception? ex) where T : Exception
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
            if (cur is T match) return match;
        return null;
    }

    static bool LooksLikeFrameworkException(string msg)
        => msg.Contains("System.", StringComparison.Ordinal)
           || msg.Contains("Object reference", StringComparison.OrdinalIgnoreCase)
           || msg.Contains("StackTrace", StringComparison.OrdinalIgnoreCase)
           || msg.Contains("HRESULT", StringComparison.OrdinalIgnoreCase);
}
