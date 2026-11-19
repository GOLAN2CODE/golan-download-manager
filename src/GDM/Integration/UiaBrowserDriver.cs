using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.Integration;

/// <summary>
/// Thin Windows UI Automation (<see cref="System.Windows.Automation"/>) implementation of <see cref="IBrowserAutomationDriver"/>.
/// All installation workflow, branching, timeouts, and manual fallback logic live in <see cref="ExtensionAutoInstaller"/>.
/// </summary>
public sealed class UiaBrowserDriver : IBrowserAutomationDriver
{
    /// <summary>Optional hook so the main UI can suppress its clipboard URL watcher before Golan copies a folder path.</summary>
    public static Action? BeforeClipboardCopy { get; set; }

    /// <summary>Extra browser arguments placed first (live tests point the browser at a throwaway user-data folder).</summary>
    public IReadOnlyList<string>? ExtraLaunchArguments { get; init; }

    static readonly string[] AddressBarIds = ["view_1012", "view_1022", "urlbar-input"];
    static readonly string[] AddressBarNames =
    [
        "Address and search bar",
        "Search or enter web address",
        "Search with Google or enter address",
        "Address field",
        "شريط العناوين والبحث",
        "البحث أو إدخال عنوان ويب",
        "شريط العنوان",
    ];

    static readonly string[] FolderConfirmNames = ["Select Folder", "اختيار مجلد", "تحديد مجلد", "Choose", "اختيار"];
    static readonly string[] FileConfirmNames = ["Open", "فتح"];
    static readonly string[] TabTitleTokens =
    [
        "Extensions", "الإضافات", "الملحقات",
        "Debugging", "this-firefox", "تصحيح",
        "chrome://extensions", "edge://extensions", "brave://extensions",
        "opera://extensions", "vivaldi://extensions", "browser://extensions", "about:debugging",
    ];
    static readonly string[] CloseTabButtonNames = ["Close", "Close tab", "إغلاق", "إغلاق علامة التبويب"];

    public PreparedExtension PrepareExtension()
    {
        BrowserIntegration.ExtractExtension();
        BrowserIntegration.Register();
        RegisterExtraChromiumNativeHostsIfPresent();

        var extDir = AppPaths.ExtensionDir;
        var manifestPath = Path.Combine(extDir, "manifest.json");
        var version = ReadManifestVersion(manifestPath)
            ?? typeof(BrowserIntegration).Assembly.GetName().Version?.ToString(3)
            ?? "2.6.2";
        var xpiPath = BrowserIntegration.FirefoxXpiPath;
        bool signed = IsSignedMozillaXpi(xpiPath);

        // The folder's manifest is the Chromium one (no Firefox id, no background.scripts), so Firefox is given its
        // own package: "Load Temporary Add-on" takes an .xpi as well as a manifest.
        var firefoxLoadPath = File.Exists(xpiPath) ? xpiPath : manifestPath;
        return new PreparedExtension(extDir, version, firefoxLoadPath, xpiPath, signed);
    }

    static void RegisterExtraChromiumNativeHostsIfPresent()
    {
        if (AppPaths.IsIsolated) return;
        foreach (var root in new[] { @"Software\Yandex\YandexBrowser", @"Software\Perplexity\Comet" })
        {
            try
            {
                using var existing = Registry.CurrentUser.OpenSubKey(root);
                if (existing == null) continue;
                using var hostKey = Registry.CurrentUser.CreateSubKey(root + @"\NativeMessagingHosts\" + BrowserIntegration.HostName);
                hostKey.SetValue(null, AppPaths.NativeManifest);
            }
            catch (Exception ex) { Log.Write("extra host registration skipped: " + ex.Message); }
        }
    }

    static string? ReadManifestVersion(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var obj = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) as JsonObject;
            return obj?["version"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
                ? s.Trim()
                : null;
        }
        catch { return null; }
    }

    public static bool IsSignedMozillaXpi(string xpiPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(xpiPath) || !File.Exists(xpiPath)) return false;
            using var zip = ZipFile.OpenRead(xpiPath);
            return zip.Entries.Any(e =>
                string.Equals(e.FullName, "META-INF/mozilla.rsa", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.FullName, "META-INF/cose.sig", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public DetailedExtensionState ReadExtensionState(
        BrowserIntegration.BrowserInfo browser,
        string? profileDirectory,
        string extensionId,
        string extensionDir)
    {
        var profilePath = BrowserProfiles.ResolveProfilePath(browser, profileDirectory);
        if (string.IsNullOrWhiteSpace(profilePath))
            return new DetailedExtensionState(false, false, false, null);
        return BrowserProfiles.ReadDetailedState(profilePath, extensionId, extensionDir);
    }

    public BrowserLaunchHandle? LaunchBrowser(
        BrowserIntegration.BrowserInfo browser,
        string? profileDirectory,
        string url,
        bool newTab)
    {
        if (browser.ExePath is not { } exe || !File.Exists(exe)) return null;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            foreach (var arg in ExtraLaunchArguments ?? [])
                psi.ArgumentList.Add(arg);
            foreach (var arg in ExtensionAutoInstaller.BuildLaunchArguments(browser, profileDirectory, url, newTab))
                psi.ArgumentList.Add(arg);

            // Chromium opens the page in a new window, so every window that exists now is not the one to work in.
            var before = ExtensionAutoInstaller.IsFirefox(browser) ? null : SnapshotBrowserWindows();
            using var proc = Process.Start(psi);
            return new BrowserLaunchHandle(exe, proc?.Id, url, newTab) { WindowsBeforeLaunch = before };
        }
        catch (Exception ex)
        {
            Log.Write("auto-install launch browser failed: " + ex.Message);
            return null;
        }
    }

    public async Task<nint> WaitForWindowAsync(BrowserLaunchHandle launch, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            // A browser that is already running hands the new tab to one of its windows a moment after the launch.
            // Until then only a window that is clearly ours counts; the plain "front-most window" guess waits.
            var hwnd = FindBrowserWindow(launch.ExePath, launch.ProcessId, launch.WindowsBeforeLaunch,
                allowFrontMostGuess: launch.WindowsBeforeLaunch != null || sw.Elapsed >= FrontMostGuessDelay);
            if (hwnd != 0)
            {
                BringToFront(hwnd);
                return hwnd;
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return 0;
    }

    public async Task<bool> NavigateAddressBarAsync(nint window, string url, TimeSpan timeout, CancellationToken ct)
    {
        if (window == 0) return false;
        var sw = Stopwatch.StartNew();
        bool sentFocusShortcut = false;
        GetWindowThreadProcessId(window, out uint windowPid);
        // Raised once: raising it again on every poll would keep pulling focus away from whatever the user moved to.
        BringToFront(window);

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = AutomationElement.FromHandle(window);
                var edit = FindAddressBar(root);
                // Keys go to whichever window is in front, so the shortcut is sent only while that is still the browser.
                if (edit == null && !sentFocusShortcut && IsForeground(window))
                {
                    sentFocusShortcut = SendChord(VK_CONTROL, VK_L);
                    await Task.Delay(150, ct).ConfigureAwait(false);
                    edit = FocusedEditOf(windowPid) ?? FindAddressBar(root);
                }

                if (edit != null)
                {
                    // The tab in front may be one the user is reading (the new tab can open in another window):
                    // only an empty bar or our own page is replaced, anything else goes to the manual guide.
                    if (!IsReplaceableAddress(ReadEditValue(edit), url)) return false;
                    try { edit.SetFocus(); } catch { /* best-effort focus; the address bar may refuse it */ }
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
                    {
                        vp.SetValue(url);
                        await Task.Delay(80, ct).ConfigureAwait(false);
                        if (!EnsureForeground(window)) return false;
                        SendKey(VK_RETURN);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                // Transient UIA tree update while browser renders.
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<UiElementState> FindElementAsync(nint window, UiElementQuery query, TimeSpan timeout, CancellationToken ct)
    {
        if (window == 0) return new UiElementState(false, false, null);
        bool checkDevPolicy = string.Equals(query.AutomationId, "devMode", StringComparison.Ordinal);
        var sw = Stopwatch.StartNew();

        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = AutomationElement.FromHandle(window);
                var el = LocateElement(root, query);
                if (el == null && RequestWebAccessibility(window))
                    await Task.Delay(350, ct).ConfigureAwait(false);
                if (el != null)
                {
                    bool enabled = el.Current.IsEnabled;
                    bool? toggled = ReadToggleState(el);
                    bool policyBlocked = checkDevPolicy && toggled != true && (!enabled || IsDevModeBlockedInRegistry(window));
                    return new UiElementState(true, enabled, toggled, policyBlocked);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { /* element went away while polling; try again next tick */ }

            if (sw.Elapsed >= timeout) break;
            await Task.Delay(180, ct).ConfigureAwait(false);
        }
        while (sw.Elapsed < timeout);

        return new UiElementState(false, false, null);
    }

    public async Task<bool> SetToggleAsync(nint window, UiElementQuery query, bool turnOn, TimeSpan timeout, CancellationToken ct)
    {
        if (window == 0) return false;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = AutomationElement.FromHandle(window);
                var el = LocateElement(root, query);
                if (el != null)
                {
                    if (!el.Current.IsEnabled) return false;
                    var current = ReadToggleState(el);
                    if (current == turnOn) return true;

                    if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tpObj) && tpObj is TogglePattern tp)
                        tp.Toggle();
                    else if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ipObj) && ipObj is InvokePattern ip)
                        ip.Invoke();
                    else
                        return false;

                    await Task.Delay(250, ct).ConfigureAwait(false);
                    var after = LocateElement(root, query);
                    if (after != null && ReadToggleState(after) == turnOn)
                        return true;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { /* element went away while polling; try again next tick */ }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<bool> InvokeElementAsync(nint window, UiElementQuery query, TimeSpan timeout, CancellationToken ct)
    {
        if (window == 0) return false;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = AutomationElement.FromHandle(window);
                var el = LocateElement(root, query);
                if (el != null && el.Current.IsEnabled)
                {
                    if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ipObj) && ipObj is InvokePattern ip)
                    {
                        // A file dialog the user already has open (a "Save as" in another window of this browser)
                        // must never be taken for the one this click is about to open.
                        _dialogsBeforeInvoke = SnapshotFileDialogs();
                        ip.Invoke();
                        return true;
                    }
                    if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tpObj) && tpObj is TogglePattern tp)
                    {
                        _dialogsBeforeInvoke = SnapshotFileDialogs();
                        tp.Toggle();
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { /* element went away while polling; try again next tick */ }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<bool> FillPickerDialogAsync(nint ownerWindow, string targetPath, bool isFolderPicker, TimeSpan timeout, CancellationToken ct)
    {
        if (ownerWindow == 0) return false;
        GetWindowThreadProcessId(ownerWindow, out uint ownerPid);
        var sw = Stopwatch.StartNew();
        nint raised = 0;

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var dlgHwnd = FindFileDialogWindow(ownerWindow, ownerPid, _dialogsBeforeInvoke);
            if (dlgHwnd != 0)
            {
                try
                {
                    if (raised != dlgHwnd)
                    {
                        raised = dlgHwnd;
                        BringToFront(dlgHwnd);
                    }
                    var dlg = AutomationElement.FromHandle(dlgHwnd);
                    if (TryFillDialogPath(dlg, targetPath))
                    {
                        await Task.Delay(120, ct).ConfigureAwait(false);
                        if (TryConfirmDialog(dlg, isFolderPicker))
                            return true;
                    }
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { /* element went away while polling; try again next tick */ }
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    public Task<bool> CloseOpenedTabAsync(nint window, BrowserLaunchHandle launch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (window == 0 || !launch.WasNewTab) return Task.FromResult(false);

        try
        {
            var root = AutomationElement.FromHandle(window);
            // A title match alone could hit a tab of the user's ("Extensions - Web Store"), so the tab must also be the
            // one in front and the page in it must really be the extensions page Golan worked on. Otherwise it stays open.
            if (LocateElement(root, ExtensionAutoInstaller.DevModeQuery) == null
                && LocateElement(root, ExtensionAutoInstaller.FirefoxLoadTempAddonQuery) == null)
                return Task.FromResult(false);

            var tabItems = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            AutomationElement? targetTab = null;

            foreach (AutomationElement tab in tabItems)
            {
                if (!IsSelectedTab(tab)) continue;
                var name = SafeName(tab);
                if (TabTitleTokens.Any(tok => name.Contains(tok, StringComparison.OrdinalIgnoreCase)))
                    targetTab = tab;
                break;
            }

            if (targetTab == null) return Task.FromResult(false);

            // Preferred: invoke the Close button nested inside the specific extensions TabItem (never affects other tabs).
            var buttons = targetTab.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement btn in buttons)
            {
                var btnName = SafeName(btn);
                var btnId = SafeAutomationId(btn);
                if ((CloseTabButtonNames.Any(n => btnName.Contains(n, StringComparison.OrdinalIgnoreCase))
                     || string.Equals(btnId, "Close", StringComparison.OrdinalIgnoreCase))
                    && btn.TryGetCurrentPattern(InvokePattern.Pattern, out var ipObj) && ipObj is InvokePattern ip)
                {
                    ip.Invoke();
                    return Task.FromResult(true);
                }
            }

            if (targetTab.TryGetCurrentPattern(WindowPattern.Pattern, out var wpObj) && wpObj is WindowPattern wp)
            {
                wp.Close();
                return Task.FromResult(true);
            }

            // Fallback: Ctrl+W closes the tab in front, which is the verified one, but only if the browser really took the keyboard.
            if (EnsureForeground(window) && SendChord(VK_CONTROL, VK_W))
                return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            Log.Write("close opened tab skipped: " + ex.Message);
        }

        return Task.FromResult(false);
    }

    public bool SetClipboardText(string text)
    {
        try
        {
            BeforeClipboardCopy?.Invoke();
            if (Application.Current?.Dispatcher is { } disp)
            {
                if (disp.CheckAccess()) Clipboard.SetText(text);
                else disp.Invoke(() => Clipboard.SetText(text));
                return true;
            }

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Clipboard.SetText(text);
                return true;
            }

            Exception? err = null;
            var sta = new Thread(() =>
            {
                try { Clipboard.SetText(text); }
                catch (Exception ex) { err = ex; }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join(3000);
            return err == null;
        }
        catch (Exception ex)
        {
            Log.Write("driver clipboard failed: " + ex.Message);
            return false;
        }
    }

    public bool OpenFolder(string folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return false;
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(folderPath);
            using var proc = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("driver open folder failed: " + ex.Message);
            return false;
        }
    }

    static AutomationElement? LocateElement(AutomationElement root, UiElementQuery query)
    {
        AutomationElement searchRoot = root;
        if (!string.IsNullOrWhiteSpace(query.ScopeAutomationId))
        {
            var scopeEl = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, query.ScopeAutomationId));
            // The scope is one extension's card. Searching the whole page instead would find the same control on
            // another extension's card and switch on or reload that one.
            if (scopeEl == null) return null;
            searchRoot = scopeEl;
        }

        if (!string.IsNullOrWhiteSpace(query.AutomationId))
        {
            var byId = searchRoot.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, query.AutomationId));
            if (byId != null) return byId;
        }

        foreach (var name in query.Names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var byName = searchRoot.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name));
            if (byName != null) return byName;
        }
        return null;
    }

    static bool? ReadToggleState(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tpObj) && tpObj is TogglePattern tp)
        {
            return tp.Current.ToggleState switch
            {
                ToggleState.On => true,
                ToggleState.Off => false,
                _ => null,
            };
        }
        return null;
    }

    static AutomationElement? FindAddressBar(AutomationElement root)
    {
        foreach (var id in AddressBarIds)
        {
            var el = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.AutomationIdProperty, id)));
            if (el != null) return el;
        }

        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement edit in edits)
        {
            var name = SafeName(edit);
            if (AddressBarNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                return edit;
        }
        return null;
    }

    static bool TryFillDialogPath(AutomationElement dlg, string targetPath)
    {
        foreach (var editId in new[] { "1152", "1148" })
        {
            var byId = dlg.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.AutomationIdProperty, editId)));
            if (byId != null && SetEditValue(byId, targetPath))
                return true;
        }

        var edits = dlg.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        AutomationElement? candidate = null;
        foreach (AutomationElement e in edits)
        {
            if (!e.Current.IsEnabled || e.Current.IsOffscreen) continue;
            var n = SafeName(e);
            // The dialog's search box is named after the folder it searches ("Search New folder"): never the path box.
            if (n.Contains("Search", StringComparison.OrdinalIgnoreCase) || n.Contains("بحث", StringComparison.Ordinal)) continue;
            if (n.Contains("Folder", StringComparison.OrdinalIgnoreCase)
                || n.Contains("File name", StringComparison.OrdinalIgnoreCase)
                || n.Contains("المجلد", StringComparison.Ordinal)
                || n.Contains("اسم الملف", StringComparison.Ordinal))
            {
                candidate = e;
                break;
            }
        }
        return candidate != null && SetEditValue(candidate, targetPath);
    }

    static bool SetEditValue(AutomationElement edit, string text)
    {
        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp && !vp.Current.IsReadOnly)
        {
            vp.SetValue(text);
            return true;
        }
        var hwnd = (nint)edit.Current.NativeWindowHandle;
        if (hwnd != 0)
        {
            SendMessageChar(hwnd, WM_SETTEXT, 0, text);
            return true;
        }
        return false;
    }

    static bool TryConfirmDialog(AutomationElement dlg, bool isFolderPicker)
    {
        var names = isFolderPicker ? FolderConfirmNames : FileConfirmNames;
        foreach (var name in names)
        {
            var btn = dlg.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)));
            if (btn != null && btn.Current.IsEnabled && btn.TryGetCurrentPattern(InvokePattern.Pattern, out var ipObj) && ipObj is InvokePattern ip)
            {
                ip.Invoke();
                return true;
            }
        }

        var okBtn = dlg.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1")));
        if (okBtn != null && okBtn.Current.IsEnabled && okBtn.TryGetCurrentPattern(InvokePattern.Pattern, out var okIp) && okIp is InvokePattern inv)
        {
            inv.Invoke();
            return true;
        }

        // No blind Enter: it would go to whatever has the keyboard. The caller retries, then falls back to the guide.
        return false;
    }

    /// <summary>Reads (never writes) the policy of the browser that owns <paramref name="window"/>: another vendor's policy says nothing about it.</summary>
    static bool IsDevModeBlockedInRegistry(nint window)
    {
        if (PolicyKeyOf(window) is not { } sub) return false;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(sub);
                if (key?.GetValue("ExtensionDeveloperModeSettings") is int v && v == 1) return true;
                if (key?.GetValue("DeveloperToolsAvailability") is int d && d == 2) return true;
            }
            catch { /* policy key unreadable; treat it as not set */ }
        }
        return false;
    }

    static string? PolicyKeyOf(nint window)
    {
        try
        {
            GetWindowThreadProcessId(window, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.ToLowerInvariant() switch
            {
                "chrome" => @"Software\Policies\Google\Chrome",
                "msedge" => @"Software\Policies\Microsoft\Edge",
                "brave" => @"Software\Policies\BraveSoftware\Brave",
                "chromium" => @"Software\Policies\Chromium",
                "browser" => @"Software\Policies\YandexBrowser",
                _ => null,
            };
        }
        catch { /* window closed under us */ return null; }
    }

    static bool IsSelectedTab(AutomationElement tab)
    {
        try
        {
            return tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var spObj)
                && spObj is SelectionItemPattern sp
                && sp.Current.IsSelected;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { /* tab went away */ return false; }
    }

    static string ReadEditValue(AutomationElement edit)
    {
        try
        {
            return edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp
                ? vp.Current.Value ?? ""
                : "";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { /* bar went away */ return ""; }
    }

    /// <summary>True when the address bar is empty, shows a blank/new tab, or already shows the page we want.</summary>
    internal static bool IsReplaceableAddress(string current, string target)
    {
        var cur = (current ?? "").Trim().TrimEnd('/');
        if (cur.Length == 0) return true;
        if (cur.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)
            || cur.StartsWith("about:newtab", StringComparison.OrdinalIgnoreCase)
            || cur.StartsWith("about:home", StringComparison.OrdinalIgnoreCase)
            || cur.Contains("://newtab", StringComparison.OrdinalIgnoreCase)
            || cur.Contains("://new-tab-page", StringComparison.OrdinalIgnoreCase))
            return true;

        var want = (target ?? "").Trim();
        int cut = want.IndexOfAny(['#', '?']);
        if (cut >= 0) want = want[..cut];
        want = want.TrimEnd('/');
        return want.Length > 0 && cur.StartsWith(want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The focused edit box, but only when it belongs to the browser we are driving (never another program's text box).</summary>
    static AutomationElement? FocusedEditOf(uint browserPid)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            return focused != null
                && focused.Current.ProcessId == (int)browserPid
                && focused.Current.ControlType == ControlType.Edit
                ? focused
                : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { /* focus moved */ return null; }
    }

    static bool IsForeground(nint hwnd)
    {
        var fg = GetForegroundWindow();
        return fg != 0 && (fg == hwnd || GetAncestor(fg, GA_ROOTOWNER) == hwnd);
    }

    static bool EnsureForeground(nint hwnd)
    {
        if (IsForeground(hwnd)) return true;
        BringToFront(hwnd);
        return IsForeground(hwnd);
    }

    static string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch { return ""; }
    }

    static string SafeAutomationId(AutomationElement el)
    {
        try { return el.Current.AutomationId ?? ""; }
        catch { return ""; }
    }

    static readonly TimeSpan FrontMostGuessDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Every Chromium-family top-level window on screen right now (taken just before a launch). Hidden ones are left
    /// out: a browser kept warm in the background shows such a window as the new one, and it is then ours.
    /// </summary>
    static HashSet<nint> SnapshotBrowserWindows()
    {
        var set = new HashSet<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowClass(hwnd) is "Chrome_WidgetWin_1" or "OperaWindowClass") set.Add(hwnd);
            return true;
        }, 0);
        return set;
    }

    HashSet<nint>? _dialogsBeforeInvoke;

    /// <summary>Every common file dialog on screen right now (taken just before the click that opens ours).</summary>
    static HashSet<nint> SnapshotFileDialogs()
    {
        var set = new HashSet<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowClass(hwnd) == "#32770") set.Add(hwnd);
            return true;
        }, 0);
        return set;
    }

    /// <summary>
    /// The window Golan's page opened in. Several browsers share one process name (Chrome, Chrome Beta, bundled
    /// Chromium copies), so a window counts only when its process runs the very executable that was launched, and
    /// never when it already existed before the launch. Order: the launched process's own window, then a window
    /// already showing the extensions page, then the front-most remaining window of that browser.
    /// </summary>
    static nint FindBrowserWindow(string exePath, int? preferredPid, IReadOnlyCollection<nint>? windowsBeforeLaunch, bool allowFrontMostGuess)
    {
        nint titled = 0, ownWithPage = 0, fallback = 0;
        var exeOfPid = new Dictionary<uint, string?>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var cls = GetWindowClass(hwnd);
            if (cls is not ("Chrome_WidgetWin_1" or "MozillaWindowClass" or "OperaWindowClass")) return true;
            if (GetWindowTitle(hwnd).Length == 0) return true;
            if (windowsBeforeLaunch != null && windowsBeforeLaunch.Contains(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            bool own = preferredPid.HasValue && pid == (uint)preferredPid.Value;
            if (!own)
            {
                if (!exeOfPid.TryGetValue(pid, out var path))
                {
                    path = ProcessImagePath(pid);
                    exeOfPid[pid] = path;
                }
                if (!string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // EnumWindows goes front to back, so the first match of each kind is the front-most one.
            if (titled == 0)
            {
                var title = GetWindowTitle(hwnd);
                if (TabTitleTokens.Any(tok => title.Contains(tok, StringComparison.OrdinalIgnoreCase)))
                    titled = hwnd;
            }
            // A browser process also owns small windows (bubbles, prompts): only a window that hosts a page is a
            // browser window Golan can work in.
            bool hostsPage = cls != "Chrome_WidgetWin_1" || HasPageWindow(hwnd);
            if (own && hostsPage && ownWithPage == 0) ownWithPage = hwnd;
            if (hostsPage && fallback == 0) fallback = hwnd;
            return true;
        }, 0);
        // While the launched process is alive it is the browser itself (nothing was running to hand the tab to),
        // so only its own window counts: a window of another running copy must never be driven by mistake.
        if (ownWithPage != 0) return ownWithPage;
        if (preferredPid.HasValue && IsProcessAlive(preferredPid.Value)) return 0;
        if (titled != 0) return titled;
        return allowFrontMostGuess ? fallback : 0;
    }

    static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone (or not ours to query): the tab was handed to a browser that was already running.
            return false;
        }
    }

    static bool HasPageWindow(nint window)
    {
        bool found = false;
        EnumChildWindows(window, (h, _) =>
        {
            if (GetWindowClass(h) != "Chrome_RenderWidgetHostHWND") return true;
            found = true;
            return false;
        }, 0);
        return found;
    }

    static string? ProcessImagePath(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process ended, or runs elevated: it is not a window Golan may drive.
            return null;
        }
    }

    static nint FindFileDialogWindow(nint ownerWindow, uint ownerPid, IReadOnlyCollection<nint>? openedBefore = null)
    {
        nint owned = 0, sameProcess = 0;
        int sameProcessCount = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindowClass(hwnd) != "#32770") return true;
            if (openedBefore != null && openedBefore.Contains(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) == ownerWindow)
            {
                owned = hwnd;
                return false;
            }
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (ownerPid != 0 && pid == ownerPid)
            {
                sameProcessCount++;
                sameProcess = hwnd;
            }
            return true;
        }, 0);
        // Without a clear owner, a dialog is used only when it is the browser's single open one: with two open, one of
        // them belongs to something else the user is doing, and a path must not be typed into it.
        return owned != 0 ? owned : sameProcessCount == 1 ? sameProcess : 0;
    }

    static readonly Dictionary<nint, long> WebAccessibilityAsked = [];

    /// <summary>
    /// Chromium keeps the page (the extensions page included) out of the accessibility tree until an accessibility
    /// client shows up, so without this step no button on the page can ever be found. It is told the same way a
    /// screen reader does it: answer the page window's "honeypot" object, read a name, and ask for IAccessible2.
    /// Asked at most once a second per window; returns true when a request was just sent.
    /// </summary>
    static bool RequestWebAccessibility(nint window)
    {
        long now = Environment.TickCount64;
        lock (WebAccessibilityAsked)
        {
            if (WebAccessibilityAsked.TryGetValue(window, out var last) && now - last < 1000) return false;
        }

        var pages = new List<nint>();
        EnumChildWindows(window, (h, _) =>
        {
            if (GetWindowClass(h) == "Chrome_RenderWidgetHostHWND") pages.Add(h);
            return true;
        }, 0);
        // A window still starting up has no page yet: nothing was asked, so the next poll may ask at once.
        if (pages.Count == 0) return false;
        lock (WebAccessibilityAsked)
        {
            if (WebAccessibilityAsked.Count > 64) WebAccessibilityAsked.Clear();
            WebAccessibilityAsked[window] = now;
        }

        bool asked = false;
        foreach (var page in pages)
        {
            try
            {
                _ = SendMessageTimeout(page, WM_GETOBJECT, 0, ScreenReaderHoneyPotId, SMTO_ABORTIFHUNG, 500, out _);
                var iid = IID_IAccessible;
                if (AccessibleObjectFromWindow(page, OBJID_CLIENT, ref iid, out var obj) != 0 || obj == null) continue;
                asked = true;
                if (obj is Accessibility.IAccessible acc)
                    _ = acc.get_accName(0);
                if (obj is IOleServiceProvider sp)
                {
                    var service = IID_IAccessible;
                    var ia2 = IID_IAccessible2;
                    if (sp.QueryService(ref service, ref ia2, out var ptr) == 0 && ptr != 0) Marshal.Release(ptr);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or NotImplementedException)
            {
                // The page went away or refused the call: the caller's polling tries again.
            }
        }
        return asked;
    }

    static void BringToFront(nint hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    static string GetWindowClass(nint hwnd)
    {
        var sb = new StringBuilder(128);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    static string GetWindowTitle(nint hwnd)
    {
        var sb = new StringBuilder(256);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    static void SendKey(ushort vk)
    {
        Span<INPUT> inputs =
        [
            MakeKeyInput(vk, keyUp: false),
            MakeKeyInput(vk, keyUp: true),
        ];
        _ = SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Marshal.SizeOf<INPUT>());
    }

    /// <summary>False (and nothing sent) while the user holds Shift, Alt or a Windows key: with one of them down the
    /// shortcut would turn into another one (Ctrl+Shift+W closes the whole window).</summary>
    static bool SendChord(ushort modifierVk, ushort keyVk)
    {
        foreach (var held in new[] { 0x10 /* Shift */, 0x12 /* Alt */, 0x5B /* LWin */, 0x5C /* RWin */ })
            if ((GetAsyncKeyState(held) & 0x8000) != 0) return false;
        // Strictly keyboard-only (never VK_LWIN / VK_RWIN, never mouse).
        Span<INPUT> inputs =
        [
            MakeKeyInput(modifierVk, keyUp: false),
            MakeKeyInput(keyVk, keyUp: false),
            MakeKeyInput(keyVk, keyUp: true),
            MakeKeyInput(modifierVk, keyUp: true),
        ];
        return SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    static INPUT MakeKeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
            }
        }
    };

    const int SW_RESTORE = 9;
    const uint GW_OWNER = 4;
    const uint GA_ROOTOWNER = 3;
    const uint WM_SETTEXT = 0x000C;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const ushort VK_RETURN = 0x0D;
    const ushort VK_CONTROL = 0x11;
    const ushort VK_L = 0x4C;
    const ushort VK_W = 0x57;

    delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    const uint WM_GETOBJECT = 0x003D;
    const uint OBJID_CLIENT = 0xFFFFFFFC;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const nint ScreenReaderHoneyPotId = 1;
    static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    static readonly Guid IID_IAccessible2 = new("E89F726E-C4F4-4C19-BB19-B647D7FA8478");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out nint ppvObject);
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(nint hwnd, uint objId, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hWnd, uint gaFlags);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hWnd, uint uCmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    static extern nint SendMessageChar(nint hWnd, uint msg, nint wParam, string lParam);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        // MOUSEINPUT is required in the union layout so Marshal.SizeOf<INPUT>() == 40 on x64 Windows; mouse events are never sent.
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }
}
