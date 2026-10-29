using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.Integration;

/// <summary>Registers the native-messaging host for Chromium and Firefox browsers and unpacks the bundled extension.</summary>
public static class BrowserIntegration
{
    public const string HostName = "com.golan.gdm";
    public const string ExtensionId = "fkjplbaglahdiphiodkgidkdfkbebgfh";
    public const string FirefoxExtensionId = "gdm@golan.local";

    /// <summary>
    /// Approved Chromium extension IDs (unpacked manifest key ID plus any Chrome Web Store / Edge Add-ons IDs added upon publication).
    /// </summary>
    public static IReadOnlyList<string> AllowedExtensionIds { get; } =
    [
        ExtensionId,
    ];

    public static IReadOnlyList<string> GetAllowedOrigins()
        => AllowedExtensionIds.Select(id => $"chrome-extension://{id}/").ToArray();

    public static JsonArray BuildAllowedOriginsJson()
        => new(GetAllowedOrigins().Select(o => (JsonNode)o).ToArray());

    public static string FirefoxNativeManifest => Path.Combine(AppPaths.LocalDir, "native", "com.golan.gdm.firefox.json");
    public static string FirefoxXpiPath => Path.Combine(AppPaths.LocalDir, "gdm-firefox.xpi");

    public sealed record BrowserInfo(string Name, string RegistryRoot, string? ExePath, string ExtensionsPage);

    public sealed record KnownBrowser(string NameKey, string Root, string Exe, string Page)
    {
        public string Name => Loc.T(NameKey);
    }

    static readonly KnownBrowser[] Known =
    [
        new("main.browser.chrome", @"Software\Google\Chrome", "chrome.exe", "chrome://extensions"),
        new("main.browser.edge", @"Software\Microsoft\Edge", "msedge.exe", "edge://extensions"),
        new("main.browser.firefox", @"Software\Mozilla", "firefox.exe", "about:debugging#/runtime/this-firefox"),
        new("main.browser.brave", @"Software\BraveSoftware\Brave-Browser", "brave.exe", "brave://extensions"),
        new("main.browser.chromium", @"Software\Chromium", "chromium.exe", "chrome://extensions"),
        new("main.browser.opera", @"Software\Opera Software", "opera.exe", "opera://extensions"),
        new("main.browser.vivaldi", @"Software\Vivaldi", "vivaldi.exe", "vivaldi://extensions"),
    ];

    public static IEnumerable<BrowserInfo> Browsers()
        => Known.Select(k => new BrowserInfo(k.Name, k.Root, FindExe(k.Exe), k.Page));

    public static IEnumerable<BrowserInfo> InstalledBrowsers() => Browsers().Where(b => b.ExePath != null);

    static string? FindExe(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
            if (key?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
        }
        return null;
    }

    /// <summary>Writes the Chromium and Firefox host manifests and points every supported browser at the appropriate one (current user only).</summary>
    public static void Register()
    {
        try
        {
            var chromiumManifest = new JsonObject
            {
                ["name"] = HostName,
                ["description"] = "Golan Download Manager",
                ["path"] = AppPaths.ExePath,
                ["type"] = "stdio",
                ["allowed_origins"] = BuildAllowedOriginsJson(),
            };
            Json.WriteAtomic(AppPaths.NativeManifest, chromiumManifest.ToJsonString(Json.Options));

            var firefoxManifest = new JsonObject
            {
                ["name"] = HostName,
                ["description"] = "Golan Download Manager",
                ["path"] = AppPaths.ExePath,
                ["type"] = "stdio",
                ["allowed_extensions"] = new JsonArray(FirefoxExtensionId),
            };
            Json.WriteAtomic(FirefoxNativeManifest, firefoxManifest.ToJsonString(Json.Options));

            // An isolated run (GDM_HOME / GOLAN_HOME) keeps its manifests to itself and never repoints the user's browsers.
            if (AppPaths.IsIsolated)
                return;

            foreach (var k in Known)
            {
                using var key = Registry.CurrentUser.CreateSubKey(k.Root + @"\NativeMessagingHosts\" + HostName);
                var manifestPath = string.Equals(k.Exe, "firefox.exe", StringComparison.OrdinalIgnoreCase)
                    ? FirefoxNativeManifest
                    : AppPaths.NativeManifest;
                key.SetValue(null, manifestPath);
            }
        }
        catch (Exception ex) { Log.Write("register host failed: " + ex.Message); }
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Google\Chrome\NativeMessagingHosts\" + HostName);
        return key?.GetValue(null) is string p && File.Exists(p);
    }

    /// <summary>Copies the extension embedded in GDM.exe to %LOCALAPPDATA%\Golan\GDM\extension and builds %LOCALAPPDATA%\Golan\GDM\gdm-firefox.xpi.</summary>
    public static void ExtractExtension()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            JsonObject? sourceManifest = null;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ext/")))
            {
                var rel = name[4..].Replace('\\', '/').TrimStart('/');
                if (rel.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(AppPaths.ExtensionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var src = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                src.CopyTo(ms);
                var bytes = ms.ToArray();
                if (string.Equals(rel, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    // Chrome/Edge load this folder: give them a manifest without Firefox-only keys (no warnings).
                    sourceManifest = JsonNode.Parse(bytes) as JsonObject;
                    if (sourceManifest != null)
                        bytes = Encoding.UTF8.GetBytes(ExtensionPackager.TransformManifestForUnpackedChromium(sourceManifest)
                            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                    File.WriteAllBytes(target, bytes);
            }

            // Firefox gets its own package built from the original manifest (gecko id, background.scripts).
            if (Directory.Exists(AppPaths.ExtensionDir))
                ExtensionPackager.BuildFirefoxXpi(AppPaths.ExtensionDir, FirefoxXpiPath, sourceManifest);
        }
        catch (Exception ex) { Log.Write("extract extension failed: " + ex.Message); }
    }

    /// <summary>Opens the unpacked extension folder in Explorer, re-extracting it first if it is missing. Never throws.</summary>
    public static bool OpenExtensionFolder()
    {
        try
        {
            if (!File.Exists(Path.Combine(AppPaths.ExtensionDir, "manifest.json"))) ExtractExtension();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.ExtensionDir}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("open extension folder failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Opens the browser's extensions page (default profile). Never throws.</summary>
    public static bool OpenExtensionsPage(BrowserInfo browser) => BrowserProfiles.OpenExtensionsPage(browser, null);
}

public static class WindowsStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Golan.GDM";

    /// <summary>True when the Run key already starts Golan.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
        catch { return false; }
    }

    public static void Apply(bool enabled)
    {
        if (AppPaths.IsPortable || AppPaths.IsIsolated)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{AppPaths.ExePath}\" --background");
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Write("startup toggle failed: " + ex.Message); }
    }
}
