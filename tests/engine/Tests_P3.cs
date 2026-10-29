using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Golan.Gdm.Core;

public static class Tests_P3
{
    static bool _alreadyRan;
    static int _cachedFails;

    // [ModuleInitializer] (called from Program.cs instead: an async run inside a module initializer deadlocks)
    internal static void Init()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!_alreadyRan)
            {
                var cmd = Environment.GetCommandLineArgs();
                string served = cmd.Length > 1 ? cmd[1] : "";
                string outDir = cmd.Length > 2 ? cmd[2] : Path.Combine(Path.GetTempPath(), "gdm_p3_test");
                int port = cmd.Length > 3 && int.TryParse(cmd[3], out var p) ? p : 48003;
                string baseUrl = $"http://127.0.0.1:{port}";
                _cachedFails = RunAsync(baseUrl, served, outDir, port).GetAwaiter().GetResult();
            }
            if (_cachedFails > 0)
                Environment.ExitCode = Math.Max(1, Environment.ExitCode + _cachedFails);
        };
    }

    public static Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        if (_alreadyRan) return Task.FromResult(_cachedFails);
        _alreadyRan = true;

        int fails = 0, passes = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [P3] {name} {detail}");
            if (ok) passes++; else fails++;
        }

        string repoRoot = FindRepoRoot();
        string gdmRoot = Path.Combine(repoRoot, "GDM", "src", "GDM");
        string i18nDir = Path.Combine(gdmRoot, "i18n");

        string? dllPath = new[]
        {
            Path.Combine(gdmRoot, "bin", "Debug", "net10.0-windows", "GDM.dll"),
            Path.Combine(gdmRoot, "bin", "Release", "net10.0-windows", "GDM.dll"),
            Path.Combine(gdmRoot, "bin", "Release", "net10.0-windows", "win-x64", "GDM.dll"),
            Path.Combine(gdmRoot, "bin", "Debug", "net10.0-windows", "win-x64", "GDM.dll"),
        }
        .Where(File.Exists)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();

        Assembly gdmAsm = dllPath != null ? Assembly.LoadFrom(dllPath) : typeof(DownloadItem).Assembly;
        var asmLocType = gdmAsm.GetType("Golan.Gdm.Core.Loc");
        var asmLocInit = asmLocType?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
        var asmLocT = asmLocType?.GetMethod("T", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);

        Dictionary<string, string> LoadDiskTable(string lang)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(i18nDir, $"*.{lang}.json"))
            {
                var t = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
                foreach (var (k, v) in t) dict[k] = v;
            }
            return dict;
        }

        void EnsureLocTables(Type? locType, string lang)
        {
            if (locType == null) return;
            var stringsField = locType.GetField("_strings", BindingFlags.Static | BindingFlags.NonPublic);
            var fallbackField = locType.GetField("_fallback", BindingFlags.Static | BindingFlags.NonPublic);
            if (stringsField?.GetValue(null) is Dictionary<string, string> cur && cur.Count == 0)
            {
                var resolved = lang is "ar" or "en" ? lang : "ar";
                var primary = LoadDiskTable(resolved);
                var fb = resolved == "en" ? primary : LoadDiskTable("en");
                stringsField.SetValue(null, primary);
                fallbackField?.SetValue(null, fb);
            }
        }

        void InitLoc(string lang)
        {
            Loc.Init(lang);
            EnsureLocTables(typeof(Loc), lang);
            asmLocInit?.Invoke(null, new object?[] { lang });
            EnsureLocTables(asmLocType, lang);
        }
        string Translate(string key) => Loc.T(key);

        // =====================================================================
        // P3-1: IPC ReadTimeout, NativeHost 1MB frame cap, UseShellExecute, i18n
        // =====================================================================
        try
        {
            InitLoc("en");
            string enStartFailed = Translate("main.ipc.startFailed");
            InitLoc("ar");
            string arStartFailed = Translate("main.ipc.startFailed");
            Check("P3-1: main.ipc.startFailed localized in en and ar",
                enStartFailed != "main.ipc.startFailed" && !HasArabic(enStartFailed) &&
                arStartFailed != "main.ipc.startFailed" && HasArabic(arStartFailed),
                $"en='{enStartFailed}', ar='{arStartFailed}'");

            string ipcCode = File.ReadAllText(Path.Combine(gdmRoot, "Integration", "Ipc.cs"));
            Check("P3-1: Ipc.SendAsync uses 10-minute read timeout instead of 15s",
                ipcCode.Contains("TimeSpan.FromMinutes(10)") && !ipcCode.Contains("new CancellationTokenSource(TimeSpan.FromSeconds(15))"));
            Check("P3-1: NativeHost.HandleAsync launches --background with UseShellExecute = true",
                ipcCode.Contains("UseShellExecute = true"));
            Check("P3-1: NativeHost.HandleAsync uses Loc.T(\"main.ipc.startFailed\")",
                ipcCode.Contains("Loc.T(\"main.ipc.startFailed\")") && !ipcCode.Contains("تعذّر تشغيل برنامج آدم"));
        }
        catch (Exception ex)
        {
            Check("P3-1: IPC checks", false, ex.Message);
        }

        // =====================================================================
        // P3-2 & M7: Program.IsNativeHostInvocation & NativeHost 1MB cap
        // =====================================================================
        try
        {
            var progType = gdmAsm.GetType("Golan.Gdm.Program");
            var pubMethod = progType?.GetMethod("IsNativeHostInvocation", BindingFlags.Public | BindingFlags.Static);
            Check("M7 / P3-2: Program.IsNativeHostInvocation is public static", pubMethod != null);

            var anyMethod = pubMethod ?? progType?.GetMethod("IsNativeHostInvocation", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            bool CallIsNative(params string[] a) => anyMethod != null && (bool)anyMethod.Invoke(null, new object?[] { a })!;

            Check("M7 / P3-2: IsNativeHostInvocation([\"--native-host\"]) == true",
                CallIsNative("--native-host"));
            Check("M7 / P3-2: IsNativeHostInvocation([\"chrome-extension://abcdef/\"]) == true",
                CallIsNative("chrome-extension://abcdef/"));
            Check("M7 / P3-2: IsNativeHostInvocation([manifest, \"chrome-extension://abcdef/\"]) == true",
                CallIsNative(@"C:\path\com.golan.gdm.json", "chrome-extension://abcdef/"));
            Check("M7 / P3-2: IsNativeHostInvocation([manifest, \"gdm@golan.local\"]) == true",
                CallIsNative(@"C:\path\com.golan.gdm.firefox.json", "gdm@golan.local"));
            Check("M7 / P3-2: IsNativeHostInvocation([manifest, legacy firefox id]) == false",
                !CallIsNative(@"C:\path\com." + "\u0061dam" + @".downloader.json", "\u0061dam@\u0061dam-dm.local")
                && !CallIsNative("chrome-extension://" + "dndjbncb" + "blcblidf" + "dcpcbeao" + "nnpccfcc" + "/")
                && !CallIsNative(@"C:\path\com." + "\u0061dam" + @".downloader.json", "--flag"));
            Check("M7 / P3-2: IsNativeHostInvocation([]) == false",
                !CallIsNative());
            Check("M7 / P3-2: IsNativeHostInvocation([\"--background\"]) == false",
                !CallIsNative("--background"));
            Check("M7 / P3-2: IsNativeHostInvocation([\"https://example.com/file.zip\"]) == false",
                !CallIsNative("https://example.com/file.zip"));
            Check("M7 / P3-2: IsNativeHostInvocation([\"C:\\\\Users\\\\User\\\\Downloads\\\\file.json\"]) == false",
                !CallIsNative(@"C:\Users\User\Downloads\file.json"));

            string progCode = File.ReadAllText(Path.Combine(gdmRoot, "Program.cs"));
            int locInitIdx = progCode.IndexOf("Loc.Init(", StringComparison.Ordinal);
            int nativeRunIdx = progCode.IndexOf("NativeHost.Run()", StringComparison.Ordinal);
            Check("P3-2: Program.Main calls Loc.Init before NativeHost.Run()",
                locInitIdx >= 0 && nativeRunIdx > locInitIdx);

            string? exePath = new[]
            {
                Path.Combine(gdmRoot, "bin", "Debug", "net10.0-windows", "GDM.exe"),
                Path.Combine(gdmRoot, "bin", "Release", "net10.0-windows", "GDM.exe"),
                Path.Combine(gdmRoot, "bin", "Release", "net10.0-windows", "win-x64", "GDM.exe"),
                Path.Combine(gdmRoot, "bin", "Debug", "net10.0-windows", "win-x64", "GDM.exe"),
            }
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

            if (exePath != null)
            {
                var psi = new ProcessStartInfo(exePath, "--native-host")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi)!;
                byte[] lenBytes = BitConverter.GetBytes(2 * 1024 * 1024); // 2 MB > 1 MB cap
                proc.StandardInput.BaseStream.Write(lenBytes);
                proc.StandardInput.BaseStream.Flush();
                proc.StandardInput.Close();

                bool exitedCleanly = false;
                if (proc.WaitForExit(5000))
                {
                    exitedCleanly = proc.ExitCode == 0;
                }
                else
                {
                    try { proc.Kill(true); } catch { }
                }
                Check("M7 / P3-1: NativeHost rejects 2MB frame cleanly (<5s, exit 0)",
                    exitedCleanly, exitedCleanly ? "" : $"exitCode={(proc.HasExited ? proc.ExitCode : -999)}");
            }
            else
            {
                Check("M7 / P3-1: GDM.exe found for NativeHost test", false, "GDM.exe not found");
            }
        }
        catch (Exception ex)
        {
            Check("M7 / P3-2: NativeHost & IsNativeHostInvocation checks", false, ex.Message);
        }

        // =====================================================================
        // P3-3 & M6: Full i18n Key Parity, Placeholder Audit & Browser Names
        // =====================================================================
        try
        {
            string[] areas = ["common", "core", "main", "windows", "system", "media", "torrent", "integrity", "remote", "integration", "license", "guide"];
            var arAll = new Dictionary<string, string>(StringComparer.Ordinal);
            var enAll = new Dictionary<string, string>(StringComparer.Ordinal);
            var missingInEn = new List<string>();
            var missingInAr = new List<string>();
            var placeholderMismatches = new List<string>();

            var phRegex = new Regex(@"\{\d+\}", RegexOptions.Compiled);
            foreach (var area in areas)
            {
                var arPath = Path.Combine(i18nDir, $"{area}.ar.json");
                var enPath = Path.Combine(i18nDir, $"{area}.en.json");
                var ar = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(arPath))!;
                var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath))!;

                foreach (var kv in ar) arAll[kv.Key] = kv.Value;
                foreach (var kv in en) enAll[kv.Key] = kv.Value;

                foreach (var k in ar.Keys)
                {
                    if (!en.ContainsKey(k)) missingInEn.Add($"{area}:{k}");
                    else
                    {
                        var phAr = string.Join(",", phRegex.Matches(ar[k]).Select(m => m.Value).Distinct().OrderBy(x => x));
                        var phEn = string.Join(",", phRegex.Matches(en[k]).Select(m => m.Value).Distinct().OrderBy(x => x));
                        if (phAr != phEn) placeholderMismatches.Add($"{area}:{k} (ar=[{phAr}] vs en=[{phEn}])");
                    }
                }
                foreach (var k in en.Keys)
                    if (!ar.ContainsKey(k)) missingInAr.Add($"{area}:{k}");
            }

            Check("M6 / P3-3: ar/en JSON key parity across all 4 areas",
                missingInEn.Count == 0 && missingInAr.Count == 0,
                $"missingInEn=[{string.Join(", ", missingInEn)}] missingInAr=[{string.Join(", ", missingInAr)}]");

            Check("M6 / P3-3: ar/en format placeholder {n} parity",
                placeholderMismatches.Count == 0,
                string.Join("; ", placeholderMismatches));

            var referencedKeys = new HashSet<string>(StringComparer.Ordinal);
            var csLocRegex = new Regex(@"Loc\.T\(\s*""([^""]+)""", RegexOptions.Compiled);
            var xamlLocRegex = new Regex(@"\{(?:core|loc):T\s+([^\}\s,]+)", RegexOptions.Compiled);

            foreach (var file in Directory.EnumerateFiles(gdmRoot, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var text = File.ReadAllText(file);
                    foreach (Match m in csLocRegex.Matches(text))
                    {
                        var key = m.Groups[1].Value;
                        if (!key.EndsWith('.')) referencedKeys.Add(key);
                    }
                }
                else if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    var text = File.ReadAllText(file);
                    foreach (Match m in xamlLocRegex.Matches(text))
                    {
                        var key = m.Groups[1].Value;
                        if (!key.EndsWith('.')) referencedKeys.Add(key);
                    }
                }
            }

            var missingRefAr = referencedKeys.Where(k => !arAll.ContainsKey(k)).ToList();
            var missingRefEn = referencedKeys.Where(k => !enAll.ContainsKey(k)).ToList();
            Check("M6 / P3-3: all Loc.T / {core:T} keys in C#/XAML exist in ar and en tables",
                missingRefAr.Count == 0 && missingRefEn.Count == 0,
                $"referenced={referencedKeys.Count}, missingAr=[{string.Join(", ", missingRefAr)}], missingEn=[{string.Join(", ", missingRefEn)}]");

            InitLoc("en");
            var biType = gdmAsm.GetType("Golan.Gdm.BrowserIntegration")
                      ?? gdmAsm.GetType("Golan.Gdm.Integration.BrowserIntegration");
            var knownField = biType?.GetField("Known", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            bool browserHasArabicInEn = false;
            string browserSample = "";
            if (knownField?.GetValue(null) is System.Collections.IEnumerable browsers)
            {
                foreach (var b in browsers)
                {
                    var nameProp = b.GetType().GetProperty("Name");
                    var nameVal = nameProp?.GetValue(b) as string ?? "";
                    if (string.IsNullOrEmpty(nameVal) || HasArabic(nameVal))
                    {
                        browserHasArabicInEn = true;
                        browserSample = string.IsNullOrEmpty(nameVal) ? "<missing Name property>" : nameVal;
                        break;
                    }
                }
            }
            else
            {
                browserHasArabicInEn = true;
                browserSample = "<Known field not found>";
            }
            Check("M6 / P3-3: BrowserIntegration.Known browser names localized in English mode",
                !browserHasArabicInEn, browserSample);

            var browsersMethod = biType?.GetMethod("Browsers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var enBrowserNames = new List<string>();
            if (browsersMethod?.Invoke(null, null) is System.Collections.IEnumerable enBrowsers)
                foreach (var b in enBrowsers) enBrowserNames.Add(b.GetType().GetProperty("Name")?.GetValue(b) as string ?? "");
            Check("P3-3: BrowserIntegration.Browsers() returns English names under Loc.Init(\"en\")",
                enBrowserNames.Count == 7 && enBrowserNames.All(n => !string.IsNullOrEmpty(n) && !HasArabic(n)),
                string.Join(", ", enBrowserNames));

            InitLoc("ar");
            var arBrowserNames = new List<string>();
            if (browsersMethod?.Invoke(null, null) is System.Collections.IEnumerable arBrowsers)
                foreach (var b in arBrowsers) arBrowserNames.Add(b.GetType().GetProperty("Name")?.GetValue(b) as string ?? "");
            Check("P3-3: BrowserIntegration.Browsers() returns Arabic names under Loc.Init(\"ar\")",
                arBrowserNames.Count == 7 && arBrowserNames.All(n => HasArabic(n)),
                string.Join(", ", arBrowserNames));
        }
        catch (Exception ex)
        {
            Check("M6 / P3-3: i18n and BrowserIntegration checks", false, ex.Message);
        }

        // =====================================================================
        // P3-4: App.Version, HandleBrowserDownloadAsync force/rawUrl,
        //       ResolveDuplicate overwrite, OnBrowserDownloadDeclined, OnItemCompleted
        // =====================================================================
        try
        {
            var csprojXml = XDocument.Load(Path.Combine(gdmRoot, "GDM.csproj"));
            string csprojVersion = csprojXml.Descendants("Version").FirstOrDefault()?.Value ?? "";
            var appType = gdmAsm.GetType("Golan.Gdm.App");
            string appVersion = appType?.GetField("Version", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string ?? "";
            Check("P3-4: App.Version matches GDM.csproj",
                appVersion.Length > 0 && appVersion == csprojVersion,
                $"App.Version={appVersion}, csproj={csprojVersion}");

            string appCode = File.ReadAllText(Path.Combine(gdmRoot, "App.xaml.cs"));
            Check("P3-4: HandleBrowserDownloadAsync honors force flag on exception sites",
                appCode.Contains("\"force\"") && appCode.Contains("!force"));
            Check("P3-4: OnBrowserDownloadDeclined checks n < 2 and resets _declinesByHost",
                appCode.Contains("if (n < 2) return;") && appCode.Contains("_declinesByHost[u.Host] = 0;"));
            Check("P3-4: OnItemCompleted shows CompleteWindow even when ProgressWindow was open",
                !Regex.IsMatch(appCode, @"else\s+if\s*\(\s*showDialog\s*&&\s*!fromQueue\s*\)") &&
                Regex.IsMatch(appCode, @"if\s*\(\s*showDialog\s*&&\s*!fromQueue\s*\)"));
            Check("P3-4: ResolveDuplicate overwrite pauses existing and avoids async delete race on incoming path",
                appCode.Contains("Manager.Pause(existing)") && !appCode.Contains("_ = Manager.RemoveAsync(existing, deleteFile: true);"));
        }
        catch (Exception ex)
        {
            Check("P3-4: App.xaml.cs checks", false, ex.Message);
        }

        // =====================================================================
        // P3-5: AddDownloadWindow StartEarly _submitted = handled & UNC folder check
        // =====================================================================
        try
        {
            string addCode = File.ReadAllText(Path.Combine(gdmRoot, "UI", "AddDownloadWindow.xaml.cs"));
            Check("P3-5: AddDownloadWindow.StartEarly sets _submitted = handled when ResolveDuplicate returns false",
                Regex.IsMatch(addCode, @"if\s*\(\s*!App\.Instance\.ResolveDuplicate\(\s*_incoming!\s*,\s*out\s+var\s+handled\s*,\s*this\s*\)\s*\)\s*\{\s*_submitted\s*=\s*handled\s*;"));

            var addWinType = gdmAsm.GetType("Golan.Gdm.UI.AddDownloadWindow");
            var isAllowedMethod = addWinType?.GetMethod("IsAllowedFolder", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var settingsType = gdmAsm.GetType("Golan.Gdm.Core.AppSettings")!;
            var mockSettings = Activator.CreateInstance(settingsType)!;
            settingsType.GetProperty("DownloadRoot")?.SetValue(mockSettings, @"\\configured-nas\share\Downloads");
            bool rejectsUntrustedUnc = isAllowedMethod != null && !(bool)isAllowedMethod.Invoke(null, new object?[] { @"\\evil-server\share\out", mockSettings })!;
            bool allowsConfiguredUnc = isAllowedMethod != null && (bool)isAllowedMethod.Invoke(null, new object?[] { @"\\configured-nas\share\Downloads\sub", mockSettings })!;
            Check("P3-5: AddDownloadWindow.SubmitAsync validates unconfigured UNC folder before Directory.CreateDirectory",
                (addCode.Contains("IsUnc") || addCode.Contains(@"StartsWith(@""\\""")) && rejectsUntrustedUnc && allowsConfiguredUnc);
        }
        catch (Exception ex)
        {
            Check("P3-5: AddDownloadWindow checks", false, ex.Message);
        }

        // =====================================================================
        // P3-6: PropertiesWindow UNC validation, temp file move order, PathRegistry.Release
        // =====================================================================
        try
        {
            string propsCode = File.ReadAllText(Path.Combine(gdmRoot, "UI", "PropertiesWindow.xaml.cs"));
            Check("P3-6: PropertiesWindow validates unconfigured UNC folder before Directory.CreateDirectory",
                propsCode.Contains("IsUnc") || propsCode.Contains(@"StartsWith(@""\\"""));
            Check("P3-6: PropertiesWindow calls PathRegistry.Release(_item.Id) before PathRegistry.Claim",
                propsCode.Contains("PathRegistry.Release(_item.Id)") &&
                propsCode.IndexOf("PathRegistry.Release(_item.Id)", StringComparison.Ordinal) <
                propsCode.IndexOf("PathRegistry.Claim(_item.Id", StringComparison.Ordinal));
            Check("P3-6: PropertiesWindow logs temp move failures via Log.Write",
                propsCode.Contains("Log.Write("));

            // Verify temp move happens before _item.SaveDir = newDir in non-running branch
            int notRunningIdx = propsCode.IndexOf("else if (!_running && !_completed)", StringComparison.Ordinal);
            string notRunningBlock = notRunningIdx >= 0 ? propsCode[notRunningIdx..] : "";
            int moveIdx = notRunningBlock.IndexOf("File.Move(", StringComparison.Ordinal);
            int saveDirAssignIdx = notRunningBlock.IndexOf("_item.SaveDir = newDir;", StringComparison.Ordinal);
            Check("P3-6: PropertiesWindow moves originalTemp before mutating _item.SaveDir",
                moveIdx >= 0 && saveDirAssignIdx > moveIdx);
        }
        catch (Exception ex)
        {
            Check("P3-6: PropertiesWindow checks", false, ex.Message);
        }

        // =====================================================================
        // P3-7: MainWindow SortMemberPath, ColumnsWindow DefaultColumnOrder,
        //       ApplyColumns, ResumeAll_Click, SetLanguage
        // =====================================================================
        try
        {
            string mainXaml = File.ReadAllText(Path.Combine(gdmRoot, "UI", "MainWindow.xaml"));
            Check("P3-7: MainWindow.xaml Speed and Eta columns have SortMemberPath",
                mainXaml.Contains("SortMemberPath=\"Speed\"") && mainXaml.Contains("SortMemberPath=\"Eta\""));

            var colWinType = gdmAsm.GetType("Golan.Gdm.ColumnsWindow")
                          ?? gdmAsm.GetType("Golan.Gdm.UI.ColumnsWindow");
            var defOrderField = colWinType?.GetField("DefaultColumnOrder", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var defOrder = defOrderField?.GetValue(null) as string[] ?? [];
            Check("P3-7: ColumnsWindow.DefaultColumnOrder includes all 11 columns (including referrer and parentPage)",
                defOrder.Length == 11 && defOrder.Contains("referrer") && defOrder.Contains("parentPage"),
                $"count={defOrder.Length} [{string.Join(", ", defOrder)}]");

            string mainCs = string.Concat(Directory.GetFiles(Path.Combine(gdmRoot, "UI"), "MainWindow*.cs").Select(File.ReadAllText));
            Check("P3-7: MainWindow.ApplyColumns detects reset via All(c => c.Width == 0)",
                mainCs.Contains("_settings.Columns.All(c => c.Width == 0)"));
            Check("P3-7: MainWindow.ResumeAll_Click respects MaxSimultaneous and starts queues",
                mainCs.Contains("MaxSimultaneous") && mainCs.Contains("StartQueue"));
            Check("P3-7: MainWindow.SetLanguage passes executable via GDM_RESTART_EXE env var",
                mainCs.Contains("GDM_RESTART_EXE") && !mainCs.Contains("start \\\"\\\" \\\"{exe}\\\""));
        }
        catch (Exception ex)
        {
            Check("P3-7: MainWindow & ColumnsWindow checks", false, ex.Message);
        }

        // =====================================================================
        // P3-8: CompleteWindow, DuplicateWindow RTL/LTR layout, DeleteConfirm quotes
        // =====================================================================
        try
        {
            string completeXaml = File.ReadAllText(Path.Combine(gdmRoot, "UI", "CompleteWindow.xaml"));
            Check("P3-8: CompleteWindow.xaml sets HorizontalAlignment=\"Left\" on NameText, UrlText, PathText",
                Regex.IsMatch(completeXaml, @"x:Name=""NameText""[^>]*HorizontalAlignment=""Left""") &&
                Regex.IsMatch(completeXaml, @"x:Name=""UrlText""[^>]*HorizontalAlignment=""Left""") &&
                Regex.IsMatch(completeXaml, @"x:Name=""PathText""[^>]*HorizontalAlignment=""Left"""));

            string dupXaml = File.ReadAllText(Path.Combine(gdmRoot, "UI", "DuplicateWindow.xaml"));
            string dupCs = File.ReadAllText(Path.Combine(gdmRoot, "UI", "DuplicateWindow.xaml.cs"));
            Check("P3-8: DuplicateWindow separates ExistingNameText and ExistingStatusText",
                dupXaml.Contains("x:Name=\"ExistingNameText\"") &&
                dupXaml.Contains("x:Name=\"ExistingStatusText\"") &&
                dupCs.Contains("ExistingNameText.FlowDirection") &&
                dupCs.Contains("ExistingStatusText.Text"));

            string dialogsCs = File.ReadAllText(Path.Combine(gdmRoot, "UI", "Dialogs.cs"));
            Check("P3-8: DeleteConfirm.Ask uses Loc.IsRtl to pick guillemets vs ASCII quotes",
                dialogsCs.Contains("Loc.IsRtl") && dialogsCs.Contains("\\\"{items[0].FileName}\\\""));
        }
        catch (Exception ex)
        {
            Check("P3-8: CompleteWindow, DuplicateWindow, Dialogs checks", false, ex.Message);
        }

        Loc.Init("ar");
        _cachedFails = fails;
        Console.WriteLine($"[P3 Summary] {(fails == 0 ? $"ALL {passes} P3 CHECKS PASSED" : $"{fails} P3 CHECKS FAILED, {passes} passed")}");
        return Task.FromResult(fails);
    }

    static bool HasArabic(string s)
    {
        foreach (char c in s)
            if (c >= 0x0600 && c <= 0x06FF) return true;
        return false;
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
