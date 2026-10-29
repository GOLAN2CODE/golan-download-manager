using System.Collections;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Resolvers;
using Golan.Gdm.Integration;
using Golan.Gdm.Integration.Remote;
using MonoTorrent;

namespace Golan.Gdm
{
    internal static partial class App
    {
        public const string Version = "2.2.0";
        public static JsonObject PingReply(AppSettings s) => new()
        {
            ["ok"] = true,
            ["version"] = Version,
            ["intercept"] = s.InterceptBrowser,
            ["clipboard"] = s.ClipboardMonitor,
        };
    }
}

public static class Tests_VB
{
    static int _passed;
    static int _failed;

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}{(detail != null ? $" ({detail})" : "")}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}{(detail != null ? $" ({detail})" : "")}");
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== Package VB Verification Suite (Phase B Integration) ===");

        string repoRoot = FindRepoRoot();
        string gdmRoot = Path.Combine(repoRoot, "GDM", "src", "GDM");
        string i18nDir = Path.Combine(gdmRoot, "i18n");
        string extDir = Path.Combine(repoRoot, "GDM", "extension");

        string workRoot = Path.Combine(Path.GetTempPath(), "gdm-vb-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        string? dllPath = new[]
        {
            Path.Combine(gdmRoot, "bin", "Release", "net10.0-windows", "GDM.dll"),
            Path.Combine(gdmRoot, "bin", "Debug", "net10.0-windows", "GDM.dll"),
            Path.Combine(gdmRoot, "bin", "Release", "net10.0-windows", "win-x64", "GDM.dll"),
            Path.Combine(gdmRoot, "bin", "Debug", "net10.0-windows", "win-x64", "GDM.dll"),
        }
        .Where(File.Exists)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();

        Assembly gdmAsm = dllPath != null ? Assembly.LoadFrom(dllPath) : typeof(DownloadItem).Assembly;
        ResolveEventHandler onResolve = (_, e) =>
            e.Name.Equals("GDM", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("GDM,", StringComparison.OrdinalIgnoreCase)
                ? gdmAsm
                : null;
        AppDomain.CurrentDomain.AssemblyResolve += onResolve;

        try
        {
            RunI18nAndMenuChecks(gdmRoot, i18nDir);
            await RunSettingsAndOptionsWindowChecksAsync(gdmAsm, workRoot);
            await RunResolverAndMegaChecksAsync(workRoot);
            await RunMagnetAndTorrentRoutingChecksAsync(gdmAsm, gdmRoot, extDir, workRoot);
            await RunCliAndPackExtensionChecksAsync(gdmAsm, extDir, workRoot);
            await RunRuntimeHookChecksAsync(baseUrl, served, workRoot);
            RunRebrandAndMigrationChecks(gdmAsm, gdmRoot, workRoot);
        }
        catch (Exception ex)
        {
            Check("VB: Unhandled suite exception", false, ex.ToString());
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= onResolve;
            Loc.Init("ar");
            var clean = new AppSettings { ProxyMode = "none" };
            clean.Normalize();
            HttpDownloader.Configure(clean);
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }

        Console.WriteLine($"[VB Summary] {(_failed == 0 ? $"ALL {_passed} VB CHECKS PASSED" : $"{_failed} VB CHECKS FAILED, {_passed} passed")}");
        return _failed;
    }

    // =========================================================================
    // 1. i18n Completeness & Menu Uniqueness (VB-01 .. VB-03)
    // =========================================================================
    static void RunI18nAndMenuChecks(string gdmRoot, string i18nDir)
    {
        try
        {
            string[] expectedAreas = ["common", "core", "main", "windows", "system", "media", "torrent", "integrity", "remote", "remote-tls", "integration", "license", "guide"];
            var arAll = new Dictionary<string, string>(StringComparer.Ordinal);
            var enAll = new Dictionary<string, string>(StringComparer.Ordinal);
            var missingAreas = new List<string>();
            var keyMismatches = new List<string>();
            var emptyValues = new List<string>();
            var phMismatches = new List<string>();
            var phRegex = new Regex(@"\{\d+(?:[:,][^}]*)?\}", RegexOptions.Compiled);

            foreach (var area in expectedAreas)
            {
                string arPath = Path.Combine(i18nDir, $"{area}.ar.json");
                string enPath = Path.Combine(i18nDir, $"{area}.en.json");
                if (!File.Exists(arPath) || !File.Exists(enPath))
                {
                    missingAreas.Add(area);
                    continue;
                }

                var ar = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(arPath)) ?? [];
                var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath)) ?? [];

                foreach (var (k, v) in ar)
                {
                    arAll[k] = v;
                    if (string.IsNullOrWhiteSpace(v)) emptyValues.Add($"{area}.ar:{k}");
                    if (!en.TryGetValue(k, out var enVal))
                    {
                        keyMismatches.Add($"{area}:missing-in-en:{k}");
                    }
                    else
                    {
                        var phAr = string.Join(",", phRegex.Matches(v).Select(m => m.Value).Distinct().OrderBy(x => x));
                        var phEn = string.Join(",", phRegex.Matches(enVal).Select(m => m.Value).Distinct().OrderBy(x => x));
                        if (phAr != phEn) phMismatches.Add($"{area}:{k}(ar=[{phAr}]!=en=[{phEn}])");
                    }
                }
                foreach (var (k, v) in en)
                {
                    enAll[k] = v;
                    if (string.IsNullOrWhiteSpace(v)) emptyValues.Add($"{area}.en:{k}");
                    if (!ar.ContainsKey(k)) keyMismatches.Add($"{area}:missing-in-ar:{k}");
                }
            }

            Check("VB-01: All 10 i18n tables (common..vb) exist with ar/en key & placeholder parity",
                missingAreas.Count == 0 && keyMismatches.Count == 0 && emptyValues.Count == 0 && phMismatches.Count == 0,
                $"missingAreas=[{string.Join(",", missingAreas)}] keyDiff=[{string.Join(",", keyMismatches.Take(5))}] phDiff=[{string.Join(",", phMismatches.Take(5))}]");

            // VB-02: Scan all C# and XAML files for localization key references
            var refKeys = new HashSet<string>(StringComparer.Ordinal);
            var csPatterns = new[]
            {
                new Regex(@"Loc\.T\(\s*\$?""([^""\{]+)""", RegexOptions.Compiled),
                new Regex(@"V1Loc\.(?:Get|Format)\(\s*""([^""]+)""", RegexOptions.Compiled),
                new Regex(@"V2Loc\.(?:Get|Format)\(\s*""([^""]+)""", RegexOptions.Compiled),
                new Regex(@"TorrentText\.T\(\s*""([^""]+)""", RegexOptions.Compiled),
                new Regex(@"V5T\.T\(\s*""([^""]+)""", RegexOptions.Compiled),
            };
            var xamlPattern = new Regex(@"\{(?:core|loc|v3|v4|v5|vb):T(?:Extension)?\s+([^\}\s,]+)", RegexOptions.Compiled);

            foreach (var file in Directory.EnumerateFiles(gdmRoot, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    string text = File.ReadAllText(file);
                    foreach (var rx in csPatterns)
                    {
                        foreach (Match m in rx.Matches(text))
                        {
                            string key = m.Groups[1].Value;
                            if (!key.EndsWith('.')) refKeys.Add(key);
                        }
                    }
                }
                else if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    string text = File.ReadAllText(file);
                    foreach (Match m in xamlPattern.Matches(text))
                    {
                        string key = m.Groups[1].Value.Trim('\'', '"');
                        if (!key.EndsWith('.')) refKeys.Add(key);
                    }
                }
            }

            var missingRefAr = refKeys.Where(k => !arAll.ContainsKey(k)).ToList();
            var missingRefEn = refKeys.Where(k => !enAll.ContainsKey(k)).ToList();
            Check("VB-02: Every localization key referenced in C#/XAML exists in merged ar and en tables",
                refKeys.Count > 200 && missingRefAr.Count == 0 && missingRefEn.Count == 0,
                $"refCount={refKeys.Count}, missingAr=[{string.Join(", ", missingRefAr.Take(8))}], missingEn=[{string.Join(", ", missingRefEn.Take(8))}]");

            // VB-03: MainWindow.xaml menu header uniqueness & required v2 menu items
            var mainXamlPath = Path.Combine(gdmRoot, "UI", "MainWindow.xaml");
            var mainDoc = XDocument.Load(mainXamlPath);
            var duplicateHeaders = new List<string>();
            foreach (var container in mainDoc.Descendants().Where(e => e.Name.LocalName is "Menu" or "MenuItem" or "ContextMenu"))
            {
                var childHeaders = container.Elements()
                    .Where(e => e.Name.LocalName == "MenuItem")
                    .Select(e => e.Attribute("Header")?.Value ?? "")
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .ToList();
                var dups = childHeaders.GroupBy(h => h, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
                duplicateHeaders.AddRange(dups);
            }

            string mainXamlText = File.ReadAllText(mainXamlPath);
            bool hasV2Menus =
                mainXamlText.Contains("AddTorrentOrMagnet_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("ImportIdm_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("HashItem_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("VerifyItem_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("ExtractItem_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("ToggleSpeedGraph_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("Stats_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("SpeedRules_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("RemoteControl_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("CheckUpdates_Click", StringComparison.Ordinal) &&
                mainXamlText.Contains("BuildPackages_Click", StringComparison.Ordinal);

            Check("VB-03: MainWindow.xaml has no duplicate menu headers and wires all v2 menu actions",
                duplicateHeaders.Count == 0 && hasV2Menus,
                $"dups=[{string.Join(", ", duplicateHeaders)}], hasV2Menus={hasV2Menus}");
        }
        catch (Exception ex)
        {
            Check("VB-01..03: i18n & menu checks", false, ex.Message);
        }
    }

    // =========================================================================
    // 2. Settings Round-Trip & OptionsWindow STA Construction (VB-04 .. VB-05)
    // =========================================================================
    static async Task RunSettingsAndOptionsWindowChecksAsync(Assembly gdmAsm, string workRoot)
    {
        try
        {
            var optWinType = gdmAsm.GetType("Golan.Gdm.UI.OptionsWindow");
            var settingsType = gdmAsm.GetType("Golan.Gdm.Core.AppSettings")!;
            var copyMethod = optWinType?.GetMethod("CopySettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var skippedField = optWinType?.GetField("SkippedSettingsProps", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var skipped = (skippedField?.GetValue(null) as IEnumerable<string>)?.ToHashSet(StringComparer.Ordinal) ?? [];
            string[] legacyOrDerived = ["SchemaVersion", "ProxyProtectedPassword", "CategoryDefs", "ScheduleStart", "ScheduleStop"];

            var src = Activator.CreateInstance(settingsType)!;
            var dst = Activator.CreateInstance(settingsType)!;

            // Mutate every writable public non-JsonIgnore property on src to a non-default value
            var props = settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite
                            && p.GetCustomAttribute<JsonIgnoreAttribute>() == null
                            && !skipped.Contains(p.Name)
                            && !legacyOrDerived.Contains(p.Name, StringComparer.Ordinal))
                .ToList();

            foreach (var p in props)
            {
                if (p.PropertyType == typeof(bool))
                {
                    bool cur = (bool)p.GetValue(src)!;
                    p.SetValue(src, !cur);
                }
                else if (p.PropertyType == typeof(int))
                {
                    int cur = (int)p.GetValue(src)!;
                    p.SetValue(src, cur + 7);
                }
                else if (p.PropertyType == typeof(double))
                {
                    double cur = (double)p.GetValue(src)!;
                    p.SetValue(src, Math.Round(cur + 1.25, 2));
                }
                else if (p.PropertyType == typeof(string))
                {
                    p.SetValue(src, $"mutated_{p.Name}");
                }
                else if (p.PropertyType == typeof(DateTime?))
                {
                    p.SetValue(src, new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
                }
            }

            copyMethod?.Invoke(null, [dst, src]);

            var uncopied = new List<string>();
            foreach (var p in props)
            {
                var sv = p.GetValue(src);
                var dv = p.GetValue(dst);
                if (sv is IList sList && dv is IList dList)
                {
                    if (sList.Count != dList.Count) uncopied.Add(p.Name);
                }
                else if (!Equals(sv, dv))
                {
                    uncopied.Add($"{p.Name}({sv}!={dv})");
                }
            }

            bool showGraphCopied = !skipped.Contains("ShowSpeedGraph");
            Check("VB-04: All AppSettings properties (including V1..V5) round-trip through OptionsWindow.CopySettings",
                copyMethod != null && uncopied.Count == 0 && showGraphCopied,
                $"props={props.Count}, uncopied=[{string.Join(", ", uncopied.Take(6))}], showGraphCopied={showGraphCopied}");

            // VB-05: Construct OptionsWindow on STA thread and cycle tabs
            bool staOk = false;
            string staDetail = "";
            await RunOnStaAsync(() =>
            {
                gdmAsm.GetType("Golan.Gdm.Core.Loc")?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, ["ar"]);

                var dmType = gdmAsm.GetType("Golan.Gdm.Core.DownloadManager")!;
                var sInst = Activator.CreateInstance(settingsType)!;
                settingsType.GetMethod("Normalize")?.Invoke(sInst, null);
                var mgr = Activator.CreateInstance(dmType, [sInst])!;
                try
                {
                    var win = Activator.CreateInstance(optWinType!, [mgr, "torrent"])!;
                    var isValidProp = optWinType!.GetProperty("IsValid");
                    var pagesField = optWinType.GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic);
                    var pages = pagesField?.GetValue(win) as IDictionary;
                    bool hasAllV2Tabs = pages != null &&
                        pages.Contains("general") &&
                        pages.Contains("video") &&
                        pages.Contains("downloads") &&
                        pages.Contains("torrent") &&
                        pages.Contains("remote") &&
                        pages.Contains("updates");
                    bool valid = isValidProp != null && (bool)isValidProp.GetValue(win)!;
                    staOk = hasAllV2Tabs && valid;
                    staDetail = $"tabs={pages?.Count ?? 0}, valid={valid}";
                }
                finally
                {
                    StopReflectedManagerTimer(mgr);
                }
                return Task.CompletedTask;
            });

            Check("VB-05: OptionsWindow constructs all tabs (including torrent, remote, updates) cleanly on STA",
                staOk, staDetail);
        }
        catch (Exception ex)
        {
            Check("VB-04..05: Settings & OptionsWindow checks", false, (ex.InnerException ?? ex).Message);
        }
    }

    // =========================================================================
    // 3. HostResolvers & MEGA Dispatch (VB-06 .. VB-08)
    // =========================================================================
    static async Task RunResolverAndMegaChecksAsync(string workRoot)
    {
        try
        {
            bool gdrive = HostResolvers.All.Any(r => r is GoogleDriveResolver && r.CanHandle(new Uri("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz/view")));
            bool dropbox = HostResolvers.All.Any(r => r is DropboxResolver && r.CanHandle(new Uri("https://www.dropbox.com/s/abc123xyz/report.pdf?dl=0")));
            bool onedrive = HostResolvers.All.Any(r => r is OneDriveResolver && r.CanHandle(new Uri("https://1drv.ms/u/s!AbCdEfGh123")));
            bool mediafire = HostResolvers.All.Any(r => r is MediaFireResolver && r.CanHandle(new Uri("https://www.mediafire.com/file/xyz987/setup.zip/file")));
            bool ghBlob = HostResolvers.All.Any(r => r is GitHubResolver && r.CanHandle(new Uri("https://github.com/owner/repo/blob/main/README.md")));
            bool ghRelIgnored = !HostResolvers.All.Any(r => r.CanHandle(new Uri("https://github.com/owner/repo/releases/download/v1.0/app.zip")));
            bool mega = HostResolvers.All.Any(r => r is MegaResolver && r.CanHandle(new Uri("https://mega.nz/file/AbCdEfGh#0123456789abcdef0123456789abcdef0123456789M")));
            bool plainIgnored = !HostResolvers.All.Any(r => r.CanHandle(new Uri("https://example.com/archive.zip")));

            Check("VB-06: HostResolvers.All CanHandle selects exact resolver for all 6 sharing hosts and ignores plain/release URLs",
                gdrive && dropbox && onedrive && mediafire && ghBlob && ghRelIgnored && mega && plainIgnored);

            // VB-07: DownloadManager resolver hook & fallback with mocked HttpMessageInvoker
            var mgr = new DownloadManager(new AppSettings());
            try
            {
                var item = mgr.CreateHttp("https://www.dropbox.com/s/test12345/hello.txt?dl=0", fileName: "");
                item.SaveDir = Path.Combine(workRoot, "resolver_dl");
                Directory.CreateDirectory(item.SaveDir);

                var tryResolveMethod = typeof(DownloadManager).GetMethod("TryResolveSharingItemAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                using var okInvoker = new HttpMessageInvoker(new MockHttpHandler((req, _) =>
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = req,
                        Content = new ByteArrayContent(new byte[42]),
                    };
                    resp.Content.Headers.ContentLength = 42;
                    resp.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "hello_resolved.txt" };
                    return Task.FromResult(resp);
                }));

                bool resolvedOk = false;
                if (tryResolveMethod != null && tryResolveMethod.Invoke(mgr, [item, okInvoker, CancellationToken.None]) is Task<bool> resolveTask)
                    resolvedOk = await resolveTask;

                var failItem = mgr.CreateHttp("https://www.dropbox.com/s/test12345/fail.txt?dl=0", fileName: "fail.txt");
                failItem.SaveDir = item.SaveDir;
                using var failInvoker = new HttpMessageInvoker(new MockHttpHandler((_, _) =>
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))));
                bool fallbackTriggered = false;
                if (tryResolveMethod != null && tryResolveMethod.Invoke(mgr, [failItem, failInvoker, CancellationToken.None]) is Task<bool> failTask)
                    fallbackTriggered = !(await failTask) && !string.IsNullOrWhiteSpace(failItem.Stage);

                Check("VB-07: DownloadManager.TryResolveSharingItemAsync rewrites Dropbox sharing link to dl=1 direct URL and falls back on error",
                    resolvedOk && (item.ResolvedUrl ?? "").Contains("dl=1", StringComparison.OrdinalIgnoreCase) && item.TotalSize == 42 && fallbackTriggered,
                    $"resolvedUrl={item.ResolvedUrl}, size={item.TotalSize}, fallback={fallbackTriggered}");
            }
            finally
            {
                StopManagerTimer(mgr);
            }

            // VB-08: MEGA secret fragment stripping, DPAPI MegaKey protection, and CreateHttp auto-sanitization
            byte[] rawMegaKey = RandomNumberGenerator.GetBytes(32);
            string b64UrlKey = Convert.ToBase64String(rawMegaKey).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string rawUrl = $"https://mega.nz/file/AbCdEfGh#{b64UrlKey}";
            var mgrMega = new DownloadManager(new AppSettings());
            DownloadItem megaItem;
            try
            {
                megaItem = mgrMega.CreateHttp(rawUrl, fileName: "secret.bin");
                megaItem.SaveDir = workRoot;
            }
            finally
            {
                StopManagerTimer(mgrMega);
            }

            string json = JsonSerializer.Serialize(megaItem);
            var roundTripped = JsonSerializer.Deserialize<DownloadItem>(json)!;
            bool urlStripped = !megaItem.Url.Contains('#') && !json.Contains(b64UrlKey, StringComparison.Ordinal);
            bool rawB64NotInJson = !json.Contains(Convert.ToBase64String(rawMegaKey), StringComparison.Ordinal);
            bool keyRecovered = megaItem.NeedsMegaDecrypt && roundTripped.MegaKey != null && roundTripped.MegaKey.SequenceEqual(rawMegaKey);

            Check("VB-08: MEGA item strips #key fragment on CreateHttp, protects MegaKey with DPAPI in JSON, and recovers 32-byte key",
                urlStripped && rawB64NotInJson && !string.IsNullOrEmpty(roundTripped.ProtectedMegaKey) && keyRecovered);
        }
        catch (Exception ex)
        {
            Check("VB-06..08: HostResolvers & MEGA checks", false, ex.Message);
        }
    }

    // =========================================================================
    // 4. Magnet & .torrent Routing (VB-09 .. VB-14)
    // =========================================================================
    static async Task RunMagnetAndTorrentRoutingChecksAsync(Assembly gdmAsm, string gdmRoot, string extDir, string workRoot)
    {
        try
        {
            // VB-09: Program.ClassifyStartupArg
            var progType = gdmAsm.GetType("Golan.Gdm.Program");
            var classifyMethod = progType?.GetMethod("ClassifyStartupArg", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            string CallClassify(params string[] a) => classifyMethod?.Invoke(null, [a])?.ToString() ?? "";

            string sampleTorrent = Path.Combine(workRoot, "route_test.torrent");
            await CreateValidSingleFileTorrentAsync(workRoot, "payload.bin", 1024, sampleTorrent);

            string cMag = CallClassify("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=test");
            string cTor = CallClassify(sampleTorrent);
            string cHttp = CallClassify("https://example.com/file.zip");
            string cPack = CallClassify("--pack-extension", workRoot);
            string cCli = CallClassify("add", "https://example.com/file.zip");
            string cInst = CallClassify("--install");
            string cNative = CallClassify("--native-host");

            Check("VB-09: Program.ClassifyStartupArg routes magnet, .torrent, http, --pack-extension, cli, installer, and native-host",
                classifyMethod != null &&
                cMag == "MagnetUri" &&
                cTor == "TorrentFile" &&
                cHttp == "HttpUrl" &&
                cPack == "PackExtension" &&
                cCli == "CliVerb" &&
                cInst == "InstallerCommand" &&
                cNative == "NativeHost",
                $"mag={cMag}, tor={cTor}, http={cHttp}, pack={cPack}, cli={cCli}, inst={cInst}, native={cNative}");

            // VB-10: DownloadManager.AddMagnet & RemoveAsync cleanup
            var mgr = new DownloadManager(new AppSettings());
            try
            {
                string saveDir = Path.Combine(workRoot, "mag_save");
                Directory.CreateDirectory(saveDir);
                string magnetUri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Ubuntu.iso";
                var magItem = mgr.AddMagnet(magnetUri, saveDir: saveDir, startNow: false);

                string dummyPayload = magItem.FullPath;
                File.WriteAllBytes(dummyPayload, [1, 2, 3, 4]);

                bool addedOk = magItem.Kind == DownloadKind.Torrent &&
                               magItem.TorrentInfoHash == "0123456789abcdef0123456789abcdef01234567" &&
                               magItem.FileName == "Ubuntu.iso" &&
                               mgr.Items.Contains(magItem);

                await mgr.RemoveAsync(magItem, deleteFile: true);
                bool removedOk = !mgr.Items.Contains(magItem) && !File.Exists(dummyPayload) && Directory.Exists(saveDir);

                Check("VB-10: DownloadManager.AddMagnet creates Torrent item and RemoveAsync cleans up torrent payload",
                    addedOk && removedOk);

                // VB-11: DownloadManager.AddTorrentFileAsync
                var torItem = await mgr.AddTorrentFileAsync(sampleTorrent, saveDir: saveDir, startNow: false);
                bool torAddedOk = torItem.Kind == DownloadKind.Torrent &&
                                  torItem.TorrentInfoHash?.Length == 40 &&
                                  torItem.TotalSize == 1024 &&
                                  !string.IsNullOrEmpty(torItem.TorrentFilePath) &&
                                  File.Exists(torItem.TorrentFilePath);
                await mgr.RemoveAsync(torItem, deleteFile: true);

                Check("VB-11: DownloadManager.AddTorrentFileAsync caches metadata and populates TorrentInfoHash & TotalSize",
                    torAddedOk);

                // VB-12: DownloadManagerCliTarget magnet, --hash, --quality routing
                var cliTarget = new DownloadManagerCliTarget(mgr);
                var magReq = new CliAddRequest(
                    "magnet:?xt=urn:btih:fedcba9876543210fedcba9876543210fedcba98&dn=CliMagnet",
                    Folder: saveDir,
                    StartNow: false,
                    ExpectedHash: "sha256:" + new string('a', 64));
                var (magCliOk, _) = await cliTarget.AddDownloadAsync(magReq);
                var cliMagItem = mgr.Items.FirstOrDefault(i => i.TorrentInfoHash == "fedcba9876543210fedcba9876543210fedcba98");

                var httpReq = new CliAddRequest(
                    "https://example.com/archive.zip",
                    Folder: saveDir,
                    FileName: "cli_arch.zip",
                    Quality: "720p",
                    ExpectedHash: "sha256:" + new string('c', 64),
                    StartNow: false);
                var (httpCliOk, _) = await cliTarget.AddDownloadAsync(httpReq);
                var cliHttpItem = mgr.Items.FirstOrDefault(i => i.FileName == "cli_arch.zip");

                Check("VB-12: DownloadManagerCliTarget routes magnet: to AddMagnet and persists ExpectedHash & MediaQuality",
                    magCliOk && cliMagItem is { Kind: DownloadKind.Torrent } &&
                    httpCliOk && cliHttpItem != null &&
                    cliHttpItem.ExpectedHash == "sha256:" + new string('c', 64) &&
                    cliHttpItem.MediaQuality == "720");

                // VB-13: DownloadManagerRemoteTarget.Add magnet vs HTTP routing
                var remoteTarget = new DownloadManagerRemoteTarget(mgr);
                Guid remMagId = remoteTarget.Add("magnet:?xt=urn:btih:1111222233334444555566667777888899990000&dn=RemMagnet", QueueDef.MainId, start: false);
                Guid remHttpId = remoteTarget.Add("https://example.com/remote.bin", QueueDef.MainId, start: false);
                var remMagItem = mgr.Items.FirstOrDefault(i => i.Id == remMagId);
                var remHttpItem = mgr.Items.FirstOrDefault(i => i.Id == remHttpId);

                Check("VB-13: DownloadManagerRemoteTarget.Add routes magnet: to AddMagnet and https:// to CreateHttp",
                    remMagItem is { Kind: DownloadKind.Torrent, QueueId: var mq } && mq == QueueDef.MainId &&
                    remHttpItem is { Kind: DownloadKind.Http, QueueId: var hq } && hq == QueueDef.MainId);
            }
            finally
            {
                StopManagerTimer(mgr);
            }

            // VB-14: Browser extension & App.xaml.cs magnet support
            string bgJs = File.ReadAllText(Path.Combine(extDir, "background.js"));
            string contentJs = File.ReadAllText(Path.Combine(extDir, "content.js"));
            string appCs = File.ReadAllText(Path.Combine(gdmRoot, "App.xaml.cs"));
            bool extHandlesMagnet = bgJs.Contains("magnet:", StringComparison.OrdinalIgnoreCase) &&
                                    contentJs.Contains("magnet:", StringComparison.OrdinalIgnoreCase);
            bool appHandlesMagnet = appCs.Contains("MagnetValidator.IsMagnetUri", StringComparison.Ordinal) &&
                                    appCs.Contains("case \"cli\":", StringComparison.Ordinal);

            Check("VB-14: Browser extension (background.js + content.js) and App.HandleMessageAsync route magnet: links and CLI IPC",
                extHandlesMagnet && appHandlesMagnet);
        }
        catch (Exception ex)
        {
            Check("VB-09..14: Magnet & Torrent routing checks", false, ex.Message);
        }
    }

    // =========================================================================
    // 5. CLI -> Pipe -> Handler Round-Trip & --pack-extension (VB-15 .. VB-16)
    // =========================================================================
    static async Task RunCliAndPackExtensionChecksAsync(Assembly gdmAsm, string extDir, string workRoot)
    {
        try
        {
            var mgr = new DownloadManager(new AppSettings());
            try
            {
                var target = new DownloadManagerCliTarget(mgr);
                string saveDir = Path.Combine(workRoot, "cli_pipe");
                Directory.CreateDirectory(saveDir);

                async Task<JsonObject> ExecCliAsync(params string[] cliArgs)
                {
                    var parsed = CliParser.Parse(cliArgs);
                    if (!parsed.Ok || parsed.Command == null)
                        return new JsonObject { ["ok"] = false, ["error"] = parsed.ErrorMessage ?? "parse failed" };
                    var pipeMsg = parsed.Command.ToPipeMessage();
                    return await CliActions.HandleAsync(pipeMsg, target);
                }

                var r1 = await ExecCliAsync("add", "https://example.com/pkg.zip", "--folder", saveDir, "--name", "pkg.zip", "--hash", "sha256:" + new string('d', 64), "--quality", "1080p", "--later");
                var r2 = await ExecCliAsync("add", "magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&dn=PipeMag", "--folder", saveDir, "--later");
                var r3 = await ExecCliAsync("start-queue", "main");
                bool qRunning = mgr.IsQueueRunning(QueueDef.MainId);
                var r4 = await ExecCliAsync("stop-queue", "main");
                bool qStopped = !mgr.IsQueueRunning(QueueDef.MainId);
                var r5 = await ExecCliAsync("pause-all");
                var r6 = await ExecCliAsync("list", "--json");

                string listJson = r6["text"]?.GetValue<string>() ?? "[]";
                var listNode = JsonNode.Parse(listJson)?.AsArray();

                bool allOk = CliActions.MapPipeReplyToExitCode(r1) == 0 &&
                             CliActions.MapPipeReplyToExitCode(r2) == 0 &&
                             CliActions.MapPipeReplyToExitCode(r3) == 0 && qRunning &&
                             CliActions.MapPipeReplyToExitCode(r4) == 0 && qStopped &&
                             CliActions.MapPipeReplyToExitCode(r5) == 0 &&
                             CliActions.MapPipeReplyToExitCode(r6) == 0 &&
                             listNode != null && listNode.Count >= 2;

                Check("VB-15: Full CLI -> Pipe -> CliActions.HandleAsync -> DownloadManagerCliTarget round-trip succeeds for all verbs",
                    allOk, $"listCount={listNode?.Count ?? 0}");
            }
            finally
            {
                StopManagerTimer(mgr);
            }

            // VB-16: --pack-extension <outDir> via Program.RunPackExtension
            var progType = gdmAsm.GetType("Golan.Gdm.Program");
            var packMethod = progType?.GetMethod("RunPackExtension", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            string packOut = Path.Combine(workRoot, "packed_store");
            int exitCode = packMethod != null
                ? (int)packMethod.Invoke(null, [new[] { "--pack-extension", packOut }])!
                : -1;
            var builtFiles = Directory.Exists(packOut) ? Directory.GetFiles(packOut) : [];
            bool hasZipAndXpi = exitCode == 0 &&
                                builtFiles.Any(f => f.EndsWith("-chrome-edge.zip", StringComparison.OrdinalIgnoreCase)) &&
                                builtFiles.Any(f => f.EndsWith("-firefox.xpi", StringComparison.OrdinalIgnoreCase));

            Check("VB-16: Program.RunPackExtension([\"--pack-extension\", outDir]) builds Chrome/Edge .zip and Firefox .xpi",
                hasZipAndXpi, $"exit={exitCode}, files={builtFiles.Length}");
        }
        catch (Exception ex)
        {
            Check("VB-15..16: CLI & --pack-extension checks", false, ex.Message);
        }
    }

    // =========================================================================
    // 6. Runtime Integration Hooks (VB-17 .. VB-20)
    // =========================================================================
    static async Task RunRuntimeHookChecksAsync(string baseUrl, string served, string workRoot)
    {
        // VB-17: SpaceCheck pre-start hook in DownloadManager.Start
        try
        {
            var mgr = new DownloadManager(new AppSettings());
            try
            {
                string saveDir = Path.Combine(workRoot, "space_check");
                Directory.CreateDirectory(saveDir);
                var hugeItem = mgr.CreateHttp($"{baseUrl}/big.bin", fileName: "too_big.bin");
                hugeItem.SaveDir = saveDir;
                hugeItem.TotalSize = long.MaxValue - (128L * 1024 * 1024);
                mgr.Items.Insert(0, hugeItem);

                DownloadItem? failedEventItem = null;
                mgr.ItemFailed += i => failedEventItem = i;

                mgr.Start(hugeItem);

                bool blocked = !mgr.IsRunning(hugeItem) &&
                               hugeItem.Status == DownloadStatus.Error &&
                               !string.IsNullOrWhiteSpace(hugeItem.ErrorMessage) &&
                               failedEventItem == hugeItem;

                Check("VB-17: DownloadManager.Start runs SpaceCheck and fails immediately when free disk space is insufficient",
                    blocked, $"status={hugeItem.Status}, err='{hugeItem.ErrorMessage}'");
            }
            finally
            {
                StopManagerTimer(mgr);
            }
        }
        catch (Exception ex)
        {
            Check("VB-17: SpaceCheck pre-start hook", false, ex.Message);
        }

        // VB-18 & VB-19: Post-download F8+F9 hook (Integrity + Extractor) & F11 (SpeedHistory + StatsStore)
        try
        {
            string zipName = "vb_postdl_test.zip";
            string servedZipPath = Path.Combine(served, zipName);
            if (File.Exists(servedZipPath)) File.Delete(servedZipPath);
            using (var zf = ZipFile.Open(servedZipPath, ZipArchiveMode.Create))
            {
                var entry = zf.CreateEntry("hello_vb.txt");
                using var sw = new StreamWriter(entry.Open(), Encoding.UTF8);
                sw.Write("Hello from Phase B integration!");
            }
            byte[] zipBytes = File.ReadAllBytes(servedZipPath);
            string sha256Hex = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

            var mgr = new DownloadManager(new AppSettings());
            try
            {
                mgr.Settings.MaxConnections = 1;
                mgr.Settings.AutoExtractEnabled = false;
                HttpDownloader.Configure(mgr.Settings);

                string dlDir = Path.Combine(workRoot, "postdl");
                Directory.CreateDirectory(dlDir);

                var itemOk = mgr.CreateHttp($"{baseUrl}/{zipName}", fileName: "valid_archive.zip");
                itemOk.SaveDir = dlDir;
                itemOk.NameLocked = true;
                itemOk.ExpectedHash = "sha256:" + sha256Hex;
                itemOk.AutoExtract = true;

                var tcsOk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                mgr.ItemCompleted += it => { if (it == itemOk) tcsOk.TrySetResult(true); };
                mgr.ItemFailed += it => { if (it == itemOk) tcsOk.TrySetResult(false); };

                mgr.Add(itemOk, startNow: true);
                bool completedInTime = await Task.WhenAny(tcsOk.Task, Task.Delay(10_000)) == tcsOk.Task && await tcsOk.Task;

                string extractedFile = Path.Combine(dlDir, "valid_archive", "hello_vb.txt");
                bool okVerifiedAndExtracted = completedInTime &&
                                              itemOk.Status == DownloadStatus.Completed &&
                                              itemOk.HashState == HashVerifyState.Verified &&
                                              itemOk.ExtractState == ArchiveExtractState.Extracted &&
                                              File.Exists(extractedFile);

                // Also test mismatch skips extraction
                var itemBad = mgr.CreateHttp($"{baseUrl}/{zipName}", fileName: "bad_archive.zip");
                itemBad.SaveDir = dlDir;
                itemBad.NameLocked = true;
                itemBad.ExpectedHash = "sha256:" + new string('0', 64);
                itemBad.AutoExtract = true;

                var tcsBad = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                mgr.ItemCompleted += it => { if (it == itemBad) tcsBad.TrySetResult(true); };
                mgr.ItemFailed += it => { if (it == itemBad) tcsBad.TrySetResult(false); };

                mgr.Add(itemBad, startNow: true);
                await Task.WhenAny(tcsBad.Task, Task.Delay(10_000));

                string badExtractedDir = Path.Combine(dlDir, "bad_archive");
                bool badSkippedExtraction = itemBad.HashState == HashVerifyState.Mismatch &&
                                            !Directory.Exists(badExtractedDir) &&
                                            File.Exists(itemBad.FullPath);

                Check("VB-18: DownloadManager post-download hook verifies hash & auto-extracts valid archive, and skips extraction on hash mismatch",
                    okVerifiedAndExtracted && badSkippedExtraction,
                    $"okState=({itemOk.HashState},{itemOk.ExtractState}), badHashState={itemBad.HashState}");

                // VB-19: Verify StatsStore & SpeedHistory on DownloadManager
                typeof(DownloadManager).GetMethod("SampleSpeedAndStats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(mgr, null);
                var statsObj = typeof(DownloadManager).GetProperty("Stats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(mgr) as StatsStore;
                var speedHistObj = typeof(DownloadManager).GetProperty("SpeedHistory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(mgr) as SpeedHistory;
                var statsSummary = statsObj?.Query(StatsRange.Today);
                var speedSnap = speedHistObj?.Snapshot();
                Check("VB-19: DownloadManager records downloaded bytes & completed files in StatsStore and samples SpeedHistory",
                    statsSummary != null &&
                    speedSnap != null &&
                    statsSummary.TotalBytes >= zipBytes.Length &&
                    statsSummary.TotalFiles >= 1 &&
                    speedHistObj!.Capacity == 600,
                    $"bytes={statsSummary?.TotalBytes ?? -1}, files={statsSummary?.TotalFiles ?? -1}");
            }
            finally
            {
                StopManagerTimer(mgr);
                (typeof(DownloadManager).GetProperty("Stats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(mgr) as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Check("VB-18..19: Post-download & Stats/SpeedHistory checks", false, ex.Message);
        }

        // VB-20: PowerGuard & SpeedRuleController lifecycle integration
        try
        {
            var calls = new List<uint>();
            using var pg = new PowerGuard(clock: null, native: flags => calls.Add(flags))
            {
                KeepSystemAwake = true,
                KeepDisplayOn = false,
            };
            pg.Update(anyActive: true);
            bool held = pg.State == PowerGuardState.Holding && calls.Count == 1;
            pg.Dispose();
            bool released = pg.State == PowerGuardState.Idle && calls.Count == 2;

            var settings = new AppSettings
            {
                SpeedLimitEnabled = false,
                SpeedLimitKB = 0,
                SpeedRules =
                [
                    new SpeedRule
                    {
                        Enabled = true,
                        Days = DaysMask.All,
                        From = new TimeOnly(0, 0),
                        To = new TimeOnly(23, 59),
                        LimitKBps = 250,
                    }
                ]
            };
            var limiter = new SpeedLimiter();
            var now = new DateTime(2026, 10, 18, 14, 0, 0);
            using var ctrl = new SpeedRuleController(settings, limiter, () => now, startTimer: false);
            ctrl.Tick();
            bool appliedRule = limiter.BytesPerSecond == 250 * 1024L;
            ctrl.SetManualOverride(true, 100);
            bool appliedManual = ctrl.IsManualOverrideActive && limiter.BytesPerSecond == 100 * 1024L;

            Check("VB-20: PowerGuard and SpeedRuleController lifecycle and manual override work end-to-end",
                held && released && appliedRule && appliedManual);
        }
        catch (Exception ex)
        {
            Check("VB-20: PowerGuard & SpeedRuleController check", false, ex.Message);
        }
    }

    // =========================================================================
    // 7. Golan/GDM Independence & Identity Checks (VB-21 .. VB-23)
    // =========================================================================
    static void RunRebrandAndMigrationChecks(Assembly gdmAsm, string gdmRoot, string workRoot)
    {
        try
        {
            var appType = gdmAsm.GetType("Golan.Gdm.App");
            var verField = appType?.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            string appVer = verField?.GetRawConstantValue() as string ?? "";
            var defaultDlRoot = new AppSettings().DownloadRoot;

            Check("VB-21: Golan/GDM 2.x constants (Version, HostName, ExtensionId, FirefoxExtensionId, PipeName, NativeManifest, Default DownloadRoot)",
                System.Text.RegularExpressions.Regex.IsMatch(appVer ?? "", @"^2\.\d+\.\d+$")
                && BrowserIntegration.HostName == "com.golan.gdm"
                && BrowserIntegration.ExtensionId == "fkjplbaglahdiphiodkgidkdfkbebgfh"
                && BrowserIntegration.FirefoxExtensionId == "gdm@golan.local"
                && Path.GetFileName(BrowserIntegration.FirefoxXpiPath) == "gdm-firefox.xpi"
                && Path.GetFileName(AppPaths.NativeManifest) == "com.golan.gdm.json"
                && Path.GetFileName(BrowserIntegration.FirefoxNativeManifest) == "com.golan.gdm.firefox.json"
                && Path.GetFileName(AppPaths.LogFile) == "gdm.log"
                && Ipc.PipeName.StartsWith("Golan.GDM.", StringComparison.Ordinal)
                && defaultDlRoot.EndsWith(Path.Combine("Downloads", "Golan"), StringComparison.OrdinalIgnoreCase),
                $"ver={appVer}, host={BrowserIntegration.HostName}, extId={BrowserIntegration.ExtensionId}, fxId={BrowserIntegration.FirefoxExtensionId}, pipe={Ipc.PipeName}, dlRoot={defaultDlRoot}");

            // VB-22: Full independence from legacy app (no migration helpers, no legacy portable marker, no legacy registry/host fields)
            string legacyName = "\u0041dam";
            string legacyLower = "\u0061dam";
            bool noMigrateMethod = typeof(AppPaths).GetMethod("MigrateFrom" + legacyName + "IfNeeded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) == null;
            bool noLegacyHostField = typeof(BrowserIntegration).GetField("LegacyHostName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) == null;
            bool noLegacyUninstallField = typeof(Installer).GetField("LegacyUninstallSubKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) == null;
            bool legacyPortableIgnored = !AppPaths.IsPortableMode(null, @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\" + legacyName + ".portable", StringComparison.OrdinalIgnoreCase));
            bool gdmPortableAccepted = AppPaths.IsPortableMode(null, @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\GDM.portable", StringComparison.OrdinalIgnoreCase));
            var legacyExportFile = Path.Combine(workRoot, "legacy_export.json");
            File.WriteAllText(legacyExportFile, $$"""{"format":"{{legacyLower}}-export","version":1,"items":[{"url":"https://example.com/a.bin","fileName":"a.bin"}]}""");
            var mgrIndep = new DownloadManager(new AppSettings());
            bool legacyExportRejected;
            try
            {
                legacyExportRejected = Transfer.Import(legacyExportFile, mgrIndep).Count == 0;
            }
            finally
            {
                StopManagerTimer(mgrIndep);
            }

            Check("VB-22: GDM is completely independent (no legacy migration helpers, no legacy portable marker, no legacy host/uninstall constants, and legacy export format rejected)",
                noMigrateMethod && noLegacyHostField && noLegacyUninstallField && legacyPortableIgnored && gdmPortableAccepted && legacyExportRejected,
                $"noMigrate={noMigrateMethod}, noLegacyHost={noLegacyHostField}, noLegacyUninst={noLegacyUninstallField}, legacyPortableIgnored={legacyPortableIgnored}, legacyExportRejected={legacyExportRejected}");

            // VB-23: RemoteTls certificate subject uses CN=Golan Remote Control and replaces non-Golan certificates on disk
            using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var legacyReq = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=" + legacyName + " Remote Control", ecdsa, System.Security.Cryptography.HashAlgorithmName.SHA256);
            legacyReq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
            legacyReq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, true));
            legacyReq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var sanBuilder = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName(Environment.MachineName);
            legacyReq.CertificateExtensions.Add(sanBuilder.Build());
            using var legacyCert = legacyReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365));

            bool legacyCertRejected = !RemoteTls.IsCertificateValidFor(legacyCert, IPAddress.Loopback);

            // Persist foreign cert to disk and verify EnsureCertificate replaces it with CN=Golan Remote Control
            Directory.CreateDirectory(Path.GetDirectoryName(RemoteTls.CertFilePath)!);
            byte[] legacyPfx = legacyCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12);
            byte[] legacyDpapi = System.Security.Cryptography.ProtectedData.Protect(
                legacyPfx, Encoding.UTF8.GetBytes("Golan.GDM.v2.RemoteTls"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
            File.WriteAllBytes(RemoteTls.CertFilePath, legacyDpapi);

            using var golanCert = RemoteTls.EnsureCertificate(IPAddress.Loopback);
            bool golanCertValid = golanCert.Subject.Contains("CN=Golan Remote Control", StringComparison.OrdinalIgnoreCase)
                && RemoteTls.IsCertificateValidFor(golanCert, IPAddress.Loopback);

            Check("VB-23: RemoteTls generates CN=Golan Remote Control certificate and automatically replaces foreign certificates on disk",
                legacyCertRejected && golanCertValid,
                $"legacyRejected={legacyCertRejected}, subject='{golanCert.Subject}'");
        }
        catch (Exception ex)
        {
            Check("VB-21..23: Independence & Identity checks", false, ex.Message);
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    static async Task CreateValidSingleFileTorrentAsync(string workRoot, string fileName, int length, string torrentOutPath)
    {
        string seedDir = Path.Combine(workRoot, "tor_seed_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(seedDir);
        string seedFile = Path.Combine(seedDir, fileName);
        byte[] data = new byte[length];
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(seedFile, data);

        var creator = new TorrentCreator(TorrentType.V1Only)
        {
            PieceLength = 16 * 1024,
            Private = false,
        };
        await creator.CreateAsync(new TorrentFileSource(seedFile), torrentOutPath);
    }

    sealed class MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    static void StopManagerTimer(DownloadManager mgr)
    {
        if (typeof(DownloadManager).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mgr) is DispatcherTimer t)
            t.Stop();
    }

    static void StopReflectedManagerTimer(object mgr)
    {
        if (mgr.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mgr) is DispatcherTimer t)
            t.Stop();
    }

    static async Task RunOnStaAsync(Func<Task> action, int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var disp = Dispatcher.CurrentDispatcher;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(disp));
                action().GetAwaiter().GetResult();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                try { disp.InvokeShutdown(); } catch { }
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (completed != tcs.Task)
            throw new TimeoutException($"STA operation timed out after {timeoutMs}ms");
        await tcs.Task;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "GDM", "src", "GDM")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
