using System.Text.Json.Nodes;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using Golan.Gdm.Integration;

namespace Golan.Gdm;

public enum StartupArgKind
{
    None,
    NativeHost,
    InstallerCommand,
    PackExtension,
    CliVerb,
    MagnetUri,
    TorrentFile,
    HttpUrl,
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Loc.Init("auto");
        var settings = AppSettings.Load();
        Loc.Init(settings.Language);

        // Chrome/Edge start the native-messaging host with the full extension origin;
        // Firefox starts it with [path-to-firefox-manifest, "gdm@golan.local"].
        if (IsNativeHostInvocation(args))
            return NativeHost.Run();

        if (args.Length > 0)
        {
            var firstArg = args[0].Trim().Trim('"');
            if (string.Equals(firstArg, "--install", StringComparison.OrdinalIgnoreCase))
                return Installer.RunCliInstall(args);
            if (string.Equals(firstArg, "--uninstall", StringComparison.OrdinalIgnoreCase))
                return Installer.RunCliUninstall(args);
            if (string.Equals(firstArg, "--apply-update", StringComparison.OrdinalIgnoreCase))
                return Updater.RunCliApplyUpdate(args);
            if (string.Equals(firstArg, "--pack-extension", StringComparison.OrdinalIgnoreCase))
                return RunPackExtension(args);
            if (CliParser.IsCliInvocation(args))
                return CliRunner.RunFromMain(args);
        }

        string? magnetArg = args.Select(a => a.Trim().Trim('"')).FirstOrDefault(MagnetValidator.IsMagnetUri);
        string? torrentArg = args.Select(a => a.Trim().Trim('"')).FirstOrDefault(IsTorrentFileArg);
        string? urlArg = args.FirstOrDefault(FileNames.IsHttpUrl);

        using var mutex = new Mutex(true, @"Local\Golan.GDM.Single." + Ipc.PipeName, out bool first);
        if (!first)
        {
            JsonObject msg;
            if (magnetArg != null)
            {
                msg = new JsonObject { ["action"] = "add", ["url"] = magnetArg };
            }
            else if (torrentArg != null)
            {
                string fullTorrent = torrentArg;
                try { fullTorrent = Path.GetFullPath(torrentArg); }
                catch (ArgumentException) { /* keep raw torrent path if normalization fails */ }
                msg = new JsonObject { ["action"] = "torrent", ["path"] = fullTorrent };
            }
            else if (urlArg != null)
            {
                msg = new JsonObject { ["action"] = "add", ["url"] = urlArg };
            }
            else
            {
                msg = new JsonObject { ["action"] = "noop".Equals(args.Contains("--background") ? "noop" : "show", StringComparison.Ordinal) ? "noop" : "show" };
            }
            try { Ipc.SendAsync(msg, 3000).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Write("ipc forward failed: " + ex.Message); }
            return 0;
        }

        var app = new App
        {
            StartHidden = args.Contains("--background"),
            StartUrl = urlArg ?? magnetArg,
            StartTorrentPath = torrentArg,
        };
        app.InitializeComponent();
        return app.Run();
    }

    public static StartupArgKind ClassifyStartupArg(string[]? args)
    {
        if (args == null || args.Length == 0)
            return StartupArgKind.None;

        if (IsNativeHostInvocation(args))
            return StartupArgKind.NativeHost;

        var first = args[0].Trim().Trim('"');
        if (string.Equals(first, "--install", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "--apply-update", StringComparison.OrdinalIgnoreCase))
            return StartupArgKind.InstallerCommand;

        if (string.Equals(first, "--pack-extension", StringComparison.OrdinalIgnoreCase))
            return StartupArgKind.PackExtension;

        if (CliParser.IsCliInvocation(args))
            return StartupArgKind.CliVerb;

        foreach (var raw in args)
        {
            var a = raw.Trim().Trim('"');
            if (string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase))
                continue;
            if (MagnetValidator.IsMagnetUri(a))
                return StartupArgKind.MagnetUri;
            if (IsTorrentFileArg(a))
                return StartupArgKind.TorrentFile;
            if (FileNames.IsHttpUrl(a))
                return StartupArgKind.HttpUrl;
        }

        return StartupArgKind.None;
    }

    public static bool IsTorrentFileArg(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return false;
        var s = arg.Trim().Trim('"');
        if (FileNames.IsHttpUrl(s) || MagnetValidator.IsMagnetUri(s))
            return false;
        return s.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    public static int RunPackExtension(string[] args)
    {
        CliRunner.TryAttachParentConsole();
        try
        {
            string outDir = args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1])
                ? Path.GetFullPath(args[1].Trim().Trim('"'))
                : Path.Combine(Environment.CurrentDirectory, "dist");

            string? extDir = FindExtensionSourceDir();
            if (extDir == null)
            {
                Console.Error.WriteLine("Extension source directory not found.");
                return 2;
            }

            var res = ExtensionPackager.Build(extDir, outDir);
            Console.Out.WriteLine($"{res.ChromeEdgeZipPath}");
            Console.Out.WriteLine($"{res.FirefoxXpiPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static string? FindExtensionSourceDir()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                foreach (var rel in new[] { Path.Combine("GDM", "extension"), "extension" })
                {
                    var candidate = Path.Combine(dir.FullName, rel);
                    if (File.Exists(Path.Combine(candidate, "manifest.json")))
                        return candidate;
                }
                dir = dir.Parent;
            }
        }
        return null;
    }

    public static bool IsNativeHostInvocation(string[] args)
    {
        if (args.Length == 0) return false;
        const string legacyExtId = "dndjbncb" + "blcblidf" + "dcpcbeao" + "nnpccfcc";
        const string legacyWord = "\u0061dam";
        if (args.Any(a => a.Contains(legacyExtId, StringComparison.OrdinalIgnoreCase) ||
                          a.Contains(legacyWord, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (args.Any(a => string.Equals(a, "--native-host", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (args.Length >= 2 &&
            args[0].Trim('"').EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(args[1], BrowserIntegration.FirefoxExtensionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!args[1].StartsWith("--", StringComparison.Ordinal))
                return false;
        }

        try
        {
            var first = args[0].Trim('"');
            var fileName = Path.GetFileName(first);
            if (string.Equals(fileName, BrowserIntegration.HostName + ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, BrowserIntegration.HostName + ".firefox.json", StringComparison.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(first);
                var hostDir = Path.GetFullPath(Path.GetDirectoryName(AppPaths.NativeManifest)!);
                if (full.StartsWith(hostDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, Path.GetFullPath(BrowserIntegration.FirefoxNativeManifest), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Ignore malformed path argument
        }

        return false;
    }
}
