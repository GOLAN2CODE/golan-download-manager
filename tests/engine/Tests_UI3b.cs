using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Golan.Gdm.Core;

public static class Tests_UI3b
{
    static int _passes;
    static int _fails;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  [UI3B] {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
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

    public static async Task<int> RunAsync()
    {
        _passes = 0;
        _fails = 0;
        Console.WriteLine("=== Task 3b Main App Path UI Audit Suite ===");

        string prevLang = Loc.Language;
        try
        {
            var gdmSrc = FindGdmSrcDir();
            var gdmAsm = LoadGdmAssembly(gdmSrc);

            // 1. App.xaml: unique resource keys, RTL hint FlowDirection, indeterminate ProgressBar animation
            var xNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
            var wpfNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
            var appDoc = XDocument.Load(Path.Combine(gdmSrc, "App.xaml"));
            var topDict = appDoc.Descendants(wpfNs + "ResourceDictionary").First();
            var topKeys = topDict.Elements()
                .Select(el => el.Attribute(xNs + "Key")?.Value)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToList();
            var dupKeys = topKeys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Check("UI3b_AppXaml_UniqueResourceKeys",
                dupKeys.Count == 0 && topKeys.Contains("DangerButton") && topKeys.Contains("Primary"),
                dupKeys.Count == 0 ? $"keys={topKeys.Count}" : "duplicates=" + string.Join(",", dupKeys));

            var appXamlRaw = File.ReadAllText(Path.Combine(gdmSrc, "App.xaml"));
            Check("UI3b_AppXaml_RtlHintAndIndeterminateProgress",
                appXamlRaw.Contains("Converter={StaticResource TextDir}") &&
                appXamlRaw.Contains("Property=\"IsIndeterminate\" Value=\"True\"") &&
                appXamlRaw.Contains("IndeterminatePulse"));

            // 2. Program.HasBackgroundFlag & path normalization safety
            var progType = gdmAsm.GetType("Golan.Gdm.Program")!;
            var hasBgMethod = progType.GetMethod("HasBackgroundFlag", BindingFlags.Public | BindingFlags.Static)!;
            bool bgUpper = (bool)hasBgMethod.Invoke(null, [new[] { "--BACKGROUND" }])!;
            bool bgQuoted = (bool)hasBgMethod.Invoke(null, [new[] { "\"--background\"" }])!;
            bool bgAbsent = (bool)hasBgMethod.Invoke(null, [new[] { "https://127.0.0.1/file.bin" }])!;
            bool bgNull = (bool)hasBgMethod.Invoke(null, [null])!;
            Check("UI3b_Program_HasBackgroundFlag",
                bgUpper && bgQuoted && !bgAbsent && !bgNull);

            // 3. App.xaml.cs: Dispatcher marshaling, host guard, static event cleanup, trimmed URLs
            var appCs = File.ReadAllText(Path.Combine(gdmSrc, "App.xaml.cs"));
            Check("UI3b_App_DispatcherMarshalingAndCleanup",
                appCs.Contains("void RunOnUi(Action action)") &&
                appCs.Contains("Manager.ItemStarted += item => RunOnUi") &&
                appCs.Contains("Manager.ItemCompleted += item => RunOnUi") &&
                appCs.Contains("Manager.ItemFailed += item => RunOnUi") &&
                appCs.Contains("MediaTools.ProgressChanged -= _toolsProgressHandler") &&
                appCs.Contains("string.IsNullOrEmpty(u.Host)"));

            // 4. MainWindow: ExtractLinksFromText, drag-drop e.Handled before await, duplicate column guard, F11, friendly errors
            var mainWinType = gdmAsm.GetType("Golan.Gdm.UI.MainWindow")!;
            var extractLinks = mainWinType.GetMethod("ExtractLinksFromText", BindingFlags.Public | BindingFlags.Static)!;
            var extracted = (System.Collections.IList)extractLinks.Invoke(null,
                ["Links: https://127.0.0.1:8080/one.zip, https://127.0.0.1:8080/two.pdf);\nDup: https://127.0.0.1:8080/one.zip."])!;
            var urlProp = extracted.Count > 0 ? extracted[0]!.GetType().GetProperty("Url")! : null;
            Check("UI3b_Clipboard_ExtractLinksFromText",
                extracted.Count == 2 &&
                (string?)urlProp?.GetValue(extracted[0]) == "https://127.0.0.1:8080/one.zip" &&
                (string?)urlProp?.GetValue(extracted[1]) == "https://127.0.0.1:8080/two.pdf",
                $"count={extracted.Count}");

            var dragDropCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "MainWindow.DragDrop.cs"));
            int handledIdx = dragDropCs.IndexOf("e.Handled = true;", StringComparison.Ordinal);
            int awaitIdx = dragDropCs.IndexOf("await App.Instance.OpenTorrentFileAsync", StringComparison.Ordinal);
            Check("UI3b_MainWindow_DropHandledBeforeAwait",
                handledIdx > 0 && awaitIdx > handledIdx,
                $"handledIdx={handledIdx}, awaitIdx={awaitIdx}");

            var colsCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "MainWindow.Columns.cs"));
            var mainCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "MainWindow.xaml.cs"));
            var menusCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "MainWindow.Menus.cs"));
            Check("UI3b_MainWindow_ColumnsShortcutAccessibilityAndFriendlyErrors",
                colsCs.Contains("_settings.Columns.Count != map.Count") &&
                mainCs.Contains("Key.F11") &&
                mainCs.Contains("AutomationProperties.SetName(b, caption)") &&
                mainCs.Contains("_hwnd.RemoveHook(WndProc)") &&
                menusCs.Contains("FriendlyError.Format(ex)") &&
                !menusCs.Contains("Loc.T(\"main.export.errorBody\", ex.Message)") &&
                !menusCs.Contains("Loc.T(\"main.import.errorBody\", ex.Message)") &&
                menusCs.Contains("Loc.T(\"vb.packager.no_source\")"));

            // 5. AddDownloadWindow: stem selection range, no double "Size: Size: Unknown", early-start cleanup on OpenVideo
            var addWinType = gdmAsm.GetType("Golan.Gdm.UI.AddDownloadWindow")!;
            var stemMethod = addWinType.GetMethod("GetFileNameStemRange", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            var r1 = ((int Start, int Length))stemMethod.Invoke(null, ["lecture_01.mp4"])!;
            var r2 = ((int Start, int Length))stemMethod.Invoke(null, [".gitignore"])!;
            var r3 = ((int Start, int Length))stemMethod.Invoke(null, ["README"])!;
            var r4 = ((int Start, int Length))stemMethod.Invoke(null, [""])!;
            Check("UI3b_AddDownload_StemSelectionRange",
                r1 == (0, 10) && r2 == (0, 10) && r3 == (0, 6) && r4 == (0, 0),
                $"r1={r1}, r2={r2}, r3={r3}");

            var addCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "AddDownloadWindow.xaml.cs"));
            var addXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "AddDownloadWindow.xaml"));
            Check("UI3b_AddDownload_SizePrefixEarlyCleanupAndSubmitGuard",
                !addCs.Contains("Loc.T(\"main.add.sizeValue\", p.Size > 0 ? Fmt.Size(p.Size) : Loc.T(\"main.add.unknownSize\"))") &&
                addCs.Contains("p.Size > 0 ? Loc.T(\"main.add.sizeValue\", Fmt.Size(p.Size)) : Loc.T(\"main.add.unknownSize\")") &&
                addCs.Contains("_ = _manager.RemoveAsync(_incoming, deleteFile: false);") &&
                addCs.Contains("if (_submitting || _submitted) return;") &&
                addXaml.Contains("x:Name=\"SubTitle\"") && addXaml.Contains("TextWrapping=\"Wrap\""));

            // 6. ProgressWindow & SegmentBar: STA render with overflow segments, observable TorrentProgressFileRow, completion badge persistence
            var segBarType = gdmAsm.GetType("Golan.Gdm.UI.SegmentBar")!;
            var gdmItemType = gdmAsm.GetType("Golan.Gdm.Core.DownloadItem")!;
            var gdmSegType = gdmAsm.GetType("Golan.Gdm.Core.Segment")!;
            bool segmentRenderSafe = false;
            await RunOnStaAsync(() =>
            {
                var bar = (FrameworkElement)Activator.CreateInstance(segBarType)!;
                var item = Activator.CreateInstance(gdmItemType)!;
                gdmItemType.GetProperty("TotalSize")!.SetValue(item, 100L);
                var segList = (System.Collections.IList)gdmItemType.GetProperty("Segments")!.GetValue(item)!;

                var overflowSeg = Activator.CreateInstance(gdmSegType)!;
                gdmSegType.GetProperty("Start")!.SetValue(overflowSeg, 150L);
                gdmSegType.GetProperty("End")!.SetValue(overflowSeg, 199L); // Length = End - Start + 1 = 50
                gdmSegType.GetProperty("Downloaded")!.SetValue(overflowSeg, 50L);
                gdmSegType.GetProperty("Active")!.SetValue(overflowSeg, true);
                segList.Add(overflowSeg);

                var hugeSeg = Activator.CreateInstance(gdmSegType)!;
                gdmSegType.GetProperty("Start")!.SetValue(hugeSeg, 20L);
                gdmSegType.GetProperty("End")!.SetValue(hugeSeg, 319L); // Length = 300
                gdmSegType.GetProperty("Downloaded")!.SetValue(hugeSeg, 300L);
                gdmSegType.GetProperty("Active")!.SetValue(hugeSeg, true);
                segList.Add(hugeSeg);

                segBarType.GetProperty("Item")!.SetValue(bar, item);

                // Render at 0x0 and 80x16
                bar.Measure(new Size(0, 0));
                bar.Arrange(new Rect(0, 0, 0, 0));
                bar.Width = 80;
                bar.Height = 16;
                bar.Measure(new Size(80, 16));
                bar.Arrange(new Rect(0, 0, 80, 16));
                bar.UpdateLayout();
                var rtb = new RenderTargetBitmap(80, 16, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(bar);
                segmentRenderSafe = true;
            });
            Check("UI3b_SegmentBar_OnRender_OverflowSafe", segmentRenderSafe);

            var torrentRowType = gdmAsm.GetType("Golan.Gdm.UI.TorrentProgressFileRow")!;
            var rowInstance = Activator.CreateInstance(torrentRowType, ["folder/file.iso", "1.2 GB", "25.0%"])!;
            int propChanges = 0;
            if (rowInstance is INotifyPropertyChanged inpc)
                inpc.PropertyChanged += (_, _) => propChanges++;
            torrentRowType.GetProperty("ProgressText")!.SetValue(rowInstance, "50.0%");
            var progCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "ProgressWindow.xaml.cs"));
            Check("UI3b_ProgressWindow_ObservableTorrentRowAndCompletionBadge",
                typeof(INotifyPropertyChanged).IsAssignableFrom(torrentRowType) &&
                propChanges == 1 &&
                progCs.Contains("_cachedDiskTorrent") &&
                progCs.Contains("OpenBtn.Visibility = Visibility.Collapsed;") &&
                progCs.Contains("RefreshUrlBtn.Visibility"));

            // 7. DropTargetWindow: ClampToVirtualScreen & DragLeave
            var dropType = gdmAsm.GetType("Golan.Gdm.UI.DropTargetWindow")!;
            var clampMethod = dropType.GetMethod("ClampToVirtualScreen", BindingFlags.Public | BindingFlags.Static)!;
            var clamped1 = ((double Left, double Top))clampMethod.Invoke(null, [3200.0, -200.0, 72.0, 72.0, 0.0, 0.0, 1920.0, 1080.0])!;
            var clamped2 = ((double Left, double Top))clampMethod.Invoke(null, [-2500.0, 500.0, 72.0, 72.0, -1920.0, 0.0, 3840.0, 1080.0])!;
            var dropXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "DropTargetWindow.xaml"));
            Check("UI3b_DropTarget_ClampAndDragLeave",
                clamped1 == (1848.0, 0.0) &&
                clamped2 == (-1920.0, 500.0) &&
                dropXaml.Contains("DragLeave=\"OnDragLeave\"") &&
                !dropXaml.Contains("MoveWindow"));

            // 8. InputDialog BiDi FlowDirection in Arabic vs English
            var inputDlgType = gdmAsm.GetType("Golan.Gdm.UI.InputDialog")!;
            var flowMethod = inputDlgType.GetMethod("GetInputFlowDirection", BindingFlags.Public | BindingFlags.Static)!;
            var gdmLocType = gdmAsm.GetType("Golan.Gdm.Core.Loc")!;
            var gdmLocInit = gdmLocType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!;
            gdmLocInit.Invoke(null, ["ar"]);
            var fdEmptyAr = (FlowDirection)flowMethod.Invoke(null, ["", ""])!;
            var fdArabic = (FlowDirection)flowMethod.Invoke(null, ["كتب", ""])!;
            var fdUrlInAr = (FlowDirection)flowMethod.Invoke(null, ["https://example.com/a.zip", "https://example.com/a.zip"])!;
            gdmLocInit.Invoke(null, ["en"]);
            var fdEmptyEn = (FlowDirection)flowMethod.Invoke(null, ["", ""])!;
            Check("UI3b_Dialogs_InputFlowDirection_BiDi",
                fdEmptyAr == FlowDirection.RightToLeft &&
                fdArabic == FlowDirection.RightToLeft &&
                fdUrlInAr == FlowDirection.LeftToRight &&
                fdEmptyEn == FlowDirection.LeftToRight,
                $"emptyAr={fdEmptyAr}, ar={fdArabic}, urlInAr={fdUrlInAr}, emptyEn={fdEmptyEn}");

            // 9. VideoWindow XAML & code: DirectExpander outside ContentPanel, keyboard defaults, folder & space validation, PickQueue
            var videoDoc = XDocument.Load(Path.Combine(gdmSrc, "UI", "VideoWindow.xaml"));
            var contentPanelEl = videoDoc.Descendants().FirstOrDefault(e => e.Attribute(xNs + "Name")?.Value == "ContentPanel");
            var directExpanderInsideContent = contentPanelEl?.Descendants().Any(e => e.Attribute(xNs + "Name")?.Value == "DirectExpander") ?? true;
            var videoXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "VideoWindow.xaml"));
            var videoCs = File.ReadAllText(Path.Combine(gdmSrc, "UI", "VideoWindow.xaml.cs"));
            Check("UI3b_VideoWindow_DirectExpanderKeyboardValidationAndQueue",
                !directExpanderInsideContent &&
                videoXaml.Contains("IsDefault=\"True\"") &&
                videoXaml.Contains("IsCancel=\"True\"") &&
                videoCs.Contains("_info = null;") &&
                videoCs.Contains("AddDownloadWindow.IsAllowedFolder(folder, _manager.Settings)") &&
                videoCs.Contains("app.PickQueue("));

            // 10. i18n parity for new keys in both Arabic and English
            var i18nDir = Path.Combine(gdmSrc, "i18n");
            var mainAr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, "main.ar.json")))!;
            var mainEn = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, "main.en.json")))!;
            var intAr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, "integration.ar.json")))!;
            var intEn = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, "integration.en.json")))!;
            Check("UI3b_I18n_NewKeysPresentInArAndEn",
                mainAr.TryGetValue("main.open.missingFile", out var mfAr) && !string.IsNullOrWhiteSpace(mfAr) &&
                mainEn.TryGetValue("main.open.missingFile", out var mfEn) && !string.IsNullOrWhiteSpace(mfEn) &&
                mainAr.TryGetValue("main.open.missingFolder", out var mdAr) && !string.IsNullOrWhiteSpace(mdAr) &&
                mainEn.TryGetValue("main.open.missingFolder", out var mdEn) && !string.IsNullOrWhiteSpace(mdEn) &&
                intAr.TryGetValue("vb.packager.no_source", out var nsAr) && !string.IsNullOrWhiteSpace(nsAr) &&
                intEn.TryGetValue("vb.packager.no_source", out var nsEn) && !string.IsNullOrWhiteSpace(nsEn));

            // 11. App.xaml.cs review fixes: background install-offer guard, idempotent ExitApp + PrepareForExit, safe modal owner, ShutdownComputer notify order
            int shutdownStartIdx = appCs.IndexOf("new ProcessStartInfo(\"shutdown\"", StringComparison.Ordinal);
            int shutdownNotifyIdx = appCs.IndexOf("Notify(notifyTitle, notifyBody", StringComparison.Ordinal);
            Check("UI3b_App_BackgroundInstallExitAndModalOwner",
                appCs.Contains("if (!StartHidden)") &&
                appCs.Contains("Dispatcher.BeginInvoke(OfferInstallOnceIfNeeded, DispatcherPriority.ApplicationIdle)") &&
                appCs.Contains("if (StartHidden || _exiting) return;") &&
                appCs.Contains("if (_exiting) return;") &&
                appCs.Contains("MainView?.PrepareForExit();") &&
                appCs.Contains("AttachOwner(w") &&
                appCs.Contains("existing.EnsureActive();") &&
                shutdownStartIdx > 0 && shutdownNotifyIdx > shutdownStartIdx);

            // 12. MainWindow closing idempotence + DragOver non-blocking torrent extension check
            bool hasTorrentFastOk = false;
            await RunOnStaAsync(() =>
            {
                var hasTorrentMethod = mainWinType.GetMethod("HasTorrentFileDrop", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
                var uncData = new DataObject(DataFormats.FileDrop, new[] { @"\\127.0.0.1\nonexistent-share\sample.torrent" });
                var txtData = new DataObject(DataFormats.FileDrop, new[] { @"C:\temp\notes.txt" });
                hasTorrentFastOk = (bool)hasTorrentMethod.Invoke(null, [uncData])!
                    && !(bool)hasTorrentMethod.Invoke(null, [txtData])!;
            });
            Check("UI3b_MainWindow_ClosingIdempotentAndDragOverNoDiskIo",
                hasTorrentFastOk &&
                mainCs.Contains("if (App.Instance.IsExiting) return;") &&
                mainCs.Contains("if (_hooksCleaned) return;") &&
                menusCs.Contains("SpeedGraphMenu.IsChecked = _settings.ShowSpeedGraph;"));

            // 13. MainWindow.ExtractLinksFromText: balanced parens, brackets, FTP, and malformed URL filtering
            var extracted2 = (System.Collections.IList)extractLinks.Invoke(null,
                ["See (https://127.0.0.1:8080/file_(1).zip). Also [https://127.0.0.1:8080/doc.pdf]; ftp://127.0.0.1:2121/pub/a.tar.gz, Broken: https://... http://,"])!;
            var prop2 = extracted2.Count > 0 ? extracted2[0]!.GetType().GetProperty("Url")! : null;
            Check("UI3b_Clipboard_ExtractLinksBalancedParensFtpAndMalformed",
                extracted2.Count == 3 &&
                (string?)prop2?.GetValue(extracted2[0]) == "https://127.0.0.1:8080/file_(1).zip" &&
                (string?)prop2?.GetValue(extracted2[1]) == "https://127.0.0.1:8080/doc.pdf" &&
                (string?)prop2?.GetValue(extracted2[2]) == "ftp://127.0.0.1:2121/pub/a.tar.gz",
                $"count={extracted2.Count}");

            // 14. MainWindow.Menus: replacement URL kind/infohash validation, data: URL prefill guard, multi-file torrent folder open
            var isValidRepl = mainWinType.GetMethod("IsValidReplacementUrl", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            var gdmKindType = gdmAsm.GetType("Golan.Gdm.Core.DownloadKind")!;
            var httpItem = Activator.CreateInstance(gdmItemType)!;
            gdmItemType.GetProperty("Kind")!.SetValue(httpItem, Enum.Parse(gdmKindType, "Http"));
            var torItem = Activator.CreateInstance(gdmItemType)!;
            gdmItemType.GetProperty("Kind")!.SetValue(torItem, Enum.Parse(gdmKindType, "Torrent"));
            gdmItemType.GetProperty("TorrentInfoHash")!.SetValue(torItem, "0123456789abcdef0123456789abcdef01234567");

            bool httpAcceptsHttps = (bool)isValidRepl.Invoke(null, [httpItem, "https://127.0.0.1:8080/new.bin"])!;
            bool httpRejectsMagnet = !(bool)isValidRepl.Invoke(null, [httpItem, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"])!;
            bool torRejectsHttp = !(bool)isValidRepl.Invoke(null, [torItem, "https://127.0.0.1:8080/new.bin"])!;
            bool torRejectsOtherHash = !(bool)isValidRepl.Invoke(null, [torItem, "magnet:?xt=urn:btih:fedcba9876543210fedcba9876543210fedcba98"])!;
            bool torAcceptsSameHash = (bool)isValidRepl.Invoke(null, [torItem, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=ubuntu"])!;
            Check("UI3b_Menus_ReplacementUrlKindValidationAndTorrentFolderOpen",
                httpAcceptsHttps && httpRejectsMagnet && torRejectsHttp && torRejectsOtherHash && torAcceptsSameHash &&
                menusCs.Contains("FileNames.IsDataUrl(item.Url) ? \"\" : item.Url") &&
                menusCs.Contains("MenuOpenWith.IsEnabled = completedFile;") &&
                menusCs.Contains("Directory.Exists(item.FullPath)"));

            // 15. AddDownloadWindow: no duplicate initial probe debounce, large data: URL display guard, BiDi NameBox, magnet custom name
            Check("UI3b_AddDownload_NoDuplicateInitialProbeDataUrlAndMagnetName",
                (addCs.Contains("async Task ProbeAsync()\n    {\n        _debounce.Stop();") || addCs.Contains("async Task ProbeAsync()\r\n    {\r\n        _debounce.Stop();")) &&
                addCs.Contains("FileNames.FormatDisplayUrl(u)") &&
                addCs.Contains("NameBox.FlowDirection = InputDialog.GetInputFlowDirection") &&
                addCs.Contains("magItem.FileName = name;") &&
                addCs.Contains("magItem.NameLocked = true;"));

            // 16. ProgressWindow: constructor _loading guard preserves OnDone options, redownload resets completed state, RTL alignments
            var progXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "ProgressWindow.xaml"));
            Check("UI3b_ProgressWindow_ConstructorLoadingGuardAndRedownloadReset",
                progCs.Contains("bool _loading = true;") &&
                progCs.Contains("if (_loading || !IsInitialized)") &&
                progCs.Contains("_completed = false;") &&
                progCs.Contains("public void EnsureActive()") &&
                progCs.Contains("FileNames.FormatDisplayUrl(_item.Url)") &&
                progXaml.Contains("x:Name=\"UrlText\"") &&
                progXaml.Contains("Property=\"FlowDirection\" Value=\"{Binding Path, Converter={StaticResource TextDir}}\""));

            // 17. VideoWindow: initial-URL isolation (DRM/media/referrer/smart-start), SmartModeBox live check, ToolCard & update cancellation cleanup
            Check("UI3b_VideoWindow_InitialUrlIsolationSmartStartAndToolCleanup",
                videoCs.Contains("bool IsInitialUrl(string url)") &&
                videoCs.Contains("ActiveMedia(") &&
                videoCs.Contains("SmartModeBox.IsChecked != true") &&
                videoCs.Contains("ToolCard.Visibility = Visibility.Collapsed;") &&
                videoCs.Contains("catch (OperationCanceledException) when (ct.IsCancellationRequested)") &&
                videoCs.Contains("CreateLinkedTokenSource(_windowCts.Token") &&
                videoCs.Contains("ItemsControl.ContainerFromElement(QualityList") &&
                videoCs.Contains("if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;"));
        }
        catch (Exception ex)
        {
            Check("UI3b_UnhandledException", false, ex.ToString());
        }
        finally
        {
            Loc.Init(prevLang);
        }

        Console.WriteLine($"UI3B summary: {_passes} passed, {_fails} failed");
        return _fails;
    }
}
