using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Golan.Gdm.Core;

// Usage: EngineTest <servedDir> <outDir> <port>
// Never touch the real %APPDATA%\Golan\GDM: every AppPaths.* points into a throw-away folder.
var gdmHome = Path.Combine(Path.GetTempPath(), "gdm-test-home-" + Environment.ProcessId);
Environment.SetEnvironmentVariable("GDM_HOME", gdmHome);
if (!AppPaths.DataDir.StartsWith(gdmHome, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("FAIL test isolation: AppPaths still points at the real profile"); return 99; }
if (args.Length > 0 && args[0] == "--internet") // real downloads, run on demand only
    return await Tests_Internet.RunAsync(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "gdm-internet"), args.Skip(2).ToArray());
var served = args[0]; var outDir = args[1]; int port = int.Parse(args[2]);
string Base = $"http://127.0.0.1:{port}";
Directory.CreateDirectory(outDir);
foreach (var f in Directory.GetFiles(outDir)) File.Delete(f);
if (args.Any(a => string.Equals(a, "--sec-only", StringComparison.OrdinalIgnoreCase)))
    return await Tests_Security.RunAsync(Base, served, outDir, port);

var bigPath = Path.Combine(served, "big.bin");
if (!File.Exists(bigPath) || new FileInfo(bigPath).Length != 40 * 1024 * 1024)
{
    // Generated, not kept in the repository (same bytes as the UI test makes).
    var rng = new Random(42);
    var buf = new byte[40 * 1024 * 1024];
    rng.NextBytes(buf);
    File.WriteAllBytes(bigPath, buf);
}
string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
var expected = Hash(bigPath);
var settings = new AppSettings { ProxyMode = "none" };
settings.Normalize();
HttpDownloader.Configure(settings);
var limiter = new SpeedLimiter();
int fails = 0, passes = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
    if (ok) passes++; else fails++;
}
async Task<string> Get(string path) => await HttpDownloader.Client.GetStringAsync(Base + path);
DownloadItem NewItem(string url, string? name = null, int conns = 8) => new()
{
    Url = url, SaveDir = outDir, MaxConnections = conns, FileName = name ?? "", NameLocked = name != null,
};

void RunQChecks()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "GDM", "src", "GDM")))
        dir = dir.Parent;
    string repoRoot = dir?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    string srcRoot = Path.Combine(repoRoot, "GDM", "src", "GDM");
    string i18nDir = Path.Combine(srcRoot, "i18n");
    string coreDir = Path.Combine(srcRoot, "Core");
    string integDir = Path.Combine(srcRoot, "Integration");
    string uiDir = Path.Combine(srcRoot, "UI");

    string[] newI18nAreas = ["common", "core", "main", "windows", "system", "media", "torrent", "integrity", "remote", "remote-tls", "integration", "license", "guide"];
    string[] oldI18nAreas = ["v1", "v2", "v3", "v4", "v5", "vb"];
    bool allNewI18nExist = newI18nAreas.All(a => File.Exists(Path.Combine(i18nDir, $"{a}.ar.json")) && File.Exists(Path.Combine(i18nDir, $"{a}.en.json")));
    bool noOldI18nExist = oldI18nAreas.All(a => !File.Exists(Path.Combine(i18nDir, $"{a}.ar.json")) && !File.Exists(Path.Combine(i18nDir, $"{a}.en.json")));
    var arMerged = new Dictionary<string, string>(StringComparer.Ordinal);
    var enMerged = new Dictionary<string, string>(StringComparer.Ordinal);
    if (allNewI18nExist)
    {
        foreach (var a in newI18nAreas)
        {
            var arMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, $"{a}.ar.json"))) ?? [];
            var enMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, $"{a}.en.json"))) ?? [];
            foreach (var kv in arMap) arMerged[kv.Key] = kv.Value;
            foreach (var kv in enMap) enMerged[kv.Key] = kv.Value;
        }
    }
    Check("Q-01: Domain i18n tables replace v1..v5/vb and preserve all 1462 AR/EN keys",
        allNewI18nExist && noOldI18nExist && arMerged.Count >= 1462 && arMerged.Count == enMerged.Count && arMerged.Keys.All(enMerged.ContainsKey),
        $"newExist={allNewI18nExist}, noOld={noOldI18nExist}, ar={arMerged.Count}, en={enMerged.Count}");

    string[] expectedCoreFiles =
    [
        "Settings.Power.cs", "Settings.Update.cs", "Settings.Media.cs",
        "Settings.Torrent.cs", "Settings.Integrity.cs", "Settings.Remote.cs",
        "DownloadItem.Integrity.cs", "DownloadItem.Media.cs"
    ];
    string[] forbiddenCoreFiles =
    [
        "Settings.V1.cs", "Settings.V2.cs", "Settings.V3.cs", "Settings.V4.cs", "Settings.V5.cs",
        "DownloadItem.V4.cs"
    ];
    bool coreRenamedOk = expectedCoreFiles.All(f => File.Exists(Path.Combine(coreDir, f)))
                      && forbiddenCoreFiles.All(f => !File.Exists(Path.Combine(coreDir, f)));
    Check("Q-02: Domain settings & DownloadItem partial files replace V1..V5 files",
        coreRenamedOk);

    string[] expectedIntegCore = ["Installer.Core.cs", "Updater.Core.cs", "Cli.Core.cs"];
    bool integCoreExist = expectedIntegCore.All(f => File.Exists(Path.Combine(integDir, f)));
    bool noMisplacedTypesInSettings = Directory.GetFiles(coreDir, "Settings*.cs")
        .All(f =>
        {
            var txt = File.ReadAllText(f);
            return !txt.Contains("class Installer") && !txt.Contains("class Updater")
                && !txt.Contains("class CliParser") && !txt.Contains("class CliActions")
                && !txt.Contains("class DownloadItem");
        });
    Check("Q-03: Pure logic (Installer, Updater, CliParser, CliActions) extracted to Integration/*.Core.cs",
        integCoreExist && noMisplacedTypesInSettings,
        $"integCoreExist={integCoreExist}, noMisplacedInSettings={noMisplacedTypesInSettings}");

    const string v1SampleJson = """
    {
      "SchemaVersion": 1,
      "Language": "en",
      "DownloadRoot": "C:\\CustomDownloads",
      "TempDir": "C:\\CustomTemp",
      "UseCategoryFolders": false,
      "MaxConnections": 16,
      "MaxSimultaneous": 5,
      "SchedulerEnabled": true,
      "ScheduleStart": "01:30",
      "ScheduleStop": "07:00",
      "AfterQueue": "shutdown",
      "SpeedLimitEnabled": true,
      "SpeedLimitKB": 2048,
      "StartWithWindows": true,
      "CloseToTray": false,
      "ShowCompleteDialog": false,
      "ShowProgressWindow": false,
      "ShowDropTarget": true,
      "DropTargetLeft": 120.5,
      "DropTargetTop": 240.25,
      "ClipboardMonitor": false,
      "DarkMode": false,
      "SmallToolbar": true,
      "HideCategories": true,
      "DuplicateAction": "overwrite",
      "SoundsEnabled": true,
      "VirusScanEnabled": true,
      "VirusScanner": "C:\\Scanner\\scan.exe",
      "VirusArgs": "--scan [FILE]",
      "UseServerFileTime": true,
      "ProxyMode": "manual",
      "ProxyType": "socks5",
      "ProxyHost": "127.0.0.1",
      "ProxyPort": 1080,
      "ProxyBypass": "localhost;*.local",
      "ManualUserAgent": "GdmTest/1.0",
      "SortBy": "FileName",
      "SortDescending": false
    }
    """;
    var loadedV1 = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(v1SampleJson, Json.Options)!;
    loadedV1.Normalize();
    bool v1CompatOk = loadedV1.SchemaVersion == 2
                   && loadedV1.Language == "en"
                   && loadedV1.DownloadRoot == @"C:\CustomDownloads"
                   && loadedV1.TempDir == @"C:\CustomTemp"
                   && !loadedV1.UseCategoryFolders
                   && loadedV1.MaxConnections == 16
                   && loadedV1.MaxSimultaneous == null
                   && loadedV1.MainQueue.MaxSimultaneous == 5
                   && loadedV1.MainQueue.ScheduleEnabled
                   && loadedV1.MainQueue.StartTime == "01:30"
                   && loadedV1.MainQueue.StopEnabled
                   && loadedV1.MainQueue.StopTime == "07:00"
                   && loadedV1.MainQueue.ShutdownWhenDone
                   && loadedV1.SpeedLimitEnabled
                   && loadedV1.SpeedLimitKB == 2048
                   && loadedV1.StartWithWindows
                   && !loadedV1.CloseToTray
                   && !loadedV1.ShowCompleteDialog
                   && !loadedV1.ShowProgressWindow
                   && loadedV1.ShowDropTarget
                   && loadedV1.DropTargetLeft == 120.5
                   && loadedV1.DropTargetTop == 240.25
                   && !loadedV1.ClipboardMonitor
                   && !loadedV1.DarkMode
                   && loadedV1.SmallToolbar
                   && loadedV1.HideCategories
                   && loadedV1.DuplicateAction == "overwrite"
                   && loadedV1.SoundsEnabled
                   && loadedV1.VirusScanEnabled
                   && loadedV1.VirusScanner == @"C:\Scanner\\scan.exe".Replace(@"\\", @"\")
                   && loadedV1.VirusArgs == "--scan [FILE]"
                   && loadedV1.UseServerFileTime
                   && loadedV1.ProxyMode == "manual"
                   && loadedV1.ProxyType == "socks5"
                   && loadedV1.ProxyHost == "127.0.0.1"
                   && loadedV1.ProxyPort == 1080
                   && loadedV1.SortBy == "FileName"
                   && !loadedV1.SortDescending
                   && loadedV1.KeepSystemAwake
                   && !loadedV1.RemoteEnabled;
    Check("Q-04: Legacy v1-style settings.json sample loads with all values intact",
        v1CompatOk);

    int optionsPartials = Directory.GetFiles(uiDir, "OptionsWindow*.cs").Length;
    int mainPartials = Directory.GetFiles(uiDir, "MainWindow*.cs").Length;
    bool editorConfigExists = File.Exists(Path.Combine(repoRoot, "GDM", ".editorconfig"));
    var bareCatchRx = new System.Text.RegularExpressions.Regex(@"catch\s*(?:\([^)]*\))?\s*(?:when\s*\([^)]*\)\s*)?\{\s*\}");
    int unannotatedEmptyCatches = 0;
    foreach (var cs in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
    {
        if (cs.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
            cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            cs.Contains($"{Path.DirectorySeparatorChar}Integration{Path.DirectorySeparatorChar}Remote{Path.DirectorySeparatorChar}") ||
            cs.EndsWith("RemoteWindow.xaml.cs", StringComparison.OrdinalIgnoreCase))
            continue;
        unannotatedEmptyCatches += bareCatchRx.Matches(File.ReadAllText(cs)).Count;
    }
    Check("Q-05: UI classes split into partials, .editorconfig present, and 0 unannotated empty catch blocks in owned files",
        optionsPartials >= 4 && mainPartials >= 4 && editorConfigExists && unannotatedEmptyCatches == 0,
        $"optPartials={optionsPartials}, mainPartials={mainPartials}, editorconfig={editorConfigExists}, emptyCatches={unannotatedEmptyCatches}");

    // Q-06: Partial-download extension .gdm for partials and no legacy partial-file recognition
    var q6Dir = Path.Combine(Path.GetTempPath(), "gdm-q6-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(q6Dir);
    string prevTempRoot = DownloadItem.TempRoot;
    try
    {
        string legacyExt = ".\u0061dam";
        DownloadItem.TempRoot = "";
        var freshItem = new DownloadItem { SaveDir = q6Dir, FileName = "fresh.bin" };
        bool newUsesGdm = freshItem.TempPath.EndsWith(".gdm", StringComparison.Ordinal);

        var tempRootSub = Path.Combine(q6Dir, "temproot");
        Directory.CreateDirectory(tempRootSub);
        DownloadItem.TempRoot = tempRootSub;
        var freshRootItem = new DownloadItem { SaveDir = q6Dir, FileName = "fresh2.bin" };
        bool newRootUsesGdm = freshRootItem.TempPath.EndsWith(".gdm", StringComparison.Ordinal)
                              && freshRootItem.TempPath.StartsWith(tempRootSub, StringComparison.OrdinalIgnoreCase);

        DownloadItem.TempRoot = "";
        var legacyAdjItem = new DownloadItem { SaveDir = q6Dir, FileName = "legacy_adj.bin" };
        var legacyAdjPath = legacyAdjItem.FullPath + legacyExt;
        File.WriteAllBytes(legacyAdjPath, [1, 2, 3]);
        legacyAdjItem.ActiveTempPath = "";
        bool ignoresAdjLegacy = string.Equals(legacyAdjItem.TempPath, legacyAdjItem.FullPath + ".gdm", StringComparison.OrdinalIgnoreCase);

        DownloadItem.TempRoot = tempRootSub;
        var legacyRootItem = new DownloadItem { SaveDir = q6Dir, FileName = "legacy_root.bin" };
        var legacyRootPath = Path.Combine(tempRootSub, $"{legacyRootItem.Id:N}{legacyExt}");
        File.WriteAllBytes(legacyRootPath, [4, 5, 6]);
        legacyRootItem.ActiveTempPath = "";
        bool ignoresRootLegacy = string.Equals(legacyRootItem.TempPath, Path.Combine(tempRootSub, $"{legacyRootItem.Id:N}.gdm"), StringComparison.OrdinalIgnoreCase);

        DownloadItem.TempRoot = "";
        File.WriteAllBytes(Path.Combine(q6Dir, "collide_gdm.bin.gdm"), [1]);
        File.WriteAllBytes(Path.Combine(q6Dir, "collide_leg.bin" + legacyExt), [1]);
        bool uniqueAvoidsGdmOnly = FileNames.Unique(Path.Combine(q6Dir, "collide_gdm.bin")) == Path.Combine(q6Dir, "collide_gdm (1).bin")
                                   && FileNames.Unique(Path.Combine(q6Dir, "collide_leg.bin")) == Path.Combine(q6Dir, "collide_leg.bin");

        var r1 = PathRegistry.Reserve(Guid.NewGuid(), Path.Combine(q6Dir, "collide_gdm.bin"));
        var r2 = PathRegistry.Reserve(Guid.NewGuid(), Path.Combine(q6Dir, "collide_leg.bin"));
        bool reserveAvoidsGdmOnly = r1 == Path.Combine(q6Dir, "collide_gdm (1).bin")
                                    && r2 == Path.Combine(q6Dir, "collide_leg.bin");

        Check("Q-06: Partials use .gdm exclusively and ignore legacy partial extension in SaveDir, TempRoot, FileNames.Unique, and PathRegistry.Reserve",
            newUsesGdm && newRootUsesGdm && ignoresAdjLegacy && ignoresRootLegacy && uniqueAvoidsGdmOnly && reserveAvoidsGdmOnly,
            $"newUsesGdm={newUsesGdm}, newRootUsesGdm={newRootUsesGdm}, ignoresAdj={ignoresAdjLegacy}, ignoresRoot={ignoresRootLegacy}, unique={uniqueAvoidsGdmOnly}, reserve={reserveAvoidsGdmOnly}");
    }
    finally
    {
        DownloadItem.TempRoot = prevTempRoot;
        try { Directory.Delete(q6Dir, true); } catch { }
    }

    // Q-07: Export format "gdm-export", accept generic items JSON, and reject legacy export format
    var q7Dir = Path.Combine(Path.GetTempPath(), "gdm-q7-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(q7Dir);
    var q7Mgr = new DownloadManager(new AppSettings());
    try
    {
        var expPath = Path.Combine(q7Dir, "list.gdm");
        Transfer.Export(expPath, [new DownloadItem { Url = "https://example.com/a.zip", FileName = "a.zip", SaveDir = q7Dir }], text: false);
        var expJson = File.ReadAllText(expPath);
        var expDoc = System.Text.Json.Nodes.JsonNode.Parse(expJson)?.AsObject();
        bool writesGdmExport = expDoc?["format"]?.GetValue<string>() == "gdm-export";

        var impGdm = Transfer.Import(expPath, q7Mgr);
        var legExpPath = Path.Combine(q7Dir, "legacy.json");
        string legFormat = "\u0061dam-export";
        File.WriteAllText(legExpPath, $$"""{"format":"{{legFormat}}","version":1,"items":[{"url":"https://example.com/b.zip","fileName":"b.zip"}]}""");
        var impLeg = Transfer.Import(legExpPath, q7Mgr);

        var genericExpPath = Path.Combine(q7Dir, "generic.json");
        File.WriteAllText(genericExpPath, """{"items":[{"url":"https://example.com/c.zip","fileName":"c.zip"}]}""");
        var impGeneric = Transfer.Import(genericExpPath, q7Mgr);

        Check("Q-07: Transfer.Export writes 'gdm-export' and Transfer.Import accepts 'gdm-export' and generic items JSON while rejecting legacy format",
            writesGdmExport && impGdm.Count == 1 && impLeg.Count == 0 && impGeneric.Count == 1,
            $"writesGdmExport={writesGdmExport}, gdm={impGdm.Count}, legacy={impLeg.Count}, generic={impGeneric.Count}");
    }
    finally
    {
        q7Mgr.Shutdown();
        try { Directory.Delete(q7Dir, true); } catch { }
    }

    // Q-08: README.md, FIREFOX.md, and store/SUBMIT.md are written in Modern Standard Arabic for GDM
    var readmeMd = File.ReadAllText(Path.Combine(repoRoot, "GDM", "README.md"));
    var firefoxMd = File.ReadAllText(Path.Combine(repoRoot, "GDM", "FIREFOX.md"));
    var submitMd = File.ReadAllText(Path.Combine(repoRoot, "GDM", "store", "SUBMIT.md"));
    var arCharRx = new System.Text.RegularExpressions.Regex(@"[\u0600-\u06FF]");
    string legWord = "\u0041dam";
    bool docsInMsaAndGdm = arCharRx.Matches(readmeMd).Count > 200
                           && arCharRx.Matches(firefoxMd).Count > 100
                           && arCharRx.Matches(submitMd).Count > 100
                           && readmeMd.Contains(".gdm", StringComparison.Ordinal)
                           && readmeMd.Contains("com.golan.gdm", StringComparison.Ordinal)
                           && firefoxMd.Contains("com.golan.gdm", StringComparison.Ordinal)
                           && firefoxMd.Contains("gdm@golan.local", StringComparison.Ordinal)
                           && submitMd.Contains("Chrome Web Store", StringComparison.Ordinal)
                           && submitMd.Contains("Edge", StringComparison.Ordinal)
                           && submitMd.Contains("addons.mozilla.org", StringComparison.Ordinal)
                           && !submitMd.Contains("آدم", StringComparison.Ordinal)
                           && !readmeMd.Contains(legWord, StringComparison.OrdinalIgnoreCase)
                           && !firefoxMd.Contains(legWord, StringComparison.OrdinalIgnoreCase)
                           && !submitMd.Contains(legWord, StringComparison.OrdinalIgnoreCase);
    Check("Q-08: README.md, FIREFOX.md, and store/SUBMIT.md are in Modern Standard Arabic with accurate GDM paths & zero legacy references",
        docsInMsaAndGdm);

    // Q-09: Independence & audit regression checks (no legacy migration method, Transfer.Import corrupt JSON & magnet round-trip, .gdm partial cleanup, MagnetValidator .local/.internal block)
    var q9Dir = Path.Combine(Path.GetTempPath(), "gdm-q9-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(q9Dir);
    var q9Mgr = new DownloadManager(new AppSettings { DownloadRoot = Path.Combine(q9Dir, "Downloads"), UseCategoryFolders = false });
    try
    {
        bool noMigrationMethod = !typeof(AppPaths).GetMethods().Any(m => m.Name == "MigrateFrom\u0041damIfNeeded")
                                 && typeof(AppPaths).GetField("MigrationMarkerFileName") == null
                                 && !AppPaths.IsPortableMode(null, @"C:\Apps\GDM", p => p.EndsWith("\u0041dam.portable", StringComparison.OrdinalIgnoreCase))
                                 && AppPaths.IsPortableMode(null, @"C:\Apps\GDM", p => p.EndsWith("GDM.portable", StringComparison.OrdinalIgnoreCase));

        var corruptJsonPath = Path.Combine(q9Dir, "corrupt.gdm");
        File.WriteAllText(corruptJsonPath, "{ \"format\": \"gdm-export\", \"items\": [");
        bool corruptHandledGracefully = false;
        try
        {
            var corruptRes = Transfer.Import(corruptJsonPath, q9Mgr);
            corruptHandledGracefully = corruptRes.Count == 0;
        }
        catch { corruptHandledGracefully = false; }

        var magnetUrl = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=UbuntuISO";
        var magnetExpPath = Path.Combine(q9Dir, "magnet.gdm");
        Transfer.Export(magnetExpPath, [new DownloadItem { Url = magnetUrl, FileName = "UbuntuISO", SaveDir = q9Mgr.Settings.DownloadRoot, Kind = DownloadKind.Torrent }], text: false);
        var magnetImp = Transfer.Import(magnetExpPath, q9Mgr);
        bool magnetRoundTripOk = magnetImp.Count == 1
                                 && magnetImp[0].Kind == DownloadKind.Torrent
                                 && magnetImp[0].Url == magnetUrl;

        var gdmRemItem = new DownloadItem { Url = "https://example.com/rem.bin", SaveDir = q9Dir, FileName = "rem.bin" };
        var legRemPart = gdmRemItem.FullPath + ".\u0061dam";
        var gdmRemPart = gdmRemItem.FullPath + ".gdm";
        File.WriteAllBytes(legRemPart, [1, 2, 3]);
        File.WriteAllBytes(gdmRemPart, [4, 5, 6]);
        gdmRemItem.ActiveTempPath = gdmRemPart;
        q9Mgr.Items.Add(gdmRemItem);
        q9Mgr.RemoveAsync(gdmRemItem, deleteFile: false).GetAwaiter().GetResult();
        bool removeDeletesOnlyGdm = File.Exists(legRemPart) && !File.Exists(gdmRemPart);

        bool magnetBlocksLocal = !Golan.Gdm.Core.Torrent.MagnetValidator.IsSafeRemoteHost("printer.local")
                                 && !Golan.Gdm.Core.Torrent.MagnetValidator.IsSafeRemoteHost("nas.internal")
                                 && !Golan.Gdm.Core.Torrent.MagnetValidator.IsSafeRemoteHost("ip6-localhost")
                                 && !Golan.Gdm.Core.Torrent.MagnetValidator.IsSafeRemoteHost("ip6-loopback");

        const string legExtId = "dndjbncb" + "blcblidf" + "dcpcbeao" + "nnpccfcc";
        var remoteHtml = File.ReadAllText(Path.Combine(integDir, "Remote", "remote.html"));
        bool extIdIndependent = Golan.Gdm.Integration.BrowserIntegration.ExtensionId == "fkjplbaglahdiphiodkgidkdfkbebgfh"
                                && Golan.Gdm.Integration.BrowserIntegration.AllowedExtensionIds.SequenceEqual(["fkjplbaglahdiphiodkgidkdfkbebgfh"])
                                && Golan.Gdm.Integration.BrowserIntegration.GetAllowedOrigins().SequenceEqual(["chrome-extension://fkjplbaglahdiphiodkgidkdfkbebgfh/"])
                                && !Golan.Gdm.Integration.BrowserIntegration.AllowedExtensionIds.Contains(legExtId)
                                && remoteHtml.Contains("gdm_remote_lang", StringComparison.Ordinal)
                                && remoteHtml.Contains("gdm_remote_token", StringComparison.Ordinal)
                                && !remoteHtml.Contains("\u0061dam_remote_lang", StringComparison.OrdinalIgnoreCase)
                                && !remoteHtml.Contains("\u0061dam_remote_token", StringComparison.OrdinalIgnoreCase)
                                && typeof(Golan.Gdm.Integration.BrowserIntegration).GetField("LegacyHostName") == null
                                && typeof(Golan.Gdm.Integration.WindowsStartup).GetField("LegacyValueName") == null
                                && typeof(Golan.Gdm.Integration.Installer).GetField("LegacyUninstallSubKey") == null
                                && typeof(Golan.Gdm.Integration.Installer).GetField("LegacyRunValueName") == null;

        Check("Q-09: Independence & audit checks (no legacy migration/registry/portable hooks, corrupt JSON guard, magnet export/import, .gdm-only cleanup, new ExtensionId)",
            noMigrationMethod && corruptHandledGracefully && magnetRoundTripOk && removeDeletesOnlyGdm && magnetBlocksLocal && extIdIndependent,
            $"noMig={noMigrationMethod}, corrupt={corruptHandledGracefully}, magnet={magnetRoundTripOk}, gdmOnlyClean={removeDeletesOnlyGdm}, magnetHost={magnetBlocksLocal}, extIdIndep={extIdIndependent}");
    }
    finally
    {
        q9Mgr.Shutdown();
        try { Directory.Delete(q9Dir, true); } catch { }
    }
}

if (Environment.GetEnvironmentVariable("GDM_TEST_FILTER") == "Q_ONLY")
{
    RunQChecks();
    Console.WriteLine(fails == 0 ? $"ALL {passes} Q CHECKS PASSED" : $"{fails} FAILED, {passes} passed");
    return fails;
}

// ---------- 1. segmented download ----------
var item = NewItem($"{Base}/big.bin");
var sw = Stopwatch.StartNew();
int maxConn = 0;
using (var t = new Timer(_ => maxConn = Math.Max(maxConn, item.ActiveConnections), null, 0, 50))
    await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
Check("segmented: name from Content-Disposition", item.FileName == "big.bin", item.FileName);
Check("segmented: resumable + etag", item.Resumable && item.ETag.Length > 0);
Check("segmented: sha256", Hash(item.FullPath) == expected);
Check("segmented: many connections", maxConn >= 4, $"max={maxConn} {sw.Elapsed.TotalSeconds:0.0}s");
Check("segmented: no temp left", !File.Exists(item.TempPath));
Check("segmented: mark of the web", File.Exists(item.FullPath + ":Zone.Identifier") && File.ReadAllText(item.FullPath + ":Zone.Identifier").Contains("ZoneId=3"));

// ---------- 2. pause / save / resume ----------
var item2 = NewItem($"{Base}/big.bin", "resume.bin", 6);
using (var cts = new CancellationTokenSource())
{
    var run = new HttpDownloader(item2, limiter).RunAsync(cts.Token);
    while (item2.Downloaded < 12 * 1024 * 1024) await Task.Delay(20);
    cts.Cancel();
    try { await run; Check("pause: cancelled", false); } catch (OperationCanceledException) { Check("pause: cancelled", true, $"{item2.Segments.Count} segments"); }
}
var json = System.Text.Json.JsonSerializer.Serialize(item2, Json.Options);
var restored = System.Text.Json.JsonSerializer.Deserialize<DownloadItem>(json, Json.Options)!;
Check("pause: state saved", restored.Segments.Count == item2.Segments.Count && restored.Downloaded == item2.Downloaded && restored.ETag == item2.ETag);
await new HttpDownloader(restored, limiter).RunAsync(CancellationToken.None);
Check("resume: sha256", Hash(restored.FullPath) == expected);

// ---------- 3. server without range ----------
var item3 = NewItem($"{Base}/norange/big.bin");
await new HttpDownloader(item3, limiter).RunAsync(CancellationToken.None);
Check("no-range: not resumable", !item3.Resumable);
Check("no-range: unique name", item3.FileName == "big (1).bin", item3.FileName);
Check("no-range: sha256", Hash(item3.FullPath) == expected);

// ---------- 4. global limiter ----------
var item4 = NewItem($"{Base}/big.bin", "limited.bin");
var lim = new SpeedLimiter { Enabled = true, BytesPerSecond = 4 * 1024 * 1024 };
using (var cts = new CancellationTokenSource())
{
    sw.Restart();
    var run = new HttpDownloader(item4, lim).RunAsync(cts.Token);
    while (item4.Downloaded < 8 * 1024 * 1024) await Task.Delay(20);
    var rate = item4.Downloaded / sw.Elapsed.TotalSeconds;
    cts.Cancel();
    try { await run; } catch (OperationCanceledException) { }
    Check("global limiter ≈ 4 MB/s", rate is < 5.2 * 1024 * 1024 and > 2.5 * 1024 * 1024, Fmt.Speed(rate));
}

// ---------- 5. per-file limiter ----------
var item5 = NewItem($"{Base}/big.bin", "perfile.bin");
item5.SpeedLimitKB = 2048;
using (var cts = new CancellationTokenSource())
{
    sw.Restart();
    var run = new HttpDownloader(item5, limiter).RunAsync(cts.Token);
    while (item5.Downloaded < 5 * 1024 * 1024) await Task.Delay(20);
    var rate = item5.Downloaded / sw.Elapsed.TotalSeconds;
    cts.Cancel();
    try { await run; } catch (OperationCanceledException) { }
    Check("per-file limiter ≈ 2 MB/s", rate is < 2.8 * 1024 * 1024 and > 1.2 * 1024 * 1024, Fmt.Speed(rate));
}

// ---------- 6. 404 ----------
try { await new HttpDownloader(NewItem($"{Base}/missing.bin"), limiter).RunAsync(CancellationToken.None); Check("404: error", false); }
catch (DownloadException ex) { Check("404: friendly error", ex.Message.Contains("404")); }

// ---------- 7. per-server connection limit ----------
await Get("/reset");
settings.ServerLimits = [new ServerLimit { Pattern = "127.0.0.1", MaxConnections = 2 }];
var item7 = NewItem($"{Base}/big.bin?t=limit", "server-limit.bin", 8);
await new HttpDownloader(item7, limiter).RunAsync(CancellationToken.None);
var seen = int.Parse(await Get("/stats?t=limit"));
Check("per-server limit: never above 2", seen <= 2, $"server saw {seen}");
Check("per-server limit: sha256", Hash(item7.FullPath) == expected);
settings.ServerLimits = [];

// ---------- 8. server refusing extra connections (503) ----------
var item8 = NewItem($"{Base}/limited/big.bin", "busy-server.bin", 8);
await new HttpDownloader(item8, limiter).RunAsync(CancellationToken.None);
Check("503 on extra connections: finishes with fewer", Hash(item8.FullPath) == expected);
Check("503 on extra connections: the accepted number is remembered for this server",
    HttpDownloader.LearnedHostLimit("127.0.0.1") is int learned8 && learned8 is >= 1 and < 8 /* fewer than asked; how many depends on timing */, $"learned={HttpDownloader.LearnedHostLimit("127.0.0.1")}");
HttpDownloader.ForgetLearnedLimits();

// ---------- 9. cookies are not sent to another host after a redirect ----------
var item9 = NewItem($"{Base}/redirect?to=" + Uri.EscapeDataString($"http://localhost:{port}/nocookie/big.bin"), "redirect.bin");
item9.Cookies = "session=secret";
item9.CookieHost = "127.0.0.1";
await new HttpDownloader(item9, limiter).RunAsync(CancellationToken.None);
Check("cookies dropped on cross-host redirect", Hash(item9.FullPath) == expected);
Check("original url kept (not the redirect target)", item9.Url.Contains("/redirect?"));

// ---------- 10. file changed on the server between pause and resume ----------
var changingPath = Path.Combine(served, "changing.bin");
File.Copy(bigPath, changingPath, true);
var item10 = NewItem($"{Base}/changing.bin", "changing.bin", 4);
using (var cts = new CancellationTokenSource())
{
    var run = new HttpDownloader(item10, limiter).RunAsync(cts.Token);
    while (item10.Downloaded < 8 * 1024 * 1024) await Task.Delay(20);
    cts.Cancel();
    try { await run; } catch (OperationCanceledException) { }
}
var changed = File.ReadAllBytes(bigPath);
for (int i = 0; i < changed.Length; i += 4096) changed[i] ^= 0x5A;
File.WriteAllBytes(changingPath, changed);
var newHash = Convert.ToHexString(SHA256.HashData(changed));
await new HttpDownloader(item10, limiter).RunAsync(CancellationToken.None);
Check("changed file: restarts and gets the new version", Hash(item10.FullPath) == newHash);

// ---------- 11. two downloads with the same name at the same time ----------
var a = NewItem($"{Base}/big.bin", "twin.bin");
var b = NewItem($"{Base}/norange/big.bin", "twin.bin");
await Task.WhenAll(new HttpDownloader(a, limiter).RunAsync(CancellationToken.None), new HttpDownloader(b, limiter).RunAsync(CancellationToken.None));
Check("same name: different files", a.FullPath != b.FullPath, $"{a.FileName} / {b.FileName}");
Check("same name: both intact", Hash(a.FullPath) == expected && Hash(b.FullPath) == expected);

// ---------- 12. zip preview ----------
var zipPath = Path.Combine(served, "sample.zip");
if (File.Exists(zipPath)) File.Delete(zipPath);
using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
{
    for (int i = 0; i < 25; i++)
    {
        var e = z.CreateEntry($"folder{i % 3}/file{i}.txt");
        using var w = new StreamWriter(e.Open());
        w.Write(new string('x', 1000 + i));
    }
}
var entries = await ZipPreview.ListAsync(new DownloadItem { Url = $"{Base}/sample.zip" }, CancellationToken.None);
Check("zip preview: all entries", entries.Count == 25, $"{entries.Count} entries");
Check("zip preview: sizes", entries.Any(e => e.Name == "folder1/file1.txt" && e.Size == 1001));

// ---------- 13. site grabber ----------
var site = Path.Combine(served, "site");
Directory.CreateDirectory(site);
File.WriteAllText(Path.Combine(site, "index.html"), "<html><a href='page2.html'>next</a> <img src='a.jpg'> <a href=\"files/b.zip\">zip</a> <a href='http://example.invalid/out.jpg'>ext</a></html>");
File.WriteAllText(Path.Combine(site, "page2.html"), "<html><a href='c.jpg'>c photo</a><a href='page3.html'>deeper</a></html>");
File.WriteAllText(Path.Combine(site, "page3.html"), "<html><a href='d.jpg'>d</a></html>");
var project = new GrabberProject { StartUrl = $"{Base}/site/index.html", LevelsBase = 1, FileMasks = "*.jpg", SearchThisSiteOnly = true };
var grabber = new SiteGrabber(project);
var found = new System.Collections.Concurrent.ConcurrentBag<GrabbedFile>();
grabber.FileFound += found.Add;
await grabber.RunAsync(CancellationToken.None);
var names = found.Select(f => f.Name).OrderBy(n => n).ToList();
Check("grabber: depth 1, *.jpg, this site only", names.SequenceEqual(["a.jpg", "c.jpg"]), string.Join(",", names));
Check("grabber: link text kept", found.Any(f => f.Name == "c.jpg" && f.LinkText == "c photo"));

// ---------- 14. synchronization check ----------
var syncItem = NewItem($"{Base}/changing.bin");
var p0 = await HttpDownloader.ProbeAsync(syncItem, CancellationToken.None);
syncItem.ETag = p0.ETag; syncItem.TotalSize = p0.Size;
Check("sync: unchanged file", !await HttpDownloader.HasChangedAsync(syncItem, CancellationToken.None));
File.SetLastWriteTimeUtc(changingPath, DateTime.UtcNow.AddMinutes(1));
Check("sync: changed file detected", await HttpDownloader.HasChangedAsync(syncItem, CancellationToken.None));

// ---------- 15. batch pattern ----------
var batch = Transfer.ExpandBatch("http://x/img*.jpg", false, "1", "12", 3);
Check("batch pattern: numbers with padding", batch.Count == 12 && batch[0] == "http://x/img001.jpg" && batch[^1] == "http://x/img012.jpg");
Check("batch pattern: letters", Transfer.ExpandBatch("http://x/*.png", true, "a", "e", 0).Count == 5);

// ---------- 16. scheduler rules ----------
var q = new QueueDef { ScheduleEnabled = true, StartTime = "02:00", Daily = true, StopEnabled = true, StopTime = "06:00" };
var day = new DateTime(2026, 10, 19); // a Monday
Check("scheduler: starts at 02:00", QueueScheduler.ShouldStart(q, day.AddHours(2).AddMinutes(1)));
Check("scheduler: not before", !QueueScheduler.ShouldStart(q, day.AddHours(1).AddMinutes(59)));
q.LastStart = day.AddHours(2);
Check("scheduler: once a day", !QueueScheduler.ShouldStart(q, day.AddHours(2).AddMinutes(30)));
Check("scheduler: stops at 06:00", QueueScheduler.ShouldStop(q, day.AddHours(6).AddMinutes(1)));
q.Days = [DayOfWeek.Friday];
q.LastStart = null;
Check("scheduler: weekdays respected", !QueueScheduler.ShouldStart(q, day.AddHours(2).AddMinutes(1)));
q.Days = Enum.GetValues<DayOfWeek>().ToList();
q.RepeatEnabled = true; q.RepeatHours = 1; q.StopEnabled = false;
q.LastStart = day.AddHours(2);
Check("scheduler: repeat every hour", QueueScheduler.ShouldStart(q, day.AddHours(3).AddMinutes(1)) && !QueueScheduler.ShouldStart(q, day.AddHours(2).AddMinutes(50)));

// ---------- 17. settings migration from v1 ----------
var v1 = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"MaxSimultaneous\":5,\"SchedulerEnabled\":true,\"ScheduleStart\":\"03:30\",\"AfterQueue\":\"shutdown\"}", Json.Options)!;
v1.Normalize();
Check("migration: main + sync queues", v1.Queues.Count == 2 && v1.MainQueue.MaxSimultaneous == 5 && v1.MainQueue.StartTime == "03:30" && v1.MainQueue.ShutdownWhenDone);
Check("migration: categories", v1.CategoryDefs.Count == 6 && v1.CategoryDefs[^1].Key == "general");
var legacyItem = System.Text.Json.JsonSerializer.Deserialize<DownloadItem>("{\"Url\":\"http://a/b.zip\",\"InQueue\":true}", Json.Options)!;
Check("migration: v1 InQueue → main queue", legacyItem.QueueId == QueueDef.MainId);

// ---------- 18. secrets ----------
var login = new SiteLogin { Pattern = "example.com", User = "u", Password = "p@ss" };
Check("logins: password protected with DPAPI", login.ProtectedPassword.Length > 0 && !login.ProtectedPassword.Contains("p@ss") && login.Password == "p@ss");
Check("logins: host match", settings.LoginFor(new Uri("https://dl.example.com/x")) == null);
settings.Logins = [login];
Check("logins: subdomain match", settings.LoginFor(new Uri("https://dl.example.com/x"))?.User == "u");
var cookieItem = new DownloadItem { Cookies = "a=b", CookieHost = "site.com" };
Check("cookies: encrypted on disk", !System.Text.Json.JsonSerializer.Serialize(cookieItem, Json.Options).Contains("a=b"));
Check("cookies: only for their exact host", cookieItem.CookiesFor(new Uri("https://cdn.other.com/x")) == ""
    && cookieItem.CookiesFor(new Uri("https://www.site.com/x")) == "" && cookieItem.CookiesFor(new Uri("https://site.com/x")) == "a=b");
var secureCookieItem = new DownloadItem { Url = "https://site.com/f.zip", Cookies = "a=b", CookieHost = "site.com" };
Check("cookies: never after https -> http downgrade", secureCookieItem.CookiesFor(new Uri("http://site.com/f.zip")) == "");

// ---------- 19. exceptions and categories ----------
settings.ExceptionSites = "ads.example.com *.tracker.net";
Check("exceptions: site", settings.IsException("https://ads.example.com/x.zip") && settings.IsException("https://a.tracker.net/x.exe") && !settings.IsException("https://example.com/x.zip"));
settings.CategoryDefs.Insert(0, new CategoryDef { Key = "gh", Name = "غيت هب", Sites = "github.com", Extensions = "zip" });
Categories.Use(settings.CategoryDefs);
Check("categories: site rule wins", Categories.Detect("x.zip", "https://github.com/a/x.zip") == "gh" && Categories.Detect("x.zip", "https://other.com/x.zip") == "compressed");

// ---------- 20. F-02 / F-03: Basic-auth credentials & SiteLogin security ----------
var authItem = NewItem($"{Base}/auth/big.bin", "auth-ok.bin", 4);
authItem.Login = "user"; authItem.Password = "pass";
await new HttpDownloader(authItem, limiter).RunAsync(CancellationToken.None);
Check("auth: per-item login sent to matching host", Hash(authItem.FullPath) == expected);

var authRedir = NewItem($"{Base}/redirect?to=" + Uri.EscapeDataString($"http://localhost:{port}/noauth/big.bin"), "auth-redir.bin", 4);
authRedir.Login = "user"; authRedir.Password = "pass";
try { await new HttpDownloader(authRedir, limiter).RunAsync(CancellationToken.None); } catch (DownloadException) { }
Check("auth: per-item login dropped on cross-host redirect & segments", File.Exists(authRedir.FullPath) && Hash(authRedir.FullPath) == expected);

settings.Logins = [new SiteLogin { Pattern = "127.0.0.1", User = "user", Password = "pass" }];
var siteAuthOk = NewItem($"{Base}/auth/big.bin", "site-auth-ok.bin", 4);
await new HttpDownloader(siteAuthOk, limiter).RunAsync(CancellationToken.None);
Check("auth: saved SiteLogin sent to matching host", Hash(siteAuthOk.FullPath) == expected);

settings.Logins = [
    new SiteLogin { Pattern = "127.0.0.1", User = "user", Password = "pass" },
    new SiteLogin { Pattern = "localhost", User = "user", Password = "pass" },
];
var siteAuthRedir = NewItem($"{Base}/redirect?to=" + Uri.EscapeDataString($"http://localhost:{port}/noauth/big.bin"), "site-auth-redir.bin", 4);
try { await new HttpDownloader(siteAuthRedir, limiter).RunAsync(CancellationToken.None); } catch (DownloadException) { }
Check("auth: saved SiteLogin dropped on cross-host redirect", File.Exists(siteAuthRedir.FullPath) && Hash(siteAuthRedir.FullPath) == expected);

settings.Logins = [
    new SiteLogin { Pattern = "https://127.0.0.1", User = "user", Password = "pass" },
    new SiteLogin { Pattern = "http://127.0", User = "user", Password = "pass" },
    new SiteLogin { Pattern = "127.0/", User = "user", Password = "pass" },
    new SiteLogin { Pattern = "127.0.0.1/noauth-other", User = "user", Password = "pass" },
    new SiteLogin { Pattern = "*", User = "user", Password = "pass" },
    new SiteLogin { Pattern = "127.*", User = "user", Password = "pass" },
    new SiteLogin { Pattern = "*0.0.1", User = "user", Password = "pass" },
];
var noLeakItem = NewItem($"{Base}/noauth/big.bin", "no-leak-login.bin", 2);
await new HttpDownloader(noLeakItem, limiter).RunAsync(CancellationToken.None);
Check("auth: SiteLogin no leak on prefix/wildcard/scheme collision", Hash(noLeakItem.FullPath) == expected);

var testLogins = new List<SiteLogin>
{
    new() { Pattern = "https://example.com", User = "u1", Password = "p" },
    new() { Pattern = "example.com/private", User = "u2", Password = "p" },
    new() { Pattern = "*.example.org", User = "u3", Password = "p" },
    new() { Pattern = "example.*", User = "u4", Password = "p" },
    new() { Pattern = "*example.com", User = "u5", Password = "p" },
    new() { Pattern = "*", User = "u6", Password = "p" },
};
Check("auth: SiteLogin domain/path/scheme boundary rules",
    HttpDownloader.MatchSiteLogin(new Uri("https://example.com/file"), testLogins)?.User == "u1" &&
    HttpDownloader.MatchSiteLogin(new Uri("https://sub.example.org/file"), testLogins)?.User == "u3" &&
    HttpDownloader.MatchSiteLogin(new Uri("https://example.com.evil.com/file"), testLogins) == null &&
    HttpDownloader.MatchSiteLogin(new Uri("http://example.com/file"), testLogins) == null &&
    HttpDownloader.MatchSiteLogin(new Uri("http://example.com/private/doc"), testLogins)?.User == "u2" &&
    HttpDownloader.MatchSiteLogin(new Uri("http://example.com/private-other"), testLogins) == null &&
    HttpDownloader.MatchSiteLogin(new Uri("https://badexample.com/file"), testLogins) == null);

var downgradeAuth = NewItem($"https://127.0.0.1:{port}/noauth/big.bin", "downgrade-auth.bin", 2);
downgradeAuth.Login = "user"; downgradeAuth.Password = "pass";
downgradeAuth.ResolvedUrl = $"{Base}/noauth/big.bin";
downgradeAuth.Resumable = true;
downgradeAuth.TotalSize = new FileInfo(bigPath).Length;
downgradeAuth.Segments = [new Segment { Start = 0, End = downgradeAuth.TotalSize - 1 }];
File.WriteAllBytes(downgradeAuth.TempPath, []);
await new HttpDownloader(downgradeAuth, limiter).RunAsync(CancellationToken.None);
Check("auth: credentials dropped on https->http downgrade", Hash(downgradeAuth.FullPath) == expected);
settings.Logins = [];

// ---------- 21. F-05: Cookie security in HttpDownloader ----------
var cookieOk = NewItem($"{Base}/requirecookie/big.bin", "cookie-ok.bin", 2);
cookieOk.Cookies = "session=secret";
cookieOk.CookieHost = "127.0.0.1";
await new HttpDownloader(cookieOk, limiter).RunAsync(CancellationToken.None);
Check("cookies: sent on exact host match", Hash(cookieOk.FullPath) == expected);

var emptyCookieHost = NewItem($"{Base}/nocookie/big.bin", "cookie-empty-host.bin", 2);
emptyCookieHost.Cookies = "session=secret";
emptyCookieHost.CookieHost = "";
await new HttpDownloader(emptyCookieHost, limiter).RunAsync(CancellationToken.None);
Check("cookies: empty CookieHost never sends cookies", Hash(emptyCookieHost.FullPath) == expected);

var subCookie = NewItem($"{Base}/nocookie/big.bin", "cookie-sub.bin", 2);
subCookie.Cookies = "session=secret";
subCookie.CookieHost = "0.0.1";
await new HttpDownloader(subCookie, limiter).RunAsync(CancellationToken.None);
Check("cookies: host-only cookie not sent to subdomain", Hash(subCookie.FullPath) == expected);

var downgradeCookie = NewItem($"https://127.0.0.1:{port}/nocookie/big.bin", "downgrade-cookie.bin", 2);
downgradeCookie.Cookies = "session=secret";
downgradeCookie.CookieHost = "127.0.0.1";
downgradeCookie.ResolvedUrl = $"{Base}/nocookie/big.bin";
downgradeCookie.Resumable = true;
downgradeCookie.TotalSize = new FileInfo(bigPath).Length;
downgradeCookie.Segments = [new Segment { Start = 0, End = downgradeCookie.TotalSize - 1 }];
File.WriteAllBytes(downgradeCookie.TempPath, []);
await new HttpDownloader(downgradeCookie, limiter).RunAsync(CancellationToken.None);
Check("cookies: dropped on https->http downgrade", Hash(downgradeCookie.FullPath) == expected);

// ---------- 22. F-07: Pre-allocation and stream size bounds ----------
var hugeItem = NewItem($"{Base}/huge-range", "huge.bin", 2);
bool hugeRejected = false;
try { await new HttpDownloader(hugeItem, limiter).RunAsync(CancellationToken.None); }
catch (DownloadException) { hugeRejected = true; }
Check("F-07: 100 TB Content-Range rejected without pre-allocation", hugeRejected && !File.Exists(hugeItem.TempPath) && !File.Exists(hugeItem.FullPath));

var overflowRangeItem = NewItem($"{Base}/overflow", "overflow-range.bin", 1);
bool overflowRangeStopped = false;
try { await new HttpDownloader(overflowRangeItem, limiter).RunAsync(CancellationToken.None); }
catch (DownloadException) { overflowRangeStopped = true; }
long overflowRangeLen = File.Exists(overflowRangeItem.FullPath) ? new FileInfo(overflowRangeItem.FullPath).Length : 0;
Check("F-07: single-stream rejects data past announced size", overflowRangeStopped && !File.Exists(overflowRangeItem.FullPath), $"len={overflowRangeLen}");

long prevMaxUnknown = HttpDownloader.MaxUnknownSize;
HttpDownloader.MaxUnknownSize = 256L * 1024;
var unboundedItem = NewItem($"{Base}/unbounded", "unbounded.bin", 1);
bool unboundedRejected = false;
try { await new HttpDownloader(unboundedItem, limiter).RunAsync(CancellationToken.None); }
catch (DownloadException) { unboundedRejected = true; }
finally { HttpDownloader.MaxUnknownSize = prevMaxUnknown; }
Check("F-07: missing Content-Length stream capped", unboundedRejected && !File.Exists(unboundedItem.FullPath));

// ---------- 23. Parity #3: Proxy bypass list & ProxyForTools ----------
var proxySettings = new AppSettings
{
    ProxyMode = "manual",
    ProxyType = "http",
    ProxyHost = "127.0.0.1",
    ProxyPort = 1,
    ProxyBypass = "localhost 127.0.0.1 example.com *.internal.test",
};
proxySettings.Normalize();
HttpDownloader.Configure(proxySettings);
Check("proxy bypass: matches localhost, 127.0.0.1, example.com, *.internal.test",
    HttpDownloader.IsProxyBypassed("http://localhost:8080/a") &&
    HttpDownloader.IsProxyBypassed("http://127.0.0.1:47915/big.bin") &&
    HttpDownloader.IsProxyBypassed("https://example.com/file.zip") &&
    HttpDownloader.IsProxyBypassed("https://sub.example.com/file.zip") &&
    HttpDownloader.IsProxyBypassed("https://api.internal.test/v1"));
Check("proxy bypass: rejects non-bypassed and prefix-collision hosts",
    !HttpDownloader.IsProxyBypassed("https://other.org/file.zip") &&
    !HttpDownloader.IsProxyBypassed("https://evil-example.com/file.zip") &&
    !HttpDownloader.IsProxyBypassed("https://example.com.attacker.com/file.zip") &&
    !HttpDownloader.IsProxyBypassed("https://internal.test.attacker.com/file.zip"));
Check("proxy bypass: ProxyForTools skips proxy for bypassed URLs",
    HttpDownloader.ProxyForTools("https://example.com/video.mp4") == "" &&
    HttpDownloader.ProxyForTools("https://other.org/video.mp4") == "http://127.0.0.1:1");
HttpDownloader.Configure(settings);

// ---------- 24. F-01 / F-14: UNC SaveDir rejection & UseServerFileTime after MOTW ----------
var uncItem = new DownloadItem { Url = $"{Base}/big.bin", SaveDir = @"\\127.0.0.1\share\folder", FileName = "unc.bin", NameLocked = true };
bool uncRejected = false;
try { await new HttpDownloader(uncItem, limiter).RunAsync(CancellationToken.None); }
catch (DownloadException) { uncRejected = true; }
Check("F-01: UNC SaveDir rejected in HttpDownloader.RunAsync", uncRejected);

settings.UseServerFileTime = true;
HttpDownloader.Configure(settings);
var mtimeItem = NewItem($"{Base}/big.bin?lm=2020", "mtime.bin", 2);
await new HttpDownloader(mtimeItem, limiter).RunAsync(CancellationToken.None);
var expectedTimeUtc = new DateTime(2020, 10, 21, 7, 28, 0, DateTimeKind.Utc);
var actualTimeUtc = File.GetLastWriteTimeUtc(mtimeItem.FullPath);
Check("F-14: UseServerFileTime preserved after Zone.Identifier write",
    File.Exists(mtimeItem.FullPath + ":Zone.Identifier") &&
    Math.Abs((actualTimeUtc - expectedTimeUtc).TotalSeconds) < 2,
    $"actual={actualTimeUtc:O}");
settings.UseServerFileTime = false;
HttpDownloader.Configure(settings);

Check("i18n: every table is embedded in the main assembly (never as satellite resources)", typeof(Loc).Assembly.GetManifestResourceNames().Count(n => n.StartsWith("i18n/")) >= 20);
RunQChecks();
fails += await Tests_P1.RunAsync(Base, served, outDir, port);
fails += await Golan.Gdm.Tests_P2.RunAsync(Base, served, outDir, port);
fails += await Tests_P3.RunAsync(Base, served, outDir, port);
fails += await Tests_P4.RunAsync(Base, served, outDir, port);
fails += await Tests_V1.RunAsync(Base, served, outDir, port);
fails += await Tests_V2.RunAsync(Base, served, outDir, port);
fails += await Tests_V3.RunAsync(Base, served, outDir, port);
fails += await Tests_V4.RunAsync(Base, served, outDir, port);
fails += await Tests_V5.RunAsync(Base, served, outDir, port);
fails += await Tests_VB.RunAsync(Base, served, outDir, port);
fails += await Tests_Security.RunAsync(Base, served, outDir, port);
fails += await Tests_Hard.RunAsync(Base, served, outDir, port);
fails += await Tests_License.RunAsync(Base, served, outDir, port);
fails += await Tests_Guide.RunAsync(Base, served, outDir, port);
fails += await Tests_Video.RunAsync(Base, served, outDir, port);
fails += await Tests_Refresh.RunAsync();
fails += await Tests_Bridge.RunAsync();
Console.WriteLine(fails == 0 ? $"ALL {passes} PASSED (+ package suites)" : $"{fails} FAILED, {passes} passed");
return fails;
