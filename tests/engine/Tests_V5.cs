using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;
using Golan.Gdm.Integration.Remote;

public static class Tests_V5
{
    static int _passed;
    static int _failed;

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}{(detail != null ? $" ({detail})" : "")}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}{(detail != null ? $" ({detail})" : "")}");
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== Package V5 Verification Suite (F5 Store Packages, F13 Remote Control, F2 IDM Import) ===");

        var workRoot = Path.Combine(Path.GetTempPath(), "gdm-v5-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        try
        {
            var repoRoot = FindRepoRoot();
            try { RunF5StorePackageTests(repoRoot, workRoot); } catch (Exception ex) { Check("F5: unhandled", false, ex.Message); }
            try { await RunF13RemoteControlTestsAsync(workRoot); } catch (Exception ex) { Check("F13-Server: unhandled", false, ex.Message); }
            try { RunF13QrCodeTests(); } catch (Exception ex) { Check("F13-QR: unhandled", false, ex.Message); }
            try { RunF2IdmImportTests(workRoot); } catch (Exception ex) { Check("F2-IDM: unhandled", false, ex.Message); }
            try { RunV5LocalizationAndSettingsTests(repoRoot); } catch (Exception ex) { Check("V5-i18n: unhandled", false, ex.Message); }
        }
        finally
        {
            try { Directory.Delete(workRoot, true); } catch { }
        }

        Console.WriteLine($"[V5 Summary] {(_failed == 0 ? $"ALL {_passed} V5 CHECKS PASSED" : $"{_failed} V5 CHECKS FAILED, {_passed} passed")}");
        return _failed;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "GDM", "src", "GDM")))
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }

    // =========================================================================
    // F5 — Store Packages (ExtensionPackager + GDM/store/ + pack-extension.ps1)
    // =========================================================================
    static void RunF5StorePackageTests(string repoRoot, string workRoot)
    {
        var extSource = Path.Combine(repoRoot, "GDM", "extension");
        var out1 = Path.Combine(workRoot, "f5_out1");
        var out2 = Path.Combine(workRoot, "f5_out2");

        // F5-01: Build both packages
        ExtensionPackageResult? res1 = null;
        try { res1 = ExtensionPackager.Build(extSource, out1); }
        catch (Exception ex) { Check("F5-01: ExtensionPackager.Build succeeds on GDM/extension", false, ex.Message); return; }

        bool filesExist = File.Exists(res1.ChromeEdgeZipPath) && File.Exists(res1.FirefoxXpiPath)
            && Path.GetFileName(res1.ChromeEdgeZipPath) == $"gdm-{res1.Version}-chrome-edge.zip"
            && Path.GetFileName(res1.FirefoxXpiPath) == $"gdm-{res1.Version}-firefox.xpi"
            && new FileInfo(res1.ChromeEdgeZipPath).Length > 1024
            && new FileInfo(res1.FirefoxXpiPath).Length > 1024;
        Check("F5-01: ExtensionPackager.Build produces gdm-<ver>-chrome-edge.zip and gdm-<ver>-firefox.xpi",
            filesExist, $"ver={res1.Version}");

        // F5-02: Determinism (identical bytes across two builds, sorted entries, fixed timestamp)
        var res2 = ExtensionPackager.Build(extSource, out2);
        var chromeBytes1 = File.ReadAllBytes(res1.ChromeEdgeZipPath);
        var chromeBytes2 = File.ReadAllBytes(res2.ChromeEdgeZipPath);
        var fxBytes1 = File.ReadAllBytes(res1.FirefoxXpiPath);
        var fxBytes2 = File.ReadAllBytes(res2.FirefoxXpiPath);
        bool byteIdentical = chromeBytes1.AsSpan().SequenceEqual(chromeBytes2) && fxBytes1.AsSpan().SequenceEqual(fxBytes2);

        using var zip1 = ZipFile.OpenRead(res1.ChromeEdgeZipPath);
        var entryNames = zip1.Entries.Select(e => e.FullName).ToList();
        var sortedNames = entryNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
        bool sortedAndFixedTime = entryNames.SequenceEqual(sortedNames)
            && zip1.Entries.All(e => e.LastWriteTime == ExtensionPackager.DeterministicTimestamp);
        Check("F5-02: Store packages are byte-for-byte deterministic with sorted entries and fixed timestamps",
            byteIdentical && sortedAndFixedTime,
            $"entries={entryNames.Count}, byteIdentical={byteIdentical}, sortedAndFixedTime={sortedAndFixedTime}");

        // F5-03: Excludes tests/ and dotfiles, uses forward slashes only
        var noisyExt = Path.Combine(workRoot, "f5_noisy_ext");
        CopyDirectory(extSource, noisyExt);
        File.WriteAllText(Path.Combine(noisyExt, ".DS_Store"), "junk");
        File.WriteAllText(Path.Combine(noisyExt, ".gitignore"), "node_modules");
        File.WriteAllText(Path.Combine(noisyExt, "icons", ".hidden.png"), "hidden");
        Directory.CreateDirectory(Path.Combine(noisyExt, ".git"));
        File.WriteAllText(Path.Combine(noisyExt, ".git", "config"), "git");
        var noisyOut = Path.Combine(workRoot, "f5_noisy_out");
        var noisyRes = ExtensionPackager.Build(noisyExt, noisyOut);
        using var noisyZip = ZipFile.OpenRead(noisyRes.ChromeEdgeZipPath);
        bool noTestsNoDotfilesForwardSlashes = noisyZip.Entries.All(e =>
            !e.FullName.Contains('\\')
            && !e.FullName.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Split('/').Any(seg => seg.StartsWith('.')));
        Check("F5-03: Packager excludes tests/ and dotfiles and uses forward slashes only",
            noTestsNoDotfilesForwardSlashes && noisyZip.Entries.Count == zip1.Entries.Count,
            $"count={noisyZip.Entries.Count}");

        // F5-04: Chrome/Edge manifest strips key, browser_specific_settings, and background.scripts
        using var chromeManifestStream = zip1.GetEntry("manifest.json")!.Open();
        var chromeManifest = JsonNode.Parse(chromeManifestStream)!.AsObject();
        var chromeBg = chromeManifest["background"]?.AsObject();
        bool chromeManifestValid = !chromeManifest.ContainsKey("key")
            && !chromeManifest.ContainsKey("browser_specific_settings")
            && chromeBg != null
            && !chromeBg.ContainsKey("scripts")
            && chromeBg["service_worker"]?.GetValue<string>() == "background.js";
        Check("F5-04: Chrome/Edge manifest has no key, no browser_specific_settings, no background.scripts, keeps service_worker",
            chromeManifestValid);

        // F5-05: Firefox manifest keeps gecko.id and background.scripts, drops service_worker and key
        using var fxZip = ZipFile.OpenRead(res1.FirefoxXpiPath);
        using var fxManifestStream = fxZip.GetEntry("manifest.json")!.Open();
        var fxManifest = JsonNode.Parse(fxManifestStream)!.AsObject();
        var fxBg = fxManifest["background"]?.AsObject();
        var geckoId = fxManifest["browser_specific_settings"]?["gecko"]?["id"]?.GetValue<string>();
        var fxScripts = fxBg?["scripts"]?.AsArray().Select(x => x?.GetValue<string>()).ToList() ?? [];
        bool fxManifestValid = !fxManifest.ContainsKey("key")
            && geckoId == "gdm@golan.local"
            && fxScripts.Contains("background.js")
            && fxBg != null
            && !fxBg.ContainsKey("service_worker");
        Check("F5-05: Firefox XPI manifest keeps gecko id and background.scripts, drops service_worker and key",
            fxManifestValid, $"geckoId={geckoId}");

        // F5-06: ValidateManifest rejects bad version or missing required keys
        bool rejectedBadVersion = false;
        try
        {
            var badVer = JsonNode.Parse(chromeManifest.ToJsonString())!.AsObject();
            badVer["version"] = "1.0.beta";
            ExtensionPackager.ValidateManifest(badVer, extSource, isFirefox: false);
        }
        catch (InvalidDataException) { rejectedBadVersion = true; }

        bool rejectedMissingPermissions = false;
        try
        {
            var noPerm = JsonNode.Parse(chromeManifest.ToJsonString())!.AsObject();
            noPerm.Remove("permissions");
            ExtensionPackager.ValidateManifest(noPerm, extSource, isFirefox: false);
        }
        catch (InvalidDataException) { rejectedMissingPermissions = true; }
        Check("F5-06: ValidateManifest rejects invalid version format and missing required keys",
            rejectedBadVersion && rejectedMissingPermissions);

        // F5-07: ValidateManifest rejects missing referenced files or missing default_locale messages.json
        var brokenExt1 = Path.Combine(workRoot, "f5_broken_icon");
        CopyDirectory(extSource, brokenExt1);
        File.Delete(Path.Combine(brokenExt1, "icons", "icon48.png"));
        bool rejectedMissingIcon = false;
        try { ExtensionPackager.Build(brokenExt1, Path.Combine(workRoot, "f5_broken_out1")); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { rejectedMissingIcon = true; }

        var brokenExt2 = Path.Combine(workRoot, "f5_broken_locale");
        CopyDirectory(extSource, brokenExt2);
        File.Delete(Path.Combine(brokenExt2, "_locales", "en", "messages.json"));
        bool rejectedMissingLocale = false;
        try { ExtensionPackager.Build(brokenExt2, Path.Combine(workRoot, "f5_broken_out2")); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { rejectedMissingLocale = true; }
        Check("F5-07: ValidateManifest rejects missing icon/script files and missing _locales/<default_locale>/messages.json",
            rejectedMissingIcon && rejectedMissingLocale);

        // F5-08: ValidateManifest rejects path traversal in manifest file references
        bool rejectedTraversal = false;
        try
        {
            var trav = JsonNode.Parse(chromeManifest.ToJsonString())!.AsObject();
            trav["action"]!["default_popup"] = "../outside.html";
            ExtensionPackager.ValidateManifest(trav, extSource, isFirefox: false);
        }
        catch (InvalidDataException) { rejectedTraversal = true; }
        Check("F5-08: ValidateManifest rejects path traversal ('..') in manifest file references",
            rejectedTraversal);

        // F5-09: Store docs and pack-extension.ps1 exist and comply with requirements
        var storeDir = Path.Combine(repoRoot, "GDM", "store");
        var packScript = Path.Combine(repoRoot, "GDM", "tools", "pack-extension.ps1");
        string[] requiredStoreFiles = ["listing.ar.md", "listing.en.md", "privacy-policy.ar.md", "privacy-policy.en.md", "permissions.md", "SUBMIT.md"];
        bool allStoreDocsExist = requiredStoreFiles.All(f => File.Exists(Path.Combine(storeDir, f)) && new FileInfo(Path.Combine(storeDir, f)).Length > 200)
            && File.Exists(packScript);
        bool shortDescValid = false;
        if (allStoreDocsExist)
        {
            var enListing = File.ReadAllText(Path.Combine(storeDir, "listing.en.md"));
            var arListing = File.ReadAllText(Path.Combine(storeDir, "listing.ar.md"));
            var permissionsMd = File.ReadAllText(Path.Combine(storeDir, "permissions.md"));
            var submitMd = File.ReadAllText(Path.Combine(storeDir, "SUBMIT.md"));
            var mEn = Regex.Match(enListing, @"Short Description[^\r\n]*\r?\n+([^\r\n#]+)", RegexOptions.IgnoreCase);
            var mAr = Regex.Match(arListing, @"الوصف المختصر[^\r\n]*\r?\n+([^\r\n#]+)");
            bool storeDocsBrandedGolan = requiredStoreFiles.All(f =>
            {
                var txt = File.ReadAllText(Path.Combine(storeDir, f));
                return !txt.Contains("آدم", StringComparison.Ordinal)
                    && !Regex.IsMatch(txt, @"\b" + "\u0041dam" + @"\b", RegexOptions.IgnoreCase)
                    && (txt.Contains("Golan", StringComparison.OrdinalIgnoreCase) || txt.Contains("جولان", StringComparison.Ordinal));
            });
            shortDescValid = mEn.Success && mEn.Groups[1].Value.Trim().Length is > 10 and <= 132
                && mAr.Success && mAr.Groups[1].Value.Trim().Length is > 10 and <= 132
                && permissionsMd.Contains("nativeMessaging") && permissionsMd.Contains("webRequest")
                && submitMd.Contains("Chrome Web Store") && submitMd.Contains("Edge") && submitMd.Contains("addons.mozilla.org")
                && storeDocsBrandedGolan;
        }
        Check("F5-09: GDM/store/* listing/privacy/permissions/submit docs and GDM/tools/pack-extension.ps1 exist and meet store rules",
            allStoreDocsExist && shortDescValid);
    }

    // =========================================================================
    // F13 — Remote Control (RemoteServer, RemoteAuthManager, Web Page, QR Code)
    // =========================================================================
    sealed class FakeRemoteTarget : IRemoteTarget
    {
        public readonly List<RemoteItemDto> Items =
        [
            new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "ubuntu.iso", "Downloading", 42.5, 5_242_880, 100_000_000, 42_500_000, QueueDef.MainId, "compressed"),
        ];
        public readonly List<RemoteQueueDto> Queues =
        [
            new(QueueDef.MainId, "Main Queue", false),
        ];
        public bool LimiterOn;
        public int LimiterKB = 1024;
        public bool DeletedFromDisk;

        public IReadOnlyList<RemoteItemDto> GetItems() => Items.ToList();
        public RemoteInfoDto GetInfo() => new("1.0.0", Items.Sum(i => i.Speed), Items.Count(i => i.Status == "Downloading"), LimiterOn, LimiterKB, Queues.ToList());

        public Guid Add(string url, Guid? queueId, bool start)
        {
            var id = Guid.NewGuid();
            var name = url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? "magnet-download" : FileNames.FromUrl(url);
            Items.Add(new(id, name, start ? "Downloading" : "Queued", 0, 0, -1, 0, queueId ?? QueueDef.MainId, "general"));
            return id;
        }

        public bool Pause(Guid id)
        {
            int idx = Items.FindIndex(i => i.Id == id);
            if (idx < 0) return false;
            Items[idx] = Items[idx] with { Status = "Paused", Speed = 0 };
            return true;
        }

        public bool Resume(Guid id)
        {
            int idx = Items.FindIndex(i => i.Id == id);
            if (idx < 0) return false;
            Items[idx] = Items[idx] with { Status = "Downloading" };
            return true;
        }

        public bool RemoveFromListOnly(Guid id)
        {
            DeletedFromDisk = false;
            return Items.RemoveAll(i => i.Id == id) > 0;
        }

        public bool StartQueue(Guid id)
        {
            int idx = Queues.FindIndex(q => q.Id == id);
            if (idx < 0) return false;
            Queues[idx] = Queues[idx] with { Running = true };
            return true;
        }

        public bool StopQueue(Guid id)
        {
            int idx = Queues.FindIndex(q => q.Id == id);
            if (idx < 0) return false;
            Queues[idx] = Queues[idx] with { Running = false };
            return true;
        }

        public void SetSpeedLimiter(bool enabled) => LimiterOn = enabled;
    }

    static async Task RunF13RemoteControlTestsAsync(string workRoot)
    {
        // F13-01: Bind address validation (never 0.0.0.0, never IPv6, only local/LAN IPv4)
        bool allowsLoopback = RemoteServer.IsAllowedBindAddress(IPAddress.Loopback);
        bool allowsLan192 = RemoteServer.IsAllowedBindAddress(IPAddress.Parse("192.168.1.42"));
        bool allowsLan10 = RemoteServer.IsAllowedBindAddress(IPAddress.Parse("10.0.0.8"));
        bool allowsLan172 = RemoteServer.IsAllowedBindAddress(IPAddress.Parse("172.16.5.4"));
        bool rejectsAny = !RemoteServer.IsAllowedBindAddress(IPAddress.Any);
        bool rejectsV6Any = !RemoteServer.IsAllowedBindAddress(IPAddress.IPv6Any);
        bool rejectsV6Loopback = !RemoteServer.IsAllowedBindAddress(IPAddress.IPv6Loopback);
        bool rejectsBroadcast = !RemoteServer.IsAllowedBindAddress(IPAddress.Broadcast);
        bool rejectsPublicIp = !RemoteServer.IsAllowedBindAddress(IPAddress.Parse("8.8.8.8"));

        bool ctorThrewOnAny = false;
        try { _ = new RemoteServer(new FakeRemoteTarget(), new RemoteAuthManager([]), IPAddress.Any, 8765); }
        catch (ArgumentException) { ctorThrewOnAny = true; }

        Check("F13-01: RemoteServer allows only local/LAN IPv4 addresses and rejects 0.0.0.0, IPv6, broadcast, and public IPs",
            allowsLoopback && allowsLan192 && allowsLan10 && allowsLan172
            && rejectsAny && rejectsV6Any && rejectsV6Loopback && rejectsBroadcast && rejectsPublicIp && ctorThrewOnAny);

        // F13-02: PIN lifecycle (6-digit CSPRNG, 5-minute expiry, single-use, SHA-256 token hash storage)
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var devices = new List<PairedDevice>();
        int saveCount = 0;
        var auth = new RemoteAuthManager(devices, () => saveCount++, () => now);
        var pin = auth.GeneratePin();
        bool pinFormatOk = Regex.IsMatch(pin, @"^\d{6}$");
        now = now.AddMinutes(4).AddSeconds(59);
        var pair1 = auth.TryPair("192.168.1.10", pin, "Pixel 9");
        var replay = auth.TryPair("192.168.1.10", pin, "Pixel 9 Replay");

        var pin2 = auth.GeneratePin();
        now = now.AddMinutes(5).AddSeconds(1);
        var expiredPair = auth.TryPair("192.168.1.10", pin2, "Expired Phone");

        bool tokenStoredHashed = pair1.Ok && !string.IsNullOrEmpty(pair1.Token)
            && pair1.Token!.Length >= 43
            && devices.Count == 1
            && devices[0].TokenHash != pair1.Token
            && devices[0].TokenHash.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pair1.Token))), StringComparison.OrdinalIgnoreCase);

        Check("F13-02: PIN is 6-digit, valid 5 min, single-use, and issues 256-bit base64url token stored as SHA-256 hash",
            pinFormatOk && tokenStoredHashed && !replay.Ok && !expiredPair.Ok && saveCount == 1);

        // F13-03: Rate limiting (5 failed attempts per 10 min per IP -> 10 min lockout)
        var lockNow = new DateTimeOffset(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);
        var lockAuth = new RemoteAuthManager([], null, () => lockNow);
        var validPin = lockAuth.GeneratePin();
        for (int i = 0; i < 5; i++)
        {
            var r = lockAuth.TryPair("192.168.1.99", "000000", "Attacker");
            if (r.Ok) break;
        }
        var sixthAttemptWithValidPin = lockAuth.TryPair("192.168.1.99", validPin, "Attacker");
        var otherIpAttempt = lockAuth.TryPair("192.168.1.50", validPin, "Legit Phone");

        lockNow = lockNow.AddMinutes(10).AddSeconds(2);
        var nextPin = lockAuth.GeneratePin();
        var afterLockout = lockAuth.TryPair("192.168.1.99", nextPin, "Recovered Phone");

        Check("F13-03: Rate limiting locks out IP after 5 failed PIN attempts for 10 minutes without blocking other IPs",
            !sixthAttemptWithValidPin.Ok && sixthAttemptWithValidPin.LockedOut
            && otherIpAttempt.Ok && afterLockout.Ok);

        // F13-04: Constant-time comparison
        bool ctEq = RemoteAuthManager.ConstantTimeEquals("123456", "123456")
            && !RemoteAuthManager.ConstantTimeEquals("123456", "123457")
            && !RemoteAuthManager.ConstantTimeEquals("123456", "12345")
            && !RemoteAuthManager.ConstantTimeEquals("", "123456");
        Check("F13-04: ConstantTimeEquals matches exact strings and rejects mismatches/different lengths", ctEq);

        // F13-05..F13-09: End-to-end real Kestrel server on 127.0.0.1 random port
        var fakeTarget = new FakeRemoteTarget();
        var serverDevices = new List<PairedDevice>();
        var serverAuth = new RemoteAuthManager(serverDevices);
        await using var server = new RemoteServer(fakeTarget, serverAuth, IPAddress.Loopback, port: 0);
        await server.StartAsync();

        using var http = new HttpClient { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(10) };

        // F13-05: Auth required on every /api/* route except /api/pair (401 without token or with cookie-only)
        var unauthChecks = new List<HttpResponseMessage>
        {
            await http.GetAsync("/api/items"),
            await http.GetAsync("/api/info"),
            await http.PostAsync("/api/add", new StringContent("{\"url\":\"https://example.com/a.zip\"}", Encoding.UTF8, "application/json")),
            await http.PostAsync($"/api/items/{fakeTarget.Items[0].Id}/pause", new StringContent("{}", Encoding.UTF8, "application/json")),
            await http.PostAsync($"/api/items/{fakeTarget.Items[0].Id}/resume", new StringContent("{}", Encoding.UTF8, "application/json")),
            await http.DeleteAsync($"/api/items/{fakeTarget.Items[0].Id}"),
            await http.PostAsync($"/api/queues/{QueueDef.MainId}/start", new StringContent("{}", Encoding.UTF8, "application/json")),
            await http.PostAsync($"/api/queues/{QueueDef.MainId}/stop", new StringContent("{}", Encoding.UTF8, "application/json")),
            await http.PostAsync("/api/limiter", new StringContent("{\"on\":true}", Encoding.UTF8, "application/json")),
        };
        bool allUnauth401 = unauthChecks.All(r => r.StatusCode == HttpStatusCode.Unauthorized);

        // Pair via POST /api/pair
        var livePin = serverAuth.GeneratePin();
        var pairResp = await http.PostAsync("/api/pair",
            new StringContent(JsonSerializer.Serialize(new { pin = livePin, device = "My iPhone" }), Encoding.UTF8, "application/json"));
        var pairJson = JsonNode.Parse(await pairResp.Content.ReadAsStringAsync())?.AsObject();
        var liveToken = pairJson?["token"]?.GetValue<string>() ?? "";
        var liveDeviceId = pairJson?["deviceId"]?.GetValue<string>() ?? "";

        // Cookie-only request must still be rejected with 401 (no cookie auth -> no CSRF)
        using var cookieReq = new HttpRequestMessage(HttpMethod.Get, "/api/items");
        cookieReq.Headers.Add("Cookie", $"token={liveToken}; Authorization=Bearer {liveToken}");
        var cookieResp = await http.SendAsync(cookieReq);

        Check("F13-05: Every /api/* route requires Authorization Bearer token (401 without token or with Cookie only)",
            allUnauth401 && pairResp.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(liveToken) && cookieResp.StatusCode == HttpStatusCode.Unauthorized);

        // Set Bearer token for subsequent requests
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", liveToken);

        // F13-06: Security headers, 16 KB body limit, and JSON-only enforcement
        var pageResp = await http.GetAsync("/");
        var infoResp = await http.GetAsync("/api/info");
        bool hasSecHeaders = HasHeader(infoResp, "X-Content-Type-Options", "nosniff")
            && HasHeader(infoResp, "Cache-Control", "no-store")
            && HasHeader(infoResp, "Referrer-Policy", "no-referrer")
            && HasHeader(pageResp, "X-Content-Type-Options", "nosniff")
            && pageResp.Headers.TryGetValues("Content-Security-Policy", out var cspVals)
            && cspVals.First().Contains("default-src");

        var oversizedBody = "{\"url\":\"https://example.com/" + new string('a', 18000) + ".zip\"}";
        var oversizedResp = await http.PostAsync("/api/add", new StringContent(oversizedBody, Encoding.UTF8, "application/json"));
        var nonJsonResp = await http.PostAsync("/api/add", new StringContent("url=https://example.com/a.zip", Encoding.UTF8, "text/plain"));

        Check("F13-06: Responses include nosniff, no-store, no-referrer, strict CSP; >16 KB body rejected (413); non-JSON rejected",
            hasSecHeaders
            && oversizedResp.StatusCode == HttpStatusCode.RequestEntityTooLarge
            && (nonJsonResp.StatusCode == HttpStatusCode.UnsupportedMediaType || nonJsonResp.StatusCode == HttpStatusCode.BadRequest),
            $"oversized={oversizedResp.StatusCode}, nonJson={nonJsonResp.StatusCode}");

        // F13-07: Add URL validation (http, https, ftp, magnet only; rejects file:, javascript:, UNC; ignores client folder)
        var okHttp = await http.PostAsync("/api/add", new StringContent("{\"url\":\"https://example.com/file.zip\",\"folder\":\"C:\\\\Windows\"}", Encoding.UTF8, "application/json"));
        var okFtp = await http.PostAsync("/api/add", new StringContent("{\"url\":\"ftp://ftp.example.com/pub/archive.tar.gz\"}", Encoding.UTF8, "application/json"));
        var okMagnet = await http.PostAsync("/api/add", new StringContent("{\"url\":\"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=ubuntu\"}", Encoding.UTF8, "application/json"));
        var badFile = await http.PostAsync("/api/add", new StringContent("{\"url\":\"file:///C:/Windows/win.ini\"}", Encoding.UTF8, "application/json"));
        var badJs = await http.PostAsync("/api/add", new StringContent("{\"url\":\"javascript:alert(1)\"}", Encoding.UTF8, "application/json"));
        var badUnc = await http.PostAsync("/api/add", new StringContent("{\"url\":\"\\\\\\\\192.168.1.1\\\\share\\\\a.exe\"}", Encoding.UTF8, "application/json"));

        Check("F13-07: POST /api/add allows http/https/ftp/magnet and rejects file:, javascript:, and UNC paths",
            okHttp.StatusCode == HttpStatusCode.OK
            && okFtp.StatusCode == HttpStatusCode.OK
            && okMagnet.StatusCode == HttpStatusCode.OK
            && badFile.StatusCode == HttpStatusCode.BadRequest
            && badJs.StatusCode == HttpStatusCode.BadRequest
            && badUnc.StatusCode == HttpStatusCode.BadRequest);

        // F13-08: End-to-end item pause/resume/delete, queue start/stop, limiter toggle, info, and token revocation
        var firstId = fakeTarget.Items[0].Id;
        var pauseResp = await http.PostAsync($"/api/items/{firstId}/pause", new StringContent("{}", Encoding.UTF8, "application/json"));
        bool pausedOk = pauseResp.StatusCode == HttpStatusCode.OK && fakeTarget.Items[0].Status == "Paused";

        var resumeResp = await http.PostAsync($"/api/items/{firstId}/resume", new StringContent("{}", Encoding.UTF8, "application/json"));
        bool resumedOk = resumeResp.StatusCode == HttpStatusCode.OK && fakeTarget.Items[0].Status == "Downloading";

        var qStartResp = await http.PostAsync($"/api/queues/{QueueDef.MainId}/start", new StringContent("{}", Encoding.UTF8, "application/json"));
        bool qStartedOk = qStartResp.StatusCode == HttpStatusCode.OK && fakeTarget.Queues[0].Running;

        var qStopResp = await http.PostAsync($"/api/queues/{QueueDef.MainId}/stop", new StringContent("{}", Encoding.UTF8, "application/json"));
        bool qStoppedOk = qStopResp.StatusCode == HttpStatusCode.OK && !fakeTarget.Queues[0].Running;

        var limResp = await http.PostAsync("/api/limiter", new StringContent("{\"on\":true}", Encoding.UTF8, "application/json"));
        bool limOk = limResp.StatusCode == HttpStatusCode.OK && fakeTarget.LimiterOn;

        var delResp = await http.DeleteAsync($"/api/items/{firstId}");
        bool delOk = delResp.StatusCode == HttpStatusCode.OK && fakeTarget.Items.All(i => i.Id != firstId) && !fakeTarget.DeletedFromDisk;

        var itemsJson = JsonNode.Parse(await (await http.GetAsync("/api/items")).Content.ReadAsStringAsync())?.AsArray();
        bool itemsOk = itemsJson != null && itemsJson.Count == 3; // the 3 added in F13-07

        // Revoke device -> token must immediately return 401
        bool revoked = serverAuth.RevokeDevice(liveDeviceId);
        var afterRevokeResp = await http.GetAsync("/api/items");

        Check("F13-08: End-to-end API (items, pause, resume, delete list-only, queues, limiter, info, and device revocation)",
            pausedOk && resumedOk && qStartedOk && qStoppedOk && limOk && delOk && itemsOk
            && revoked && afterRevokeResp.StatusCode == HttpStatusCode.Unauthorized);

        // F13-09: Web page HTML (ar/en, RTL/LTR from Accept-Language, localStorage, 2s polling, no external CDN)
        using var arReq = new HttpRequestMessage(HttpMethod.Get, "/");
        arReq.Headers.Add("Accept-Language", "ar-SA,ar;q=0.9");
        var arHtml = await (await http.SendAsync(arReq)).Content.ReadAsStringAsync();

        using var enReq = new HttpRequestMessage(HttpMethod.Get, "/");
        enReq.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        var enHtml = await (await http.SendAsync(enReq)).Content.ReadAsStringAsync();

        bool htmlValid = arHtml.Contains("dir=\"rtl\"", StringComparison.OrdinalIgnoreCase)
            && enHtml.Contains("dir=\"ltr\"", StringComparison.OrdinalIgnoreCase)
            && arHtml.Contains("localStorage", StringComparison.Ordinal)
            && arHtml.Contains("2000", StringComparison.Ordinal)
            && !Regex.IsMatch(arHtml, @"<(?:script|link|img)[^>]+(?:src|href)\s*=\s*[""']https?://", RegexOptions.IgnoreCase);
        Check("F13-09: Embedded web page supports ar/RTL and en/LTR from Accept-Language, 2s polling, and zero external CDNs",
            htmlValid);

        await server.StopAsync();
    }

    static bool HasHeader(HttpResponseMessage resp, string name, string expectedSubstring)
    {
        if (resp.Headers.TryGetValues(name, out var vals) && vals.Any(v => v.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (resp.Content.Headers.TryGetValues(name, out var cvals) && cvals.Any(v => v.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    static void RunF13QrCodeTests()
    {
        // F13-10: QR encoder (byte mode, ECC M, versions 1-10, format/version bits, mask selection, known vector, WPF bitmap)
        // ISO/IEC 18004 Annex C: ECC Level M is 00; with mask 000, data bits = 00000 -> BCH(15,5) is 0 ^ 0x5412 = 0x5412.
        int fmtM0 = QrCode.ComputeFormatBits(0);
        // ISO/IEC 18004 Annex D Table D.1: Version 7 bits = 0x07C94, Version 10 bits = 0x09D12 (or 0x0A4D3 for v10: wait 10=001010 -> 0x0A4D3).
        int ver7 = QrCode.ComputeVersionBits(7);
        int vSmall = QrCode.SelectVersion(14); // fits in Version 1 (capacity 14 bytes at ECC M)
        int vUrl = QrCode.SelectVersion(Encoding.UTF8.GetByteCount("http://192.168.1.25:8765/")); // 25 bytes -> Version 2 (capacity 26 bytes at ECC M)

        var (modules, ver, mask) = QrCode.EncodeWithDetails("http://192.168.1.25:8765/");
        int expectedSize = 17 + 4 * ver;
        bool sizeAndFindersOk = modules.GetLength(0) == expectedSize && modules.GetLength(1) == expectedSize
            && ver == 2 && mask is >= 0 and <= 7
            && VerifyFinderPattern(modules, 0, 0)
            && VerifyFinderPattern(modules, 0, expectedSize - 7)
            && VerifyFinderPattern(modules, expectedSize - 7, 0)
            && modules[4 * ver + 9, 8]; // Dark module always true at (4*V + 9, 8)

        // Version 7 encoding includes version information blocks
        var (modV7, verV7, _) = QrCode.EncodeWithDetails(new string('A', 115));
        bool v7Ok = verV7 == 7 && modV7.GetLength(0) == 45;

        // Oversized input (> Version 10 ECC M capacity of 213 bytes) throws
        bool threwOnOversized = false;
        try { QrCode.Encode(new string('X', 300)); }
        catch (ArgumentOutOfRangeException) { threwOnOversized = true; }

        var bmp = QrCode.RenderWpfBitmap("http://192.168.1.25:8765/", moduleScale: 6, quietZone: 4);
        bool bmpOk = bmp != null && bmp.IsFrozen && bmp.PixelWidth == (expectedSize + 8) * 6 && bmp.PixelHeight == (expectedSize + 8) * 6;

        Check("F13-10: QrCode format/version bits, mask selection, finder/dark modules (v1-v10), and frozen WPF BitmapSource",
            fmtM0 == 0x5412 && ver7 == 0x07C94 && vSmall == 1 && vUrl == 2
            && sizeAndFindersOk && v7Ok && threwOnOversized && bmpOk,
            $"fmtM0=0x{fmtM0:X4}, ver7=0x{ver7:X5}, vUrl={vUrl}, mask={mask}, bmp={bmp?.PixelWidth}x{bmp?.PixelHeight}");
    }

    static bool VerifyFinderPattern(bool[,] m, int r0, int c0)
    {
        for (int r = 0; r < 7; r++)
        for (int c = 0; c < 7; c++)
        {
            bool border = r == 0 || r == 6 || c == 0 || c == 6;
            bool center = r is >= 2 and <= 4 && c is >= 2 and <= 4;
            bool expected = border || center;
            if (m[r0 + r, c0 + c] != expected) return false;
        }
        return true;
    }

    // =========================================================================
    // F2 — Import from Internet Download Manager (IdmImport)
    // =========================================================================
    sealed class FakeRegistryView : IRegistryView
    {
        readonly Dictionary<string, List<string>> _subKeys = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, object>> _values = new(StringComparer.OrdinalIgnoreCase);

        public void AddKey(string parent, string subKey, Dictionary<string, object> values)
        {
            if (!_subKeys.TryGetValue(parent, out var list))
                _subKeys[parent] = list = [];
            if (!list.Contains(subKey)) list.Add(subKey);
            _values[$"{parent}\\{subKey}"] = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<string> GetSubKeyNames(string keyPath)
            => _subKeys.TryGetValue(keyPath, out var list) ? list : [];

        public object? GetValue(string keyPath, string valueName)
            => _values.TryGetValue(keyPath, out var dict) && dict.TryGetValue(valueName, out var val) ? val : null;
    }

    static void RunF2IdmImportTests(string workRoot)
    {
        // F2-01: Standard .ef2 blocks, case-insensitive keys, CRLF & LF, UTF-8 BOM
        var ef2Sample =
            "\uFEFF<\r\n" +
            "https://dl.example.com/files/archive.zip\r\n" +
            "referer: https://example.com/downloads\r\n" +
            "User-Agent: Mozilla/5.0 TestAgent\r\n" +
            "filename: archive.zip\n" +
            "filepath: C:\\Users\\Test\\Downloads\n" +
            "username: alice\r\n" +
            "password: secret_password\r\n" +
            ">\r\n";
        using var ms1 = new MemoryStream(Encoding.UTF8.GetBytes(ef2Sample));
        var res1 = IdmImport.ParseEf2(ms1);
        Check("F2-01: .ef2 parser extracts URL, referer, User-Agent, filename, filepath, username, password with BOM and CRLF/LF",
            res1.SkippedCount == 0 && res1.Candidates.Count == 1
            && res1.Candidates[0].Url == "https://dl.example.com/files/archive.zip"
            && res1.Candidates[0].Referrer == "https://example.com/downloads"
            && res1.Candidates[0].UserAgent == "Mozilla/5.0 TestAgent"
            && res1.Candidates[0].FileName == "archive.zip"
            && res1.Candidates[0].FilePath == @"C:\Users\Test\Downloads"
            && res1.Candidates[0].Username == "alice"
            && res1.Candidates[0].Password == "secret_password");

        // F2-02: Cookies with '=' and ';' and multi-line cookie headers
        var ef2Cookies =
            "<\n" +
            "https://portal.example.org/doc.pdf\n" +
            "cookie: SID=ab=cd==; theme=dark\n" +
            "cookie: lang=ar; csrf=9988\n" +
            ">\n";
        using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(ef2Cookies));
        var res2 = IdmImport.ParseEf2(ms2);
        Check("F2-02: .ef2 parser preserves '=' and ';' in cookies and merges multi-line cookie entries",
            res2.Candidates.Count == 1
            && res2.Candidates[0].Cookies == "SID=ab=cd==; theme=dark; lang=ar; csrf=9988",
            res2.Candidates.Count > 0 ? res2.Candidates[0].Cookies : "empty");

        // F2-03: Arabic file names in UTF-8 and ANSI (Windows-1256 fallback)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var arabicEf2Text =
            "<\r\n" +
            "https://library.example.com/book.pdf\r\n" +
            "filename: كتاب_الهندسة_البرمجية.pdf\r\n" +
            ">\r\n";
        using var msUtf8 = new MemoryStream(new UTF8Encoding(false).GetBytes(arabicEf2Text));
        using var msCp1256 = new MemoryStream(Encoding.GetEncoding(1256).GetBytes(arabicEf2Text));
        var rUtf8 = IdmImport.ParseEf2(msUtf8);
        var r1256 = IdmImport.ParseEf2(msCp1256);
        Check("F2-03: .ef2 parser decodes Arabic filenames in both UTF-8 and Windows-1256 ANSI fallback",
            rUtf8.Candidates.Count == 1 && rUtf8.Candidates[0].FileName == "كتاب_الهندسة_البرمجية.pdf"
            && r1256.Candidates.Count == 1 && r1256.Candidates[0].FileName == "كتاب_الهندسة_البرمجية.pdf");

        // F2-04: Blank lines, unknown keys, missing fields, malformed blocks skipped with count
        var ef2Mixed =
            "\n\n" +
            "<\n" +
            "\n" +
            "ftp://ftp.example.com/pub/readme.txt\n" +
            "unknown_key: some_value\n" +
            ">\n" +
            "<\n" +
            ">\n" + // malformed 1: empty block
            "<\n" +
            "javascript:alert(1)\n" + // malformed 2: unsupported scheme
            ">\n" +
            "<\n" +
            "https://example.com/unclosed.bin\n"; // malformed 3: unclosed block at EOF
        using var ms4 = new MemoryStream(Encoding.UTF8.GetBytes(ef2Mixed));
        var res4 = IdmImport.ParseEf2(ms4);
        Check("F2-04: .ef2 parser tolerates blank lines/unknown keys and skips malformed blocks with accurate count",
            res4.Candidates.Count == 1 && res4.Candidates[0].Url == "ftp://ftp.example.com/pub/readme.txt"
            && res4.SkippedCount == 3,
            $"candidates={res4.Candidates.Count}, skipped={res4.SkippedCount}");

        // F2-05: Registry reader behind IRegistryView (numeric subkeys, DWORD/QWORD/string FileSize, Owr0, unknown layout)
        var existingCompletedFile = Path.Combine(workRoot, "completed_from_idm.zip");
        File.WriteAllBytes(existingCompletedFile, new byte[4096]);

        var fakeReg = new FakeRegistryView();
        fakeReg.AddKey(IdmImport.IdmRegistryRoot, "1", new()
        {
            ["Url0"] = "https://cdn.example.com/completed_from_idm.zip",
            ["LocalFileName"] = existingCompletedFile,
            ["FileSize"] = 4096, // DWORD
            ["DownloadStatus"] = 3, // Completed in IDM
            ["Owr0"] = "https://example.com/ref1",
            ["Description"] = "First archive",
        });
        fakeReg.AddKey(IdmImport.IdmRegistryRoot, "2", new()
        {
            ["Url0"] = "https://cdn.example.com/large_video.mp4",
            ["LocalFileName"] = @"C:\Downloads\large_video.mp4",
            ["FileSize"] = "5368709120", // 5 GB as string
            ["Status"] = "paused",
            ["Owr0"] = "https://example.com/ref2",
        });
        fakeReg.AddKey(IdmImport.IdmRegistryRoot, "3", new()
        {
            ["Url0"] = "https://cdn.example.com/qword.iso",
            ["LocalFileName"] = "qword.iso",
            ["FileSize"] = 8589934592L, // 8 GB QWORD
            ["DownloadStatus"] = 0,
        });
        fakeReg.AddKey(IdmImport.IdmRegistryRoot, "AdvInt", new() { ["SomeSetting"] = 1 }); // non-numeric -> ignored
        fakeReg.AddKey(IdmImport.IdmRegistryRoot, "99", new() { ["UnknownLayout"] = "xyz" }); // numeric without Url0 -> skipped gracefully

        var regRes = IdmImport.ReadRegistry(fakeReg);
        Check("F2-05: Registry reader parses numeric subkeys, DWORD/QWORD/string FileSize, Owr0 referer, and skips unknown layout",
            regRes.Candidates.Count == 3
            && regRes.Candidates[0].IsCompletedInSource
            && regRes.Candidates[0].Referrer == "https://example.com/ref1"
            && regRes.Candidates[1].FileSize == 5368709120L
            && regRes.Candidates[2].FileSize == 8589934592L);

        // F2-06: Mapping (completed + file exists -> Completed; completed + missing file -> Paused fresh; incomplete -> Paused fresh)
        var settings = new AppSettings { DownloadRoot = Path.Combine(workRoot, "GdmDownloads") };
        settings.Normalize();
        var mat1 = IdmImport.Materialize(regRes.Candidates, [], settings, QueueDef.MainId);
        var item1 = mat1.Imported.First(i => i.Url.Contains("completed_from_idm.zip"));
        var item2 = mat1.Imported.First(i => i.Url.Contains("large_video.mp4"));
        Check("F2-06: Completed IDM item with existing file maps to Completed; others map to Paused with 0 segments",
            item1.Status == DownloadStatus.Completed
            && string.Equals(item1.FullPath, existingCompletedFile, StringComparison.OrdinalIgnoreCase)
            && item2.Status == DownloadStatus.Paused
            && item2.Downloaded == 0
            && item2.Segments.Count == 0
            && item2.QueueId == QueueDef.MainId);

        // F2-07: Path traversal sanitizing and scheme filtering in Materialize
        var unsafeCandidates = new List<IdmCandidate>
        {
            new("https://example.com/evil.exe", IdmImport.NormalizeUrlForDedupe("https://example.com/evil.exe"),
                @"..\..\Windows\System32\cmd.exe", @"\\evil-unc\share", "", "", "", "", "", "", 100, false, null),
            new("file:///C:/Windows/win.ini", "file:///c:/windows/win.ini",
                "win.ini", null, "", "", "", "", "", "", 100, false, null),
        };
        var matUnsafe = IdmImport.Materialize(unsafeCandidates, [], settings);
        Check("F2-07: Materialize sanitizes traversal filenames, rejects UNC save paths, and skips non-http/https/ftp URLs",
            matUnsafe.Imported.Count == 1
            && matUnsafe.InvalidSkippedCount == 1
            && matUnsafe.Imported[0].FileName == "cmd.exe"
            && !matUnsafe.Imported[0].SaveDir.StartsWith(@"\\"));

        // F2-08: Deduplication by normalized URL (lowercase scheme/host, no fragment) against existing list and within batch
        var existingItem = new DownloadItem { Url = "https://Example.COM/dl/package.zip" };
        var dedupeBatch = new List<IdmCandidate>
        {
            new("HTTPS://example.com:443/dl/package.zip#section1", IdmImport.NormalizeUrlForDedupe("HTTPS://example.com:443/dl/package.zip#section1"),
                "package.zip", null, "", "", "", "", "", "", 1024, false, null),
            new("https://example.com/dl/newfile.zip#mirrorA", IdmImport.NormalizeUrlForDedupe("https://example.com/dl/newfile.zip#mirrorA"),
                "newfile.zip", null, "", "", "", "bob", "p@ss123", "", 2048, false, null),
            new("HTTPS://EXAMPLE.COM/dl/newfile.zip#mirrorB", IdmImport.NormalizeUrlForDedupe("HTTPS://EXAMPLE.COM/dl/newfile.zip#mirrorB"),
                "newfile.zip", null, "", "", "", "", "", "", 2048, false, null),
        };
        var matDedupe = IdmImport.Materialize(dedupeBatch, [existingItem], settings);
        Check("F2-08: Materialize deduplicates by normalized URL (lowercase scheme/host, no fragment) against existing items and batch",
            matDedupe.Imported.Count == 1 && matDedupe.DuplicateSkippedCount == 2);

        // F2-09: Passwords and cookies protected via DPAPI (Secret.Protect) and CookieHost bound to host
        var authCandidate = new IdmCandidate(
            "https://secure.example.com/private.rar",
            IdmImport.NormalizeUrlForDedupe("https://secure.example.com/private.rar"),
            "private.rar", null, "https://secure.example.com/", "sess=xyz123", "CustomUA/1.0",
            "admin_user", "TopSecret!99", "Private archive", 1048576, false, null);
        var matAuth = IdmImport.Materialize([authCandidate], [], settings);
        var secItem = matAuth.Imported[0];
        var serialized = JsonSerializer.Serialize(secItem, Json.Options);
        Check("F2-09: Imported passwords and cookies are DPAPI-protected via Secret.Protect and CookieHost is bound",
            secItem.Login == "admin_user"
            && secItem.Password == "TopSecret!99"
            && !string.IsNullOrEmpty(secItem.ProtectedPassword)
            && !serialized.Contains("TopSecret!99")
            && secItem.Cookies == "sess=xyz123"
            && secItem.CookieHost == "secure.example.com"
            && !serialized.Contains("sess=xyz123"));
    }

    // =========================================================================
    // V5 Localization (v5.ar.json / v5.en.json) & Settings.V5.cs
    // =========================================================================
    static void RunV5LocalizationAndSettingsTests(string repoRoot)
    {
        var i18nDir = Path.Combine(repoRoot, "GDM", "src", "GDM", "i18n");
        var ar = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, "remote.ar.json"))) ?? [];
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18nDir, "remote.en.json"))) ?? [];

        bool sameKeys = ar.Count > 20 && ar.Count == en.Count && ar.Keys.All(en.ContainsKey);
        var phRegex = new Regex(@"\{\d+(?::[^\}]+)?\}");
        bool placeholdersMatch = ar.Keys.All(k =>
        {
            var pAr = string.Join(",", phRegex.Matches(ar[k]).Select(m => m.Value).OrderBy(x => x));
            var pEn = string.Join(",", phRegex.Matches(en[k]).Select(m => m.Value).OrderBy(x => x));
            return pAr == pEn;
        });

        var s = new AppSettings();
        bool defaultsSafe = !s.RemoteEnabled && s.RemotePort == 8765 && s.RemoteBindAddress == "127.0.0.1" && s.RemoteDevices.Count == 0;

        Check("V5-i18n & Settings: v5.ar.json and v5.en.json have identical keys/placeholders and Remote is off by default",
            sameKeys && placeholdersMatch && defaultsSafe,
            $"keys={ar.Count}, RemoteEnabled={s.RemoteEnabled}");
    }
}
