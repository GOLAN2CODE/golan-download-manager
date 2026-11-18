using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;

/// <summary>
/// Task 4b tests: first-run automatic extension offer decision (shown once, not shown when installed,
/// not shown after Later), settings persistence, limits audit, and real WPF UI panel background-thread execution,
/// progress updates, per-browser results, manual guide fallback, and cancellation.
/// </summary>
public static class Tests_AutoInstallUi
{
    static int _pass;
    static int _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  [AUTOINSTALL-UI] {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
        if (ok) _pass++; else _fail++;
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

    static async Task RunOnStaAsync(Func<Task> asyncAction, int timeoutMs = 15000)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var disp = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(disp));
            var frame = new DispatcherFrame();
            _ = ExecuteAsync();
            Dispatcher.PushFrame(frame);
            try { disp.InvokeShutdown(); } catch { /* dispatcher shutting down */ }

            async Task ExecuteAsync()
            {
                try
                {
                    await asyncAction();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (done != tcs.Task)
            throw new TimeoutException($"STA async test timed out after {timeoutMs}ms");
        await tcs.Task;
    }

    static IEnumerable<TextBlock> AllTextBlocks(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        if (count == 0 && root is ContentControl cc && cc.Content is DependencyObject childObj)
        {
            if (childObj is TextBlock ctb) yield return ctb;
            foreach (var sub in AllTextBlocks(childObj)) yield return sub;
            yield break;
        }
        if (count == 0 && root is Decorator dec && dec.Child is DependencyObject decChild)
        {
            if (decChild is TextBlock dtb) yield return dtb;
            foreach (var sub in AllTextBlocks(decChild)) yield return sub;
            yield break;
        }
        if (count == 0 && root is Panel pnl)
        {
            foreach (UIElement el in pnl.Children)
            {
                if (el is TextBlock ptb) yield return ptb;
                foreach (var sub in AllTextBlocks(el)) yield return sub;
            }
            yield break;
        }
        for (int i = 0; i < count; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is TextBlock tb) yield return tb;
            foreach (var sub in AllTextBlocks(c)) yield return sub;
        }
    }

    public class GdmDriverProxy : DispatchProxy
    {
        public Assembly GdmAsm { get; set; } = null!;
        public string ExtDir { get; set; } = "";
        public ConcurrentBag<int> ThreadIds { get; } = [];
        public TaskCompletionSource EnteredWaitWindow { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWaitWindow { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancelledObserved { get; private set; }
        /// <summary>Simulates a driver step that finishes after cancellation instead of observing the token.</summary>
        public bool IgnoreCancel { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            if (targetMethod == null) return null;

            switch (targetMethod.Name)
            {
                case "PrepareExtension":
                {
                    var prepType = GdmAsm.GetType("Golan.Gdm.Integration.PreparedExtension")!;
                    return Activator.CreateInstance(
                        prepType,
                        ExtDir,
                        "2.5.1",
                        Path.Combine(ExtDir, "manifest.json"),
                        Path.Combine(ExtDir, "gdm-firefox.xpi"),
                        false);
                }

                case "ReadExtensionState":
                {
                    var browserObj = args![0]!;
                    var exeProp = browserObj.GetType().GetProperty("ExePath")!;
                    var exe = (string?)exeProp.GetValue(browserObj) ?? "";
                    var stateType = GdmAsm.GetType("Golan.Gdm.Integration.DetailedExtensionState")!;
                    if (exe.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase))
                        return Activator.CreateInstance(stateType, true, true, false, "2.5.1");
                    return Activator.CreateInstance(stateType, false, false, false, null);
                }

                case "LaunchBrowser":
                {
                    var browserObj = args![0]!;
                    var exe = (string?)browserObj.GetType().GetProperty("ExePath")!.GetValue(browserObj) ?? "";
                    var url = (string)args[2]!;
                    var newTab = (bool)args[3]!;
                    var handleType = GdmAsm.GetType("Golan.Gdm.Integration.BrowserLaunchHandle")!;
                    return Activator.CreateInstance(handleType, exe, 4242, url, newTab);
                }

                case "WaitForWindowAsync":
                {
                    var launchObj = args![0]!;
                    var exe = (string?)launchObj.GetType().GetProperty("ExePath")!.GetValue(launchObj) ?? "";
                    var ct = (CancellationToken)args[2]!;
                    return WaitWindowImplAsync(exe, ct);
                }

                case "NavigateAddressBarAsync":
                    return Task.FromResult(true);

                case "FindElementAsync":
                {
                    var stateType = GdmAsm.GetType("Golan.Gdm.Integration.UiElementState")!;
                    var state = Activator.CreateInstance(stateType, true, true, (bool?)false, false)!;
                    return WrapTask(stateType, state);
                }

                case "SetToggleAsync":
                    return Task.FromResult(false);

                case "InvokeElementAsync":
                    return Task.FromResult(true);

                case "FillPickerDialogAsync":
                    return Task.FromResult(true);

                case "CloseOpenedTabAsync":
                    return Task.FromResult(true);

                case "SetClipboardText":
                case "OpenFolder":
                    return true;

                default:
                    return null;
            }
        }

        async Task<nint> WaitWindowImplAsync(string exe, CancellationToken ct)
        {
            EnteredWaitWindow.TrySetResult();
            try
            {
                if (IgnoreCancel)
                    await ReleaseWaitWindow.Task.ConfigureAwait(false);
                else
                    await ReleaseWaitWindow.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancelledObserved = true;
                throw;
            }
            // Firefox succeeds via temporary load; Edge fails with window timeout -> ManualFallback.
            return exe.EndsWith("firefox.exe", StringComparison.OrdinalIgnoreCase) ? (nint)1001 : nint.Zero;
        }

        static object WrapTask(Type resultType, object value)
        {
            var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [value])!;
        }
    }

    public static async Task<int> RunAsync(string outDir)
    {
        _pass = 0;
        _fail = 0;
        Console.WriteLine("Automatic extension installer UI & First-Run Offer (Task 4b)");

        var prevLang = Loc.Language;
        var root = Path.GetFullPath(Path.Combine(outDir, "autoinstall-ui-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(root);
        string? backupSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;

        var chrome = new BrowserIntegration.BrowserInfo("Google Chrome", @"Software\Google\Chrome", @"C:\Fake\chrome.exe", "chrome://extensions");
        var edge = new BrowserIntegration.BrowserInfo("Microsoft Edge", @"Software\Microsoft\Edge", @"C:\Fake\msedge.exe", "edge://extensions");
        var firefox = new BrowserIntegration.BrowserInfo("Mozilla Firefox", @"Software\Mozilla", @"C:\Fake\firefox.exe", "about:debugging#/runtime/this-firefox");

        try
        {
            ExtensionContact.Reset();
            Loc.Init("en");

            // 1. First-run offer decision: shown once, not shown on non-first-run, not shown when no browsers exist.
            {
                var fresh = new AppSettings();
                bool offeredInitially = ExtensionOfferPolicy.ShouldOffer(fresh, [chrome, edge], _ => false);
                bool notOfferedWithoutBrowsers = !ExtensionOfferPolicy.ShouldOffer(fresh, [], _ => false);

                ExtensionOfferPolicy.MarkShown(fresh, save: false);
                bool notOfferedSecondTime = !ExtensionOfferPolicy.ShouldOffer(fresh, [chrome, edge], _ => false);

                var nonFirstRun = new AppSettings { FirstRun = false };
                bool notOfferedWhenNotFirstRun = !ExtensionOfferPolicy.ShouldOffer(nonFirstRun, [chrome, edge], _ => false);

                Check("1. first-run offer is shown once on first run and never repeated after MarkShown or on later runs",
                    offeredInitially && notOfferedWithoutBrowsers && notOfferedSecondTime && notOfferedWhenNotFirstRun,
                    $"initial={offeredInitially}, noBrowsers={notOfferedWithoutBrowsers}, second={notOfferedSecondTime}, nonFirstRun={notOfferedWhenNotFirstRun}");
            }

            // 2. First-run offer decision: not shown when any detected browser already has the extension installed.
            {
                var fresh = new AppSettings();
                bool suppressedByDelegate = !ExtensionOfferPolicy.ShouldOffer(fresh, [chrome, edge], b => b == edge);

                var extDir = Path.Combine(root, "ext");
                Directory.CreateDirectory(extDir);
                var chromeUserData = Path.Combine(root, "ChromeUserData");
                var defaultProfile = Path.Combine(chromeUserData, "Default");
                Directory.CreateDirectory(defaultProfile);

                // Disabled extension -> does NOT count as active installation.
                File.WriteAllText(Path.Combine(defaultProfile, "Preferences"), new JsonObject
                {
                    ["extensions"] = new JsonObject
                    {
                        ["settings"] = new JsonObject
                        {
                            [BrowserIntegration.ExtensionId] = new JsonObject { ["path"] = extDir, ["state"] = 0 },
                        },
                    },
                }.ToJsonString());
                bool disabledNotCounted = !ExtensionOfferPolicy.IsExtensionInstalledInBrowser(chrome, extDir, userDataDirOverride: chromeUserData);

                // Enabled extension -> detected read-only, suppresses first-run offer.
                File.WriteAllText(Path.Combine(defaultProfile, "Preferences"), new JsonObject
                {
                    ["extensions"] = new JsonObject
                    {
                        ["settings"] = new JsonObject
                        {
                            [BrowserIntegration.ExtensionId] = new JsonObject { ["path"] = extDir, ["state"] = 1 },
                        },
                    },
                }.ToJsonString());
                bool enabledDetected = ExtensionOfferPolicy.IsExtensionInstalledInBrowser(chrome, extDir, userDataDirOverride: chromeUserData);
                bool offerSuppressedForChromium = !ExtensionOfferPolicy.ShouldOffer(
                    new AppSettings(),
                    [chrome, edge],
                    b => b == chrome && ExtensionOfferPolicy.IsExtensionInstalledInBrowser(b, extDir, userDataDirOverride: chromeUserData));

                // Firefox read-only extensions.json detection.
                var ffRoot = Path.Combine(root, "FirefoxProfiles");
                var ffProf = Path.Combine(ffRoot, "abcd.default-release");
                Directory.CreateDirectory(ffProf);
                File.WriteAllText(Path.Combine(ffProf, "extensions.json"), new JsonObject
                {
                    ["addons"] = new JsonArray(new JsonObject
                    {
                        ["id"] = BrowserIntegration.FirefoxExtensionId,
                        ["userDisabled"] = false,
                        ["appDisabled"] = false,
                    }),
                }.ToJsonString());
                bool ffDetected = ExtensionOfferPolicy.IsExtensionInstalledInBrowser(firefox, extDir, firefoxProfilesRootOverride: ffRoot);

                Check("2. first-run offer is not shown when any detected Chromium or Firefox profile already has the extension",
                    suppressedByDelegate && disabledNotCounted && enabledDetected && offerSuppressedForChromium && ffDetected,
                    $"delegate={suppressedByDelegate}, disabledIgnored={disabledNotCounted}, chromium={enabledDetected}, ff={ffDetected}");
            }

            // 3. First-run offer decision: not shown after Later + settings persistence across Save()/Load().
            {
                var sLater = new AppSettings();
                ExtensionOfferPolicy.RecordChoice(sLater, installNow: false, save: true);

                bool memStateOk = !sLater.FirstRun
                    && sLater.ExtensionOfferShown
                    && sLater.ExtensionOfferChoice == ExtensionOfferPolicy.ChoiceLater;

                // Even if FirstRun were somehow true again, Later choice blocks re-offering.
                var forcedFirstRun = sLater.Clone();
                forcedFirstRun.FirstRun = true;
                bool blockedAfterLater = !ExtensionOfferPolicy.ShouldOffer(forcedFirstRun, [chrome], _ => false);

                var loadedLater = AppSettings.Load();
                bool diskLaterOk = !loadedLater.FirstRun
                    && loadedLater.ExtensionOfferShown
                    && loadedLater.ExtensionOfferChoice == ExtensionOfferPolicy.ChoiceLater
                    && !ExtensionOfferPolicy.ShouldOffer(loadedLater, [chrome], _ => false);

                var sInstall = new AppSettings();
                ExtensionOfferPolicy.RecordChoice(sInstall, installNow: true, save: true);
                var loadedInstall = AppSettings.Load();
                bool diskInstallOk = !loadedInstall.FirstRun
                    && loadedInstall.ExtensionOfferShown
                    && loadedInstall.ExtensionOfferChoice == ExtensionOfferPolicy.ChoiceInstallNow
                    && !ExtensionOfferPolicy.ShouldOffer(loadedInstall, [chrome], _ => false);

                Check("3. choosing Later suppresses future offers and choice persists across AppSettings.Save()/Load()",
                    memStateOk && blockedAfterLater && diskLaterOk && diskInstallOk,
                    $"mem={memStateOk}, blockedAfterLater={blockedAfterLater}, diskLater={diskLaterOk}, diskInstall={diskInstallOk}");
            }

            // 4. Bilingual strings, UI entry points, and safety limits check.
            var gdmSrc = FindGdmSrcDir();
            {
                Loc.Init("en");
                string enBtn = Loc.T("autoinstall.ui.button");
                string enInstallNow = Loc.T("autoinstall.offer.install_now");
                string enLater = Loc.T("autoinstall.offer.later");

                Loc.Init("ar");
                string arBtn = Loc.T("autoinstall.ui.button");
                string arInstallNow = Loc.T("autoinstall.offer.install_now");
                string arLater = Loc.T("autoinstall.offer.later");
                Loc.Init("en");

                string guideCode = File.ReadAllText(Path.Combine(gdmSrc, "UI", "BrowserGuideWindow.cs"));
                string optionsCode = File.ReadAllText(Path.Combine(gdmSrc, "UI", "OptionsWindow.General.cs"));
                string welcomeCode = File.ReadAllText(Path.Combine(gdmSrc, "UI", "SmallWindows.cs"));
                string mainXaml = File.ReadAllText(Path.Combine(gdmSrc, "UI", "MainWindow.xaml"));
                string appCode = File.ReadAllText(Path.Combine(gdmSrc, "App.xaml.cs"));
                string uiCode = File.ReadAllText(Path.Combine(gdmSrc, "UI", "ExtensionAutoInstallUi.cs"));
                string policyCode = File.ReadAllText(Path.Combine(gdmSrc, "Integration", "ExtensionOfferPolicy.cs"));

                bool labelsExact = enBtn == "Install the extension automatically"
                    && arBtn == "تثبيت الإضافة تلقائيًا"
                    && enInstallNow == "Install now"
                    && enLater == "Later"
                    && !string.IsNullOrWhiteSpace(arInstallNow)
                    && !string.IsNullOrWhiteSpace(arLater);

                bool wiredEverywhere = guideCode.Contains("ExtensionAutoInstallPanel")
                    && optionsCode.Contains("ExtensionAutoInstallPanel")
                    && welcomeCode.Contains("ExtensionAutoInstallPanel")
                    && mainXaml.Contains("HelpAutoInstallExtensionMenu")
                    && appCode.Contains("ExtensionOfferPolicy.ShouldOffer")
                    && appCode.Contains("AskFirstRunOffer");

                bool limitsRespected = !uiCode.Contains(".Result")
                    && !uiCode.Contains(".Wait(")
                    && !uiCode.Contains("Registry.")
                    && !policyCode.Contains("Registry.")
                    && !policyCode.Contains("WriteAllText")
                    && !uiCode.Contains(".Kill(")
                    && !policyCode.Contains(".Kill(");

                Check("4. button labels match AR/EN spec, UI entry points are wired, and safety/threading limits hold",
                    labelsExact && wiredEverywhere && limitsRespected,
                    $"en='{enBtn}', ar='{arBtn}', wired={wiredEverywhere}, limits={limitsRespected}");
            }

            // 5. Real WPF ExtensionAutoInstallPanel on STA Dispatcher thread:
            //    - runs installer off the UI thread
            //    - disables install button and shows Cancel + progress while running
            //    - renders per-browser results and opens manual guide on failure
            var gdmAsm = LoadGdmAssembly(gdmSrc);
            await RunOnStaAsync(async () =>
            {
                int uiThreadId = Environment.CurrentManagedThreadId;
                var gdmLocType = gdmAsm.GetType("Golan.Gdm.Core.Loc")!;
                gdmLocType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, ["en"]);

                var driverIface = gdmAsm.GetType("Golan.Gdm.Integration.IBrowserAutomationDriver")!;
                var proxyObj = DispatchProxy.Create(driverIface, typeof(GdmDriverProxy));
                var proxy = (GdmDriverProxy)proxyObj;
                proxy.GdmAsm = gdmAsm;
                proxy.ExtDir = Path.Combine(root, "ui-ext");
                Directory.CreateDirectory(proxy.ExtDir);

                var timeoutsType = gdmAsm.GetType("Golan.Gdm.Integration.AutoInstallTimeouts")!;
                var fastTimeouts = Activator.CreateInstance(
                    timeoutsType,
                    TimeSpan.FromMilliseconds(80),
                    TimeSpan.FromMilliseconds(80),
                    TimeSpan.FromMilliseconds(80),
                    TimeSpan.FromMilliseconds(80),
                    TimeSpan.FromMilliseconds(80),
                    TimeSpan.FromMilliseconds(20))!;

                var installerType = gdmAsm.GetType("Golan.Gdm.Integration.ExtensionAutoInstaller")!;
                var installer = Activator.CreateInstance(installerType, proxyObj, fastTimeouts)!;

                var browserInfoType = gdmAsm.GetType("Golan.Gdm.Integration.BrowserIntegration+BrowserInfo")!;
                var gdmChrome = Activator.CreateInstance(browserInfoType, "Google Chrome", @"Software\Google\Chrome", @"C:\Fake\chrome.exe", "chrome://extensions")!;
                var gdmFirefox = Activator.CreateInstance(browserInfoType, "Mozilla Firefox", @"Software\Mozilla", @"C:\Fake\firefox.exe", "about:debugging#/runtime/this-firefox")!;
                var gdmEdge = Activator.CreateInstance(browserInfoType, "Microsoft Edge", @"Software\Microsoft\Edge", @"C:\Fake\msedge.exe", "edge://extensions")!;

                var listType = typeof(List<>).MakeGenericType(browserInfoType);
                var targetBrowsers = Activator.CreateInstance(listType)!;
                var addMethod = listType.GetMethod("Add")!;
                addMethod.Invoke(targetBrowsers, [gdmChrome]);
                addMethod.Invoke(targetBrowsers, [gdmFirefox]);
                addMethod.Invoke(targetBrowsers, [gdmEdge]);

                string? openedGuideExe = null;
                int guideOpenCount = 0;
                Action<string?> onOpenGuide = exe => { guideOpenCount++; openedGuideExe = exe; };

                var panelType = gdmAsm.GetType("Golan.Gdm.UI.ExtensionAutoInstallPanel")!;
                var panel = (Border)Activator.CreateInstance(panelType, installer, targetBrowsers, onOpenGuide, null, false)!;
                var installBtn = (Button)panelType.GetProperty("InstallButton")!.GetValue(panel)!;
                var cancelBtn = (Button)panelType.GetProperty("CancelButton")!.GetValue(panel)!;
                var progressText = (TextBlock)panelType.GetProperty("ProgressText")!.GetValue(panel)!;
                var resultsPanel = (StackPanel)panelType.GetProperty("ResultsPanel")!.GetValue(panel)!;

                // Trigger real WPF button click on the STA UI thread.
                installBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                // Wait until background task reaches WaitForWindowAsync — proves UI thread was NOT blocked!
                await proxy.EnteredWaitWindow.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.Yield(DispatcherPriority.Background);

                bool disabledWhileRunning = !installBtn.IsEnabled;
                bool cancelShownWhileRunning = cancelBtn.Visibility == Visibility.Visible && cancelBtn.IsEnabled;
                bool progressVisibleWhileRunning = progressText.Visibility == Visibility.Visible
                    && !string.IsNullOrWhiteSpace(progressText.Text);

                // Release background wait and wait for UI completion.
                proxy.ReleaseWaitWindow.TrySetResult();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while ((bool)panelType.GetProperty("IsRunning")!.GetValue(panel)! && sw.ElapsedMilliseconds < 5000)
                    await Task.Delay(15);
                await Dispatcher.Yield(DispatcherPriority.Background);

                bool allOffUiThread = !proxy.ThreadIds.IsEmpty && proxy.ThreadIds.All(id => id != uiThreadId);
                bool reenabledAfter = installBtn.IsEnabled && cancelBtn.Visibility == Visibility.Collapsed;
                bool hasThreeRows = resultsPanel.Visibility == Visibility.Visible && resultsPanel.Children.Count == 3;

                var renderedTexts = AllTextBlocks(resultsPanel).Select(t => t.Text).ToList();
                bool showsAllStatuses = renderedTexts.Any(t => t.Contains("Already installed", StringComparison.Ordinal))
                    && renderedTexts.Any(t => t.Contains("Installed (temporary)", StringComparison.Ordinal))
                    && renderedTexts.Any(t => t.Contains("Needs your action", StringComparison.Ordinal))
                    && renderedTexts.Any(t => t.Contains("edge://extensions/", StringComparison.Ordinal));
                bool manualGuideOpenedForFailed = guideOpenCount == 1
                    && string.Equals(openedGuideExe, "msedge.exe", StringComparison.OrdinalIgnoreCase);

                Check("5. WPF button runs installer off UI thread, disables while running, shows step progress & per-browser results, and opens manual guide on failure",
                    disabledWhileRunning && cancelShownWhileRunning && progressVisibleWhileRunning
                    && allOffUiThread && reenabledAfter && hasThreeRows && showsAllStatuses && manualGuideOpenedForFailed,
                    $"disabled={disabledWhileRunning}, cancel={cancelShownWhileRunning}, prog='{progressText.Text}', offUi={allOffUiThread}, rows={resultsPanel.Children.Count}, guide={openedGuideExe}");
            });

            // 6. Real WPF Cancel button cancels in-flight background install without opening manual guide.
            await RunOnStaAsync(async () =>
            {
                var driverIface = gdmAsm.GetType("Golan.Gdm.Integration.IBrowserAutomationDriver")!;
                var proxyObj = DispatchProxy.Create(driverIface, typeof(GdmDriverProxy));
                var proxy = (GdmDriverProxy)proxyObj;
                proxy.GdmAsm = gdmAsm;
                proxy.ExtDir = Path.Combine(root, "ui-ext-cancel");
                Directory.CreateDirectory(proxy.ExtDir);

                var installerType = gdmAsm.GetType("Golan.Gdm.Integration.ExtensionAutoInstaller")!;
                var installer = Activator.CreateInstance(installerType, proxyObj, null)!;

                var browserInfoType = gdmAsm.GetType("Golan.Gdm.Integration.BrowserIntegration+BrowserInfo")!;
                var gdmEdge = Activator.CreateInstance(browserInfoType, "Microsoft Edge", @"Software\Microsoft\Edge", @"C:\Fake\msedge.exe", "edge://extensions")!;
                var listType = typeof(List<>).MakeGenericType(browserInfoType);
                var targetBrowsers = Activator.CreateInstance(listType)!;
                listType.GetMethod("Add")!.Invoke(targetBrowsers, [gdmEdge]);

                bool manualGuideCalled = false;
                Action<string?> onOpenGuide = _ => manualGuideCalled = true;

                var panelType = gdmAsm.GetType("Golan.Gdm.UI.ExtensionAutoInstallPanel")!;
                var panel = (Border)Activator.CreateInstance(panelType, installer, targetBrowsers, onOpenGuide, null, false)!;
                var installBtn = (Button)panelType.GetProperty("InstallButton")!.GetValue(panel)!;
                var cancelBtn = (Button)panelType.GetProperty("CancelButton")!.GetValue(panel)!;
                var progressText = (TextBlock)panelType.GetProperty("ProgressText")!.GetValue(panel)!;

                installBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await proxy.EnteredWaitWindow.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.Yield(DispatcherPriority.Background);

                cancelBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while ((bool)panelType.GetProperty("IsRunning")!.GetValue(panel)! && sw.ElapsedMilliseconds < 5000)
                    await Task.Delay(15);
                await Dispatcher.Yield(DispatcherPriority.Background);

                bool cancelWorked = proxy.CancelledObserved
                    && installBtn.IsEnabled
                    && cancelBtn.Visibility == Visibility.Collapsed
                    && !manualGuideCalled
                    && progressText.Text.Contains("cancelled", StringComparison.OrdinalIgnoreCase);

                Check("6. Cancel button aborts background installer cleanly, re-enables button, and does not open manual guide",
                    cancelWorked,
                    $"cancelledObs={proxy.CancelledObserved}, btnEnabled={installBtn.IsEnabled}, guideCalled={manualGuideCalled}, text='{progressText.Text}'");
            });

            // 8. Closing the window (cancel) while a driver step ignores the token: the run still ends as cancelled,
            //    and the manual guide is never opened over a closed window.
            await RunOnStaAsync(async () =>
            {
                var driverIface = gdmAsm.GetType("Golan.Gdm.Integration.IBrowserAutomationDriver")!;
                var proxyObj = DispatchProxy.Create(driverIface, typeof(GdmDriverProxy));
                var proxy = (GdmDriverProxy)proxyObj;
                proxy.GdmAsm = gdmAsm;
                proxy.ExtDir = Path.Combine(root, "ui-ext-late-cancel");
                proxy.IgnoreCancel = true;
                Directory.CreateDirectory(proxy.ExtDir);

                var installerType = gdmAsm.GetType("Golan.Gdm.Integration.ExtensionAutoInstaller")!;
                var installer = Activator.CreateInstance(installerType, proxyObj, null)!;

                var browserInfoType = gdmAsm.GetType("Golan.Gdm.Integration.BrowserIntegration+BrowserInfo")!;
                var gdmEdge = Activator.CreateInstance(browserInfoType, "Microsoft Edge", @"Software\Microsoft\Edge", @"C:\Fake\msedge.exe", "edge://extensions")!;
                var listType = typeof(List<>).MakeGenericType(browserInfoType);
                var targetBrowsers = Activator.CreateInstance(listType)!;
                listType.GetMethod("Add")!.Invoke(targetBrowsers, [gdmEdge]);

                bool manualGuideCalled = false;
                Action<string?> onOpenGuide = _ => manualGuideCalled = true;

                var panelType = gdmAsm.GetType("Golan.Gdm.UI.ExtensionAutoInstallPanel")!;
                var panel = (Border)Activator.CreateInstance(panelType, installer, targetBrowsers, onOpenGuide, null, false)!;
                var installBtn = (Button)panelType.GetProperty("InstallButton")!.GetValue(panel)!;
                var progressText = (TextBlock)panelType.GetProperty("ProgressText")!.GetValue(panel)!;

                installBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await proxy.EnteredWaitWindow.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.Yield(DispatcherPriority.Background);

                panelType.GetMethod("CancelInstall")!.Invoke(panel, null);
                proxy.ReleaseWaitWindow.TrySetResult();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while ((bool)panelType.GetProperty("IsRunning")!.GetValue(panel)! && sw.ElapsedMilliseconds < 5000)
                    await Task.Delay(15);
                await Dispatcher.Yield(DispatcherPriority.Background);

                bool ok = !manualGuideCalled
                    && installBtn.IsEnabled
                    && progressText.Text.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
                Check("8. a run cancelled by closing the window never opens the manual guide, even if the last step finished anyway",
                    ok, $"guideCalled={manualGuideCalled}, btnEnabled={installBtn.IsEnabled}, text='{progressText.Text}'");
            });

            // 7. BrowserIntegration.IsRegistered() and InstalledSupportedBrowsers() tolerate restricted registry tokens without throwing.
            {
                bool noThrow = false;
                try
                {
                    _ = BrowserIntegration.IsRegistered();
                    _ = ExtensionAutoInstaller.InstalledSupportedBrowsers();
                    noThrow = true;
                }
                catch (Exception ex)
                {
                    Check("7. BrowserIntegration.IsRegistered and InstalledSupportedBrowsers tolerate restricted registry access", false, ex.Message);
                }
                if (noThrow)
                    Check("7. BrowserIntegration.IsRegistered and InstalledSupportedBrowsers tolerate restricted registry access", true);
            }
        }
        finally
        {
            Loc.Init(prevLang);
            ExtensionContact.Reset();
            try
            {
                if (backupSettings != null) File.WriteAllText(AppPaths.SettingsFile, backupSettings);
                else if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
            }
            catch { /* best-effort settings restore */ }
            try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine($"[AUTOINSTALL-UI Summary] {_pass} passed, {_fail} failed");
        return _fail;
    }
}
