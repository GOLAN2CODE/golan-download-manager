using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const int WM_MOUSEMOVE = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private static readonly List<int> StartedPids = [];
    private static int _passes;
    private static int _fails;
    private static string _outDir = "";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var baseDir = AppContext.BaseDirectory;
        var uiTestDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        if (!File.Exists(Path.Combine(uiTestDir, "UiTest.csproj")))
            uiTestDir = Directory.GetCurrentDirectory();

        var gdmRoot = Path.GetFullPath(Path.Combine(uiTestDir, "..", ".."));
        var gdmExe = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? Path.GetFullPath(args[0])
            : Path.Combine(gdmRoot, "src", "GDM", "bin", "Release", "net10.0-windows", "GDM.exe");
        int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 18763;

        _outDir = Path.Combine(uiTestDir, "out");
        Directory.CreateDirectory(_outDir);

        if (!File.Exists(gdmExe))
        {
            var gdmProj = Path.Combine(gdmRoot, "src", "GDM", "GDM.csproj");
            if (File.Exists(gdmProj))
            {
                using var buildProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList = { "build", gdmProj, "-c", "Release", "-v", "minimal" },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                StartedPids.Add(buildProc.Id);
                await buildProc.WaitForExitAsync();
                StartedPids.Remove(buildProc.Id);
            }
            if (!File.Exists(gdmExe))
            {
                Console.WriteLine($"FAIL: GDM.exe not found at {gdmExe}");
                return 1;
            }
        }

        var engineDir = Path.Combine(gdmRoot, "tests", "engine");
        var servedDir = Path.Combine(engineDir, "served");
        Directory.CreateDirectory(servedDir);

        var bigPath = Path.Combine(servedDir, "big.bin");
        if (!File.Exists(bigPath) || new FileInfo(bigPath).Length != 40 * 1024 * 1024)
        {
            var rng = new Random(42);
            var buf = new byte[40 * 1024 * 1024];
            rng.NextBytes(buf);
            File.WriteAllBytes(bigPath, buf);
        }
        var smallPath = Path.Combine(servedDir, "small.bin");
        if (!File.Exists(smallPath) || new FileInfo(smallPath).Length != 65536)
        {
            var rng = new Random(1337);
            var buf = new byte[65536];
            rng.NextBytes(buf);
            File.WriteAllBytes(smallPath, buf);
        }

        string expectedBigHash = HashFile(bigPath);
        string expectedSmallHash = HashFile(smallPath);

        Process? serverProc = null;
        try
        {
            if (!await IsPortListeningAsync(port))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    WorkingDirectory = engineDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(Path.Combine(engineDir, "testserver.py"));
                psi.ArgumentList.Add(servedDir);
                psi.ArgumentList.Add(port.ToString());
                serverProc = Process.Start(psi)!;
                StartedPids.Add(serverProc.Id);

                bool ready = false;
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(100);
                    if (await IsPortListeningAsync(port)) { ready = true; break; }
                }
                Check("testserver: listening on port " + port, ready, $"pid={serverProc.Id}");
            }

            foreach (var lang in new[] { "ar", "en" })
            {
                Console.WriteLine($"\n=== Running UI Automation Suite (Language={lang}) ===");
                await RunLanguageSuiteAsync(gdmExe, lang, port, expectedSmallHash, expectedBigHash);
            }
        }
        finally
        {
            foreach (int pid in StartedPids)
                KillOwnedProcess(pid);
        }

        Console.WriteLine($"\n=== UI Test Summary: {_passes} passed, {_fails} failed ===");
        return _fails == 0 ? 0 : 1;
    }

    static readonly System.Net.Http.HttpClient UiHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    static long TokenBytes(int port, string token)
    {
        try { return long.Parse(UiHttp.GetStringAsync($"http://127.0.0.1:{port}/tokbytes?token={token}").GetAwaiter().GetResult()); }
        catch { return 0; }
    }

    /// <summary>Talks to the isolated Golan the way the browser's native host does (same per-home pipe name).</summary>
    static async Task<JsonObject?> SendToGolanAsync(string gdmHome, JsonObject message)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gdmHome.Trim()).ToUpperInvariant()));
        var user = new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var name = "Golan.GDM." + user + "." + Convert.ToHexString(hash, 0, 6);
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        var utf8 = new UTF8Encoding(false);
        using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, utf8, leaveOpen: true);
        await writer.WriteLineAsync(message.ToJsonString());
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return line == null ? null : JsonNode.Parse(line) as JsonObject;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "  [PASS]" : "  [FAIL]")} {name}{(string.IsNullOrEmpty(detail) ? "" : $" ({detail})")}");
        if (ok) _passes++; else _fails++;
    }

    private static async Task RunLanguageSuiteAsync(
        string gdmExe,
        string lang,
        int port,
        string expectedSmallHash,
        string expectedBigHash)
    {
        string runId = Guid.NewGuid().ToString("N")[..8];
        string gdmHome = Path.Combine(Path.GetTempPath(), $"gdm-ui-home-{lang}-{runId}");
        string roamingDir = Path.Combine(gdmHome, "Roaming");
        string localDir = Path.Combine(gdmHome, "Local");
        string dlDir = Path.Combine(gdmHome, "Downloads");
        string tempDir = Path.Combine(gdmHome, "Temp");
        Directory.CreateDirectory(roamingDir);
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(dlDir);
        Directory.CreateDirectory(tempDir);

        string settingsPath = Path.Combine(roamingDir, "settings.json");
        string downloadsPath = Path.Combine(roamingDir, "downloads.json");

        var initialSettings = new JsonObject
        {
            ["FirstRun"] = false,
            ["Language"] = lang,
            ["DownloadRoot"] = dlDir,
            ["DownloadFolder"] = dlDir,
            ["TempDir"] = tempDir,
            ["UseCategoryFolders"] = false,
            ["ShowAddDialogFromBrowser"] = true, // only browser downloads use it (S4b, S4c)
            ["ShowStartDialog"] = false,
            ["ShowProgressWindow"] = false,
            ["ShowCompleteDialog"] = false,
            ["ClipboardMonitor"] = false,
            ["SoundsEnabled"] = false,
            ["EnableSounds"] = false,
            ["CloseToTray"] = false,
            ["ConfirmDelete"] = false,
            ["SpeedLimitKB"] = 4096,
            ["SpeedLimitEnabled"] = false,
            ["ShowSpeedGraph"] = false,
            ["ShowDropTarget"] = false,
            ["RemoteEnabled"] = false,
        };
        File.WriteAllText(settingsPath, initialSettings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        Process? appProc = null;
        Window? mainWin = null;
        using var automation = new UIA3Automation();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gdmExe,
                WorkingDirectory = Path.GetDirectoryName(gdmExe)!,
                UseShellExecute = false,
            };
            psi.Environment["GDM_HOME"] = gdmHome;

            appProc = Process.Start(psi)!;
            StartedPids.Add(appProc.Id);

            mainWin = await WaitForMainWindowAsync(automation, appProc.Id, TimeSpan.FromSeconds(15));
            if (mainWin == null)
            {
                Check($"[{lang}] S1: Main window opened", false, "Timed out waiting for MainWindow");
                return;
            }

            // -----------------------------------------------------------------
            // Scenario 1: Main window & menus
            // -----------------------------------------------------------------
            await RunScenarioAsync(lang, "S1-MainWindowAndMenus", mainWin, async () =>
            {
                string expectedTitle = lang == "ar" ? "جولان - مدير التنزيل" : "Golan Download Manager";
                string actualTitle = mainWin.Title ?? "";
                Check($"[{lang}] S1.1: Main window opens with localized title", actualTitle == expectedTitle, $"title='{actualTitle}'");

                var menuBar = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")));
                Check($"[{lang}] S1.2: MainMenu element found", menuBar != null);

                var topMenus = menuBar!.FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem));
                Check($"[{lang}] S1.3: MainMenu has 5 top-level menus (Tasks, File, Downloads, View, Help)", topMenus.Length == 5, $"count={topMenus.Length}");

                // Verify FlowDirection via layout coordinates of first (Tasks) vs last (Help) menu item
                var firstRect = topMenus[0].BoundingRectangle;
                var lastRect = topMenus[4].BoundingRectangle;
                bool rtlLayout = firstRect.Left > lastRect.Left;
                Check(
                    $"[{lang}] S1.4: FlowDirection matches {(lang == "ar" ? "RightToLeft" : "LeftToRight")}",
                    lang == "ar" ? rtlLayout : !rtlLayout,
                    $"firstX={firstRect.Left}, lastX={lastRect.Left}");

                string[] menuIds = ["TasksMenu", "FileMenu", "DownloadsMenu", "ViewMenu", "HelpMenu"];
                for (int i = 0; i < menuIds.Length; i++)
                {
                    var mi = WaitForElement(() => menuBar.FindFirstDescendant(cf => cf.ByAutomationId(menuIds[i]))) ?? topMenus[i];
                    var ec = mi.Patterns.ExpandCollapse.Pattern;
                    ExpandMenuItem(mainWin, mi);
                    var subItems = mi.FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem));
                    bool expandedOk = ec.ExpandCollapseState.Value == ExpandCollapseState.Expanded && subItems.Length > 0;
                    CollapseMenuItem(mi);
                    bool collapsedOk = ec.ExpandCollapseState.Value == ExpandCollapseState.Collapsed;
                    Check($"[{lang}] S1.5.{i + 1}: Menu '{menuIds[i]}' ({mi.Name}) expands and collapses cleanly", expandedOk && collapsedOk, $"subItems={subItems.Length}");
                }
                await Task.CompletedTask;
            });

            // -----------------------------------------------------------------
            // Scenario 2: Options window
            // -----------------------------------------------------------------
            await RunScenarioAsync(lang, "S2-OptionsWindow", mainWin, async () =>
            {
                // Open Options via Downloads -> Options (by AutomationId)
                var openTask = OpenMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "DownloadsOptionsMenu");
                var optWin = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                Check($"[{lang}] S2.1: Options window opened", optWin != null, optWin?.Title ?? "");

                var navList = WaitForElement(() => optWin!.FindFirstDescendant(cf => cf.ByAutomationId("NavList")));
                Check($"[{lang}] S2.2: NavList found in OptionsWindow", navList != null);

                var navItems = navList!.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
                Check($"[{lang}] S2.3: NavList has all 14 navigation tabs", navItems.Length == 14, $"count={navItems.Length}");

                bool allTabsSelected = true;
                for (int i = 0; i < navItems.Length; i++)
                {
                    var selPattern = navItems[i].Patterns.SelectionItem.Pattern;
                    selPattern.Select();
                    if (!await WaitUntilAsync(() => selPattern.IsSelected.Value, TimeSpan.FromSeconds(2)))
                        allTabsSelected = false;
                }
                Check($"[{lang}] S2.4: Selected every navigation tab in NavList via SelectionItemPattern", allTabsSelected);

                // Select General tab (index 0) and toggle ClipboardMonitor checkbox (index 1 on General page)
                navItems[0].Patterns.SelectionItem.Pattern.Select();
                await WaitUntilAsync(() =>
                    navItems[0].Patterns.SelectionItem.Pattern.IsSelected.Value &&
                    optWin!.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).Length >= 2,
                    TimeSpan.FromSeconds(2));

                var checkBoxes = optWin!.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
                Check($"[{lang}] S2.5: Found checkboxes on General tab", checkBoxes.Length >= 2, $"count={checkBoxes.Length}");
                var clipCheck = checkBoxes[1]; // RunOnStartup is [0], ClipboardMonitor is [1]
                var initialState = clipCheck.Patterns.Toggle.Pattern.ToggleState.Value;
                clipCheck.Patterns.Toggle.Pattern.Toggle();
                await WaitUntilAsync(() => clipCheck.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On, TimeSpan.FromSeconds(2));
                var toggledState = clipCheck.Patterns.Toggle.Pattern.ToggleState.Value;
                Check($"[{lang}] S2.6: Toggled ClipboardMonitor checkbox via TogglePattern", initialState == ToggleState.Off && toggledState == ToggleState.On);

                // Invoke Save button (first direct Button child of OptionsWindow; Cancel is second)
                var saveBtn = WaitForElement(() => GetDirectButtons(optWin).FirstOrDefault())!;
                saveBtn.Patterns.Invoke.Pattern.Invoke();
                await openTask.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForWindowClosedAsync(optWin, TimeSpan.FromSeconds(5));

                bool persistedTrue = await WaitUntilAsync(() => ReadSettingBool(settingsPath, "ClipboardMonitor") == true, TimeSpan.FromSeconds(2));
                Check($"[{lang}] S2.7: Saving Options persists ClipboardMonitor=true to settings.json", persistedTrue);

                // Reopen Options, toggle ClipboardMonitor off, and invoke Cancel
                var reopenTask = OpenMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "DownloadsOptionsMenu");
                var optWin2 = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                Check($"[{lang}] S2.8: Reopened Options window", optWin2 != null);

                await WaitUntilAsync(() => optWin2!.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).Length >= 2, TimeSpan.FromSeconds(2));
                var checkBoxes2 = optWin2!.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
                var clipCheck2 = checkBoxes2[1];
                clipCheck2.Patterns.Toggle.Pattern.Toggle();
                await WaitUntilAsync(() => clipCheck2.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off, TimeSpan.FromSeconds(2));
                Check($"[{lang}] S2.9: Toggled ClipboardMonitor to Off before Cancel", clipCheck2.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off);

                var cancelBtn = WaitForElement(() =>
                {
                    var btns = GetDirectButtons(optWin2);
                    return btns.Length > 1 ? btns[1] : null;
                })!;
                cancelBtn.Patterns.Invoke.Pattern.Invoke();
                await reopenTask.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForWindowClosedAsync(optWin2, TimeSpan.FromSeconds(5));

                bool stillTrueAfterCancel = ReadSettingBool(settingsPath, "ClipboardMonitor") == true;
                Check($"[{lang}] S2.10: Cancelling Options does NOT persist change to settings.json", stillTrueAfterCancel);

                // Restore ClipboardMonitor=false via Options -> Save so clipboard does not interfere later
                var restoreTask = OpenMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "DownloadsOptionsMenu");
                var optWin3 = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                await WaitUntilAsync(() => optWin3!.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).Length >= 2, TimeSpan.FromSeconds(2));
                var clipCheck3 = optWin3!.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))[1];
                clipCheck3.Patterns.Toggle.Pattern.Toggle();
                await WaitUntilAsync(() => clipCheck3.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off, TimeSpan.FromSeconds(2));
                var saveBtn3 = WaitForElement(() => GetDirectButtons(optWin3).FirstOrDefault())!;
                saveBtn3.Patterns.Invoke.Pattern.Invoke();
                await restoreTask.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForWindowClosedAsync(optWin3, TimeSpan.FromSeconds(5));
                await WaitUntilAsync(() => ReadSettingBool(settingsPath, "ClipboardMonitor") == false, TimeSpan.FromSeconds(2));
            });

            // -----------------------------------------------------------------
            // Scenario 3: Add download
            // -----------------------------------------------------------------
            string smallFileName = $"ui-small-{lang}.bin";
            await RunScenarioAsync(lang, "S3-AddDownload", mainWin, async () =>
            {
                // Open Add dialog via Tasks -> Add New Download (by AutomationId)
                var openAddTask = OpenMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksAddUrlMenu");
                var addWin = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                Check($"[{lang}] S3.1: AddDownloadWindow opened", addWin != null, addWin?.Title ?? "");

                var urlBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("UrlBox")))!;
                var nameBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("NameBox")))!;
                var folderBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("FolderBox")))!;
                var startBtn = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("StartBtn")))!;

                string url = $"http://127.0.0.1:{port}/small.bin?lang={lang}";
                urlBox.Patterns.Value.Pattern.SetValue(url);
                await WaitUntilAsync(() => (urlBox.Patterns.Value.Pattern.Value.Value ?? "") == url, TimeSpan.FromSeconds(2));
                nameBox.Patterns.Value.Pattern.SetValue(smallFileName);
                folderBox.Patterns.Value.Pattern.SetValue(dlDir);
                await WaitUntilAsync(() =>
                    (nameBox.Patterns.Value.Pattern.Value.Value ?? "") == smallFileName &&
                    (folderBox.Patterns.Value.Pattern.Value.Value ?? "") == dlDir &&
                    startBtn.IsEnabled,
                    TimeSpan.FromSeconds(2));

                startBtn.Patterns.Invoke.Pattern.Invoke();
                await openAddTask.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForWindowClosedAsync(addWin!, TimeSpan.FromSeconds(5));

                string completedText = lang == "ar" ? "مكتمل" : "Complete";
                bool reachedCompleted = await WaitForGridRowStatusAsync(mainWin, smallFileName, s => s == completedText, TimeSpan.FromSeconds(15));
                Check($"[{lang}] S3.2: Row '{smallFileName}' in DataGrid reached Completed", reachedCompleted);

                string downloadedSmallPath = Path.Combine(dlDir, smallFileName);
                bool hashMatches = File.Exists(downloadedSmallPath) && HashFile(downloadedSmallPath) == expectedSmallHash;
                Check($"[{lang}] S3.3: Downloaded file SHA-256 matches served/small.bin", hashMatches);
            });

            // -----------------------------------------------------------------
            // Scenario 4: Pause/resume a 40 MB download
            // -----------------------------------------------------------------
            string bigFileName = $"ui-big-{lang}.bin";
            await RunScenarioAsync(lang, "S4-PauseResume40MB", mainWin, async () =>
            {
                // Turn on Speed Limiter (4 MB/s) via Downloads -> Speed Limiter -> Turn On so 40 MB takes ~10s while active
                await InvokeNestedMenuItemByAutomationIdAsync(mainWin, "DownloadsMenu", "SpeedLimiterSubMenu", "LimiterOnMenu");
                await WaitUntilAsync(() => ReadSettingBool(settingsPath, "SpeedLimitEnabled") == true, TimeSpan.FromSeconds(3));

                // Open AddDownloadWindow and start /big.bin
                var openAddTask = OpenMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksAddUrlMenu");
                var addWin = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                var urlBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("UrlBox")))!;
                var nameBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("NameBox")))!;
                var folderBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("FolderBox")))!;
                var startBtn = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("StartBtn")))!;

                string bigUrl = $"http://127.0.0.1:{port}/big.bin?lang={lang}";
                urlBox.Patterns.Value.Pattern.SetValue(bigUrl);
                await WaitUntilAsync(() => (urlBox.Patterns.Value.Pattern.Value.Value ?? "") == bigUrl, TimeSpan.FromSeconds(2));
                nameBox.Patterns.Value.Pattern.SetValue(bigFileName);
                folderBox.Patterns.Value.Pattern.SetValue(dlDir);
                await WaitUntilAsync(() =>
                    (nameBox.Patterns.Value.Pattern.Value.Value ?? "") == bigFileName &&
                    (folderBox.Patterns.Value.Pattern.Value.Value ?? "") == dlDir &&
                    startBtn.IsEnabled,
                    TimeSpan.FromSeconds(2));

                startBtn.Patterns.Invoke.Pattern.Invoke();
                await openAddTask.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForWindowClosedAsync(addWin!, TimeSpan.FromSeconds(5));

                // Wait until row appears and downloaded bytes > 0, select it, and invoke PauseBtn
                string pausedPrefix = lang == "ar" ? "متوقف مؤقتًا" : "Paused";
                string completedText = lang == "ar" ? "مكتمل" : "Complete";

                var bigRow = await WaitForGridRowAsync(mainWin, bigFileName, TimeSpan.FromSeconds(10));
                Check($"[{lang}] S4.1: 40 MB download row '{bigFileName}' appeared in DataGrid", bigRow != null);

                // Poll until downloaded bytes > 0 on disk or in row progress before pausing
                string partialPath = Path.Combine(dlDir, bigFileName + ".gdm");
                await WaitUntilAsync(() =>
                {
                    try
                    {
                        if (File.Exists(partialPath) && new FileInfo(partialPath).Length > 0)
                            return true;
                        var pb = bigRow?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ProgressBar));
                        return pb != null && pb.Patterns.RangeValue.IsSupported && pb.Patterns.RangeValue.Pattern.Value.Value > 0;
                    }
                    catch
                    {
                        return false;
                    }
                }, TimeSpan.FromSeconds(10));

                bigRow = await WaitForGridRowAsync(mainWin, bigFileName, TimeSpan.FromSeconds(5));
                bigRow!.Patterns.SelectionItem.Pattern.Select();
                await WaitUntilAsync(() => bigRow.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(2));

                var pauseBtn = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("PauseBtn")))!;
                await WaitUntilEnabledAsync(pauseBtn, TimeSpan.FromSeconds(10));
                pauseBtn.Patterns.Invoke.Pattern.Invoke();

                bool isPaused = await WaitForGridRowStatusAsync(mainWin, bigFileName, s => s.StartsWith(pausedPrefix, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
                Check($"[{lang}] S4.2: Invoking Pause sets row status to Paused", isPaused);

                // Turn off Speed Limiter so resume completes at full speed
                await InvokeNestedMenuItemByAutomationIdAsync(mainWin, "DownloadsMenu", "SpeedLimiterSubMenu", "LimiterOffMenu");
                await WaitUntilAsync(() => ReadSettingBool(settingsPath, "SpeedLimitEnabled") == false, TimeSpan.FromSeconds(3));

                bigRow = await WaitForGridRowAsync(mainWin, bigFileName, TimeSpan.FromSeconds(5));
                bigRow!.Patterns.SelectionItem.Pattern.Select();
                await WaitUntilAsync(() => bigRow.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(2));

                var resumeBtn = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("ResumeBtn")))!;
                await WaitUntilEnabledAsync(resumeBtn, TimeSpan.FromSeconds(10));
                resumeBtn.Patterns.Invoke.Pattern.Invoke();

                bool isCompleted = await WaitForGridRowStatusAsync(mainWin, bigFileName, s => s == completedText, TimeSpan.FromSeconds(30));
                Check($"[{lang}] S4.3: Invoking Resume reaches Completed status", isCompleted);

                string downloadedBigPath = Path.Combine(dlDir, bigFileName);
                bool bigHashOk = File.Exists(downloadedBigPath) && HashFile(downloadedBigPath) == expectedBigHash;
                Check($"[{lang}] S4.4: Resumed 40 MB file SHA-256 matches served/big.bin", bigHashOk);
            });

            // -----------------------------------------------------------------
            // Scenario 4b: IDM "refresh download address" after the site expires the link
            // -----------------------------------------------------------------
            await RunScenarioAsync(lang, "S4b-RefreshAddress", mainWin, async () =>
            {
                string token = $"tok-{lang}-{runId}";
                string refreshName = $"ui-refresh-{lang}.bin";
                string completedText = lang == "ar" ? "مكتمل" : "Complete";
                string errorPrefix = lang == "ar" ? "خطأ" : "Error";
                long bigSize = 40L * 1024 * 1024;

                var openAddTask = OpenMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksAddUrlMenu");
                var addWin = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                var urlBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("UrlBox")))!;
                var nameBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("NameBox")))!;
                var folderBox = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("FolderBox")))!;
                var startBtn = WaitForElement(() => addWin!.FindFirstDescendant(cf => cf.ByAutomationId("StartBtn")))!;
                string expiringUrl = $"http://127.0.0.1:{port}/expiring/{token}/big.bin";
                urlBox.Patterns.Value.Pattern.SetValue(expiringUrl);
                await WaitUntilAsync(() => (urlBox.Patterns.Value.Pattern.Value.Value ?? "") == expiringUrl, TimeSpan.FromSeconds(2));
                nameBox.Patterns.Value.Pattern.SetValue(refreshName);
                folderBox.Patterns.Value.Pattern.SetValue(dlDir);
                await WaitUntilAsync(() =>
                    (nameBox.Patterns.Value.Pattern.Value.Value ?? "") == refreshName &&
                    (folderBox.Patterns.Value.Pattern.Value.Value ?? "") == dlDir &&
                    startBtn.IsEnabled,
                    TimeSpan.FromSeconds(2));
                startBtn.Patterns.Invoke.Pattern.Invoke();
                await openAddTask.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForWindowClosedAsync(addWin!, TimeSpan.FromSeconds(5));

                var row = await WaitForGridRowAsync(mainWin, refreshName, TimeSpan.FromSeconds(10));
                Check($"[{lang}] S4b.1: download from an expiring link started", row != null);

                // Part of the file arrives, then the site expires the link (open transfers drop, new requests get 403).
                await WaitUntilAsync(() => TokenBytes(port, token) >= 8L * 1024 * 1024, TimeSpan.FromSeconds(30));
                long servedBefore = TokenBytes(port, token);
                await UiHttp.GetStringAsync($"http://127.0.0.1:{port}/revoke?token={token}");
                bool failed = await WaitForGridRowStatusAsync(mainWin, refreshName, t => t.StartsWith(errorPrefix, StringComparison.Ordinal), TimeSpan.FromSeconds(60));
                Check($"[{lang}] S4b.2: the expired link stops the download with an error", failed, $"servedBefore={servedBefore}");

                row = await WaitForGridRowAsync(mainWin, refreshName, TimeSpan.FromSeconds(5));
                row!.Patterns.SelectionItem.Pattern.Select();
                await WaitUntilAsync(() => row.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(2));
                _ = OpenMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "DownloadsRefreshUrlMenu");
                AutomationElement? refreshWin = null;
                await WaitUntilAsync(() =>
                {
                    refreshWin = automation.GetDesktop().FindFirstDescendant(cf => cf.ByProcessId(appProc.Id).And(cf.ByAutomationId("RefreshAddressWindow")));
                    return refreshWin != null;
                }, TimeSpan.FromSeconds(10));
                var waiting = refreshWin?.FindFirstDescendant(cf => cf.ByAutomationId("RefreshWaitingText"))?.Name ?? "";
                Check($"[{lang}] S4b.3: Downloads -> Refresh download address opens the waiting window", refreshWin != null && waiting.Any(char.IsDigit), $"waiting='{waiting}'");

                // The browser hands over the re-clicked download: a new address, the site's own name, the same size.
                var reply = await SendToGolanAsync(gdmHome, new JsonObject
                {
                    ["action"] = "download",
                    ["url"] = $"http://127.0.0.1:{port}/expiring/{token}-new/big.bin",
                    ["filename"] = "big.bin",
                    ["fileSize"] = bigSize,
                });
                bool accepted = reply?["accepted"]?.GetValue<bool>() == true;
                Check($"[{lang}] S4b.4: Golan takes the re-clicked download for the waiting file", accepted, reply?.ToJsonString() ?? "no reply");

                bool completed = await WaitForGridRowStatusAsync(mainWin, refreshName, t => t == completedText, TimeSpan.FromSeconds(60));
                Check($"[{lang}] S4b.5: the download continues and completes", completed);
                long servedAfter = TokenBytes(port, token + "-new");
                string finalPath = Path.Combine(dlDir, refreshName);
                bool intact = File.Exists(finalPath) && HashFile(finalPath) == expectedBigHash;
                Check($"[{lang}] S4b.6: only the missing part came again and the file is intact",
                    intact && servedAfter > 0 && servedAfter < bigSize - servedBefore / 2, $"before={servedBefore} after={servedAfter}");
                bool noSecondRow = await WaitForGridRowAsync(mainWin, "big.bin", TimeSpan.FromMilliseconds(500)) == null;
                bool closed = false;
                await WaitUntilAsync(() =>
                {
                    closed = automation.GetDesktop().FindFirstDescendant(cf => cf.ByProcessId(appProc.Id).And(cf.ByAutomationId("RefreshAddressWindow"))) == null;
                    return closed;
                }, TimeSpan.FromSeconds(5));
                Check($"[{lang}] S4b.7: no second copy was added and the window closed itself", noSecondRow && closed, $"noSecondRow={noSecondRow} closed={closed}");
            });

            // -----------------------------------------------------------------
            // Scenario 4c: a browser download is answered at once (the browser drops its copy), then Golan asks
            // -----------------------------------------------------------------
            await RunScenarioAsync(lang, "S4c-BrowserAnsweredAtOnce", mainWin, async () =>
            {
                string browserName = $"ui-browser-{lang}.bin";
                var sw = Stopwatch.StartNew();
                var reply = await SendToGolanAsync(gdmHome, new JsonObject
                {
                    ["action"] = "download",
                    ["url"] = $"http://127.0.0.1:{port}/small.bin?browser={lang}",
                    ["filename"] = browserName,
                    ["referrer"] = $"http://127.0.0.1:{port}/site/",
                });
                long answeredMs = sw.ElapsedMilliseconds;
                bool accepted = reply?["accepted"]?.GetValue<bool>() == true;
                Check($"[{lang}] S4c.1: Golan answers the browser at once, before its own window is answered", accepted && answeredMs < 3000,
                    $"{answeredMs} ms {reply?.ToJsonString()}");

                var addWin = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                var nameBox = addWin?.FindFirstDescendant(cf => cf.ByAutomationId("NameBox"));
                string shownName = nameBox?.Patterns.Value.Pattern.Value.Value ?? "";
                Check($"[{lang}] S4c.2: then Golan's window opens with the file", addWin != null && shownName == browserName, $"name='{shownName}'");

                addWin?.Close();
                if (addWin != null) await WaitForWindowClosedAsync(addWin, TimeSpan.FromSeconds(5));
                bool noRow = await WaitForGridRowAsync(mainWin, browserName, TimeSpan.FromMilliseconds(800)) == null;
                string log = "";
                await WaitUntilAsync(() =>
                {
                    try
                    {
                        log = string.Join("\n", Directory.GetFiles(gdmHome, "gdm.log", SearchOption.AllDirectories)
                            .Select(f => { using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs).ReadToEnd(); }));
                    }
                    catch (IOException) { }
                    return log.Contains("cancelled in Golan's window: " + browserName);
                }, TimeSpan.FromSeconds(5));
                Check($"[{lang}] S4c.3: Cancel in Golan's window drops it, and the log tells what happened",
                    noRow && log.Contains("taken by Golan") && log.Contains("cancelled in Golan's window: " + browserName),
                    $"noRow={noRow} logLines={log.Split('\n').Length}");
            });

            // -----------------------------------------------------------------
            // Scenario 5: Windows open/close cleanly
            // -----------------------------------------------------------------
            await RunScenarioAsync(lang, "S5-WindowsOpenClose", mainWin, async () =>
            {
                // 5.1 Speed graph toggle (View -> Show Speed Graph)
                await InvokeMenuItemByAutomationIdAsync(mainWin, "ViewMenu", "SpeedGraphMenu");
                bool graphOn = await WaitUntilAsync(() => ReadSettingBool(settingsPath, "ShowSpeedGraph") == true, TimeSpan.FromSeconds(3));
                await InvokeMenuItemByAutomationIdAsync(mainWin, "ViewMenu", "SpeedGraphMenu");
                bool graphOff = await WaitUntilAsync(() => ReadSettingBool(settingsPath, "ShowSpeedGraph") == false, TimeSpan.FromSeconds(3));
                Check($"[{lang}] S5.1: Speed graph toggle (View -> Show Speed Graph) toggles cleanly", graphOn && graphOff);

                // 5.2 Statistics window (View -> Statistics)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.2: Statistics window opens and closes cleanly",
                    () => OpenMenuActionByAutomationIdAsync(mainWin, "ViewMenu", "ViewStatsMenu"),
                    win => GetDirectButtons(win).FirstOrDefault());

                // 5.3 Speed rules window (Downloads -> Speed Limiter -> Speed Rules)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.3: Speed rules window opens and closes cleanly",
                    () => OpenNestedMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "SpeedLimiterSubMenu", "SpeedRulesMenu"),
                    win => GetDirectButtons(win).LastOrDefault());

                // 5.4 Hash window (on the downloaded file)
                var smallRow = await WaitForGridRowAsync(mainWin, smallFileName, TimeSpan.FromSeconds(5));
                smallRow!.Patterns.SelectionItem.Pattern.Select();
                await WaitUntilAsync(() => smallRow.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(2));

                var hashTask = OpenMenuActionByAutomationIdAsync(mainWin, "FileMenu", "FileHash");
                var hashWin = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                var sha256Box = WaitForElement(() => hashWin!.FindFirstDescendant(cf => cf.ByAutomationId("Sha256Box")))!;
                string computedSha = "";
                await WaitUntilAsync(() =>
                {
                    computedSha = sha256Box.Patterns.Value.Pattern.Value.Value ?? "";
                    return computedSha.Length == 64;
                }, TimeSpan.FromSeconds(5));
                var hashCloseBtn = WaitForElement(() => GetDirectButtons(hashWin!).LastOrDefault())!;
                hashCloseBtn.Patterns.Invoke.Pattern.Invoke();
                await hashTask.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForWindowClosedAsync(hashWin!, TimeSpan.FromSeconds(5));
                Check(
                    $"[{lang}] S5.4: Hash window opens on downloaded file, computes SHA-256, and closes cleanly",
                    string.Equals(computedSha, expectedSmallHash, StringComparison.OrdinalIgnoreCase),
                    $"sha256={computedSha}");

                // 5.5 Torrent/magnet dialog (cancel): Tasks -> Add Torrent or Magnet Link
                // First cancels Win32 OpenFileDialog (Button AutomationId="2"), then cancels InputDialog
                var torrentTask = OpenMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksAddTorrentMenu");
                var fileDlg = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                bool fileDlgClosed = false;
                if (fileDlg != null)
                {
                    var fileDlgCancel = WaitForElement(() =>
                        fileDlg.FindFirstChild(cf => cf.ByControlType(ControlType.Button).And(cf.ByAutomationId("2")))
                        ?? fileDlg.FindAllChildren(cf => cf.ByControlType(ControlType.Button)).LastOrDefault());
                    fileDlgCancel?.Patterns.Invoke.Pattern.Invoke();
                    fileDlgClosed = await WaitForWindowClosedAsync(fileDlg, TimeSpan.FromSeconds(5));
                }

                var magnetInputDlg = await WaitForModalWindowAsync(mainWin, automation, appProc.Id, TimeSpan.FromSeconds(10));
                bool magnetDlgClosed = false;
                if (magnetInputDlg != null)
                {
                    var magnetCancelBtn = WaitForElement(() =>
                    {
                        var btns = GetDirectButtons(magnetInputDlg);
                        return btns.Length > 1 ? btns[1] : null;
                    })!;
                    magnetCancelBtn.Patterns.Invoke.Pattern.Invoke();
                    await torrentTask.WaitAsync(TimeSpan.FromSeconds(5));
                    magnetDlgClosed = await WaitForWindowClosedAsync(magnetInputDlg, TimeSpan.FromSeconds(5));
                }
                Check($"[{lang}] S5.5: Torrent/magnet dialog opens and cancels cleanly", fileDlgClosed && magnetDlgClosed);

                // 5.6 IDM import window (cancel): Tasks -> Import -> Import from IDM
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.6: IDM import window opens and cancels cleanly",
                    () => OpenNestedMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksImportMenu", "TasksImportIdmMenu"),
                    win => GetDirectButtons(win).FirstOrDefault());

                // 5.7 Remote window (do not enable; just open and close): Downloads -> Remote Control
                bool remoteUnchecked = false;
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.7: Remote window opens (not enabled) and closes cleanly",
                    () => OpenMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "RemoteControlMenu"),
                    win =>
                    {
                        var enableChk = win.FindFirstDescendant(cf => cf.ByAutomationId("EnableCheck"));
                        var closeBtn = GetDirectButtons(win).FirstOrDefault();
                        if (enableChk == null || closeBtn == null) return null;
                        remoteUnchecked = enableChk.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off;
                        return closeBtn;
                    });
                Check($"[{lang}] S5.7b: Remote server remains disabled", remoteUnchecked);

                // 5.8 Scheduler window (Downloads -> Scheduler)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.8: Scheduler window opens and closes cleanly",
                    () => OpenMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "DownloadsSchedulerMenu"),
                    win =>
                    {
                        var btns = GetDirectButtons(win);
                        return btns.Length > 3 ? btns[3] : null;
                    });

                // 5.9 Speed limiter window (Downloads -> Speed Limiter -> Settings)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.9: Speed limiter window opens and cancels cleanly",
                    () => OpenNestedMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "SpeedLimiterSubMenu", "LimiterSettingsMenu"),
                    win => GetDirectButtons(win).LastOrDefault());

                // 5.10 Find window (Downloads -> Find)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.10: Find window opens and cancels cleanly",
                    () => OpenMenuActionByAutomationIdAsync(mainWin, "DownloadsMenu", "DownloadsFindMenu"),
                    win =>
                    {
                        var btns = GetDirectButtons(win);
                        return btns.Length > 1 ? btns[1] : null;
                    });

                // 5.11 Columns window (View -> Columns)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.11: Columns window opens and cancels cleanly",
                    () => OpenMenuActionByAutomationIdAsync(mainWin, "ViewMenu", "ViewColumnsMenu"),
                    win =>
                    {
                        var btns = GetDirectButtons(win);
                        return btns.Length > 1 ? btns[1] : null;
                    });

                // 5.12 Batch window (Toolbar BatchBtn) + BatchPatternWindow (Tasks -> TasksBatchPatternMenu)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.12a: Batch window opens and cancels cleanly",
                    () =>
                    {
                        var batchBtn = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("BatchBtn")))
                            ?? GetDirectButtons(mainWin)[2];
                        return Task.Run(() => batchBtn.Patterns.Invoke.Pattern.Invoke());
                    },
                    win => win.FindFirstDescendant(cf => cf.ByAutomationId("CancelBtn"))
                        ?? GetDirectButtons(win).FirstOrDefault());

                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.12b: BatchPattern window opens and cancels cleanly",
                    () => OpenMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksBatchPatternMenu"),
                    win =>
                    {
                        var btns = GetDirectButtons(win);
                        return btns.Length > 1 ? btns[1] : null;
                    });

                // 5.13 Grabber window (Tasks -> Grabber)
                await OpenAndCloseModalAsync(
                    mainWin, automation, appProc.Id,
                    $"[{lang}] S5.13: Grabber window opens and cancels cleanly",
                    () => OpenMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksGrabberMenu"),
                    win =>
                    {
                        var btns = GetDirectButtons(win);
                        return btns.Length > 2 ? btns[2] : null;
                    });
            });

            // -----------------------------------------------------------------
            // Scenario 6: CLI while UI runs
            // -----------------------------------------------------------------
            string cliFileName = $"ui-cli-{lang}.bin";
            await RunScenarioAsync(lang, "S6-CliWhileUiRuns", mainWin, async () =>
            {
                string cliUrl = $"http://127.0.0.1:{port}/small.bin?cli={lang}";
                var addRes = await RunCliAsync(gdmExe, gdmHome, ["add", cliUrl, "--name", cliFileName, "--folder", dlDir, "--start"]);
                Check($"[{lang}] S6.1: CLI 'add --start' succeeded while UI is running", addRes.ExitCode == 0, addRes.StdOut);

                string completedText = lang == "ar" ? "مكتمل" : "Complete";
                bool rowCompleted = await WaitForGridRowStatusAsync(mainWin, cliFileName, s => s == completedText, TimeSpan.FromSeconds(15));
                Check($"[{lang}] S6.2: CLI-added item appeared in UI DataGrid and reached Completed", rowCompleted);

                var listRes = await RunCliAsync(gdmExe, gdmHome, ["list", "--json"]);
                Check($"[{lang}] S6.3: CLI 'list --json' exit code 0", listRes.ExitCode == 0);

                var listArr = JsonNode.Parse(listRes.StdOut)!.AsArray();
                bool foundInList = listArr.Any(n =>
                    string.Equals(n?["fileName"]?.GetValue<string>(), cliFileName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(n?["status"]?.GetValue<string>(), "Completed", StringComparison.OrdinalIgnoreCase));
                Check($"[{lang}] S6.4: CLI 'list --json' includes '{cliFileName}' with status=Completed", foundInList);
            });

            // -----------------------------------------------------------------
            // Scenario 7: Exit from tray/menu
            // -----------------------------------------------------------------
            await RunScenarioAsync(lang, "S7-ExitFromMenu", mainWin, async () =>
            {
                // Tasks -> Exit (by AutomationId)
                var exitTask = OpenMenuActionByAutomationIdAsync(mainWin, "TasksMenu", "TasksExitMenu");
                bool exited = appProc.WaitForExit(10000);
                try { await exitTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                Check($"[{lang}] S7.1: Process exited within 10s after invoking Tasks -> Exit", exited);

                bool settingsValid = IsValidJsonFile(settingsPath);
                bool downloadsValid = IsValidJsonFile(downloadsPath);
                Check($"[{lang}] S7.2: settings.json and downloads.json exist and are valid JSON", settingsValid && downloadsValid);
            });
        }
        finally
        {
            if (appProc != null)
            {
                KillOwnedProcess(appProc.Id);
                StartedPids.Remove(appProc.Id);
            }
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (Directory.Exists(gdmHome))
                        Directory.Delete(gdmHome, recursive: true);
                    break;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }
        }
    }

    private static async Task RunScenarioAsync(string lang, string scenarioName, Window mainWin, Func<Task> body)
    {
        int failsBefore = _fails;
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Check($"[{lang}] {scenarioName} unhandled exception", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        if (_fails > failsBefore)
        {
            string shotPath = Path.Combine(_outDir, $"fail-{lang}-{scenarioName}-{DateTime.Now:HHmmss}.png");
            CaptureWindowScreenshot(mainWin, shotPath);
            Console.WriteLine($"  [SCREENSHOT] Saved failure screenshot to {shotPath}");
        }
    }

    private static void CaptureWindowScreenshot(Window win, string filePath)
    {
        try
        {
            IntPtr hwnd = new IntPtr(win.Properties.NativeWindowHandle.ValueOrDefault);
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rc))
            {
                int w = Math.Max(1, rc.Right - rc.Left);
                int h = Math.Max(1, rc.Bottom - rc.Top);
                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT = 2 captures WPF hardware/software rendered window without needing focus
                        PrintWindow(hwnd, hdc, 2);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
                bmp.Save(filePath, ImageFormat.Png);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Screenshot capture failed: {ex.Message}");
        }
    }

    private static async Task<Window?> WaitForMainWindowAsync(UIA3Automation automation, int processId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var desktop = automation.GetDesktop();
                var wins = desktop.FindAllChildren(cf => cf.ByProcessId(processId).And(cf.ByControlType(ControlType.Window)));
                foreach (var w in wins)
                {
                    if (w.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")) != null)
                        return w.AsWindow();
                }
            }
            catch { }
            await Task.Delay(150);
        }
        return null;
    }

    private static bool IsDialogCandidate(AutomationElement el, IntPtr mainHwnd)
    {
        try
        {
            IntPtr h = new IntPtr(el.Properties.NativeWindowHandle.ValueOrDefault);
            if (h == IntPtr.Zero || h == mainHwnd || !IsWindowVisible(h))
                return false;
            string cls = el.Properties.ClassName.ValueOrDefault ?? "";
            if (string.Equals(cls, "Popup", StringComparison.OrdinalIgnoreCase))
                return false;
            string title = (el.Properties.Name.ValueOrDefault ?? "").Trim();
            if (title.Length == 0)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Window?> WaitForModalWindowAsync(Window mainWin, UIA3Automation automation, int processId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        IntPtr mainHwnd = new IntPtr(mainWin.Properties.NativeWindowHandle.ValueOrDefault);
        while (sw.Elapsed < timeout)
        {
            try
            {
                // Check child windows of mainWin first (WPF owned dialogs & Win32 common dialogs)
                var childWins = mainWin.FindAllChildren(cf => cf.ByControlType(ControlType.Window));
                foreach (var cw in childWins)
                {
                    if (IsDialogCandidate(cw, mainHwnd))
                        return cw.AsWindow();
                }

                // Also check desktop top-level windows belonging to the same process
                var topWins = automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(processId).And(cf.ByControlType(ControlType.Window)));
                foreach (var tw in topWins)
                {
                    if (IsDialogCandidate(tw, mainHwnd))
                        return tw.AsWindow();
                }
            }
            catch { }
            await Task.Delay(50);
        }
        return null;
    }

    private static async Task<bool> WaitForWindowClosedAsync(Window win, TimeSpan timeout)
    {
        IntPtr hwnd;
        try
        {
            hwnd = new IntPtr(win.Properties.NativeWindowHandle.ValueOrDefault);
        }
        catch
        {
            return true;
        }
        if (hwnd == IntPtr.Zero) return true;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsWindowVisible(hwnd))
                return true;
            await Task.Delay(30);
        }
        return !IsWindowVisible(hwnd);
    }

    private static async Task WaitUntilEnabledAsync(AutomationElement element, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (element.IsEnabled) return; } catch (FlaUI.Core.Exceptions.ElementNotAvailableException) { }
            await Task.Delay(40);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, int pollMs = 25)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (predicate())
                    return true;
            }
            catch { }
            await Task.Delay(pollMs);
        }
        try { return predicate(); } catch { return false; }
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout, int pollMs = 20)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (predicate())
                    return true;
            }
            catch { }
            Thread.Sleep(pollMs);
        }
        try { return predicate(); } catch { return false; }
    }

    private static bool? ReadSettingBool(string settingsPath, string key)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
            return node?[key]?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    private static AutomationElement? WaitForElement(Func<AutomationElement?> finder, int retries = 50, int delayMs = 30)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                var el = finder();
                if (el != null) return el;
            }
            catch { }
            Thread.Sleep(delayMs);
        }
        return null;
    }

    private static AutomationElement[] GetDirectButtons(Window win) =>
        win.FindAllChildren(cf => cf.ByControlType(ControlType.Button))
           .Where(b => b.Patterns.Invoke.IsSupported)
           .ToArray();

    private static void ExpandMenuItem(Window mainWin, AutomationElement mi, bool isSubMenu = false)
    {
        var ec = mi.Patterns.ExpandCollapse.Pattern;
        // Ensure any previous popup from an earlier action is fully collapsed first
        if (!isSubMenu && ec.ExpandCollapseState.Value != ExpandCollapseState.Collapsed)
            CollapseMenuItem(mi);

        var sw = Stopwatch.StartNew();
        int attempt = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            try
            {
                if (attempt > 0 && !isSubMenu)
                {
                    IntPtr hwnd = new IntPtr(mainWin.Properties.NativeWindowHandle.ValueOrDefault);
                    if (hwnd != IntPtr.Zero)
                        SendMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((10 << 16) | 10));
                }
                ec.Expand();
            }
            catch { }

            if (WaitUntil(() =>
                ec.ExpandCollapseState.Value == ExpandCollapseState.Expanded &&
                mi.FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem)).Length > 0,
                TimeSpan.FromMilliseconds(400),
                pollMs: 15))
            {
                return;
            }
            attempt++;
        }
    }

    private static void CollapseMenuItem(AutomationElement mi)
    {
        try
        {
            var ec = mi.Patterns.ExpandCollapse.Pattern;
            if (ec.ExpandCollapseState.Value != ExpandCollapseState.Collapsed)
                ec.Collapse();
            WaitUntil(() => ec.ExpandCollapseState.Value == ExpandCollapseState.Collapsed, TimeSpan.FromMilliseconds(600), pollMs: 15);
        }
        catch { }
    }

    private static Task OpenMenuActionByAutomationIdAsync(Window mainWin, string topMenuId, string itemAutomationId)
    {
        var menuBar = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")))!;
        var top = WaitForElement(() => menuBar.FindFirstDescendant(cf => cf.ByAutomationId(topMenuId)))!;
        ExpandMenuItem(mainWin, top);
        var target = WaitForElement(() => top.FindFirstDescendant(cf => cf.ByAutomationId(itemAutomationId)))!;
        WaitUntil(() => target.IsEnabled, TimeSpan.FromSeconds(2));
        return Task.Run(() => target.Patterns.Invoke.Pattern.Invoke());
    }

    private static async Task InvokeMenuItemByAutomationIdAsync(Window mainWin, string topMenuId, string itemAutomationId)
    {
        var menuBar = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")))!;
        var top = WaitForElement(() => menuBar.FindFirstDescendant(cf => cf.ByAutomationId(topMenuId)))!;
        ExpandMenuItem(mainWin, top);
        var target = WaitForElement(() => top.FindFirstDescendant(cf => cf.ByAutomationId(itemAutomationId)))!;
        WaitUntil(() => target.IsEnabled, TimeSpan.FromSeconds(2));
        target.Patterns.Invoke.Pattern.Invoke();
        CollapseMenuItem(top);
        await Task.CompletedTask;
    }

    private static Task OpenNestedMenuActionByAutomationIdAsync(Window mainWin, string topMenuId, string subMenuId, string leafAutomationId)
    {
        var menuBar = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")))!;
        var top = WaitForElement(() => menuBar.FindFirstDescendant(cf => cf.ByAutomationId(topMenuId)))!;
        ExpandMenuItem(mainWin, top);
        var sub = WaitForElement(() => top.FindFirstDescendant(cf => cf.ByAutomationId(subMenuId)))!;
        ExpandMenuItem(mainWin, sub, isSubMenu: true);
        var leaf = WaitForElement(() => sub.FindFirstDescendant(cf => cf.ByAutomationId(leafAutomationId)))!;
        WaitUntil(() => leaf.IsEnabled, TimeSpan.FromSeconds(2));
        return Task.Run(() => leaf.Patterns.Invoke.Pattern.Invoke());
    }

    private static async Task InvokeNestedMenuItemByAutomationIdAsync(Window mainWin, string topMenuId, string subMenuId, string leafAutomationId)
    {
        var menuBar = WaitForElement(() => mainWin.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")))!;
        var top = WaitForElement(() => menuBar.FindFirstDescendant(cf => cf.ByAutomationId(topMenuId)))!;
        ExpandMenuItem(mainWin, top);
        var sub = WaitForElement(() => top.FindFirstDescendant(cf => cf.ByAutomationId(subMenuId)))!;
        ExpandMenuItem(mainWin, sub, isSubMenu: true);
        var leaf = WaitForElement(() => sub.FindFirstDescendant(cf => cf.ByAutomationId(leafAutomationId)))!;
        WaitUntil(() => leaf.IsEnabled, TimeSpan.FromSeconds(2));
        leaf.Patterns.Invoke.Pattern.Invoke();
        CollapseMenuItem(top);
        await Task.CompletedTask;
    }

    private static async Task OpenAndCloseModalAsync(
        Window mainWin,
        UIA3Automation automation,
        int processId,
        string checkName,
        Func<Task> openAction,
        Func<Window, AutomationElement?> closeButtonFinder)
    {
        var openTask = openAction();
        var modal = await WaitForModalWindowAsync(mainWin, automation, processId, TimeSpan.FromSeconds(10));
        if (modal == null)
        {
            Check(checkName, false, "Modal window did not open");
            return;
        }
        string modalTitle = modal.Title ?? "";
        try
        {
            var closeBtn = WaitForElement(() => closeButtonFinder(modal), retries: 60, delayMs: 25);
            if (closeBtn == null)
            {
                Check(checkName, false, $"Close/Cancel button not found in '{modalTitle}'");
                return;
            }
            await WaitUntilEnabledAsync(closeBtn, TimeSpan.FromSeconds(2));
            closeBtn.Patterns.Invoke.Pattern.Invoke();
            await openTask.WaitAsync(TimeSpan.FromSeconds(5));
            bool closed = await WaitForWindowClosedAsync(modal, TimeSpan.FromSeconds(5));
            Check(checkName, closed, $"title='{modalTitle}'");
        }
        finally
        {
            try
            {
                IntPtr h = new IntPtr(modal.Properties.NativeWindowHandle.ValueOrDefault);
                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    modal.Patterns.Window.Pattern.Close();
                }
            }
            catch { }
        }
    }

    private static async Task<AutomationElement?> WaitForGridRowAsync(Window mainWin, string fileName, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var grid = mainWin.FindFirstDescendant(cf => cf.ByAutomationId("Grid"));
                if (grid != null)
                {
                    var rows = grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem));
                    foreach (var row in rows)
                    {
                        var texts = row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
                        if (texts.Any(t => string.Equals(t.Name, fileName, StringComparison.OrdinalIgnoreCase)))
                            return row;
                    }
                }
            }
            catch { }
            await Task.Delay(50);
        }
        return null;
    }

    private static async Task<bool> WaitForGridRowStatusAsync(
        Window mainWin,
        string fileName,
        Func<string, bool> statusPredicate,
        TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var row = await WaitForGridRowAsync(mainWin, fileName, TimeSpan.FromMilliseconds(200));
                if (row != null)
                {
                    var texts = row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .Select(t => t.Name ?? "")
                        .ToList();
                    if (texts.Any(statusPredicate))
                        return true;
                }
            }
            catch { }
            await Task.Delay(60);
        }
        return false;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string gdmExe, string gdmHome, string[] cliArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gdmExe,
            WorkingDirectory = Path.GetDirectoryName(gdmExe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in cliArgs) psi.ArgumentList.Add(a);
        psi.Environment["GDM_HOME"] = gdmHome;

        using var proc = Process.Start(psi)!;
        StartedPids.Add(proc.Id);
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        StartedPids.Remove(proc.Id);
        return (proc.ExitCode, (await outTask).Trim(), (await errTask).Trim());
    }

    private static bool IsValidJsonFile(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<bool> IsPortListeningAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromMilliseconds(300));
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void KillOwnedProcess(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch { }
    }
}
