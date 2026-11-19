using System.Text.Json;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;

namespace Golan.Gdm.Integration;

/// <summary>
/// Read-only detection and first-run offer decision for the automatic browser extension installer.
/// Never launches browsers, never edits browser files or policies, and never writes to the registry.
/// </summary>
public static class ExtensionOfferPolicy
{
    public const string ChoiceInstallNow = "install";
    public const string ChoiceLater = "later";

    public readonly record struct StartupFlowPlan(
        bool ShowWelcome,
        bool CheckExtensionOffer,
        bool ClearFirstRunAfterWelcome,
        bool OfferAppInstall);

    /// <summary>
    /// Pure startup flow decision for the four combinations of <paramref name="firstRun"/> and <paramref name="startHidden"/>.
    /// Starting hidden never opens dialogs and never clears <see cref="AppSettings.FirstRun"/> before Welcome was actually shown.
    /// </summary>
    public static StartupFlowPlan DecideStartupFlow(bool firstRun, bool startHidden)
    {
        if (startHidden)
            return new StartupFlowPlan(ShowWelcome: false, CheckExtensionOffer: false, ClearFirstRunAfterWelcome: false, OfferAppInstall: false);
        if (firstRun)
            return new StartupFlowPlan(ShowWelcome: true, CheckExtensionOffer: true, ClearFirstRunAfterWelcome: true, OfferAppInstall: true);
        return new StartupFlowPlan(ShowWelcome: false, CheckExtensionOffer: false, ClearFirstRunAfterWelcome: false, OfferAppInstall: true);
    }

    /// <summary>
    /// Runs the sequential startup dialog flow without stacking dialogs and clears <see cref="AppSettings.FirstRun"/>
    /// only after the Welcome window was actually shown.
    /// </summary>
    public static async Task ExecuteStartupFlowAsync(
        AppSettings settings,
        bool startHidden,
        Action showWelcome,
        Func<AppSettings, Task<(IReadOnlyList<BrowserIntegration.BrowserInfo> Browsers, bool ShouldOffer)>> checkExtensionOfferAsync,
        Action<IReadOnlyList<BrowserIntegration.BrowserInfo>> showExtensionOffer,
        Action offerAppInstall,
        Func<bool>? isExiting = null,
        bool saveSettings = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(showWelcome);
        ArgumentNullException.ThrowIfNull(checkExtensionOfferAsync);
        ArgumentNullException.ThrowIfNull(showExtensionOffer);
        ArgumentNullException.ThrowIfNull(offerAppInstall);

        var plan = DecideStartupFlow(settings.FirstRun, startHidden);
        if (isExiting?.Invoke() == true)
            return;

        if (plan.ShowWelcome)
        {
            showWelcome();
            // Welcome's language button asks for a restart and wants Welcome again in the new language:
            // leave FirstRun as it is when the app is already on its way out.
            if (isExiting?.Invoke() == true)
                return;
            if (plan.ClearFirstRunAfterWelcome)
            {
                settings.FirstRun = false;
                if (saveSettings)
                    settings.Save();
            }
        }

        if (plan.CheckExtensionOffer && isExiting?.Invoke() != true)
        {
            var offerCheckSnapshot = settings.Clone();
            offerCheckSnapshot.FirstRun = true;
            var (browsers, shouldOffer) = await checkExtensionOfferAsync(offerCheckSnapshot).ConfigureAwait(true);
            if (shouldOffer && isExiting?.Invoke() != true)
                showExtensionOffer(browsers);
        }

        if (plan.OfferAppInstall && isExiting?.Invoke() != true)
            offerAppInstall();
    }

    /// <summary>
    /// Returns true when the first-run automatic extension install offer should be shown:
    /// only on first run, at most once, never after Later/Install, and only when at least one
    /// supported browser is detected and none of the detected browsers already has Golan's extension enabled.
    /// </summary>
    public static bool ShouldOffer(
        AppSettings settings,
        IReadOnlyList<BrowserIntegration.BrowserInfo>? detectedBrowsers = null,
        Func<BrowserIntegration.BrowserInfo, bool>? isExtensionInstalled = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.FirstRun)
            return false;
        if (settings.ExtensionOfferShown)
            return false;
        if (!string.IsNullOrWhiteSpace(settings.ExtensionOfferChoice))
            return false;

        var browsers = detectedBrowsers ?? ExtensionAutoInstaller.InstalledSupportedBrowsers();
        if (browsers.Count == 0)
            return false;

        var detector = isExtensionInstalled ?? (b => IsExtensionInstalledInBrowser(b, AppPaths.ExtensionDir));
        return !browsers.Any(detector);
    }

    /// <summary>Marks that the first-run offer dialog has been displayed so it is never offered again.</summary>
    public static void MarkShown(AppSettings settings, bool save = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ExtensionOfferShown = true;
        if (save)
            settings.Save();
    }

    /// <summary>Records the user's choice ("install" or "later") in settings and optionally persists immediately.</summary>
    public static void RecordChoice(AppSettings settings, bool installNow, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.FirstRun = false;
        settings.ExtensionOfferShown = true;
        settings.ExtensionOfferChoice = installNow ? ChoiceInstallNow : ChoiceLater;
        if (save)
            settings.Save();
    }

    /// <summary>
    /// Checks read-only whether <paramref name="browser"/> already has Golan's extension installed and enabled
    /// in any known profile (or has already connected via native messaging).
    /// </summary>
    public static bool IsExtensionInstalledInBrowser(
        BrowserIntegration.BrowserInfo browser,
        string extensionDir,
        string? userDataDirOverride = null,
        string? firefoxProfilesRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var exeName = string.IsNullOrWhiteSpace(browser.ExePath) ? "" : Path.GetFileName(browser.ExePath);
        if (exeName.Length > 0 && ExtensionContact.Last(exeName) != null)
            return true;

        if (ExtensionAutoInstaller.IsFirefox(browser))
            return IsInstalledInFirefox(firefoxProfilesRootOverride);

        var userData = userDataDirOverride ?? (exeName.Length > 0 ? BrowserProfiles.UserDataDir(browser.ExePath!) : null);
        if (string.IsNullOrWhiteSpace(userData) || !Directory.Exists(userData))
            return false;

        var profiles = BrowserProfiles.ReadProfiles(userData);
        if (profiles.Count > 0)
        {
            foreach (var p in profiles)
            {
                var profilePath = p.Directory.Length == 0 ? userData : Path.Combine(userData, p.Directory);
                var st = BrowserProfiles.ReadState(profilePath, BrowserIntegration.ExtensionId, extensionDir);
                if (st.Installed && !st.Disabled)
                    return true;
            }
            return false;
        }

        foreach (var candidate in new[] { Path.Combine(userData, "Default"), userData })
        {
            var st = BrowserProfiles.ReadState(candidate, BrowserIntegration.ExtensionId, extensionDir);
            if (st.Installed && !st.Disabled)
                return true;
        }
        return false;
    }

    static bool IsInstalledInFirefox(string? firefoxProfilesRootOverride)
    {
        try
        {
            var root = firefoxProfilesRootOverride
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return false;

            foreach (var profileDir in Directory.EnumerateDirectories(root))
            {
                var xpiFile = Path.Combine(profileDir, "extensions", BrowserIntegration.FirefoxExtensionId + ".xpi");
                if (File.Exists(xpiFile))
                    return true;

                var extJson = Path.Combine(profileDir, "extensions.json");
                if (!File.Exists(extJson))
                    continue;

                using var fs = new FileStream(extJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > 32L * 1024 * 1024)
                    continue;
                var doc = JsonNode.Parse(fs, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
                if (doc?["addons"] is not JsonArray addons)
                    continue;

                foreach (var item in addons)
                {
                    if (item is not JsonObject addon)
                        continue;
                    var id = addon["id"] is JsonValue idVal && idVal.TryGetValue<string>(out var s) ? s : "";
                    if (!string.Equals(id, BrowserIntegration.FirefoxExtensionId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool userDisabled = addon["userDisabled"] is JsonValue ud && ud.TryGetValue<bool>(out var udb) && udb;
                    bool appDisabled = addon["appDisabled"] is JsonValue ad && ad.TryGetValue<bool>(out var adb) && adb;
                    if (!userDisabled && !appDisabled)
                        return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Read-only best effort: never fail startup if Firefox files are locked or unreadable.
        }
        return false;
    }
}
