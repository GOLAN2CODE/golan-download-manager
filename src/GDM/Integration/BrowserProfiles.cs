using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;

namespace Golan.Gdm.Integration;

/// <summary>One browser profile (Chrome "Person"), as listed in the browser's Local State.</summary>
public sealed record BrowserProfile(string Directory, string DisplayName, string Account, bool IsLastUsed)
{
    /// <summary>"Work (someone@example.com)" — the account part only when the profile is signed in.</summary>
    public string Label => Account.Length > 0 && !string.Equals(Account, DisplayName, StringComparison.OrdinalIgnoreCase)
        ? $"{DisplayName} ({Account})" : DisplayName;
}

/// <summary>What GDM can tell about its extension inside one profile, from the profile's own files (read-only).</summary>
public sealed record ProfileExtensionState(bool DeveloperMode, bool Installed, bool Disabled);

/// <summary>Extended read-only profile state including the loaded extension version from Preferences / Secure Preferences.</summary>
public sealed record DetailedExtensionState(bool DeveloperMode, bool Installed, bool Disabled, string? Version)
{
    /// <summary>
    /// The extension is loaded unpacked from a folder other than Golan's own (an old install location, a copy that
    /// was moved or deleted). It stops working when that folder goes away, so it has to be loaded again from here.
    /// </summary>
    public bool LoadedFromOtherFolder { get; init; }
}

/// <summary>
/// Reads Chromium-family profile data (Chrome, Edge, Brave, Chromium, Vivaldi, Opera, Yandex, Comet) so the guide and auto-installer
/// can inspect profiles, Developer mode, and whether Golan's extension is loaded and enabled. Never writes browser files.
/// </summary>
public static class BrowserProfiles
{
    const long MaxJsonBytes = 64L * 1024 * 1024;

    /// <summary>The "User Data" folder for a browser executable name, or null for non-Chromium browsers.</summary>
    public static string? UserDataDir(string exeNameOrPath)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exeName = Path.GetFileName(exeNameOrPath);
        // Beta, Dev and Canary are separate browsers with their own profiles: a chrome.exe under "Chrome Beta"
        // keeps its data in "Google\Chrome Beta\User Data". Reading the stable folder for it shows the wrong profiles.
        var channel = ChannelOf(exeNameOrPath);
        return exeName.ToLowerInvariant() switch
        {
            "chrome.exe" => Path.Combine(local, "Google", "Chrome" + channel, "User Data"),
            "msedge.exe" => Path.Combine(local, "Microsoft", "Edge" + channel, "User Data"),
            "brave.exe" => Path.Combine(local, "BraveSoftware", "Brave-Browser" + channel.Replace(' ', '-'), "User Data"),
            "chromium.exe" => Path.Combine(local, "Chromium", "User Data"),
            "vivaldi.exe" => Path.Combine(local, "Vivaldi", "User Data"),
            "opera.exe" => Path.Combine(roaming, "Opera Software", "Opera Stable"),
            "browser.exe" => Path.Combine(local, "Yandex", "YandexBrowser", "User Data"),
            "comet.exe" => Path.Combine(local, "Perplexity", "Comet", "User Data"),
            _ => null,
        };
    }

    /// <summary>" Beta", " Dev", " SxS" (Canary) or "" (stable, or just an exe name), taken from the install folder's name.</summary>
    internal static string ChannelOf(string exePath)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(exePath) ?? "") ?? "");
        foreach (var suffix in new[] { " Beta", " Dev", " SxS", " Nightly", "-Beta", "-Dev", "-Nightly" })
            if (folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return " " + suffix[1..];
        return "";
    }

    /// <summary>
    /// Profiles from "Local State" (profile.info_cache), last used first, then by name. Opera (single profile, no info_cache)
    /// and damaged files fall back to the "Default" folder when it exists.
    /// </summary>
    public static IReadOnlyList<BrowserProfile> ReadProfiles(string userDataDir)
    {
        var list = new List<BrowserProfile>();
        var state = ReadJson(Path.Combine(userDataDir, "Local State")) as JsonObject;
        var profile = state?["profile"] as JsonObject;
        var lastUsed = Str(profile?["last_used"]);
        if (profile?["info_cache"] is JsonObject cache)
        {
            foreach (var (dir, node) in cache)
            {
                if (node is not JsonObject info || dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                if (info["is_ephemeral"] is JsonValue eph && eph.TryGetValue<bool>(out var e) && e) continue;
                // The browser's own guest and profile-picker folders are not places a person browses in.
                if (dir.Equals("System Profile", StringComparison.OrdinalIgnoreCase) || dir.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Str(info["name"]);
                if (name.Length == 0) name = Str(info["gaia_name"]);
                if (name.Length == 0) name = dir;
                list.Add(new BrowserProfile(dir, name, Str(info["user_name"]), string.Equals(dir, lastUsed, StringComparison.OrdinalIgnoreCase)));
            }
        }
        if (list.Count == 0)
        {
            if (File.Exists(Path.Combine(userDataDir, "Default", "Preferences")))
                list.Add(new BrowserProfile("Default", "Default", "", true));
            else if (File.Exists(Path.Combine(userDataDir, "Preferences")))
                list.Add(new BrowserProfile("", Path.GetFileName(userDataDir), "", true)); // Opera keeps its only profile at the root
        }
        return list.OrderByDescending(p => p.IsLastUsed).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Developer mode and our extension's presence in one profile folder (Preferences + Secure Preferences).</summary>
    public static ProfileExtensionState ReadState(string profilePath, string extensionId, string extensionDir)
    {
        var d = ReadDetailedState(profilePath, extensionId, extensionDir);
        return new ProfileExtensionState(d.DeveloperMode, d.Installed, d.Disabled);
    }

    /// <summary>Developer mode, extension presence, disabled flag, and loaded manifest version in one profile folder (read-only).</summary>
    public static DetailedExtensionState ReadDetailedState(string profilePath, string extensionId, string extensionDir)
    {
        var prefs = ReadJson(Path.Combine(profilePath, "Preferences")) as JsonObject;
        var secure = ReadJson(Path.Combine(profilePath, "Secure Preferences")) as JsonObject;
        bool devMode = Bool(prefs?["extensions"]?["ui"]?["developer_mode"]) || Bool(secure?["extensions"]?["ui"]?["developer_mode"]);

        bool installed = false, disabled = false, otherFolder = false, exactSeen = false;
        string? version = null;
        var wanted = NormalizeDir(extensionDir);
        foreach (var settings in new[] { prefs?["extensions"]?["settings"], secure?["extensions"]?["settings"] })
        {
            if (settings is not JsonObject all) continue;
            foreach (var (id, node) in all)
            {
                if (node is not JsonObject ext) continue;
                var path = Str(ext["path"]);
                bool ours = string.Equals(id, extensionId, StringComparison.Ordinal)
                            || (wanted.Length > 0 && path.Length > 0 && Path.IsPathRooted(path) && NormalizeDir(path) == wanted);
                if (!ours) continue;
                installed = true;
                // The entry under our own ID decides, and the later file (Secure Preferences) is the one the browser
                // trusts: a stale copy of the entry must not leave "turned off" or "other folder" stuck on.
                bool exact = string.Equals(id, extensionId, StringComparison.Ordinal);
                if (exactSeen && !exact) continue;
                exactSeen |= exact;
                // Only an unpacked extension has an absolute path here (a store install keeps a relative one).
                otherFolder = wanted.Length > 0 && path.Length > 0 && Path.IsPathRooted(path) && NormalizeDir(path) != wanted;
                // state 0 / non-empty disable_reasons = turned off in chrome://extensions
                disabled = (ext["state"] is JsonValue st && st.TryGetValue<int>(out var s) && s == 0)
                    || (ext["disable_reasons"] is JsonArray reasons && reasons.Count > 0)
                    || (ext["disable_reasons"] is JsonValue dr && dr.TryGetValue<int>(out var r) && r != 0);
                var mfVer = Str(ext["manifest"]?["version"]);
                if (mfVer.Length == 0) mfVer = Str(ext["version"]);
                if (mfVer.Length > 0) version = mfVer;
            }
        }
        // Chromium stores no manifest in the profile for an unpacked ("Load unpacked") extension: it reads the folder
        // again on every start. So when the profile gives no version, the folder's own manifest is the version loaded.
        if (installed && version == null)
            version = ReadManifestVersion(extensionDir);
        return new DetailedExtensionState(devMode, installed, disabled, version) { LoadedFromOtherFolder = otherFolder };
    }

    static string? ReadManifestVersion(string extensionDir)
    {
        try
        {
            var manifest = ReadJson(Path.Combine(extensionDir, "manifest.json")) as JsonObject;
            var v = Str(manifest?["version"]);
            return v.Length > 0 ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* no readable manifest there */ return null; }
    }

    /// <summary>Resolves the active or requested profile folder path for a Chromium browser, or null for non-Chromium browsers.</summary>
    public static string? ResolveProfilePath(BrowserIntegration.BrowserInfo browser, string? profileDirectory)
    {
        var exeName = string.IsNullOrWhiteSpace(browser.ExePath) ? "" : Path.GetFileName(browser.ExePath);
        if (exeName.Length == 0 || UserDataDir(browser.ExePath!) is not { } userData) return null;
        if (profileDirectory != null)
            return profileDirectory.Length == 0 ? userData : Path.Combine(userData, profileDirectory);
        var profiles = ReadProfiles(userData);
        if (profiles.Count > 0)
            return profiles[0].Directory.Length == 0 ? userData : Path.Combine(userData, profiles[0].Directory);
        if (File.Exists(Path.Combine(userData, "Preferences"))) return userData;
        return Path.Combine(userData, "Default");
    }

    /// <summary>Arguments to open the browser's extensions page in a given profile ("" = the browser's own default).</summary>
    public static IReadOnlyList<string> LaunchArguments(string? profileDirectory, string page)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(profileDirectory)) args.Add("--profile-directory=" + profileDirectory);
        args.Add(page);
        return args;
    }

    /// <summary>Opens the extensions page of <paramref name="browser"/> in <paramref name="profileDirectory"/>. Returns false (logged) on failure.</summary>
    public static bool OpenExtensionsPage(BrowserIntegration.BrowserInfo browser, string? profileDirectory)
    {
        if (browser.ExePath is not { } exe || !File.Exists(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in LaunchArguments(profileDirectory, browser.ExtensionsPage)) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("open extensions page failed: " + ex.Message);
            return false;
        }
    }

    static JsonNode? ReadJson(string path)
    {
        // A running browser replaces these files by swapping in a new copy; for an instant the open fails or the
        // text is half-written. A couple of short retries keep a profile from vanishing off the list for that.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                // The browser keeps these files open while it runs: share everything, read once, never lock.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > MaxJsonBytes) return null;
                return JsonNode.Parse(fs, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (attempt >= 2) return null;
                Thread.Sleep(60);
            }
        }
    }

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
    static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static string NormalizeDir(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}

/// <summary>
/// When the browser extension last talked to GDM, per browser (the native host reports which browser started it).
/// Lets the guide confirm "connected" the moment the user finishes loading the extension.
/// </summary>
public static class ExtensionContact
{
    static readonly ConcurrentDictionary<string, DateTime> LastByBrowser = new(StringComparer.OrdinalIgnoreCase);

    public static void Record(string? browserExe, DateTime? utcNow = null)
    {
        var at = utcNow ?? DateTime.UtcNow;
        LastByBrowser[string.IsNullOrWhiteSpace(browserExe) ? "*" : browserExe.Trim()] = at;
        LastByBrowser["*"] = at;
    }

    /// <summary>Last contact from that browser (exe name such as "chrome.exe"), or from any browser when null.</summary>
    public static DateTime? Last(string? browserExe = null)
        => LastByBrowser.TryGetValue(string.IsNullOrWhiteSpace(browserExe) ? "*" : browserExe, out var t) ? t : null;

    public static void Reset() => LastByBrowser.Clear();

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    static readonly string[] BrowserExes = ["chrome.exe", "msedge.exe", "brave.exe", "chromium.exe", "vivaldi.exe", "opera.exe", "browser.exe", "comet.exe", "firefox.exe"];

    /// <summary>
    /// The browser that launched this native host: Chrome on Windows starts hosts through cmd.exe, so walk up a few parents.
    /// Returns the exe name ("chrome.exe") or "" when unknown.
    /// </summary>
    public static string DetectLaunchingBrowser()
    {
        Process? current = null;
        try
        {
            current = Process.GetCurrentProcess();
            for (int depth = 0; depth < 4; depth++)
            {
                var info = new ProcessBasicInformation();
                if (NtQueryInformationProcess(current.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) break;
                int parentId = info.InheritedFromUniqueProcessId.ToInt32();
                if (parentId <= 0) break;
                Process parent;
                try { parent = Process.GetProcessById(parentId); }
                catch (ArgumentException) { /* parent process already exited */ break; }
                string name;
                try
                {
                    name = parent.ProcessName + ".exe";
                }
                catch
                {
                    parent.Dispose();
                    throw;
                }
                if (BrowserExes.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    parent.Dispose();
                    return name.ToLowerInvariant();
                }
                current.Dispose();
                current = parent;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Write("browser detection failed: " + ex.Message);
        }
        finally
        {
            current?.Dispose();
        }
        return "";
    }
}
