using System.Text.Json.Nodes;
using Golan.Gdm.Integration;

/// <summary>Browser guide: profile listing, developer mode and extension detection, launch arguments, connection tracking.</summary>
static class Tests_Guide
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  [GUIDE] {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  [GUIDE] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
    }

    static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString());
    }

    public static Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        Console.WriteLine("Browser guide");
        var root = Path.GetFullPath(Path.Combine(outDir, "guide-tests-" + Guid.NewGuid().ToString("N")[..8])); // browsers store absolute paths
        var userData = Path.Combine(root, "User Data");
        var extDir = Path.Combine(root, "ext");
        Directory.CreateDirectory(extDir);
        const string ourId = "fkjplbaglahdiphiodkgidkdfkbebgfh";

        // ---- profiles ----
        WriteJson(Path.Combine(userData, "Local State"), new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["last_used"] = "Profile 2",
                ["info_cache"] = new JsonObject
                {
                    ["Default"] = new JsonObject { ["name"] = "Personal", ["user_name"] = "me@example.com" },
                    ["Profile 2"] = new JsonObject { ["name"] = "Work", ["user_name"] = "" },
                    ["Profile 3"] = new JsonObject { ["name"] = "Guest-like", ["is_ephemeral"] = true },
                    ["Profile 4"] = new JsonObject { ["gaia_name"] = "عمل", ["user_name"] = "a@b.c" },
                    ["..\\evil"] = new JsonObject { ["name"] = "bad" },
                },
            },
        });
        var profiles = BrowserProfiles.ReadProfiles(userData);
        Check("profiles listed, ephemeral and invalid folder names skipped", profiles.Count == 3, string.Join(",", profiles.Select(p => p.Directory)));
        Check("last used profile comes first", profiles.Count > 0 && profiles[0].Directory == "Profile 2" && profiles[0].IsLastUsed);
        Check("label shows the signed-in account", profiles.Any(p => p.Label == "Personal (me@example.com)"));
        Check("gaia name is used when the profile has no name", profiles.Any(p => p.Directory == "Profile 4" && p.DisplayName == "عمل"));

        // ---- per-profile state ----
        var p1 = Path.Combine(userData, "Default");
        WriteJson(Path.Combine(p1, "Preferences"), new JsonObject { ["extensions"] = new JsonObject { ["ui"] = new JsonObject { ["developer_mode"] = true } } });
        WriteJson(Path.Combine(p1, "Secure Preferences"), new JsonObject
        {
            ["extensions"] = new JsonObject { ["settings"] = new JsonObject { [ourId] = new JsonObject { ["path"] = extDir, ["state"] = 1, ["location"] = 4 } } },
        });
        var s1 = BrowserProfiles.ReadState(p1, ourId, extDir);
        Check("developer mode on + extension installed and enabled", s1.DeveloperMode && s1.Installed && !s1.Disabled);

        var p2 = Path.Combine(userData, "Profile 2");
        WriteJson(Path.Combine(p2, "Preferences"), new JsonObject { ["browser"] = new JsonObject() });
        var s2 = BrowserProfiles.ReadState(p2, ourId, extDir);
        Check("missing developer_mode means off, extension missing", !s2.DeveloperMode && !s2.Installed);

        var p4 = Path.Combine(userData, "Profile 4");
        WriteJson(Path.Combine(p4, "Preferences"), new JsonObject { ["extensions"] = new JsonObject { ["ui"] = new JsonObject { ["developer_mode"] = false } } });
        WriteJson(Path.Combine(p4, "Secure Preferences"), new JsonObject
        {
            ["extensions"] = new JsonObject { ["settings"] = new JsonObject { ["someotherid000000000000000000000"] = new JsonObject { ["path"] = extDir + "\\", ["disable_reasons"] = new JsonArray(1) } } },
        });
        var s4 = BrowserProfiles.ReadState(p4, ourId, extDir);
        Check("extension recognised by its folder path even under another id, and seen as disabled", s4.Installed && s4.Disabled && !s4.DeveloperMode, s4.ToString());

        var pBad = Path.Combine(userData, "Profile 9");
        Directory.CreateDirectory(pBad);
        File.WriteAllText(Path.Combine(pBad, "Preferences"), "{ this is not json");
        var sBad = BrowserProfiles.ReadState(pBad, ourId, extDir);
        Check("corrupt Preferences do not throw", !sBad.DeveloperMode && !sBad.Installed);

        bool lockedOk;
        using (var lockStream = new FileStream(Path.Combine(p1, "Preferences"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { BrowserProfiles.ReadState(p1, ourId, extDir); lockedOk = true; }
            catch (Exception) { lockedOk = false; }
        }
        Check("a locked Preferences file is tolerated", lockedOk);

        // ---- fallbacks ----
        var noState = Path.Combine(root, "NoState");
        Directory.CreateDirectory(Path.Combine(noState, "Default"));
        File.WriteAllText(Path.Combine(noState, "Default", "Preferences"), "{}");
        Check("no Local State: falls back to the Default profile", BrowserProfiles.ReadProfiles(noState) is [{ Directory: "Default" }]);
        var opera = Path.Combine(root, "Opera Stable");
        Directory.CreateDirectory(opera);
        File.WriteAllText(Path.Combine(opera, "Preferences"), "{}");
        Check("Opera-style root profile is supported", BrowserProfiles.ReadProfiles(opera) is [{ Directory: "" }]);
        Check("missing User Data gives an empty list", BrowserProfiles.ReadProfiles(Path.Combine(root, "nothing")).Count == 0);
        Check("user data folders are known for Chrome, Edge and Brave",
            BrowserProfiles.UserDataDir("chrome.exe")!.EndsWith(@"Google\Chrome\User Data")
            && BrowserProfiles.UserDataDir("MSEDGE.EXE")!.EndsWith(@"Microsoft\Edge\User Data")
            && BrowserProfiles.UserDataDir("brave.exe")!.EndsWith(@"Brave-Browser\User Data")
            && BrowserProfiles.UserDataDir("firefox.exe") == null);

        // ---- launch arguments ----
        var args = BrowserProfiles.LaunchArguments("Profile 2", "chrome://extensions");
        Check("launch opens the extensions page in the chosen profile", args is ["--profile-directory=Profile 2", "chrome://extensions"]);
        Check("no profile → only the page", BrowserProfiles.LaunchArguments("", "edge://extensions") is ["edge://extensions"]);
        Check("opening a browser whose exe is missing fails softly",
            !BrowserProfiles.OpenExtensionsPage(new BrowserIntegration.BrowserInfo("x", "", Path.Combine(root, "missing.exe"), "chrome://extensions"), "Default"));

        // ---- connection tracking ----
        ExtensionContact.Reset();
        var t = new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);
        Check("no contact recorded yet", ExtensionContact.Last("chrome.exe") == null && ExtensionContact.Last() == null);
        ExtensionContact.Record("chrome.exe", t);
        Check("contact is recorded per browser and overall", ExtensionContact.Last("CHROME.EXE") == t && ExtensionContact.Last() == t && ExtensionContact.Last("msedge.exe") == null);
        ExtensionContact.Record("", t.AddMinutes(1));
        Check("an unknown browser still counts as a contact", ExtensionContact.Last() == t.AddMinutes(1));
        ExtensionContact.Reset();
        string detected = "?";
        try { detected = ExtensionContact.DetectLaunchingBrowser(); } catch (Exception ex) { detected = "threw " + ex.GetType().Name; }
        Check("browser detection outside a browser returns empty without throwing", detected == "", detected);

        try { Directory.Delete(root, true); } catch (IOException) { /* best-effort cleanup of test output */ }
        Console.WriteLine($"[GUIDE Summary] {_pass} passed, {_fail} failed");
        return Task.FromResult(_fail);
    }
}
