using System.IO.Compression;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;

/// <summary>
/// Unit tests for <see cref="ExtensionAutoInstaller"/> and <see cref="BrowserProfiles.ReadDetailedState"/>
/// using a deterministic in-memory fake <see cref="IBrowserAutomationDriver"/> (never drives a real browser).
/// </summary>
static class Tests_AutoInstall
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  [AUTOINSTALL] {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  [AUTOINSTALL] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
    }

    static readonly AutoInstallTimeouts FastTimeouts = new(
        WindowTimeout: TimeSpan.FromMilliseconds(30),
        PageProbeTimeout: TimeSpan.FromMilliseconds(15),
        StepTimeout: TimeSpan.FromMilliseconds(30),
        PickerTimeout: TimeSpan.FromMilliseconds(30),
        VerifyTimeout: TimeSpan.FromMilliseconds(40),
        VerifyPollInterval: TimeSpan.FromMilliseconds(10));

    sealed class FakeBrowserDriver : IBrowserAutomationDriver
    {
        public PreparedExtension Prepared { get; set; } = new(
            ExtensionDir: @"C:\Temp\GolanExt",
            Version: "2.5.1",
            FirefoxManifestPath: @"C:\Temp\GolanExt\manifest.json",
            FirefoxXpiPath: @"C:\Temp\gdm-firefox.xpi",
            IsFirefoxXpiSigned: false);

        public DetailedExtensionState ProfileState { get; set; } = new(false, false, false, null);
        public nint NextWindowHandle { get; set; } = 0x1234;
        public bool ExtensionsPageInitiallyOpen { get; set; } = true;
        public bool AddressBarNavigationOpensPage { get; set; } = true;

        public bool DevModeEnabledControl { get; set; } = true;
        public bool DevModeBlockedByPolicy { get; set; }
        public bool DevModeToggledOn { get; set; }
        public bool AllowDevModeToggle { get; set; } = true;

        public bool LoadUnpackedAvailable { get; set; } = true;
        public bool PickerOpensAndConfirms { get; set; } = true;
        public bool ReloadButtonAvailable { get; set; } = true;
        public bool UpdateNowButtonAvailable { get; set; } = true;
        public bool EnableToggleAvailable { get; set; } = true;
        public bool FirefoxLoadTempButtonAvailable { get; set; } = true;

        public List<string> OpenTabs { get; } = ["https://example.com/user-tab-1", "https://example.com/user-tab-2"];
        public int LaunchCalls { get; private set; }
        public List<IReadOnlyList<string>> LaunchArgHistory { get; } = [];
        public int WaitWindowCalls { get; private set; }
        public List<string> AddressBarNavigations { get; } = [];
        public List<string> ToggledIds { get; } = [];
        public List<string> InvokedIds { get; } = [];
        public List<(string Path, bool IsFolder)> PickerSubmissions { get; } = [];
        public List<BrowserLaunchHandle> ClosedTabLaunches { get; } = [];
        public string? ClipboardText { get; private set; }
        public List<string> OpenedFolders { get; } = [];
        public bool BrowserProcessKilled { get; private set; }

        bool _extensionsPageActive;

        public PreparedExtension PrepareExtension() => Prepared;

        public DetailedExtensionState ReadExtensionState(
            BrowserIntegration.BrowserInfo browser,
            string? profileDirectory,
            string extensionId,
            string extensionDir) => ProfileState;

        public BrowserLaunchHandle? LaunchBrowser(
            BrowserIntegration.BrowserInfo browser,
            string? profileDirectory,
            string url,
            bool newTab)
        {
            if (string.IsNullOrWhiteSpace(browser.ExePath)) return null;
            LaunchCalls++;
            LaunchArgHistory.Add(ExtensionAutoInstaller.BuildLaunchArguments(browser, profileDirectory, url, newTab));
            if (newTab) OpenTabs.Add(url);
            _extensionsPageActive = ExtensionsPageInitiallyOpen;
            return new BrowserLaunchHandle(browser.ExePath, 4242, url, newTab);
        }

        public Task<nint> WaitForWindowAsync(BrowserLaunchHandle launch, TimeSpan timeout, CancellationToken ct)
        {
            WaitWindowCalls++;
            return Task.FromResult(NextWindowHandle);
        }

        public Task<bool> NavigateAddressBarAsync(nint window, string url, TimeSpan timeout, CancellationToken ct)
        {
            AddressBarNavigations.Add(url);
            if (AddressBarNavigationOpensPage)
                _extensionsPageActive = true;
            return Task.FromResult(AddressBarNavigationOpensPage);
        }

        public Task<UiElementState> FindElementAsync(nint window, UiElementQuery query, TimeSpan timeout, CancellationToken ct)
        {
            if (!_extensionsPageActive)
                return Task.FromResult(new UiElementState(false, false, null));

            return query.AutomationId switch
            {
                "devMode" => Task.FromResult(new UiElementState(
                    Exists: true,
                    IsEnabled: DevModeEnabledControl && !DevModeBlockedByPolicy,
                    IsToggledOn: DevModeToggledOn,
                    IsBlockedByPolicy: DevModeBlockedByPolicy)),
                "enableToggle" => Task.FromResult(EnableToggleAvailable && ProfileState.Installed
                    ? new UiElementState(true, true, !ProfileState.Disabled)
                    : new UiElementState(false, false, null)),
                "qa-temporary-extension-install-button" => Task.FromResult(
                    new UiElementState(FirefoxLoadTempButtonAvailable, FirefoxLoadTempButtonAvailable, null)),
                _ => Task.FromResult(new UiElementState(true, true, null)),
            };
        }

        public Task<bool> SetToggleAsync(nint window, UiElementQuery query, bool turnOn, TimeSpan timeout, CancellationToken ct)
        {
            if (!_extensionsPageActive) return Task.FromResult(false);
            var id = query.AutomationId ?? "";

            if (id == "devMode")
            {
                if (DevModeBlockedByPolicy || !DevModeEnabledControl || !AllowDevModeToggle)
                    return Task.FromResult(false);
                DevModeToggledOn = turnOn;
                ProfileState = ProfileState with { DeveloperMode = turnOn };
                ToggledIds.Add($"devMode:{turnOn}");
                return Task.FromResult(true);
            }

            if (id == "enableToggle")
            {
                if (!EnableToggleAvailable || query.ScopeAutomationId != BrowserIntegration.ExtensionId)
                    return Task.FromResult(false);
                ProfileState = ProfileState with { Disabled = !turnOn };
                ToggledIds.Add($"enableToggle:{turnOn}");
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> InvokeElementAsync(nint window, UiElementQuery query, TimeSpan timeout, CancellationToken ct)
        {
            if (!_extensionsPageActive) return Task.FromResult(false);
            var id = query.AutomationId ?? "";

            if (id == "loadUnpacked")
            {
                if (!LoadUnpackedAvailable || !DevModeToggledOn) return Task.FromResult(false);
                InvokedIds.Add(id);
                return Task.FromResult(true);
            }

            if (id == "dev-reload-button")
            {
                if (!ReloadButtonAvailable || !DevModeToggledOn || query.ScopeAutomationId != BrowserIntegration.ExtensionId)
                    return Task.FromResult(false);
                InvokedIds.Add(id);
                ProfileState = ProfileState with { Version = Prepared.Version };
                return Task.FromResult(true);
            }

            if (id == "updateNow")
            {
                if (!UpdateNowButtonAvailable || !DevModeToggledOn) return Task.FromResult(false);
                InvokedIds.Add(id);
                ProfileState = ProfileState with { Version = Prepared.Version };
                return Task.FromResult(true);
            }

            if (id == "qa-temporary-extension-install-button")
            {
                if (!FirefoxLoadTempButtonAvailable) return Task.FromResult(false);
                InvokedIds.Add(id);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> FillPickerDialogAsync(nint ownerWindow, string targetPath, bool isFolderPicker, TimeSpan timeout, CancellationToken ct)
        {
            if (!PickerOpensAndConfirms)
                return Task.FromResult(false);

            PickerSubmissions.Add((targetPath, isFolderPicker));
            if (isFolderPicker)
                ProfileState = new DetailedExtensionState(DevModeToggledOn, Installed: true, Disabled: false, Prepared.Version);
            return Task.FromResult(true);
        }

        public Task<bool> CloseOpenedTabAsync(nint window, BrowserLaunchHandle launch, CancellationToken ct)
        {
            if (!launch.WasNewTab) return Task.FromResult(false);
            ClosedTabLaunches.Add(launch);
            OpenTabs.Remove(launch.OpenedUrl);
            return Task.FromResult(true);
        }

        public bool SetClipboardText(string text)
        {
            ClipboardText = text;
            return true;
        }

        public bool OpenFolder(string folderPath)
        {
            OpenedFolders.Add(folderPath);
            return true;
        }
    }

    public static async Task<int> RunAsync(string outDir)
    {
        _pass = 0;
        _fail = 0;
        Console.WriteLine("Automatic browser extension installer (Task 4a)");

        var prevLang = Loc.Language;
        var root = Path.GetFullPath(Path.Combine(outDir, "autoinstall-tests-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(root);

        var chrome = new BrowserIntegration.BrowserInfo("Google Chrome", @"Software\Google\Chrome", @"C:\Fake\chrome.exe", "chrome://extensions");
        var edge = new BrowserIntegration.BrowserInfo("Microsoft Edge", @"Software\Microsoft\Edge", @"C:\Fake\msedge.exe", "edge://extensions");
        var yandex = new BrowserIntegration.BrowserInfo("Yandex", @"Software\Yandex\YandexBrowser", @"C:\Fake\browser.exe", "browser://extensions");
        var firefox = new BrowserIntegration.BrowserInfo("Mozilla Firefox", @"Software\Mozilla", @"C:\Fake\firefox.exe", "about:debugging#/runtime/this-firefox");

        try
        {
            Loc.Init("en");

            // 1. Already installed & enabled at current version -> stops immediately without launching browser.
            {
                var drv = new FakeBrowserDriver
                {
                    ProfileState = new DetailedExtensionState(DeveloperMode: true, Installed: true, Disabled: false, Version: "2.5.1"),
                };
                var progressLog = new List<AutoInstallProgress>();
                var installer = new ExtensionAutoInstaller(drv, FastTimeouts);
                var res = await installer.InstallAsync(chrome, "Default", progressLog.Add);

                Check("1. already installed stops immediately without launching browser",
                    res.Status == AutoInstallStatus.AlreadyInstalled
                    && res.Succeeded
                    && drv.LaunchCalls == 0
                    && drv.ClosedTabLaunches.Count == 0
                    && res.MessageKey == "autoinstall.result.already_installed"
                    && progressLog.Any(p => p.Key == "autoinstall.result.already_installed"),
                    $"status={res.Status}, launches={drv.LaunchCalls}");
            }

            // 2. Developer mode off -> turned on, Load unpacked clicked, folder picked, verified, tab closed.
            {
                var drv = new FakeBrowserDriver
                {
                    DevModeToggledOn = false,
                    ProfileState = new DetailedExtensionState(DeveloperMode: false, Installed: false, Disabled: false, Version: null),
                };
                var progressLog = new List<AutoInstallProgress>();
                var installer = new ExtensionAutoInstaller(drv, FastTimeouts);
                var res = await installer.InstallAsync(chrome, "Profile 1", progressLog.Add);

                Check("2. dev mode off then turned on and unpacked extension loaded",
                    res.Status == AutoInstallStatus.Installed
                    && drv.DevModeToggledOn
                    && drv.ToggledIds.Contains("devMode:True")
                    && drv.InvokedIds.Contains("loadUnpacked")
                    && drv.PickerSubmissions is [(@"C:\Temp\GolanExt", true)]
                    && drv.LaunchArgHistory is [["--profile-directory=Profile 1", "--new-window", "chrome://extensions/"]]
                    && progressLog.Any(p => p.Key == "autoinstall.progress.enabling_dev_mode"),
                    $"status={res.Status}, toggled={string.Join(",", drv.ToggledIds)}");
            }

            // 3. Enterprise policy blocks Developer mode -> stops with clear organisation policy message (EN + AR).
            {
                var drvEn = new FakeBrowserDriver { DevModeBlockedByPolicy = true, DevModeEnabledControl = false, DevModeToggledOn = false };
                var resEn = await new ExtensionAutoInstaller(drvEn, FastTimeouts).InstallAsync(chrome);

                Loc.Init("ar");
                var drvAr = new FakeBrowserDriver { DevModeBlockedByPolicy = true, DevModeEnabledControl = false, DevModeToggledOn = false };
                var resAr = await new ExtensionAutoInstaller(drvAr, FastTimeouts).InstallAsync(chrome);
                Loc.Init("en");

                Check("3. enterprise policy blocking dev mode stops with clear EN & AR message",
                    resEn.Status == AutoInstallStatus.BlockedByPolicy
                    && resEn.FailureReason == AutoInstallFailureReason.BlockedByPolicy
                    && resEn.MessageKey == "autoinstall.err.policy_blocked"
                    && resEn.Message.Contains("organisation policy blocks developer mode", StringComparison.OrdinalIgnoreCase)
                    && resAr.Message.Contains("سياسة المؤسسة", StringComparison.Ordinal)
                    && !drvEn.InvokedIds.Contains("loadUnpacked"),
                    $"en={resEn.Message} | ar={resAr.Message}");
            }

            // 4. Folder picker missing/times out -> manual fallback copies folder path, opens folder, keeps tab open.
            {
                var drv = new FakeBrowserDriver
                {
                    DevModeToggledOn = true,
                    PickerOpensAndConfirms = false,
                };
                var installer = new ExtensionAutoInstaller(drv, FastTimeouts);
                var res = await installer.InstallAsync(chrome);

                Check("4. missing folder picker triggers manual fallback (copies path & opens folder via driver)",
                    res.Status == AutoInstallStatus.ManualFallback
                    && res.FailureReason == AutoInstallFailureReason.PickerTimeout
                    && res.ClipboardCopied
                    && drv.ClipboardText == @"C:\Temp\GolanExt"
                    && res.FolderOpened
                    && drv.OpenedFolders is [@"C:\Temp\GolanExt"]
                    && !res.OpenedTabClosed
                    && drv.ClosedTabLaunches.Count == 0
                    && res.ManualSteps.Count == 3,
                    $"status={res.Status}, clip={drv.ClipboardText}, folders={drv.OpenedFolders.Count}");
            }

            // 5. Extension already installed but disabled -> switches enableToggle on without opening folder picker.
            {
                var drv = new FakeBrowserDriver
                {
                    DevModeToggledOn = true,
                    ProfileState = new DetailedExtensionState(DeveloperMode: true, Installed: true, Disabled: true, Version: "2.5.1"),
                };
                var installer = new ExtensionAutoInstaller(drv, FastTimeouts);
                var res = await installer.InstallAsync(chrome);

                Check("5. disabled extension is switched on via enableToggle and confirmed",
                    res.Status == AutoInstallStatus.Installed
                    && drv.ToggledIds.Contains("enableToggle:True")
                    && drv.PickerSubmissions.Count == 0
                    && !drv.ProfileState.Disabled
                    && res.OpenedTabClosed,
                    $"status={res.Status}, toggles={string.Join(",", drv.ToggledIds)}");
            }

            // 6. Older version installed -> presses dev-reload-button (or updateNow fallback) and verifies updated version.
            {
                var drvReload = new FakeBrowserDriver
                {
                    DevModeToggledOn = false,
                    ProfileState = new DetailedExtensionState(DeveloperMode: false, Installed: true, Disabled: false, Version: "2.4.0"),
                };
                var resReload = await new ExtensionAutoInstaller(drvReload, FastTimeouts).InstallAsync(chrome);

                var drvUpdate = new FakeBrowserDriver
                {
                    DevModeToggledOn = true,
                    ReloadButtonAvailable = false,
                    UpdateNowButtonAvailable = true,
                    ProfileState = new DetailedExtensionState(DeveloperMode: true, Installed: true, Disabled: false, Version: "2.4.0"),
                };
                var resUpdate = await new ExtensionAutoInstaller(drvUpdate, FastTimeouts).InstallAsync(chrome);

                Check("6. older version triggers dev-reload-button (and updateNow fallback) and updates to 2.5.1",
                    resReload.Status == AutoInstallStatus.Installed
                    && drvReload.ToggledIds.Contains("devMode:True")
                    && drvReload.InvokedIds.Contains("dev-reload-button")
                    && drvReload.ProfileState.Version == "2.5.1"
                    && resUpdate.Status == AutoInstallStatus.Installed
                    && drvUpdate.InvokedIds.Contains("updateNow")
                    && drvUpdate.ProfileState.Version == "2.5.1",
                    $"reload={string.Join(",", drvReload.InvokedIds)}, update={string.Join(",", drvUpdate.InvokedIds)}");
            }

            // 7. Success closes ONLY the tab Golan opened, preserving all existing user tabs and browser process.
            {
                var drv = new FakeBrowserDriver { DevModeToggledOn = true };
                var res = await new ExtensionAutoInstaller(drv, FastTimeouts).InstallAsync(edge);

                Check("7. success closes ONLY the opened extensions tab and preserves user's other tabs",
                    res.Status == AutoInstallStatus.Installed
                    && res.OpenedTabClosed
                    && drv.ClosedTabLaunches is [{ OpenedUrl: "edge://extensions/", WasNewTab: true }]
                    && drv.OpenTabs.SequenceEqual(["https://example.com/user-tab-1", "https://example.com/user-tab-2"])
                    && !drv.BrowserProcessKilled,
                    $"openTabs={string.Join(",", drv.OpenTabs)}");
            }

            // 8. Browser window timeout -> clear EN/AR timeout message + manual fallback (copies path & opens folder).
            {
                var drvEn = new FakeBrowserDriver { NextWindowHandle = 0 };
                var resEn = await new ExtensionAutoInstaller(drvEn, FastTimeouts).InstallAsync(chrome);

                Loc.Init("ar");
                var drvAr = new FakeBrowserDriver { NextWindowHandle = 0 };
                var resAr = await new ExtensionAutoInstaller(drvAr, FastTimeouts).InstallAsync(chrome);
                Loc.Init("en");

                Check("8. browser window timeout reports clear error and falls back to manual guide",
                    resEn.Status == AutoInstallStatus.ManualFallback
                    && resEn.FailureReason == AutoInstallFailureReason.WindowTimeout
                    && resEn.MessageKey == "autoinstall.err.window_timeout"
                    && resEn.Message.Contains("Could not wait for the browser window, try again", StringComparison.OrdinalIgnoreCase)
                    && resAr.Message.Contains("تعذّر انتظار نافذة المتصفح", StringComparison.Ordinal)
                    && resEn.ClipboardCopied
                    && drvEn.ClipboardText == @"C:\Temp\GolanExt"
                    && resEn.FolderOpened
                    && drvEn.OpenedFolders is [@"C:\Temp\GolanExt"]
                    && drvEn.LaunchCalls == 1,
                    $"en={resEn.Message}");
            }

            // 9. Firefox temporary add-on path -> opens about:debugging, picks manifest.json, warns about restart removal.
            {
                var drvEn = new FakeBrowserDriver();
                var resEn = await new ExtensionAutoInstaller(drvEn, FastTimeouts).InstallAsync(firefox);

                Loc.Init("ar");
                var drvAr = new FakeBrowserDriver();
                var resAr = await new ExtensionAutoInstaller(drvAr, FastTimeouts).InstallAsync(firefox);
                Loc.Init("en");

                Check("9. Firefox temporary add-on loads manifest.json and warns about removal on restart (EN + AR)",
                    resEn.Status == AutoInstallStatus.InstalledTemporary
                    && resEn.Succeeded
                    && resEn.RequiresRestartWarning
                    && resEn.OpenedTabClosed
                    && drvEn.LaunchArgHistory is [["--new-tab", "about:debugging#/runtime/this-firefox"]]
                    && drvEn.InvokedIds.Contains("qa-temporary-extension-install-button")
                    && drvEn.PickerSubmissions is [(@"C:\Temp\GolanExt\manifest.json", false)]
                    && resEn.Message.Contains("removes temporary add-ons", StringComparison.OrdinalIgnoreCase)
                    && resAr.Message.Contains("عند إعادة تشغيله", StringComparison.Ordinal),
                    $"en={resEn.Message}");
            }

            // 10. Address-bar fallback (step d), signed Firefox XPI check, and BrowserProfiles detailed version reading.
            {
                var drvAddr = new FakeBrowserDriver
                {
                    ExtensionsPageInitiallyOpen = false,
                    AddressBarNavigationOpensPage = true,
                    DevModeToggledOn = true,
                };
                var resYandex = await new ExtensionAutoInstaller(drvAddr, FastTimeouts).InstallAsync(yandex);

                var unsignedXpi = Path.Combine(root, "unsigned.xpi");
                using (var z = ZipFile.Open(unsignedXpi, ZipArchiveMode.Create))
                {
                    var e = z.CreateEntry("manifest.json");
                    using var sw = new StreamWriter(e.Open());
                    sw.Write("{\"manifest_version\":3}");
                }

                var signedXpi = Path.Combine(root, "signed.xpi");
                using (var z = ZipFile.Open(signedXpi, ZipArchiveMode.Create))
                {
                    var e = z.CreateEntry("META-INF/mozilla.rsa");
                    using var sw = new StreamWriter(e.Open());
                    sw.Write("sig");
                }

                var drvSigned = new FakeBrowserDriver
                {
                    Prepared = new PreparedExtension(@"C:\Temp\GolanExt", "2.5.1", @"C:\Temp\GolanExt\manifest.json", signedXpi, IsFirefoxXpiSigned: true),
                };
                var resSigned = await new ExtensionAutoInstaller(drvSigned, FastTimeouts).InstallAsync(firefox);

                var profDir = Path.Combine(root, "YandexProfile");
                Directory.CreateDirectory(profDir);
                var prefs = new JsonObject
                {
                    ["extensions"] = new JsonObject
                    {
                        ["ui"] = new JsonObject { ["developer_mode"] = true },
                        ["settings"] = new JsonObject
                        {
                            [BrowserIntegration.ExtensionId] = new JsonObject
                            {
                                ["path"] = @"C:\Temp\GolanExt",
                                ["state"] = 1,
                                ["manifest"] = new JsonObject { ["version"] = "2.5.1" },
                            }
                        }
                    }
                };
                File.WriteAllText(Path.Combine(profDir, "Preferences"), prefs.ToJsonString());
                var detailed = BrowserProfiles.ReadDetailedState(profDir, BrowserIntegration.ExtensionId, @"C:\Temp\GolanExt");

                Check("10. address-bar fallback (Yandex browser://extensions/), signed XPI detection, and ReadDetailedState",
                    resYandex.Status == AutoInstallStatus.Installed
                    && drvAddr.AddressBarNavigations is ["browser://extensions/"]
                    && !UiaBrowserDriver.IsSignedMozillaXpi(unsignedXpi)
                    && UiaBrowserDriver.IsSignedMozillaXpi(signedXpi)
                    && resSigned.Status == AutoInstallStatus.Installed
                    && !resSigned.RequiresRestartWarning
                    && drvSigned.LaunchArgHistory is [[var launchedXpi]] && launchedXpi == signedXpi
                    && detailed is { DeveloperMode: true, Installed: true, Disabled: false, Version: "2.5.1" }
                    && BrowserProfiles.UserDataDir("browser.exe")!.EndsWith(@"Yandex\YandexBrowser\User Data")
                    && BrowserProfiles.UserDataDir("comet.exe")!.EndsWith(@"Perplexity\Comet\User Data"),
                    $"yandex={resYandex.Status}, nav={string.Join(",", drvAddr.AddressBarNavigations)}, detailed={detailed}");
            }
        }
        finally
        {
            Loc.Init(prevLang);
            try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine($"[AUTOINSTALL Summary] {_pass} passed, {_fail} failed");
        return _fail;
    }
}
