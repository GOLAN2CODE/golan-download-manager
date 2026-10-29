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

    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A unique temp name per write: two saves (or a reader such as a CLI instance or antivirus scan) never collide.
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
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
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
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

    public static string FromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var last = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "");
            if (string.IsNullOrWhiteSpace(last)) last = uri.Host;
            return Sanitize(last);
        }
        catch { return "download"; }
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
}
