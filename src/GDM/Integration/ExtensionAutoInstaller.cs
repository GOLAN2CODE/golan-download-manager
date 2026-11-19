using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.Integration;

/// <summary>Prepared extension files and metadata produced before running browser automation.</summary>
public sealed record PreparedExtension(
    string ExtensionDir,
    string Version,
    string FirefoxManifestPath,
    string FirefoxXpiPath,
    bool IsFirefoxXpiSigned);

/// <summary>Handle representing a browser launch initiated by Golan so only the opened tab is closed on completion.</summary>
public sealed record BrowserLaunchHandle(
    string ExePath,
    int? ProcessId,
    string OpenedUrl,
    bool WasNewTab)
{
    /// <summary>Browser windows that already existed before the launch: none of them is the window Golan opened.</summary>
    public IReadOnlyCollection<nint>? WindowsBeforeLaunch { get; init; }
}

/// <summary>Identifies a browser UI Automation control by AutomationId and/or localized accessible names.</summary>
public sealed record UiElementQuery(
    string? AutomationId,
    IReadOnlyList<string> Names,
    string? ScopeAutomationId = null);

/// <summary>Snapshot of a queried browser UI Automation control.</summary>
public sealed record UiElementState(
    bool Exists,
    bool IsEnabled,
    bool? IsToggledOn,
    bool IsBlockedByPolicy = false);

/// <summary>
/// Thin UI-automation and OS abstraction behind <see cref="ExtensionAutoInstaller"/> so all decision logic,
/// timeouts, and fallbacks stay testable without driving a real browser.
/// </summary>
public interface IBrowserAutomationDriver
{
    PreparedExtension PrepareExtension();
    DetailedExtensionState ReadExtensionState(BrowserIntegration.BrowserInfo browser, string? profileDirectory, string extensionId, string extensionDir);
    BrowserLaunchHandle? LaunchBrowser(BrowserIntegration.BrowserInfo browser, string? profileDirectory, string url, bool newTab);
    Task<nint> WaitForWindowAsync(BrowserLaunchHandle launch, TimeSpan timeout, CancellationToken ct);
    Task<bool> NavigateAddressBarAsync(nint window, string url, TimeSpan timeout, CancellationToken ct);
    Task<UiElementState> FindElementAsync(nint window, UiElementQuery query, TimeSpan timeout, CancellationToken ct);
    Task<bool> SetToggleAsync(nint window, UiElementQuery query, bool turnOn, TimeSpan timeout, CancellationToken ct);
    Task<bool> InvokeElementAsync(nint window, UiElementQuery query, TimeSpan timeout, CancellationToken ct);
    Task<bool> FillPickerDialogAsync(nint ownerWindow, string targetPath, bool isFolderPicker, TimeSpan timeout, CancellationToken ct);
    Task<bool> CloseOpenedTabAsync(nint window, BrowserLaunchHandle launch, CancellationToken ct);
    bool SetClipboardText(string text);
    bool OpenFolder(string folderPath);
}

/// <summary>Per-step timeouts for <see cref="ExtensionAutoInstaller"/>.</summary>
public sealed record AutoInstallTimeouts(
    TimeSpan WindowTimeout,
    TimeSpan PageProbeTimeout,
    TimeSpan StepTimeout,
    TimeSpan PickerTimeout,
    TimeSpan VerifyTimeout,
    TimeSpan VerifyPollInterval)
{
    public static AutoInstallTimeouts Default { get; } = new(
        WindowTimeout: TimeSpan.FromSeconds(12),
        PageProbeTimeout: TimeSpan.FromMilliseconds(2500),
        StepTimeout: TimeSpan.FromSeconds(8),
        PickerTimeout: TimeSpan.FromSeconds(10),
        // Chromium writes a profile change to disk up to ten seconds after it happens, so a shorter wait reports a
        // good install as failed.
        VerifyTimeout: TimeSpan.FromSeconds(15),
        VerifyPollInterval: TimeSpan.FromMilliseconds(250));
}

public enum AutoInstallStatus
{
    AlreadyInstalled,
    Installed,
    InstalledTemporary,
    BlockedByPolicy,
    ManualFallback,
}

public enum AutoInstallFailureReason
{
    None,
    BrowserNotFound,
    WindowTimeout,
    ExtensionsPageTimeout,
    BlockedByPolicy,
    DevModeToggleFailed,
    LoadUnpackedNotFound,
    PickerTimeout,
    EnableToggleFailed,
    ReloadFailed,
    VerificationTimeout,
    FirefoxTemporaryFailed,
}

/// <summary>
/// One place the extension can be installed into: a browser and one of its profiles (no profile for Firefox or a
/// browser whose profiles cannot be read). Shown to the user to choose from before anything is installed.
/// </summary>
public sealed record AutoInstallTarget(
    BrowserIntegration.BrowserInfo Browser,
    string? ProfileDirectory,
    string ProfileLabel,
    bool Installed,
    bool NeedsUpdate,
    bool IsLastUsed)
{
    public string Label => ProfileLabel.Length > 0 ? $"{Browser.Name} — {ProfileLabel}" : Browser.Name;
}

/// <summary>Localized progress update emitted during automatic extension installation.</summary>
public sealed record AutoInstallProgress(string BrowserName, string Key, string Message);

/// <summary>Outcome of running automatic extension installation for a single browser/profile.</summary>
public sealed record AutoInstallResult(
    BrowserIntegration.BrowserInfo Browser,
    string? ProfileDirectory,
    AutoInstallStatus Status,
    AutoInstallFailureReason FailureReason,
    string MessageKey,
    string Message,
    string ExtensionPath,
    string ExtensionsUrl,
    bool OpenedTabClosed,
    bool ClipboardCopied,
    bool FolderOpened,
    bool RequiresRestartWarning,
    IReadOnlyList<string> ManualSteps)
{
    public bool Succeeded => Status is AutoInstallStatus.AlreadyInstalled
        or AutoInstallStatus.Installed
        or AutoInstallStatus.InstalledTemporary;
}

/// <summary>
/// One-button visible browser extension installer for Chromium-family browsers (Chrome, Edge, Brave, Chromium,
/// Opera, Vivaldi, Yandex, Comet) and Firefox, with per-step timeouts and safe manual fallback.
/// </summary>
public sealed class ExtensionAutoInstaller
{
    public static UiElementQuery DevModeQuery { get; } = new(
        "devMode",
        ["Developer mode", "وضع المطوّر", "وضع المطور"]);

    public static UiElementQuery LoadUnpackedQuery { get; } = new(
        "loadUnpacked",
        ["Load unpacked", "تحميل إضافة غير مضغوطة", "تحميل عنصر غير مضغوط"]);

    public static UiElementQuery EnableToggleQuery { get; } = new(
        "enableToggle",
        ["Enable", "On", "تفعيل", "تمكين"],
        ScopeAutomationId: BrowserIntegration.ExtensionId);

    public static UiElementQuery ReloadButtonQuery { get; } = new(
        "dev-reload-button",
        ["Reload", "إعادة التحميل", "إعادة تحميل"],
        ScopeAutomationId: BrowserIntegration.ExtensionId);

    public static UiElementQuery UpdateNowQuery { get; } = new(
        "updateNow",
        ["Update", "تحديث"]);

    public static UiElementQuery FirefoxLoadTempAddonQuery { get; } = new(
        "qa-temporary-extension-install-button",
        ["Load Temporary Add-on…", "Load Temporary Add-on...", "Load Temporary Add-on", "تحميل إضافة مؤقتة…", "تحميل إضافة مؤقتة..."]);

    readonly IBrowserAutomationDriver _driver;
    readonly AutoInstallTimeouts _timeouts;
    // Manual-guide helpers already done in this run: several browsers failing must not open one folder window each,
    // or keep replacing what the user has on the clipboard.
    readonly HashSet<string> _copiedPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _openedFolders = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionAutoInstaller(IBrowserAutomationDriver? driver = null, AutoInstallTimeouts? timeouts = null)
    {
        _driver = driver ?? new UiaBrowserDriver();
        _timeouts = timeouts ?? AutoInstallTimeouts.Default;
    }

    /// <summary>
    /// All browsers supported by the auto-installer: reuses <see cref="BrowserIntegration.Browsers"/> plus Yandex and Comet.
    /// </summary>
    public static IReadOnlyList<BrowserIntegration.BrowserInfo> SupportedBrowsers()
    {
        var list = BrowserIntegration.Browsers().ToList();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Beta, Dev and Canary are browsers of their own (own profiles): listed apart, never mistaken for stable Chrome.
        foreach (var (name, folder) in new[] { ("Google Chrome Beta", "Chrome Beta"), ("Google Chrome Dev", "Chrome Dev"), ("Google Chrome Canary", "Chrome SxS") })
        {
            var exe = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
                .Select(root => Path.Combine(Environment.GetFolderPath(root), "Google", folder, "Application", "chrome.exe"))
                .FirstOrDefault(File.Exists);
            if (exe != null && !list.Any(b => string.Equals(b.ExePath, exe, StringComparison.OrdinalIgnoreCase)))
                list.Add(new BrowserIntegration.BrowserInfo(name, @"Software\Google\Chrome", exe, "chrome://extensions/"));
        }
        list.Add(new BrowserIntegration.BrowserInfo(
            Loc.T("autoinstall.browser.yandex"),
            @"Software\Yandex\YandexBrowser",
            FindExtraBrowserExe("browser.exe", Path.Combine(local, "Yandex", "YandexBrowser", "Application", "browser.exe")),
            "browser://extensions/"));
        list.Add(new BrowserIntegration.BrowserInfo(
            Loc.T("autoinstall.browser.comet"),
            @"Software\Perplexity\Comet",
            FindExtraBrowserExe("comet.exe", Path.Combine(local, "Perplexity", "Comet", "Application", "comet.exe")),
            "chrome://extensions/"));
        return list;
    }

    /// <summary>
    /// Read-only search of every installed browser's profiles, with the state of Golan's extension in each one.
    /// Nothing is launched or changed: the result is what the user picks from.
    /// </summary>
    public static IReadOnlyList<AutoInstallTarget> ScanTargets(
        IReadOnlyList<BrowserIntegration.BrowserInfo>? browsers = null,
        Func<BrowserIntegration.BrowserInfo, string?>? userDataDirOf = null,
        string? extensionDir = null,
        string? currentVersion = null)
    {
        var extDir = extensionDir ?? AppPaths.ExtensionDir;
        var version = currentVersion ?? App.Version;
        var targets = new List<AutoInstallTarget>();
        foreach (var browser in browsers ?? InstalledSupportedBrowsers())
        {
            if (string.IsNullOrWhiteSpace(browser.ExePath)) continue;
            if (IsFirefox(browser))
            {
                bool inFirefox = ExtensionOfferPolicy.IsExtensionInstalledInBrowser(browser, extDir);
                targets.Add(new AutoInstallTarget(browser, null, "", inFirefox, NeedsUpdate: false, IsLastUsed: true));
                continue;
            }

            var userData = userDataDirOf != null ? userDataDirOf(browser) : BrowserProfiles.UserDataDir(browser.ExePath);
            var profiles = userData != null && Directory.Exists(userData) ? BrowserProfiles.ReadProfiles(userData) : [];
            if (userData == null || profiles.Count == 0)
            {
                targets.Add(new AutoInstallTarget(browser, null, "", Installed: false, NeedsUpdate: false, IsLastUsed: true));
                continue;
            }

            foreach (var p in profiles)
            {
                var path = p.Directory.Length == 0 ? userData : Path.Combine(userData, p.Directory);
                var st = BrowserProfiles.ReadDetailedState(path, BrowserIntegration.ExtensionId, extDir);
                bool active = st.Installed && !st.Disabled;
                bool outdated = st.Installed && (st.LoadedFromOtherFolder || !IsCurrentOrNewerVersion(st.Version, version));
                targets.Add(new AutoInstallTarget(browser, p.Directory, p.Label, active && !outdated, outdated || (st.Installed && st.Disabled), p.IsLastUsed));
            }
        }
        return targets;
    }

    /// <summary>Installed browsers supported by the auto-installer.</summary>
    public static IReadOnlyList<BrowserIntegration.BrowserInfo> InstalledSupportedBrowsers()
        => SupportedBrowsers().Where(b => !string.IsNullOrWhiteSpace(b.ExePath)).ToList();

    static string? FindExtraBrowserExe(string exeName, string localFallbackPath)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (key?.GetValue(null) is string p && File.Exists(p.Trim('"')))
                    return p.Trim('"');
            }
            catch { /* ignore registry read issues */ }
        }
        return File.Exists(localFallbackPath) ? localFallbackPath : null;
    }

    public static bool IsFirefox(BrowserIntegration.BrowserInfo browser)
    {
        var exe = string.IsNullOrWhiteSpace(browser.ExePath) ? "" : Path.GetFileName(browser.ExePath);
        return string.Equals(exe, "firefox.exe", StringComparison.OrdinalIgnoreCase)
            || browser.ExtensionsPage.StartsWith("about:debugging", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalizes the browser's extensions page URL (e.g. chrome://extensions/, edge://extensions/, browser://extensions/).</summary>
    public static string GetExtensionsUrl(BrowserIntegration.BrowserInfo browser)
    {
        if (IsFirefox(browser))
            return "about:debugging#/runtime/this-firefox";

        var exe = string.IsNullOrWhiteSpace(browser.ExePath) ? "" : Path.GetFileName(browser.ExePath).ToLowerInvariant();
        var raw = string.IsNullOrWhiteSpace(browser.ExtensionsPage)
            ? exe switch
            {
                "msedge.exe" => "edge://extensions/",
                "brave.exe" => "brave://extensions/",
                "opera.exe" => "opera://extensions/",
                "vivaldi.exe" => "vivaldi://extensions/",
                "browser.exe" => "browser://extensions/",
                _ => "chrome://extensions/",
            }
            : browser.ExtensionsPage.Trim();

        if (!raw.EndsWith('/') && !raw.Contains('#') && !raw.Contains('?'))
            raw += "/";
        return raw;
    }

    /// <summary>Command-line arguments used to open the extensions page in a new tab (or open a signed Firefox XPI).</summary>
    public static IReadOnlyList<string> BuildLaunchArguments(BrowserIntegration.BrowserInfo browser, string? profileDirectory, string url, bool newTab)
    {
        var args = new List<string>();
        if (!IsFirefox(browser) && !string.IsNullOrWhiteSpace(profileDirectory))
            args.Add("--profile-directory=" + profileDirectory);
        // Chromium gets a window of its own in the chosen profile: the page is then never mixed up with a window the
        // user already has open (possibly of another profile), and closing it afterwards touches nothing of theirs.
        if (newTab)
            args.Add(IsFirefox(browser) ? "--new-tab" : "--new-window");
        args.Add(url);
        return args;
    }

    public static bool IsCurrentOrNewerVersion(string? installedVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion)) return false;
        if (string.IsNullOrWhiteSpace(currentVersion)) return true;
        var inst = installedVersion.Trim();
        var curr = currentVersion.Trim();
        if (Version.TryParse(inst, out var iv) && Version.TryParse(curr, out var cv))
            return iv >= cv;
        return string.Equals(inst, curr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs automatic installation across every detected installed browser sequentially.</summary>
    public async Task<IReadOnlyList<AutoInstallResult>> InstallAllInstalledAsync(
        Action<AutoInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<AutoInstallResult>();
        foreach (var browser in InstalledSupportedBrowsers())
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await InstallAsync(browser, profileDirectory: null, progress, ct).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>Runs the automatic extension installation flow (steps a–i) for one browser and optional profile.</summary>
    public async Task<AutoInstallResult> InstallAsync(
        BrowserIntegration.BrowserInfo browser,
        string? profileDirectory = null,
        Action<AutoInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ct.ThrowIfCancellationRequested();

        var extensionsUrl = GetExtensionsUrl(browser);
        bool firefox = IsFirefox(browser);
        PreparedExtension? prepared = null;
        BrowserLaunchHandle? launch = null;

        try
        {
            // Step (a): Extract/refresh the unpacked extension folder (fixed manifest key -> BrowserIntegration.ExtensionId).
            Report(progress, browser.Name, "autoinstall.progress.extracting", Loc.T("autoinstall.progress.extracting"));
            prepared = _driver.PrepareExtension();

            if (firefox)
                return await InstallFirefoxAsync(browser, prepared, extensionsUrl, progress, ct).ConfigureAwait(false);

            // Step (b): Check BrowserProfiles for existing active installation at the current version.
            Report(progress, browser.Name, "autoinstall.progress.checking_state", Loc.T("autoinstall.progress.checking_state", browser.Name));
            var initialState = _driver.ReadExtensionState(browser, profileDirectory, BrowserIntegration.ExtensionId, prepared.ExtensionDir);
            bool hasCurrentVersion = IsCurrentOrNewerVersion(initialState.Version, prepared.Version);

            // Loaded from another folder (an old location, a deleted copy): it must be loaded again from Golan's own.
            bool wrongFolder = initialState.Installed && initialState.LoadedFromOtherFolder;

            if (initialState.Installed && !initialState.Disabled && hasCurrentVersion && !wrongFolder)
            {
                var verText = string.IsNullOrWhiteSpace(initialState.Version) ? prepared.Version : initialState.Version!;
                var doneMsg = Loc.T("autoinstall.result.already_installed", browser.Name, verText);
                Report(progress, browser.Name, "autoinstall.result.already_installed", doneMsg);
                return new AutoInstallResult(
                    Browser: browser,
                    ProfileDirectory: profileDirectory,
                    Status: AutoInstallStatus.AlreadyInstalled,
                    FailureReason: AutoInstallFailureReason.None,
                    MessageKey: "autoinstall.result.already_installed",
                    Message: doneMsg,
                    ExtensionPath: prepared.ExtensionDir,
                    ExtensionsUrl: extensionsUrl,
                    OpenedTabClosed: false,
                    ClipboardCopied: false,
                    FolderOpened: false,
                    RequiresRestartWarning: false,
                    ManualSteps: []);
            }

            // Step (c): Launch the browser with a new window at <scheme>://extensions/ and wait for its window.
            if (string.IsNullOrWhiteSpace(browser.ExePath))
            {
                return FallbackToManual(
                    browser, profileDirectory, prepared, extensionsUrl, launch,
                    AutoInstallFailureReason.BrowserNotFound,
                    "autoinstall.err.browser_not_found",
                    Loc.T("autoinstall.err.browser_not_found", browser.Name),
                    progress);
            }

            Report(progress, browser.Name, "autoinstall.progress.launching", Loc.T("autoinstall.progress.launching", browser.Name));
            launch = _driver.LaunchBrowser(browser, profileDirectory, extensionsUrl, newTab: true);
            if (launch == null)
            {
                return FallbackToManual(
                    browser, profileDirectory, prepared, extensionsUrl, launch: null,
                    AutoInstallFailureReason.BrowserNotFound,
                    "autoinstall.err.browser_not_found",
                    Loc.T("autoinstall.err.browser_not_found", browser.Name),
                    progress);
            }

            Report(progress, browser.Name, "autoinstall.progress.waiting_window", Loc.T("autoinstall.progress.waiting_window", browser.Name));
            var window = await _driver.WaitForWindowAsync(launch, _timeouts.WindowTimeout, ct).ConfigureAwait(false);
            if (window == 0)
            {
                return FallbackToManual(
                    browser, profileDirectory, prepared, extensionsUrl, launch,
                    AutoInstallFailureReason.WindowTimeout,
                    "autoinstall.err.window_timeout",
                    Loc.T("autoinstall.err.window_timeout"),
                    progress);
            }

            // Step (d): Probe for the extensions page; if it did not open, navigate via the address bar.
            var devModeState = await _driver.FindElementAsync(window, DevModeQuery, _timeouts.PageProbeTimeout, ct).ConfigureAwait(false);
            if (!devModeState.Exists)
            {
                Report(progress, browser.Name, "autoinstall.progress.typing_url", Loc.T("autoinstall.progress.typing_url", extensionsUrl));
                await _driver.NavigateAddressBarAsync(window, extensionsUrl, _timeouts.StepTimeout, ct).ConfigureAwait(false);
                devModeState = await _driver.FindElementAsync(window, DevModeQuery, _timeouts.StepTimeout, ct).ConfigureAwait(false);
            }

            if (!devModeState.Exists)
            {
                return FallbackToManual(
                    browser, profileDirectory, prepared, extensionsUrl, launch,
                    AutoInstallFailureReason.ExtensionsPageTimeout,
                    "autoinstall.err.page_timeout",
                    Loc.T("autoinstall.err.page_timeout", extensionsUrl),
                    progress);
            }

            // Step (e): Ensure Developer mode is on; stop immediately if blocked by enterprise policy.
            if (devModeState.IsBlockedByPolicy || (!devModeState.IsEnabled && devModeState.IsToggledOn != true))
            {
                var policyMsg = Loc.T("autoinstall.err.policy_blocked");
                Report(progress, browser.Name, "autoinstall.err.policy_blocked", policyMsg);
                return new AutoInstallResult(
                    Browser: browser,
                    ProfileDirectory: profileDirectory,
                    Status: AutoInstallStatus.BlockedByPolicy,
                    FailureReason: AutoInstallFailureReason.BlockedByPolicy,
                    MessageKey: "autoinstall.err.policy_blocked",
                    Message: policyMsg,
                    ExtensionPath: prepared.ExtensionDir,
                    ExtensionsUrl: extensionsUrl,
                    OpenedTabClosed: false,
                    ClipboardCopied: false,
                    FolderOpened: false,
                    RequiresRestartWarning: false,
                    // No manual steps: they would tell the user to switch on the very thing the policy forbids.
                    ManualSteps: []);
            }

            if (devModeState.IsToggledOn != true)
            {
                Report(progress, browser.Name, "autoinstall.progress.enabling_dev_mode", Loc.T("autoinstall.progress.enabling_dev_mode"));
                bool devTurnedOn = await _driver.SetToggleAsync(window, DevModeQuery, turnOn: true, _timeouts.StepTimeout, ct).ConfigureAwait(false);
                if (!devTurnedOn)
                {
                    var recheck = await _driver.FindElementAsync(window, DevModeQuery, _timeouts.PageProbeTimeout, ct).ConfigureAwait(false);
                    if (recheck.IsBlockedByPolicy || (!recheck.IsEnabled && recheck.IsToggledOn != true))
                    {
                        var policyMsg = Loc.T("autoinstall.err.policy_blocked");
                        Report(progress, browser.Name, "autoinstall.err.policy_blocked", policyMsg);
                        return new AutoInstallResult(
                            Browser: browser,
                            ProfileDirectory: profileDirectory,
                            Status: AutoInstallStatus.BlockedByPolicy,
                            FailureReason: AutoInstallFailureReason.BlockedByPolicy,
                            MessageKey: "autoinstall.err.policy_blocked",
                            Message: policyMsg,
                            ExtensionPath: prepared.ExtensionDir,
                            ExtensionsUrl: extensionsUrl,
                            OpenedTabClosed: false,
                            ClipboardCopied: false,
                            FolderOpened: false,
                            RequiresRestartWarning: false,
                            ManualSteps: []);
                    }

                    return FallbackToManual(
                        browser, profileDirectory, prepared, extensionsUrl, launch,
                        AutoInstallFailureReason.DevModeToggleFailed,
                        "autoinstall.err.dev_mode_failed",
                        Loc.T("autoinstall.err.dev_mode_failed"),
                        progress);
                }
            }

            bool needsVersionReload = initialState.Installed && !hasCurrentVersion && !wrongFolder;

            if (!initialState.Installed || wrongFolder)
            {
                // Step (f): Click "Load unpacked", fill folder picker with extensionDir, and confirm.
                Report(progress, browser.Name, "autoinstall.progress.loading_unpacked", Loc.T("autoinstall.progress.loading_unpacked"));
                bool clickedLoad = await _driver.InvokeElementAsync(window, LoadUnpackedQuery, _timeouts.StepTimeout, ct).ConfigureAwait(false);
                if (!clickedLoad)
                {
                    return FallbackToManual(
                        browser, profileDirectory, prepared, extensionsUrl, launch,
                        AutoInstallFailureReason.LoadUnpackedNotFound,
                        "autoinstall.err.picker_missing",
                        Loc.T("autoinstall.err.picker_missing"),
                        progress);
                }

                bool picked = await _driver.FillPickerDialogAsync(window, prepared.ExtensionDir, isFolderPicker: true, _timeouts.PickerTimeout, ct).ConfigureAwait(false);
                if (!picked)
                {
                    return FallbackToManual(
                        browser, profileDirectory, prepared, extensionsUrl, launch,
                        AutoInstallFailureReason.PickerTimeout,
                        "autoinstall.err.picker_missing",
                        Loc.T("autoinstall.err.picker_missing"),
                        progress);
                }

                // If Chrome loaded it in a disabled state, enable it via enableToggle.
                var postLoadToggle = await _driver.FindElementAsync(window, EnableToggleQuery, _timeouts.PageProbeTimeout, ct).ConfigureAwait(false);
                if (postLoadToggle.Exists && postLoadToggle.IsToggledOn == false)
                {
                    Report(progress, browser.Name, "autoinstall.progress.enabling_ext", Loc.T("autoinstall.progress.enabling_ext"));
                    await _driver.SetToggleAsync(window, EnableToggleQuery, turnOn: true, _timeouts.StepTimeout, ct).ConfigureAwait(false);
                }
            }
            else
            {
                // Step (g): Extension is already present — enable if disabled, reload/update if older version.
                if (initialState.Disabled)
                {
                    Report(progress, browser.Name, "autoinstall.progress.enabling_ext", Loc.T("autoinstall.progress.enabling_ext"));
                    bool enabled = await _driver.SetToggleAsync(window, EnableToggleQuery, turnOn: true, _timeouts.StepTimeout, ct).ConfigureAwait(false);
                    if (!enabled)
                    {
                        return FallbackToManual(
                            browser, profileDirectory, prepared, extensionsUrl, launch,
                            AutoInstallFailureReason.EnableToggleFailed,
                            "autoinstall.err.enable_failed",
                            Loc.T("autoinstall.err.enable_failed"),
                            progress);
                    }
                }

                if (needsVersionReload)
                {
                    Report(progress, browser.Name, "autoinstall.progress.reloading_ext", Loc.T("autoinstall.progress.reloading_ext", prepared.Version));
                    bool reloaded = await _driver.InvokeElementAsync(window, ReloadButtonQuery, _timeouts.StepTimeout, ct).ConfigureAwait(false);
                    if (!reloaded)
                        reloaded = await _driver.InvokeElementAsync(window, UpdateNowQuery, _timeouts.StepTimeout, ct).ConfigureAwait(false);

                    if (!reloaded)
                    {
                        return FallbackToManual(
                            browser, profileDirectory, prepared, extensionsUrl, launch,
                            AutoInstallFailureReason.ReloadFailed,
                            "autoinstall.err.reload_failed",
                            Loc.T("autoinstall.err.reload_failed"),
                            progress);
                    }
                }
            }

            // Step (h): Confirm success by re-reading BrowserProfiles, then close ONLY the tab Golan opened.
            Report(progress, browser.Name, "autoinstall.progress.verifying", Loc.T("autoinstall.progress.verifying"));
            bool confirmed = await WaitForInstalledStateAsync(browser, profileDirectory, prepared, needsVersionReload, ct).ConfigureAwait(false);
            if (!confirmed)
            {
                return FallbackToManual(
                    browser, profileDirectory, prepared, extensionsUrl, launch,
                    AutoInstallFailureReason.VerificationTimeout,
                    "autoinstall.err.verify_timeout",
                    Loc.T("autoinstall.err.verify_timeout"),
                    progress);
            }

            bool closedTab = await _driver.CloseOpenedTabAsync(window, launch, ct).ConfigureAwait(false);
            var installedMsg = Loc.T("autoinstall.result.installed", browser.Name);
            Report(progress, browser.Name, "autoinstall.result.installed", installedMsg);
            return new AutoInstallResult(
                Browser: browser,
                ProfileDirectory: profileDirectory,
                Status: AutoInstallStatus.Installed,
                FailureReason: AutoInstallFailureReason.None,
                MessageKey: "autoinstall.result.installed",
                Message: installedMsg,
                ExtensionPath: prepared.ExtensionDir,
                ExtensionsUrl: extensionsUrl,
                OpenedTabClosed: closedTab,
                ClipboardCopied: false,
                FolderOpened: false,
                RequiresRestartWarning: false,
                ManualSteps: []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"auto-install ({browser.Name}) failed: {ex.Message}");
            var safePrepared = prepared ?? new PreparedExtension(
                AppPaths.ExtensionDir, "0.0.0",
                Path.Combine(AppPaths.ExtensionDir, "manifest.json"),
                BrowserIntegration.FirefoxXpiPath, false);
            return FallbackToManual(
                browser, profileDirectory, safePrepared, extensionsUrl, launch,
                firefox ? AutoInstallFailureReason.FirefoxTemporaryFailed : AutoInstallFailureReason.VerificationTimeout,
                firefox ? "autoinstall.err.firefox_fallback" : "autoinstall.err.verify_timeout",
                firefox ? Loc.T("autoinstall.err.firefox_fallback") : Loc.T("autoinstall.err.verify_timeout"),
                progress);
        }
    }

    async Task<AutoInstallResult> InstallFirefoxAsync(
        BrowserIntegration.BrowserInfo browser,
        PreparedExtension prepared,
        string extensionsUrl,
        Action<AutoInstallProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(browser.ExePath))
        {
            return FallbackToManual(
                browser, null, prepared, extensionsUrl, launch: null,
                AutoInstallFailureReason.BrowserNotFound,
                "autoinstall.err.browser_not_found",
                Loc.T("autoinstall.err.browser_not_found", browser.Name),
                progress);
        }

        // Step (i-signed): If a Mozilla-signed XPI exists at BrowserIntegration.FirefoxXpiPath, open it directly.
        if (prepared.IsFirefoxXpiSigned && !string.IsNullOrWhiteSpace(prepared.FirefoxXpiPath))
        {
            Report(progress, browser.Name, "autoinstall.progress.firefox_temp", Loc.T("autoinstall.progress.firefox_temp"));
            var xpiLaunch = _driver.LaunchBrowser(browser, null, prepared.FirefoxXpiPath, newTab: false);
            if (xpiLaunch == null)
            {
                return FallbackToManual(
                    browser, null, prepared, extensionsUrl, launch: null,
                    AutoInstallFailureReason.BrowserNotFound,
                    "autoinstall.err.browser_not_found",
                    Loc.T("autoinstall.err.browser_not_found", browser.Name),
                    progress);
            }

            var xpiWin = await _driver.WaitForWindowAsync(xpiLaunch, _timeouts.WindowTimeout, ct).ConfigureAwait(false);
            if (xpiWin == 0)
            {
                return FallbackToManual(
                    browser, null, prepared, extensionsUrl, xpiLaunch,
                    AutoInstallFailureReason.WindowTimeout,
                    "autoinstall.err.window_timeout",
                    Loc.T("autoinstall.err.window_timeout"),
                    progress);
            }

            var signedMsg = Loc.T("autoinstall.result.firefox_signed");
            Report(progress, browser.Name, "autoinstall.result.firefox_signed", signedMsg);
            return new AutoInstallResult(
                Browser: browser,
                ProfileDirectory: null,
                Status: AutoInstallStatus.Installed,
                FailureReason: AutoInstallFailureReason.None,
                MessageKey: "autoinstall.result.firefox_signed",
                Message: signedMsg,
                ExtensionPath: prepared.FirefoxXpiPath,
                ExtensionsUrl: extensionsUrl,
                OpenedTabClosed: false,
                ClipboardCopied: false,
                FolderOpened: false,
                RequiresRestartWarning: false,
                ManualSteps: []);
        }

        // Step (i-temp): Open about:debugging#/runtime/this-firefox and click "Load Temporary Add-on..." with manifest.json.
        Report(progress, browser.Name, "autoinstall.progress.launching", Loc.T("autoinstall.progress.launching", browser.Name));
        var launch = _driver.LaunchBrowser(browser, null, extensionsUrl, newTab: true);
        if (launch == null)
        {
            return FallbackToManual(
                browser, null, prepared, extensionsUrl, launch: null,
                AutoInstallFailureReason.BrowserNotFound,
                "autoinstall.err.browser_not_found",
                Loc.T("autoinstall.err.browser_not_found", browser.Name),
                progress);
        }

        Report(progress, browser.Name, "autoinstall.progress.waiting_window", Loc.T("autoinstall.progress.waiting_window", browser.Name));
        var window = await _driver.WaitForWindowAsync(launch, _timeouts.WindowTimeout, ct).ConfigureAwait(false);
        if (window == 0)
        {
            return FallbackToManual(
                browser, null, prepared, extensionsUrl, launch,
                AutoInstallFailureReason.WindowTimeout,
                "autoinstall.err.window_timeout",
                Loc.T("autoinstall.err.window_timeout"),
                progress);
        }

        var loadBtn = await _driver.FindElementAsync(window, FirefoxLoadTempAddonQuery, _timeouts.PageProbeTimeout, ct).ConfigureAwait(false);
        if (!loadBtn.Exists)
        {
            Report(progress, browser.Name, "autoinstall.progress.typing_url", Loc.T("autoinstall.progress.typing_url", extensionsUrl));
            await _driver.NavigateAddressBarAsync(window, extensionsUrl, _timeouts.StepTimeout, ct).ConfigureAwait(false);
            loadBtn = await _driver.FindElementAsync(window, FirefoxLoadTempAddonQuery, _timeouts.StepTimeout, ct).ConfigureAwait(false);
        }

        if (!loadBtn.Exists)
        {
            return FallbackToManual(
                browser, null, prepared, extensionsUrl, launch,
                AutoInstallFailureReason.FirefoxTemporaryFailed,
                "autoinstall.err.firefox_fallback",
                Loc.T("autoinstall.err.firefox_fallback"),
                progress);
        }

        Report(progress, browser.Name, "autoinstall.progress.firefox_temp", Loc.T("autoinstall.progress.firefox_temp"));
        bool invoked = await _driver.InvokeElementAsync(window, FirefoxLoadTempAddonQuery, _timeouts.StepTimeout, ct).ConfigureAwait(false);
        if (!invoked)
        {
            return FallbackToManual(
                browser, null, prepared, extensionsUrl, launch,
                AutoInstallFailureReason.FirefoxTemporaryFailed,
                "autoinstall.err.firefox_fallback",
                Loc.T("autoinstall.err.firefox_fallback"),
                progress);
        }

        bool picked = await _driver.FillPickerDialogAsync(window, prepared.FirefoxManifestPath, isFolderPicker: false, _timeouts.PickerTimeout, ct).ConfigureAwait(false);
        if (!picked)
        {
            return FallbackToManual(
                browser, null, prepared, extensionsUrl, launch,
                AutoInstallFailureReason.PickerTimeout,
                "autoinstall.err.firefox_fallback",
                Loc.T("autoinstall.err.firefox_fallback"),
                progress);
        }

        bool closed = await _driver.CloseOpenedTabAsync(window, launch, ct).ConfigureAwait(false);
        var warnMsg = Loc.T("autoinstall.result.firefox_temp_warning");
        Report(progress, browser.Name, "autoinstall.result.firefox_temp_warning", warnMsg);
        return new AutoInstallResult(
            Browser: browser,
            ProfileDirectory: null,
            Status: AutoInstallStatus.InstalledTemporary,
            FailureReason: AutoInstallFailureReason.None,
            MessageKey: "autoinstall.result.firefox_temp_warning",
            Message: warnMsg,
            ExtensionPath: prepared.FirefoxManifestPath,
            ExtensionsUrl: extensionsUrl,
            OpenedTabClosed: closed,
            ClipboardCopied: false,
            FolderOpened: false,
            RequiresRestartWarning: true,
            ManualSteps: []);
    }

    async Task<bool> WaitForInstalledStateAsync(
        BrowserIntegration.BrowserInfo browser,
        string? profileDirectory,
        PreparedExtension prepared,
        bool requireCurrentVersion,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var state = _driver.ReadExtensionState(browser, profileDirectory, BrowserIntegration.ExtensionId, prepared.ExtensionDir);
            if (state.Installed && !state.Disabled && !state.LoadedFromOtherFolder
                && (!requireCurrentVersion || IsCurrentOrNewerVersion(state.Version, prepared.Version)))
                return true;

            if (sw.Elapsed >= _timeouts.VerifyTimeout)
                break;

            var remaining = _timeouts.VerifyTimeout - sw.Elapsed;
            var delay = remaining < _timeouts.VerifyPollInterval ? remaining : _timeouts.VerifyPollInterval;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        while (sw.Elapsed < _timeouts.VerifyTimeout);

        var finalState = _driver.ReadExtensionState(browser, profileDirectory, BrowserIntegration.ExtensionId, prepared.ExtensionDir);
        return finalState.Installed && !finalState.Disabled && !finalState.LoadedFromOtherFolder
            && (!requireCurrentVersion || IsCurrentOrNewerVersion(finalState.Version, prepared.Version));
    }

    AutoInstallResult FallbackToManual(
        BrowserIntegration.BrowserInfo browser,
        string? profileDirectory,
        PreparedExtension prepared,
        string extensionsUrl,
        BrowserLaunchHandle? launch,
        AutoInstallFailureReason reason,
        string messageKey,
        string message,
        Action<AutoInstallProgress>? progress)
    {
        bool firefox = IsFirefox(browser);
        var pathToCopy = firefox ? prepared.FirefoxManifestPath : prepared.ExtensionDir;

        bool copied = _copiedPaths.Contains(pathToCopy);
        if (!copied)
        {
            try { copied = _driver.SetClipboardText(pathToCopy); }
            catch (Exception ex) { Log.Write("auto-install clipboard fallback failed: " + ex.Message); }
            if (copied) _copiedPaths.Add(pathToCopy);
        }

        bool folderOpened = _openedFolders.Contains(prepared.ExtensionDir);
        if (!folderOpened)
        {
            try { folderOpened = _driver.OpenFolder(prepared.ExtensionDir); }
            catch (Exception ex) { Log.Write("auto-install open folder fallback failed: " + ex.Message); }
            if (folderOpened) _openedFolders.Add(prepared.ExtensionDir);
        }

        if (launch == null && !string.IsNullOrWhiteSpace(browser.ExePath))
        {
            try { _driver.LaunchBrowser(browser, profileDirectory, extensionsUrl, newTab: true); }
            catch (Exception ex) { Log.Write("auto-install open page fallback failed: " + ex.Message); }
        }

        var steps = BuildManualSteps(firefox, extensionsUrl, prepared);
        Report(progress, browser.Name, messageKey, message);

        return new AutoInstallResult(
            Browser: browser,
            ProfileDirectory: profileDirectory,
            Status: AutoInstallStatus.ManualFallback,
            FailureReason: reason,
            MessageKey: messageKey,
            Message: message,
            ExtensionPath: pathToCopy,
            ExtensionsUrl: extensionsUrl,
            OpenedTabClosed: false,
            ClipboardCopied: copied,
            FolderOpened: folderOpened,
            RequiresRestartWarning: firefox,
            ManualSteps: steps);
    }

    static IReadOnlyList<string> BuildManualSteps(bool firefox, string extensionsUrl, PreparedExtension prepared)
    {
        if (firefox)
        {
            return
            [
                Loc.T("autoinstall.manual.ff_step1"),
                Loc.T("autoinstall.manual.ff_step2", prepared.FirefoxManifestPath),
            ];
        }

        return
        [
            Loc.T("autoinstall.manual.step1_open", extensionsUrl),
            Loc.T("autoinstall.manual.step2_dev"),
            Loc.T("autoinstall.manual.step3_load", prepared.ExtensionDir),
        ];
    }

    static void Report(Action<AutoInstallProgress>? progress, string browserName, string key, string message)
    {
        // The step keys in the log show where an install stopped (they hold no path or account name).
        Log.Write($"auto-install [{browserName}] {key}");
        progress?.Invoke(new AutoInstallProgress(browserName, key, message));
    }
}
