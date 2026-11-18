using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Golan.Gdm.Core;

public static class Tests_UI3c
{
    static int _passes;
    static int _fails;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  [UI3C] {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
        if (ok) _passes++; else _fails++;
    }

    static string FindGdmSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var direct = Path.Combine(dir.FullName, "src", "GDM");
            if (Directory.Exists(direct) && File.Exists(Path.Combine(direct, "GDM.csproj")))
                return direct;
            var nested = Path.Combine(dir.FullName, "GDM", "src", "GDM");
            if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, "GDM.csproj")))
                return nested;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/GDM directory.");
    }

    static Assembly LoadGdmAssembly(string gdmSrc)
    {
        var already = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "GDM", StringComparison.OrdinalIgnoreCase));
        if (already != null)
            return already;

        var binDir = Path.Combine(gdmSrc, "bin");
        var dll = Directory.Exists(binDir)
            ? Directory.GetFiles(binDir, "GDM.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (dll == null)
            throw new FileNotFoundException("GDM.dll not found under src/GDM/bin.");
        return Assembly.LoadFrom(dll);
    }

    static async Task RunOnStaAsync(Action action, int timeoutMs = 15000)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var disp = Dispatcher.CurrentDispatcher;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(disp));
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                try { disp.InvokeShutdown(); } catch { /* dispatcher shutting down */ }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (done != tcs.Task)
            throw new TimeoutException($"STA test timed out after {timeoutMs}ms");
        await tcs.Task;
    }

    static IEnumerable<DependencyObject> WalkLogicalOrVisual(DependencyObject? root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var nested in WalkLogicalOrVisual(child))
                yield return nested;
        }
    }

    public static async Task<int> RunAsync()
    {
        _passes = 0;
        _fails = 0;
        Console.WriteLine("=== Task 3c Secondary Windows & i18n Audit Suite ===");

        string prevLang = Loc.Language;
        try
        {
            var gdmSrc = FindGdmSrcDir();
            var gdmAsm = LoadGdmAssembly(gdmSrc);
            var gdmLocType = gdmAsm.GetType("Golan.Gdm.Core.Loc")!;
            var gdmLocInit = gdmLocType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!;

            // 1. OptionsWindow: CopySettings preserves live state (RemoteDevices, LastUpdateCheck, DarkMode) + STA headless tabs & validation
            var optWinType = gdmAsm.GetType("Golan.Gdm.UI.OptionsWindow")!;
            var gdmSettingsType = gdmAsm.GetType("Golan.Gdm.Core.AppSettings")!;
            var gdmMgrType = gdmAsm.GetType("Golan.Gdm.Core.DownloadManager")!;
            var gdmDeviceType = gdmAsm.GetType("Golan.Gdm.Core.PairedDevice")!;
            var copySettings = optWinType.GetMethod("CopySettings", BindingFlags.Public | BindingFlags.Static)!;

            var targetSettings = Activator.CreateInstance(gdmSettingsType)!;
            var sourceSettings = Activator.CreateInstance(gdmSettingsType)!;
            var checkTime = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
            gdmSettingsType.GetProperty("DarkMode")!.SetValue(targetSettings, true);
            gdmSettingsType.GetProperty("LastUpdateCheck")!.SetValue(targetSettings, (DateTime?)checkTime);
            gdmSettingsType.GetProperty("DarkMode")!.SetValue(sourceSettings, false);
            gdmSettingsType.GetProperty("LastUpdateCheck")!.SetValue(sourceSettings, (DateTime?)null);
            gdmSettingsType.GetProperty("MaxConnections")!.SetValue(sourceSettings, 24);

            var srcDevices = (System.Collections.IList)gdmSettingsType.GetProperty("RemoteDevices")!.GetValue(sourceSettings)!;
            var dev = Activator.CreateInstance(gdmDeviceType)!;
            gdmDeviceType.GetProperty("Id")!.SetValue(dev, "dev-1");
            gdmDeviceType.GetProperty("Name")!.SetValue(dev, "Phone");
            srcDevices.Add(dev);
            var targetDevicesRefBefore = gdmSettingsType.GetProperty("RemoteDevices")!.GetValue(targetSettings)!;

            copySettings.Invoke(null, [targetSettings, sourceSettings]);
            var targetDevicesAfter = (System.Collections.IList)gdmSettingsType.GetProperty("RemoteDevices")!.GetValue(targetSettings)!;
            bool copySettingsOk =
                (int)gdmSettingsType.GetProperty("MaxConnections")!.GetValue(targetSettings)! == 24 &&
                (bool)gdmSettingsType.GetProperty("DarkMode")!.GetValue(targetSettings)! &&
                (DateTime?)gdmSettingsType.GetProperty("LastUpdateCheck")!.GetValue(targetSettings) == checkTime &&
                ReferenceEquals(targetDevicesRefBefore, targetDevicesAfter) &&
                targetDevicesAfter.Count == 0; // the live paired-device list is never rewritten from the Options copy
            Check("UI3c_Options_CopySettings_PreservesLiveState", copySettingsOk);

            bool optionsStaOk = false;
            await RunOnStaAsync(() =>
            {
                var s = Activator.CreateInstance(gdmSettingsType)!;
                var mgr = Activator.CreateInstance(gdmMgrType, [s])!;
                try
                {
                    var win = (Window)Activator.CreateInstance(optWinType, [mgr, "torrent"])!;
                    var isValidProp = optWinType.GetProperty("IsValid")!;
                    var pagesField = optWinType.GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var pages = (System.Collections.IDictionary)pagesField.GetValue(win)!;
                    var torrentPage = (StackPanel)pages["torrent"]!;
                    var boxes = WalkLogicalOrVisual(torrentPage).OfType<TextBox>().ToList();
                    var portBox = boxes[0]; // TorrentListenPort (0..65535)
                    portBox.Text = "99999";
                    bool invalidOnOutOfRange = !(bool)isValidProp.GetValue(win)!;
                    portBox.Text = "51413";
                    bool validOnRestore = (bool)isValidProp.GetValue(win)!;
                    optionsStaOk = pages.Count == 14 && invalidOnOutOfRange && validOnRestore;
                    win.Close();
                }
                finally
                {
                    gdmMgrType.GetMethod("Shutdown")?.Invoke(mgr, null);
                }
            });
            Check("UI3c_Options_HeadlessTabsAndValidation", optionsStaOk);

            var optSysCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "OptionsWindow.System.cs"));
            var optMainCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "OptionsWindow.xaml.cs"));
            var optGenCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "OptionsWindow.General.cs"));
            Check("UI3c_Options_TempDirValidation_SoundMissing_AndUpdatesIsolation",
                optMainCs.Contains("win.options.save.invalid_temp") &&
                optGenCs.Contains("win.options.sounds.file_missing") &&
                optSysCs.Contains("var prevFeed = _manager.Settings.UpdateFeedUrl;") &&
                optSysCs.Contains("_manager.Settings.UpdateFeedUrl = prevFeed;"));

            // 2. BatchWindow: cookie isolation, range cap, extension-preserving rename, single PropertyChanged
            var batchWinType = gdmAsm.GetType("Golan.Gdm.UI.BatchWindow")!;
            var resolveCookies = batchWinType.GetMethod("ResolveCookiesForLink", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            var cSameHttps = ((string Cookies, string Host))resolveCookies.Invoke(null, ["https://cdn.example.com/a.zip", "https://example.com/page", "sid=1"])!;
            var cDowngrade = ((string Cookies, string Host))resolveCookies.Invoke(null, ["http://example.com/a.zip", "https://example.com/page", "sid=1"])!;
            var cNonHttp = ((string Cookies, string Host))resolveCookies.Invoke(null, ["ftp://example.com/a.zip", "https://example.com/page", "sid=1"])!;
            Check("UI3c_Batch_ResolveCookiesForLink",
                cSameHttps == ("sid=1", "cdn.example.com") &&
                cDowngrade == ("", "") &&
                cNonHttp == ("", ""));

            var parseBatch = batchWinType.GetMethod("ParseBatchInput", BindingFlags.Public | BindingFlags.Static)!;
            var parsedSmall = (System.Collections.IList)parseBatch.Invoke(null, ["https://127.0.0.1/part[01-04].rar\nSee https://127.0.0.1/readme.pdf.", 100])!;
            var parsedFtp = (System.Collections.IList)parseBatch.Invoke(null, ["ftp://127.0.0.1/pub/f[01-02].zip\nMirror ftps://127.0.0.1/pub/doc.pdf\nfile:///C:/Windows/win.ini", 100])!;
            var parsedCapped = (System.Collections.IList)parseBatch.Invoke(null, ["https://127.0.0.1/f[1-999999].bin", 25])!;
            var formatRename = batchWinType.GetMethod("FormatBatchRename", BindingFlags.Public | BindingFlags.Static)!;
            var ren1 = (string)formatRename.Invoke(null, ["lecture_*", "intro.mp4", 1])!;
            var ren2 = (string)formatRename.Invoke(null, ["photo_*.jpg", "raw.png", 3])!;
            var ren3 = (string)formatRename.Invoke(null, ["episode", "ep01.mkv", 2])!;

            var batchRowType = batchWinType.GetNestedType("BatchLinkRow", BindingFlags.Public)!;
            var batchRow = (INotifyPropertyChanged)Activator.CreateInstance(batchRowType)!;
            int selectedEvents = 0;
            batchRow.PropertyChanged += (_, e) => { if (e.PropertyName == "Selected") selectedEvents++; };
            batchRowType.GetProperty("Selected")!.SetValue(batchRow, true);

            Check("UI3c_Batch_ParseRangeCap_RenameExt_AndSingleNotify",
                parsedSmall.Count == 5 &&
                parsedFtp.Count == 3 &&
                parsedCapped.Count == 25 &&
                ren1 == "lecture_1.mp4" &&
                ren2 == "photo_3.jpg" &&
                ren3 == "episode_2.mkv" &&
                selectedEvents == 1,
                $"small={parsedSmall.Count}, ftp={parsedFtp.Count}, cap={parsedCapped.Count}, ren1={ren1}, events={selectedEvents}");

            // 3. BatchPatternWindow & Transfer.ExpandBatch: OOM pad clamp & signed overflow guard
            var expPad = Transfer.ExpandBatch("http://127.0.0.1/img*.jpg", false, "1", "3", 999_999_999);
            var expOverflow = Transfer.ExpandBatch("http://127.0.0.1/img*.jpg", false, long.MinValue.ToString(), "10", 0);
            var expWideLetters = Transfer.ExpandBatch("http://127.0.0.1/*.png", true, "\u0000", "\uFFFF", 0);
            Check("UI3c_BatchPattern_ExpandBatch_OomAndOverflowSafe",
                expPad.Count == 3 && expPad[0].Length < 80 &&
                expOverflow.Count == 0 &&
                expWideLetters.Count == 0);

            // 4. GrabberWindow: crawl depth clamp, credential origin/HTTPS guard, stale crawl race guard
            var grabWinType = gdmAsm.GetType("Golan.Gdm.UI.GrabberWindow")!;
            var clampDepth = grabWinType.GetMethod("ClampCrawlDepth", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            var shouldCreds = grabWinType.GetMethod("ShouldCopyProjectCredentials", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            int dLow = (int)clampDepth.Invoke(null, [-5])!;
            int dHigh = (int)clampDepth.Invoke(null, [500])!;
            bool credSame = (bool)shouldCreds.Invoke(null, ["https://site.example/dir/index.html", "https://site.example/files/a.zip", "alice"])!;
            bool credOtherHost = (bool)shouldCreds.Invoke(null, ["https://site.example/index.html", "https://cdn.other.example/a.zip", "alice"])!;
            bool credDowngrade = (bool)shouldCreds.Invoke(null, ["https://site.example/index.html", "http://site.example/a.zip", "alice"])!;
            var grabCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "GrabberWindow.xaml.cs"));
            Check("UI3c_Grabber_DepthClamp_CredentialScope_AndStaleCrawlGuard",
                dLow == 0 && dHigh is > 0 and <= 10 &&
                credSame && !credOtherHost && !credDowngrade &&
                grabCs.Contains("ReferenceEquals(_grabber, grabber)") &&
                grabCs.Contains("(_project.MinSizeKB, _project.MaxSizeKB) = (_project.MaxSizeKB, _project.MinSizeKB)"));

            // 5. SchedulerWindow: strict clock validation, built-in read-only guard, no per-tick Items.Refresh
            var schedWinType = gdmAsm.GetType("Golan.Gdm.UI.SchedulerWindow")!;
            var validClock = schedWinType.GetMethod("IsValidClockTime", BindingFlags.Public | BindingFlags.Static)!;
            bool clkOk =
                (bool)validClock.Invoke(null, ["02:00"])! &&
                (bool)validClock.Invoke(null, ["9:05"])! &&
                (bool)validClock.Invoke(null, ["23:59:30"])! &&
                !(bool)validClock.Invoke(null, [""])! &&
                !(bool)validClock.Invoke(null, ["25:00"])! &&
                !(bool)validClock.Invoke(null, ["12:60"])! &&
                !(bool)validClock.Invoke(null, ["0930"])!;
            var schedCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "SchedulerWindow.xaml.cs"));
            Check("UI3c_Scheduler_ClockValidation_AndQueueLifecycle",
                clkOk &&
                schedCs.Contains("QueueNameBox.IsReadOnly = _queue.BuiltIn;") &&
                schedCs.Contains("_manager.StopQueue(toDelete.Id);") &&
                schedCs.Contains("void UpdateQueueRunningTags()"));

            // 6. SpeedRulesWindow.RuleRow & SpeedLimiterWindow manual override sync
            var rulesWinType = gdmAsm.GetType("Golan.Gdm.UI.SpeedRulesWindow")!;
            var ruleRowType = rulesWinType.GetNestedType("RuleRow", BindingFlags.Public)!;
            var ruleRow = Activator.CreateInstance(ruleRowType)!;
            ruleRowType.GetProperty("FromText")!.SetValue(ruleRow, "2:05");
            ruleRowType.GetProperty("FromText")!.SetValue(ruleRow, "99:99");
            ruleRowType.GetProperty("LimitText")!.SetValue(ruleRow, "2048");
            ruleRowType.GetProperty("LimitText")!.SetValue(ruleRow, "");
            ruleRowType.GetProperty("LimitText")!.SetValue(ruleRow, "99999999");
            var limiterCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "SpeedLimiterWindow.xaml.cs"));
            Check("UI3c_SpeedRules_RuleRow_AndLimiterManualOverride",
                (string)ruleRowType.GetProperty("FromText")!.GetValue(ruleRow)! == "02:05" &&
                (int)ruleRowType.GetProperty("LimitKBps")!.GetValue(ruleRow)! == 1024000 &&
                limiterCs.Contains("SpeedRuleController?.SetManualOverride"));

            // 7. PropertiesWindow: SanitizeCredentialsOnUrlChange protects cookies & Basic-Auth secrets
            var propsWinType = gdmAsm.GetType("Golan.Gdm.UI.PropertiesWindow")!;
            var sanitizeCreds = propsWinType.GetMethod("SanitizeCredentialsOnUrlChange", BindingFlags.Public | BindingFlags.Static)!;
            var gdmItemType = gdmAsm.GetType("Golan.Gdm.Core.DownloadItem")!;

            object MakeGdmItem(string cookies, string host)
            {
                var it = Activator.CreateInstance(gdmItemType)!;
                gdmItemType.GetProperty("Cookies")!.SetValue(it, cookies);
                gdmItemType.GetProperty("CookieHost")!.SetValue(it, host);
                return it;
            }

            // Cross-host with unchanged prefilled login -> clears cookies AND login/password
            var pItem1 = MakeGdmItem("sid=sec", "site-a.com");
            object?[] args1 = [pItem1, "https://site-a.com/a.zip", "https://site-b.org/a.zip", "alice", "secret", "alice", "secret"];
            sanitizeCreds.Invoke(null, args1);
            bool crossHostCleared =
                (string)gdmItemType.GetProperty("Cookies")!.GetValue(pItem1)! == "" &&
                (string)args1[5]! == "" && (string)args1[6]! == "";

            // Cross-host with explicitly edited login -> clears old cookies, keeps newly typed login
            var pItem2 = MakeGdmItem("sid=sec", "site-a.com");
            object?[] args2 = [pItem2, "https://site-a.com/a.zip", "https://site-b.org/a.zip", "alice", "secret", "bob", "newpass"];
            sanitizeCreds.Invoke(null, args2);
            bool explicitLoginKept =
                (string)gdmItemType.GetProperty("Cookies")!.GetValue(pItem2)! == "" &&
                (string)args2[5]! == "bob" && (string)args2[6]! == "newpass";

            // HTTPS -> HTTP downgrade -> clears both cookies and credentials
            var pItem3 = MakeGdmItem("sid=sec", "site-a.com");
            object?[] args3 = [pItem3, "https://site-a.com/a.zip", "http://site-a.com/a.zip", "alice", "secret", "alice", "secret"];
            sanitizeCreds.Invoke(null, args3);
            bool downgradeCleared =
                (string)gdmItemType.GetProperty("Cookies")!.GetValue(pItem3)! == "" &&
                (string)args3[5]! == "" && (string)args3[6]! == "";

            // Same-host HTTPS token update -> keeps both cookies and credentials
            var pItem4 = MakeGdmItem("sid=sec", "site-a.com");
            object?[] args4 = [pItem4, "https://site-a.com/a.zip?t=1", "https://site-a.com/a.zip?t=2", "alice", "secret", "alice", "secret"];
            sanitizeCreds.Invoke(null, args4);
            bool sameHostKept =
                (string)gdmItemType.GetProperty("Cookies")!.GetValue(pItem4)! == "sid=sec" &&
                (string)args4[5]! == "alice" && (string)args4[6]! == "secret";

            Check("UI3c_Properties_SanitizeCredentialsOnUrlChange",
                crossHostCleared && explicitLoginKept && downgradeCleared && sameHostKept);

            // 8. HashWindow & ColumnsWindow static parsers
            var hashWinType = gdmAsm.GetType("Golan.Gdm.UI.HashWindow")!;
            var normHash = hashWinType.GetMethod("NormalizeExpectedHashInput", BindingFlags.Public | BindingFlags.Static)!;
            const string emptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            var hGnu = (string)normHash.Invoke(null, [$"{emptySha256}  ubuntu.iso"])!;
            var hBsd = (string)normHash.Invoke(null, [$"SHA256(ubuntu.iso)= {emptySha256}"])!;
            Check("UI3c_Hash_NormalizeExpectedHashInput",
                hGnu == emptySha256 && hBsd == $"SHA256:{emptySha256}");

            var colsWinType = gdmAsm.GetType("Golan.Gdm.UI.ColumnsWindow")!;
            var parseWidth = colsWinType.GetMethod("ParseColumnWidth", BindingFlags.Public | BindingFlags.Static)!;
            var wZero = (double?)parseWidth.Invoke(null, [""]);
            var wMin = (double?)parseWidth.Invoke(null, ["10"]);
            var wMax = (double?)parseWidth.Invoke(null, ["9999"]);
            var wNeg = (double?)parseWidth.Invoke(null, ["-5"]);
            var wBad = (double?)parseWidth.Invoke(null, ["abc"]);
            Check("UI3c_Columns_ParseColumnWidth",
                wZero == 0.0 && wMin == 30.0 && wMax == 2000.0 && wNeg == null && wBad == null);

            // 9. SpeedGraph STA OnRender (RTL & LTR) + RefreshAddressWindow headless keyboard defaults
            var speedGraphType = gdmAsm.GetType("Golan.Gdm.UI.SpeedGraph")!;
            var refreshWinType = gdmAsm.GetType("Golan.Gdm.UI.RefreshAddressWindow")!;
            bool staRenderAndRefreshOk = false;
            await RunOnStaAsync(() =>
            {
                gdmLocInit.Invoke(null, ["ar"]);
                var graph = (FrameworkElement)Activator.CreateInstance(speedGraphType)!;
                var pushMethod = speedGraphType.GetMethod("PushSample", BindingFlags.Public | BindingFlags.Instance)!;
                var refreshMethod = speedGraphType.GetMethod("Refresh", BindingFlags.Public | BindingFlags.Instance)!;
                pushMethod.Invoke(graph, [0L, true]);
                pushMethod.Invoke(graph, [512_000L, true]);
                pushMethod.Invoke(graph, [8_400_000L, true]);
                var arName = AutomationProperties.GetName(graph);

                graph.Measure(new Size(0, 0));
                graph.Arrange(new Rect(0, 0, 0, 0));
                graph.Width = 360;
                graph.Height = 96;
                graph.Measure(new Size(360, 96));
                graph.Arrange(new Rect(0, 0, 360, 96));
                graph.UpdateLayout();
                var rtbAr = new RenderTargetBitmap(360, 96, 96, 96, PixelFormats.Pbgra32);
                rtbAr.Render(graph);

                gdmLocInit.Invoke(null, ["en"]);
                refreshMethod.Invoke(graph, [true]);
                var enName = AutomationProperties.GetName(graph);
                var rtbEn = new RenderTargetBitmap(360, 96, 96, 96, PixelFormats.Pbgra32);
                rtbEn.Render(graph);

                var noPageItem = Activator.CreateInstance(gdmItemType)!;
                gdmItemType.GetProperty("Url")!.SetValue(noPageItem, "https://127.0.0.1/file.bin");
                gdmItemType.GetProperty("FileName")!.SetValue(noPageItem, "file.bin");
                var rwNoPage = (Window)Activator.CreateInstance(refreshWinType, [noPageItem])!;
                var pasteBtn = WalkLogicalOrVisual(rwNoPage).OfType<Button>()
                    .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "RefreshPasteBtn");
                bool pasteDefaultWhenNoPage = pasteBtn?.IsDefault == true;
                rwNoPage.Close();

                var withPageItem = Activator.CreateInstance(gdmItemType)!;
                gdmItemType.GetProperty("Url")!.SetValue(withPageItem, "https://127.0.0.1/file.bin");
                gdmItemType.GetProperty("ParentPage")!.SetValue(withPageItem, "https://127.0.0.1/download.html");
                gdmItemType.GetProperty("FileName")!.SetValue(withPageItem, "file.bin");
                var rwWithPage = (Window)Activator.CreateInstance(refreshWinType, [withPageItem])!;
                var openBtn = WalkLogicalOrVisual(rwWithPage).OfType<Button>()
                    .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "RefreshOpenPageBtn");
                bool openDefaultWhenHasPage = openBtn?.IsDefault == true;
                rwWithPage.Close();

                staRenderAndRefreshOk = !string.IsNullOrEmpty(arName) && !string.IsNullOrEmpty(enName)
                                        && arName != enName && pasteDefaultWhenNoPage && openDefaultWhenHasPage;
            });
            Check("UI3c_SpeedGraph_StaRender_AndRefreshAddressKeyboard", staRenderAndRefreshOk);

            // 10. XAML & Theme structural checks (sort paths, CategoriesGrid read-only, modal guards, theme parity)
            var xNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
            var darkKeys = XDocument.Load(Path.Combine(gdmSrc, "UI", "Theme", "Dark.xaml"))
                .Root!.Elements().Select(e => e.Attribute(xNs + "Key")?.Value).Where(k => k != null).ToHashSet();
            var lightKeys = XDocument.Load(Path.Combine(gdmSrc, "UI", "Theme", "Light.xaml"))
                .Root!.Elements().Select(e => e.Attribute(xNs + "Key")?.Value).Where(k => k != null).ToHashSet();
            var statsXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "StatsWindow.xaml"));
            var zipXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "ZipPreviewWindow.xaml"));
            var idmXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "IdmImportWindow.xaml"));
            var remoteCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "RemoteWindow.xaml.cs"));
            string[] modalGuardedWindows =
            [
                "BatchPatternWindow.xaml.cs", "ColumnsWindow.xaml.cs", "DuplicateWindow.xaml.cs",
                "ExportWindow.xaml.cs", "FindWindow.xaml.cs", "IdmImportWindow.xaml.cs",
                "PropertiesWindow.xaml.cs", "QueuePickerWindow.xaml.cs", "SpeedLimiterWindow.xaml.cs",
                "SpeedRulesWindow.xaml.cs", "TorrentFilesWindow.xaml.cs"
            ];
            bool allModalGuarded = modalGuardedWindows.All(f =>
                File.ReadAllText(Path.Combine(gdmSrc, "UI", f)).Contains("IsThreadModal", StringComparison.Ordinal));

            Check("UI3c_ThemeParity_GridSortPaths_RemoteCertGuard_AndModalSafety",
                darkKeys.SetEquals(lightKeys) &&
                darkKeys.Contains("DangerHover") && darkKeys.Contains("DangerPressed") &&
                statsXaml.Contains("x:Name=\"CategoriesGrid\" AutoGenerateColumns=\"False\" IsReadOnly=\"True\"") &&
                statsXaml.Contains("SortMemberPath=\"Bytes\"") &&
                zipXaml.Contains("SortMemberPath=\"RawSize\"") &&
                idmXaml.Contains("SortMemberPath=\"FileSize\"") &&
                remoteCs.Contains("Log.Write(\"remote cert load failed: \"") &&
                allModalGuarded);

            // 11. Complete i18n audit: every AR/EN table matches 1-to-1 and every static key used in src/GDM exists in both
            var i18nDir = Path.Combine(gdmSrc, "i18n");
            var arAll = new Dictionary<string, string>(StringComparer.Ordinal);
            var enAll = new Dictionary<string, string>(StringComparer.Ordinal);
            bool domainParityOk = true;
            foreach (var arPath in Directory.GetFiles(i18nDir, "*.ar.json").OrderBy(p => p))
            {
                var enPath = arPath[..^".ar.json".Length] + ".en.json";
                if (!File.Exists(enPath)) { domainParityOk = false; break; }
                var arMap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(arPath)) ?? [];
                var enMap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath)) ?? [];
                if (arMap.Count != enMap.Count || !arMap.Keys.All(enMap.ContainsKey))
                    domainParityOk = false;
                if (arMap.Values.Any(string.IsNullOrWhiteSpace) || enMap.Values.Any(string.IsNullOrWhiteSpace))
                    domainParityOk = false;
                foreach (var kv in arMap) arAll[kv.Key] = kv.Value;
                foreach (var kv in enMap) enAll[kv.Key] = kv.Value;
            }

            var xamlKeyRx = new Regex(@"\{\s*[A-Za-z0-9_]+:T(?:Extension)?\s+(?:Key\s*=\s*)?['""]?([A-Za-z0-9_.-]+)['""]?\s*\}");
            var csDirectRx = new Regex(@"\b(?:Loc\.T|V5T|TorrentText\.T)\s*\(\s*\$?""([A-Za-z0-9_.-]+)""");
            var csTernaryRx = new Regex(@"\b(?:Loc\.T|V5T|TorrentText\.T)\s*\([^;()]*?\?\s*\$?""([A-Za-z0-9_.-]+)""\s*:\s*\$?""([A-Za-z0-9_.-]+)""");
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.EnumerateFiles(gdmSrc, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                if (f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match m in xamlKeyRx.Matches(File.ReadAllText(f)))
                        usedKeys.Add(m.Groups[1].Value);
                }
                else if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var txt = File.ReadAllText(f);
                    foreach (Match m in csDirectRx.Matches(txt))
                        usedKeys.Add(m.Groups[1].Value);
                    foreach (Match m in csTernaryRx.Matches(txt))
                    {
                        usedKeys.Add(m.Groups[1].Value);
                        usedKeys.Add(m.Groups[2].Value);
                    }
                }
            }
            var missingKeys = usedKeys.Where(k => !arAll.ContainsKey(k) || !enAll.ContainsKey(k)).ToList();
            Check("UI3c_I18n_AllUsedKeysAndTablesMatch1To1",
                domainParityOk && missingKeys.Count == 0 && arAll.Count == enAll.Count && usedKeys.Count >= 1300,
                missingKeys.Count == 0 ? $"keys={arAll.Count}, used={usedKeys.Count}" : "missing=" + string.Join(",", missingKeys));
        }
        catch (Exception ex)
        {
            Check("UI3c_UnhandledException", false, ex.ToString());
        }
        finally
        {
            gdmLocInitSafe(gdmSrc: null, prevLang);
            Loc.Init(prevLang);
        }

        Console.WriteLine($"UI3C summary: {_passes} passed, {_fails} failed");
        return _fails;
    }

    static void gdmLocInitSafe(string? gdmSrc, string lang)
    {
        try
        {
            var gdmAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "GDM", StringComparison.OrdinalIgnoreCase));
            gdmAsm?.GetType("Golan.Gdm.Core.Loc")?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, [lang]);
        }
        catch
        {
            /* best effort language restore */
        }
    }
}
