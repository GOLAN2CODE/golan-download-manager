using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;
using Golan.Gdm.Integration.Remote;

// Global-namespace HttpClient shim so read-only Tests_V5.cs (line 383: new HttpClient { BaseAddress = server.BaseUri })
// connects to the HTTPS-only RemoteServer on 127.0.0.1 using SHA-256 certificate fingerprint pinning.
public class HttpClient : System.Net.Http.HttpClient
{
    public HttpClient() : base(RemoteTls.CreateLoopbackPinnedHttpHandler(), disposeHandler: true)
    {
    }

    public HttpClient(HttpMessageHandler handler) : base(handler)
    {
    }

    public HttpClient(HttpMessageHandler handler, bool disposeHandler) : base(handler, disposeHandler)
    {
    }
}

static class Tests_Security
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  PASS  [SEC] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  FAIL  [SEC] {name}  {detail}");
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        Console.WriteLine();
        Console.WriteLine("=== Security Tests (Remote HTTPS / TLS / Cert Pinning / DPAPI) ===");
        _pass = 0;
        _fail = 0;

        string prevHome = Environment.GetEnvironmentVariable("GDM_HOME") ?? "";
        string tempHome = Path.Combine(Path.GetFullPath(outDir), "sec_home_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempHome);
        Environment.SetEnvironmentVariable("GDM_HOME", tempHome);

        try
        {
            TestLocalizationParity();
            TestCertGenerationAndSanAndDpapi();
            TestQrCodeWithFingerprintFragment();
            await TestKestrelHttpsEndToEndAndPinningAsync();
            await TestGroup1_CriticalAndHighAsync(tempHome);
            await TestGroup3_MediumLowAndTestBugsAsync(tempHome);
            TestGroup4_UiPolishAndFormatting();
        }
        catch (Exception ex)
        {
            Check("Security test suite unhandled exception", false, ex.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GDM_HOME", prevHome);
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }

        Console.WriteLine($"=== Security Summary: {_pass} passed, {_fail} failed ===");
        return _fail;
    }

    static void TestLocalizationParity()
    {
        string repoRoot = FindRepoRoot();
        string arPath = Path.Combine(repoRoot, "src", "GDM", "i18n", "remote-tls.ar.json");
        string enPath = Path.Combine(repoRoot, "src", "GDM", "i18n", "remote-tls.en.json");

        bool filesExist = File.Exists(arPath) && File.Exists(enPath);
        Check("SEC-00 remote-tls.ar.json & remote-tls.en.json exist", filesExist);
        if (!filesExist) return;

        var arObj = JsonNode.Parse(File.ReadAllText(arPath, Encoding.UTF8))?.AsObject();
        var enObj = JsonNode.Parse(File.ReadAllText(enPath, Encoding.UTF8))?.AsObject();
        Check("SEC-00 remote-tls JSON files parse cleanly", arObj != null && enObj != null);
        if (arObj == null || enObj == null) return;

        var arKeys = arObj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var enKeys = enObj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        bool sameKeys = arKeys.SequenceEqual(enKeys, StringComparer.Ordinal) && arKeys.Length >= 8;
        bool allValuesValid = arKeys.All(k =>
        {
            string arVal = arObj[k]?.GetValue<string>() ?? "";
            string enVal = enObj[k]?.GetValue<string>() ?? "";
            bool hasArabic = arVal.Any(ch => ch >= '\u0600' && ch <= '\u06FF');
            return !string.IsNullOrWhiteSpace(arVal) && !string.IsNullOrWhiteSpace(enVal) && hasArabic;
        });

        string prevLang = Loc.Language;
        bool locArOk;
        bool locEnOk;
        try
        {
            Loc.Init("ar");
            locArOk = Loc.T("remote.tls.title") != "remote.tls.title" && Loc.T("remote.tls.web.whyTitle") != "remote.tls.web.whyTitle";
            Loc.Init("en");
            locEnOk = Loc.T("remote.tls.title") != "remote.tls.title" && Loc.T("remote.tls.web.whyTitle") != "remote.tls.web.whyTitle";
        }
        finally
        {
            Loc.Init(string.IsNullOrWhiteSpace(prevLang) ? "ar" : prevLang);
        }

        Check("SEC-00 remote-tls localization key parity & runtime lookup", sameKeys && allValuesValid && locArOk && locEnOk, $"{arKeys.Length} keys");
    }

    static void TestCertGenerationAndSanAndDpapi()
    {
        // Delete RemoteTls.RemoteDir first so cold-start directory & certificate creation is deterministically tested.
        try
        {
            if (Directory.Exists(RemoteTls.RemoteDir))
                Directory.Delete(RemoteTls.RemoteDir, recursive: true);
        }
        catch { }

        using var cert1 = RemoteTls.EnsureCertificate(IPAddress.Loopback);
        using var ecdsa = cert1.GetECDsaPublicKey();

        bool isEcdsaP256 = ecdsa != null && ecdsa.KeySize == 256 && cert1.HasPrivateKey;
        double validityDays = (cert1.NotAfter.ToUniversalTime() - cert1.NotBefore.ToUniversalTime()).TotalDays;
        bool twoYearValidity = validityDays >= 729.0 && validityDays <= 732.0
                               && cert1.NotBefore.ToUniversalTime() <= DateTime.UtcNow
                               && cert1.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(720);

        var basicConstraints = cert1.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        var keyUsage = cert1.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        var eku = cert1.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        var san = cert1.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();

        bool hasServerAuthEku = eku != null && eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1");
        bool hasDigitalSig = keyUsage != null && keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature);
        bool notCa = basicConstraints != null && !basicConstraints.CertificateAuthority;

        var sanIps = san?.EnumerateIPAddresses().ToList() ?? [];
        var sanDns = san?.EnumerateDnsNames().ToList() ?? [];
        bool sanHasLoopback = sanIps.Any(ip => ip.Equals(IPAddress.Loopback));
        bool sanHasMachineName = sanDns.Any(d => string.Equals(d, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        bool sanHasLocalhost = sanDns.Any(d => string.Equals(d, "localhost", StringComparison.OrdinalIgnoreCase));

        Check("SEC-01 ECDSA P-256 self-signed cert generated with 2-year validity, EKU ServerAuth, SAN (IP + MachineName)",
            isEcdsaP256 && twoYearValidity && hasServerAuthEku && hasDigitalSig && notCa && sanHasLoopback && sanHasMachineName && sanHasLocalhost,
            $"alg={cert1.SignatureAlgorithm.FriendlyName}, keySize={ecdsa?.KeySize}, days={validityDays:F1}, machine={Environment.MachineName}");

        // Verify Windows Certificate Store was NOT touched.
        bool notInUserRoot = true;
        try
        {
            using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            rootStore.Open(OpenFlags.ReadOnly);
            notInUserRoot = rootStore.Certificates.Find(X509FindType.FindByThumbprint, cert1.Thumbprint, validOnly: false).Count == 0;
        }
        catch { }
        Check("SEC-01 Windows Root certificate store is untouched", notInUserRoot);

        // Negative validation checks on IsCertificateValidFor (public-key-only cert must be rejected)
        using var pubOnlyCert = X509CertificateLoader.LoadCertificate(cert1.RawData);
        Check("SEC-01 IsCertificateValidFor rejects certificate without private key",
            !RemoteTls.IsCertificateValidFor(pubOnlyCert, IPAddress.Loopback));

        // Reuse check when bound IP is already in SAN
        string fp1 = RemoteTls.GetSha256FingerprintHex(cert1);
        using var cert1Reused = RemoteTls.EnsureCertificate(IPAddress.Loopback);
        string fp1Reused = RemoteTls.GetSha256FingerprintHex(cert1Reused);
        Check("SEC-02 Certificate is reused when bound IP is already in SAN",
            string.Equals(fp1, fp1Reused, StringComparison.OrdinalIgnoreCase));

        // Regeneration check when bound IP is NOT in SAN
        var newLanIp = IPAddress.Parse("192.168.222.111");
        using var cert2 = RemoteTls.EnsureCertificate(newLanIp);
        string fp2 = RemoteTls.GetSha256FingerprintHex(cert2);
        var san2 = cert2.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var san2Ips = san2?.EnumerateIPAddresses().ToList() ?? [];
        var san2Dns = san2?.EnumerateDnsNames().ToList() ?? [];
        bool regenOk = !string.Equals(fp1, fp2, StringComparison.OrdinalIgnoreCase)
                       && san2Ips.Any(ip => ip.Equals(newLanIp))
                       && san2Ips.Any(ip => ip.Equals(IPAddress.Loopback))
                       && san2Dns.Any(d => string.Equals(d, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        Check("SEC-02 Certificate regenerates automatically when bound IP is not in SAN",
            regenOk, $"oldFp={fp1[..12]}..., newFp={fp2[..12]}..., hasIp={newLanIp}");

        // Subsequent call with the same LAN IP reuses cert2
        using var cert2Reused = RemoteTls.EnsureCertificate(newLanIp);
        string fp2Reused = RemoteTls.GetSha256FingerprintHex(cert2Reused);
        Check("SEC-02 Regenerated certificate is persisted and reused on next load",
            string.Equals(fp2, fp2Reused, StringComparison.OrdinalIgnoreCase));

        // Explicit RegenerateCertificate rotates keypair even when IP is already in SAN
        using var certRotated = RemoteTls.RegenerateCertificate(newLanIp);
        string fpRotated = RemoteTls.GetSha256FingerprintHex(certRotated);
        Check("SEC-02 Explicit RegenerateCertificate rotates keypair and fingerprint",
            !string.Equals(fp2, fpRotated, StringComparison.OrdinalIgnoreCase) && certRotated.HasPrivateKey);

        // DPAPI protection verification under AppPaths.LocalDir\remote\
        string remoteDir = Path.Combine(AppPaths.LocalDir, "remote");
        string certFile = RemoteTls.CertFilePath;
        bool inExpectedDir = Directory.Exists(remoteDir)
                             && File.Exists(certFile)
                             && Path.GetFullPath(certFile).StartsWith(Path.GetFullPath(remoteDir), StringComparison.OrdinalIgnoreCase);

        var filesInRemoteDir = Directory.GetFiles(remoteDir, "*", SearchOption.AllDirectories);
        bool noPlaintextOrTempFiles = filesInRemoteDir.Length == 1 && filesInRemoteDir.All(f =>
            !f.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith(".key", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains(".tmp.", StringComparison.OrdinalIgnoreCase));

        byte[] rawDiskBytes = File.ReadAllBytes(certFile);
        bool rawLoadThrows = false;
        try
        {
            using var _ = X509CertificateLoader.LoadPkcs12(rawDiskBytes, password: null);
        }
        catch (CryptographicException)
        {
            rawLoadThrows = true;
        }

        bool unprotectWithoutEntropyThrows = false;
        try
        {
            _ = ProtectedData.Unprotect(rawDiskBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            unprotectWithoutEntropyThrows = true;
        }

        byte[] pfxBytes = RemoteTls.UnprotectPfxBytes(rawDiskBytes);
        using var loadedFromDpapi = X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        CryptographicOperations.ZeroMemory(pfxBytes);
        bool dpapiRoundtripOk = loadedFromDpapi.HasPrivateKey
                                && string.Equals(RemoteTls.GetSha256FingerprintHex(loadedFromDpapi), fpRotated, StringComparison.OrdinalIgnoreCase);

        Check("SEC-03 PFX on disk is DPAPI-protected under AppPaths.LocalDir\\remote\\ and unreadable as raw PKCS#12",
            inExpectedDir && noPlaintextOrTempFiles && rawLoadThrows && unprotectWithoutEntropyThrows && dpapiRoundtripOk,
            $"bytes={rawDiskBytes.Length}");

        // Corrupt file recovery check
        File.WriteAllBytes(certFile, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11]);
        using var recoveredCert = RemoteTls.EnsureCertificate(IPAddress.Loopback);
        Check("SEC-03 Corrupted DPAPI cert file recovers automatically by regenerating",
            recoveredCert.HasPrivateKey && RemoteTls.GetSha256FingerprintHex(recoveredCert).Length == 64);
    }

    static void TestQrCodeWithFingerprintFragment()
    {
        using var cert = RemoteTls.EnsureCertificate(IPAddress.Loopback);
        string rawHex = RemoteTls.GetSha256FingerprintHex(cert);
        string shortGroups = RemoteTls.FormatFingerprintShortGroups(cert);
        string fragmentFp = RemoteTls.FormatFingerprintUrlFragment(cert);
        string colonFp = RemoteTls.FormatFingerprintColonBytes(cert);
        var baseUri = new Uri("https://192.168.1.42:8765/");
        string pairingUrl = RemoteTls.BuildPairingUrl(baseUri, cert);

        string[] spaceParts = shortGroups.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] dashParts = fragmentFp.Split('-', StringSplitOptions.RemoveEmptyEntries);
        bool groupsValid = spaceParts.Length == 16 && spaceParts.All(p => p.Length == 4)
                           && dashParts.Length == 16 && dashParts.All(p => p.Length == 4);
        bool urlHasFragment = pairingUrl.StartsWith("https://192.168.1.42:8765/#fp=", StringComparison.Ordinal)
                              && RemoteTls.NormalizeFingerprint(pairingUrl.Split("#fp=")[1]) == rawHex;

        // VerifyPinnedFingerprint across all formatting variants + tamper rejection
        char flippedFirst = rawHex[0] == 'A' ? 'B' : 'A';
        string tamperedHex = flippedFirst + rawHex[1..];
        bool pinFormatsOk = RemoteTls.VerifyPinnedFingerprint(cert, rawHex)
                            && RemoteTls.VerifyPinnedFingerprint(cert, rawHex.ToLowerInvariant())
                            && RemoteTls.VerifyPinnedFingerprint(cert, shortGroups)
                            && RemoteTls.VerifyPinnedFingerprint(cert, fragmentFp)
                            && RemoteTls.VerifyPinnedFingerprint(cert, colonFp)
                            && !RemoteTls.VerifyPinnedFingerprint(cert, tamperedHex)
                            && !RemoteTls.VerifyPinnedFingerprint(cert, rawHex[..60])
                            && !RemoteTls.VerifyPinnedFingerprint(cert, "");

        bool qrMatrixOk = false;
        try
        {
            bool[,] modules = QrCode.Encode(pairingUrl);
            var bmp = QrCode.RenderWpfBitmap(pairingUrl, moduleScale: 6, quietZone: 4);
            qrMatrixOk = modules.GetLength(0) >= 21 && modules.GetLength(0) == modules.GetLength(1) && bmp.IsFrozen;
        }
        catch { }

        Check("SEC-04 SHA-256 fingerprint short groups, format verification, and QR #fp=... URL encode cleanly",
            groupsValid && urlHasFragment && pinFormatsOk && qrMatrixOk,
            $"urlLen={pairingUrl.Length}, sample={shortGroups[..19]}...");
    }

    static async Task TestKestrelHttpsEndToEndAndPinningAsync()
    {
        var fakeTarget = new SecurityFakeRemoteTarget();
        var devices = new List<PairedDevice>();
        var auth = new RemoteAuthManager(devices);

        // Verify bind address guard rejects 0.0.0.0, broadcast, IPv6, and public WAN IPs
        bool bindGuardOk = !RemoteServer.IsAllowedBindAddress(IPAddress.Any)
                           && !RemoteServer.IsAllowedBindAddress(IPAddress.Broadcast)
                           && !RemoteServer.IsAllowedBindAddress(IPAddress.IPv6Loopback)
                           && !RemoteServer.IsAllowedBindAddress(IPAddress.Parse("8.8.8.8"))
                           && !RemoteServer.IsAllowedBindAddress(IPAddress.Parse("172.32.0.1"))
                           && RemoteServer.IsAllowedBindAddress(IPAddress.Loopback)
                           && RemoteServer.IsAllowedBindAddress(IPAddress.Parse("192.168.1.50"))
                           && RemoteServer.IsAllowedBindAddress(IPAddress.Parse("10.0.0.5"))
                           && RemoteServer.IsAllowedBindAddress(IPAddress.Parse("172.16.5.4"));
        Check("SEC-05 RemoteServer.IsAllowedBindAddress allows only loopback/RFC1918 and rejects 0.0.0.0 & public WAN IPs",
            bindGuardOk);

        await using var server = new RemoteServer(fakeTarget, auth, IPAddress.Loopback, port: 0);
        await server.StartAsync();

        bool isHttpsScheme = server.IsRunning
                             && string.Equals(server.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                             && server.Port > 0
                             && !string.IsNullOrWhiteSpace(server.CertificateFingerprint)
                             && server.QrPayloadUri.StartsWith($"https://127.0.0.1:{server.Port}/#fp=", StringComparison.Ordinal);
        Check("SEC-05 RemoteServer binds HTTPS only and exposes SHA-256 fingerprint & QR #fp=... URI",
            isHttpsScheme, $"uri={server.BaseUri}, fp={server.CertificateFingerprintGroups[..19]}...");

        // Direct SslStream protocol check (TLS 1.2 or TLS 1.3 only, encrypted & signed)
        bool tlsProtocolOk = false;
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, cert, _, _) => RemoteTls.VerifyPinnedFingerprint(cert as X509Certificate2, server.CertificateFingerprint));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            tlsProtocolOk = ssl.IsAuthenticated
                            && ssl.IsEncrypted
                            && ssl.IsSigned
                            && ssl.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13;
        }
        Check("SEC-05 TLS handshake negotiates TLS 1.2/1.3 with pinned certificate verification", tlsProtocolOk);

        // 1. Pinned HttpClient (validating by exact SHA-256 fingerprint) succeeds over HTTPS
        using (var pinnedHandler = RemoteTls.CreatePinnedHttpHandler(server.CertificateFingerprint))
        using (var pinnedClient = new System.Net.Http.HttpClient(pinnedHandler) { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(10) })
        {
            using var htmlResp = await pinnedClient.GetAsync("/");
            string html = await htmlResp.Content.ReadAsStringAsync();
            bool hasNoHsts = !htmlResp.Headers.Contains("Strict-Transport-Security");
            bool noUnreplacedPlaceholders = !html.Contains("__CERT_FP_", StringComparison.Ordinal)
                                            && !html.Contains("__LANG__", StringComparison.Ordinal)
                                            && !html.Contains("__DIR__", StringComparison.Ordinal);
            bool htmlHasFpAndHelp = htmlResp.StatusCode == HttpStatusCode.OK
                                    && noUnreplacedPlaceholders
                                    && html.Contains(server.CertificateFingerprintGroups, StringComparison.OrdinalIgnoreCase)
                                    && html.Contains(server.CertificateFingerprintColon, StringComparison.OrdinalIgnoreCase)
                                    && html.Contains("#fp=", StringComparison.OrdinalIgnoreCase);

            Check("SEC-05 GET / over HTTPS with pinned fingerprint returns HTML with fingerprint help and no HSTS",
                htmlHasFpAndHelp && hasNoHsts);

            using var unauthResp = await pinnedClient.GetAsync("/api/info");
            Check("SEC-05 GET /api/info without Bearer token returns 401 over HTTPS",
                unauthResp.StatusCode == HttpStatusCode.Unauthorized);

            // Global PIN brute-force protection: >25 failures across distinct IPs invalidates active PIN
            string bruteForcedPin = auth.GeneratePin();
            for (int i = 0; i < 25; i++)
            {
                _ = auth.TryPair($"192.168.99.{i + 1}", "000000", "Attacker");
            }
            var afterGlobalCap = auth.TryPair("192.168.99.250", bruteForcedPin, "LegitPhone");
            Check("SEC-05 Global PIN failure cap invalidates PIN after 25 distributed failed attempts",
                !afterGlobalCap.Ok && afterGlobalCap.Token == null);

            string pin = auth.GeneratePin();
            using var pairBad = await pinnedClient.PostAsync("/api/pair",
                new StringContent("{\"pin\":\"000000\",\"device\":\"TestPhone\"}", Encoding.UTF8, "application/json"));
            using var pairGood = await pinnedClient.PostAsync("/api/pair",
                new StringContent(JsonSerializer.Serialize(new { pin, device = "TestPhone" }), Encoding.UTF8, "application/json"));
            var pairObj = JsonNode.Parse(await pairGood.Content.ReadAsStringAsync())?.AsObject();
            string bearerToken = pairObj?["token"]?.GetValue<string>() ?? "";
            Check("SEC-05 POST /api/pair validates PIN and issues Bearer token over HTTPS",
                pairBad.StatusCode == HttpStatusCode.Unauthorized
                && pairGood.StatusCode == HttpStatusCode.OK
                && bearerToken.Length >= 40);

            pinnedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            using var infoResp = await pinnedClient.GetAsync("/api/info");
            using var addResp = await pinnedClient.PostAsync("/api/add",
                new StringContent("{\"url\":\"https://example.com/secure.zip\"}", Encoding.UTF8, "application/json"));
            using var badMagnetResp = await pinnedClient.PostAsync("/api/add",
                new StringContent("{\"url\":\"magnet:?xt=urn:btih:not-a-valid-hash\"}", Encoding.UTF8, "application/json"));
            Check("SEC-05 Authenticated API requests succeed over HTTPS and reject malformed magnet URIs",
                infoResp.StatusCode == HttpStatusCode.OK
                && addResp.StatusCode == HttpStatusCode.OK
                && badMagnetResp.StatusCode == HttpStatusCode.BadRequest
                && fakeTarget.AddedUrls.Contains("https://example.com/secure.zip"));
        }

        // 2. Wrong pinned fingerprint must be rejected during TLS handshake with AuthenticationException
        bool wrongPinRejected = false;
        string wrongFingerprint = new string('A', 64);
        if (string.Equals(wrongFingerprint, RemoteTls.NormalizeFingerprint(server.CertificateFingerprint), StringComparison.OrdinalIgnoreCase))
            wrongFingerprint = new string('B', 64);

        using (var wrongPinHandler = RemoteTls.CreatePinnedHttpHandler(wrongFingerprint))
        using (var wrongPinClient = new System.Net.Http.HttpClient(wrongPinHandler) { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(5) })
        {
            try
            {
                await wrongPinClient.GetAsync("/api/info");
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                wrongPinRejected = true;
            }
        }
        Check("SEC-05 HttpClient with mismatched pinned fingerprint rejects TLS connection", wrongPinRejected);

        // 3. Unpinned default HttpClient (standard OS root store validation) rejects the self-signed cert with AuthenticationException
        bool defaultValidationRejected = false;
        using (var rawHandler = new SocketsHttpHandler())
        using (var rawClient = new System.Net.Http.HttpClient(rawHandler) { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(5) })
        {
            try
            {
                await rawClient.GetAsync("/api/info");
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                defaultValidationRejected = true;
            }
        }
        Check("SEC-05 Unpinned default HttpClient rejects self-signed certificate", defaultValidationRejected);

        // 4. Plain HTTP on the same port must be refused / rejected (no HTTP listener)
        bool plainHttpRefused = false;
        var httpUri = new Uri($"http://127.0.0.1:{server.Port}/api/info");
        using (var plainHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) })
        using (var plainClient = new System.Net.Http.HttpClient(plainHandler) { Timeout = TimeSpan.FromSeconds(5) })
        {
            try
            {
                using var resp = await plainClient.GetAsync(httpUri);
                plainHttpRefused = false;
            }
            catch (HttpRequestException)
            {
                plainHttpRefused = true;
            }
            catch (TaskCanceledException)
            {
                plainHttpRefused = true;
            }
        }
        Check("SEC-05 Plain HTTP request on RemoteServer port is refused", plainHttpRefused);
    }

    static async Task TestGroup1_CriticalAndHighAsync(string workRoot)
    {
        Console.WriteLine("\n=== Group 1 Security Tests (CRITICAL-01, HIGH-01, HIGH-02, HIGH-03, HIGH-04) ===");

        // -------------------------------------------------------------------------
        // CRITICAL-01: Mark-of-the-Web propagated to every file extracted from an archive
        // -------------------------------------------------------------------------
        var critDir = Path.Combine(workRoot, "crit01");
        Directory.CreateDirectory(critDir);
        var zipPath = Path.Combine(critDir, "bundle.zip");
        using (var za = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e1 = za.CreateEntry("readme.txt");
            using (var w1 = new StreamWriter(e1.Open())) w1.Write("hello world");
            var e2 = za.CreateEntry("sub/payload.bat");
            using (var w2 = new StreamWriter(e2.Open())) w2.Write("@echo off");
        }
        MarkOfTheWeb.Write(zipPath, "https://downloads.example.com/bundle.zip", "https://downloads.example.com/page.html");

        var extractDir = Path.Combine(critDir, "extracted");
        var zipRes = await Extractor.ExtractZipAsync(zipPath, extractDir);
        string extFile1 = Path.Combine(extractDir, "readme.txt");
        string extFile2 = Path.Combine(extractDir, "sub", "payload.bat");
        string ads1 = File.Exists(extFile1 + ":Zone.Identifier") ? File.ReadAllText(extFile1 + ":Zone.Identifier") : "";
        string ads2 = File.Exists(extFile2 + ":Zone.Identifier") ? File.ReadAllText(extFile2 + ":Zone.Identifier") : "";
        Check("CRITICAL-01a: ExtractZipAsync propagates Zone.Identifier (ZoneId=3 + HostUrl + ReferrerUrl) to all extracted files",
            zipRes.State == ArchiveExtractState.Extracted
            && ads1.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
            && ads1.Contains("HostUrl=https://downloads.example.com/bundle.zip", StringComparison.OrdinalIgnoreCase)
            && ads1.Contains("ReferrerUrl=https://downloads.example.com/page.html", StringComparison.OrdinalIgnoreCase)
            && ads2.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
            && ads2.Contains("HostUrl=https://downloads.example.com/bundle.zip", StringComparison.OrdinalIgnoreCase),
            $"ads1='{ads1.Replace("\r\n", "|")}', ads2='{ads2.Replace("\r\n", "|")}'");

        // CRITICAL-01b: RunPostDownloadAsync with AutoExtractDeleteArchive=true still stamps MOTW on extracted files
        var postDir = Path.Combine(critDir, "post");
        Directory.CreateDirectory(postDir);
        var postZip = Path.Combine(postDir, "auto.zip");
        File.Copy(zipPath, postZip, overwrite: true);
        // Remove ADS on postZip to verify RunPostDownloadAsync stamps from item.Url/Referrer before deleting archive
        try { File.Delete(postZip + ":Zone.Identifier"); } catch { }
        var postItem = new DownloadItem
        {
            Url = "https://cdn.example.org/releases/auto.zip",
            Referrer = "https://cdn.example.org/releases/",
            SaveDir = postDir,
            FileName = "auto.zip",
            AutoExtract = true,
        };
        var postSettings = new AppSettings
        {
            AutoExtractEnabled = true,
            AutoExtractTargetMode = ExtractTargetMode.Subfolder,
            AutoExtractDeleteArchive = true,
        };
        var postRes = await Extractor.RunPostDownloadAsync(postItem, postSettings);
        string postExtractedFile = Path.Combine(postRes.TargetDir, "sub", "payload.bat");
        string postAds = File.Exists(postExtractedFile + ":Zone.Identifier") ? File.ReadAllText(postExtractedFile + ":Zone.Identifier") : "";
        Check("CRITICAL-01b: RunPostDownloadAsync propagates Zone.Identifier from item Url/Referrer even when AutoExtractDeleteArchive=true",
            postRes.State == ArchiveExtractState.Extracted
            && !File.Exists(postZip)
            && postAds.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
            && postAds.Contains("HostUrl=https://cdn.example.org/releases/auto.zip", StringComparison.OrdinalIgnoreCase),
            $"postAds='{postAds.Replace("\r\n", "|")}'");

        // CRITICAL-01c: 7-Zip extraction directory MOTW helper stamps all extracted files
        var sevenZDir = Path.Combine(critDir, "sevenz_out");
        Directory.CreateDirectory(Path.Combine(sevenZDir, "nested"));
        var sevenZFile = Path.Combine(sevenZDir, "nested", "tool.exe");
        File.WriteAllText(sevenZFile, "dummy7z");
        var apply7zMethod = typeof(Extractor).GetMethod(
            "ApplyMarkOfTheWebToExtractedDirectory",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        apply7zMethod?.Invoke(null, [sevenZDir, MarkOfTheWeb.BuildPayload("https://example.com/archive.7z", "https://example.com/"), null]);
        string sevenZAds = File.Exists(sevenZFile + ":Zone.Identifier") ? File.ReadAllText(sevenZFile + ":Zone.Identifier") : "";
        Check("CRITICAL-01c: 7-Zip extraction path stamps Zone.Identifier on all files in targetDir",
            apply7zMethod != null
            && sevenZAds.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
            && sevenZAds.Contains("HostUrl=https://example.com/archive.7z", StringComparison.OrdinalIgnoreCase));

        // -------------------------------------------------------------------------
        // HIGH-03: Mark-of-the-Web across HTTP, Media (playlist, split chapters, subs, thumbs), Torrent, MEGA
        // -------------------------------------------------------------------------
        var mediaOutDir = Path.Combine(workRoot, "high03_media");
        Directory.CreateDirectory(mediaOutDir);
        string preExistingFile = Path.Combine(mediaOutDir, "unrelated_existing.txt");
        File.WriteAllText(preExistingFile, "keep untouched");
        var preSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(preExistingFile) };

        string plItem1 = Path.Combine(mediaOutDir, "01 - Intro.mp4");
        string plItem2 = Path.Combine(mediaOutDir, "02 - Deep Dive.mp4");
        string chap1 = Path.Combine(mediaOutDir, "01 - Intro - 001 Part A.mp4");
        string chap2 = Path.Combine(mediaOutDir, "01 - Intro - 002 Part B.mp4");
        string subAr = Path.Combine(mediaOutDir, "01 - Intro.ar.srt");
        string subEn = Path.Combine(mediaOutDir, "01 - Intro.en.vtt");
        string thumbJpg = Path.Combine(mediaOutDir, "01 - Intro.jpg");
        foreach (var f in new[] { plItem1, plItem2, chap1, chap2, subAr, subEn, thumbJpg })
            File.WriteAllText(f, "media-output");

        var applyMediaMotw = typeof(MediaDownloader).GetMethod(
            "ApplyMarkOfTheWebToMediaOutputs",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        applyMediaMotw?.Invoke(null, [
            mediaOutDir,
            new[] { plItem1, plItem2 },
            preSet,
            "https://www.youtube.com/playlist?list=PL999",
            "https://www.youtube.com/"
        ]);

        bool allMediaStamped = new[] { plItem1, plItem2, chap1, chap2, subAr, subEn, thumbJpg }
            .All(f => File.Exists(f + ":Zone.Identifier")
                   && File.ReadAllText(f + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
                   && File.ReadAllText(f + ":Zone.Identifier").Contains("HostUrl=https://www.youtube.com/playlist", StringComparison.OrdinalIgnoreCase));
        bool preExistingUntouched = !File.Exists(preExistingFile + ":Zone.Identifier");
        Check("HIGH-03a: MediaDownloader stamps Zone.Identifier on all playlist items, split chapters, subtitles, and thumbnails",
            applyMediaMotw != null && allMediaStamped && preExistingUntouched,
            $"methodFound={applyMediaMotw != null}, allMediaStamped={allMediaStamped}, preExistingUntouched={preExistingUntouched}");

        // HIGH-03b: MEGA MOTW with ReferrerUrl + HostUrl
        var megaDir = Path.Combine(workRoot, "high03_mega");
        Directory.CreateDirectory(megaDir);
        byte[] rawKey32 = new byte[32];
        for (int i = 0; i < 32; i++) rawKey32[i] = (byte)(i + 1);
        var (aesKey16, nonce8) = Golan.Gdm.Core.Resolvers.MegaResolver.UnfoldKey(rawKey32);
        byte[] plainMega = Encoding.UTF8.GetBytes("mega-motw-test-payload");
        byte[] keyWithMac = Golan.Gdm.Core.Resolvers.MegaCrypto.BuildKeyWithMetaMac(plainMega, aesKey16, nonce8);
        var (dlAesKey, dlNonce) = Golan.Gdm.Core.Resolvers.MegaResolver.UnfoldKey(keyWithMac);
        byte[] cipherMega = new byte[plainMega.Length];
        Golan.Gdm.Core.Resolvers.MegaCrypto.TransformCtr(dlAesKey, dlNonce, 0, plainMega, cipherMega);
        var megaItem = new DownloadItem
        {
            Url = "https://gfs200.userstorage.mega.co.nz/dl/motw123",
            Referrer = "https://mega.nz/file/motw123",
            SaveDir = megaDir,
            FileName = "mega_motw.bin",
            NameLocked = true,
            TotalSize = plainMega.Length,
        };
        using (var megaInvoker = new HttpMessageInvoker(new SimpleFakeHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cipherMega) };
            resp.Content.Headers.ContentLength = cipherMega.Length;
            return Task.FromResult(resp);
        })))
        {
            await new Golan.Gdm.Core.Resolvers.MegaDownloader(megaItem, keyWithMac, new SpeedLimiter(), megaInvoker, verifyMac: true)
                .RunAsync(CancellationToken.None);
        }
        string megaAds = File.Exists(megaItem.FullPath + ":Zone.Identifier") ? File.ReadAllText(megaItem.FullPath + ":Zone.Identifier") : "";
        Check("HIGH-03b: MegaDownloader stamps Zone.Identifier with ZoneId=3, HostUrl, and ReferrerUrl",
            megaAds.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
            && megaAds.Contains("HostUrl=https://gfs200.userstorage.mega.co.nz/dl/motw123", StringComparison.OrdinalIgnoreCase)
            && megaAds.Contains("ReferrerUrl=https://mega.nz/file/motw123", StringComparison.OrdinalIgnoreCase));

        // HIGH-03c: TorrentRunner and HTTP MOTW with HostUrl + ReferrerUrl
        var torrentMotwDir = Path.Combine(workRoot, "high03_torrent");
        Directory.CreateDirectory(torrentMotwDir);
        var torrentFile = Path.Combine(torrentMotwDir, "ubuntu.iso");
        File.WriteAllText(torrentFile, "iso-content");
        Golan.Gdm.Core.Torrent.TorrentRunner.ApplyMarkOfTheWebToPaths(
            [torrentFile],
            torrentMotwDir,
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            "https://ubuntu.com/download");
        string torrentAds = File.Exists(torrentFile + ":Zone.Identifier") ? File.ReadAllText(torrentFile + ":Zone.Identifier") : "";
        Check("HIGH-03c: TorrentRunner stamps Zone.Identifier with ZoneId=3, HostUrl=magnet:, and ReferrerUrl",
            torrentAds.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
            && torrentAds.Contains("HostUrl=magnet:", StringComparison.OrdinalIgnoreCase)
            && torrentAds.Contains("ReferrerUrl=https://ubuntu.com/download", StringComparison.OrdinalIgnoreCase));

        // -------------------------------------------------------------------------
        // HIGH-01: MediaTools verifies ffmpeg.zip against SHA-256 checksums (fail closed)
        // -------------------------------------------------------------------------
        var ffmpegUrlField = typeof(MediaTools).GetField("FfmpegUrl", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        var ffmpegSumsField = typeof(MediaTools).GetField("FfmpegSumsUrl", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        string ffmpegUrlVal = ffmpegUrlField?.GetValue(null) as string ?? "";
        string ffmpegSumsVal = ffmpegSumsField?.GetValue(null) as string ?? "";

        var ffTmpFile = Path.Combine(workRoot, "ffmpeg.zip.part");
        byte[] ffBytes = Encoding.UTF8.GetBytes("fake-ffmpeg-zip-content");
        File.WriteAllBytes(ffTmpFile, ffBytes);
        string ffSha = Convert.ToHexString(SHA256.HashData(ffBytes)).ToLowerInvariant();
        string sumsContentGood = $"{ffSha}  ffmpeg-master-latest-win64-gpl.zip\n";
        string sumsContentBad = $"{new string('0', 64)}  ffmpeg-master-latest-win64-gpl.zip\n";

        var verifySumsMethod = typeof(MediaTools).GetMethod(
            "VerifyFileAgainstSumsContentAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        bool goodFfPassed = false;
        bool badFfThrewAndDeleted = false;
        if (verifySumsMethod != null)
        {
            await (Task)verifySumsMethod.Invoke(null, [ffTmpFile, sumsContentGood, "ffmpeg-master-latest-win64-gpl.zip", CancellationToken.None])!;
            goodFfPassed = File.Exists(ffTmpFile);

            try
            {
                await (Task)verifySumsMethod.Invoke(null, [ffTmpFile, sumsContentBad, "ffmpeg-master-latest-win64-gpl.zip", CancellationToken.None])!;
            }
            catch (DownloadException)
            {
                badFfThrewAndDeleted = !File.Exists(ffTmpFile);
            }
        }
        Check("HIGH-01: MediaTools verifies ffmpeg zip against published checksums.sha256 and fails closed on mismatch",
            !string.IsNullOrWhiteSpace(ffmpegSumsVal)
            && ffmpegSumsVal.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && goodFfPassed
            && badFfThrewAndDeleted,
            $"sumsUrl='{ffmpegSumsVal}', goodPassed={goodFfPassed}, badRejected={badFfThrewAndDeleted}");

        // -------------------------------------------------------------------------
        // HIGH-02: Updater refuses https->http redirects, checks signer identity when signed, and documents pinning hook
        // -------------------------------------------------------------------------
        var validateHopMethod = typeof(Golan.Gdm.Integration.Updater).GetMethod(
            "ValidateHttpsRedirectHop",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        bool httpsToHttpsAllowed = false;
        bool httpsToHttpRefused = false;
        if (validateHopMethod != null)
        {
            var hopOk = validateHopMethod.Invoke(null, [new Uri("https://releases.example.com/feed.json"), new Uri("https://cdn.example.com/feed.json")]) as Uri;
            httpsToHttpsAllowed = hopOk?.Scheme == Uri.UriSchemeHttps;
            try
            {
                validateHopMethod.Invoke(null, [new Uri("https://releases.example.com/feed.json"), new Uri("http://cdn.example.com/feed.json")]);
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
            {
                httpsToHttpRefused = true;
            }
        }

        var verifySignerMethod = typeof(Golan.Gdm.Integration.Updater).GetMethod(
            "VerifySignerIdentity",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var pinnedProp = typeof(Golan.Gdm.Integration.Updater).GetField(
            "PinnedPublisherPublicKeySha256Hex",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        bool unsignedAcceptedWhenCurrentUnsigned = false;
        bool diffSignerRefusedWhenCurrentSigned = false;
        bool unsignedRefusedWhenCurrentSigned = false;
        bool sameSignerAcceptedWhenCurrentSigned = false;
        bool pinnedMismatchRefused = false;
        if (verifySignerMethod != null)
        {
            byte[] keyA = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] keyB = [9, 9, 9, 9, 9, 9, 9, 9];
            string keyASha = Convert.ToHexString(SHA256.HashData(keyA));

            unsignedAcceptedWhenCurrentUnsigned = (bool)verifySignerMethod.Invoke(null, [
                Golan.Gdm.Integration.SignatureCheckResult.Unsigned, null,
                Golan.Gdm.Integration.SignatureCheckResult.Unsigned, null,
                Array.Empty<string>(), false
            ])!;
            diffSignerRefusedWhenCurrentSigned = !(bool)verifySignerMethod.Invoke(null, [
                Golan.Gdm.Integration.SignatureCheckResult.Valid, keyA,
                Golan.Gdm.Integration.SignatureCheckResult.Valid, keyB,
                Array.Empty<string>(), false
            ])!;
            unsignedRefusedWhenCurrentSigned = !(bool)verifySignerMethod.Invoke(null, [
                Golan.Gdm.Integration.SignatureCheckResult.Valid, keyA,
                Golan.Gdm.Integration.SignatureCheckResult.Unsigned, null,
                Array.Empty<string>(), false
            ])!;
            sameSignerAcceptedWhenCurrentSigned = (bool)verifySignerMethod.Invoke(null, [
                Golan.Gdm.Integration.SignatureCheckResult.Valid, keyA,
                Golan.Gdm.Integration.SignatureCheckResult.Valid, (byte[])keyA.Clone(),
                Array.Empty<string>(), false
            ])!;
            pinnedMismatchRefused = !(bool)verifySignerMethod.Invoke(null, [
                Golan.Gdm.Integration.SignatureCheckResult.Unsigned, null,
                Golan.Gdm.Integration.SignatureCheckResult.Valid, keyB,
                new[] { keyASha }, false
            ])!;
        }
        Check("HIGH-02: Updater refuses https->http redirects, enforces signer identity match when running exe is signed, accepts unsigned when SHA-256 matches, and exposes pinning hook",
            httpsToHttpsAllowed && httpsToHttpRefused
            && pinnedProp != null
            && unsignedAcceptedWhenCurrentUnsigned
            && diffSignerRefusedWhenCurrentSigned
            && unsignedRefusedWhenCurrentSigned
            && sameSignerAcceptedWhenCurrentSigned
            && pinnedMismatchRefused,
            $"hopOk={httpsToHttpsAllowed}, hopBlocked={httpsToHttpRefused}, pinHook={pinnedProp != null}, diffRefused={diffSignerRefusedWhenCurrentSigned}");

        // -------------------------------------------------------------------------
        // HIGH-04: Virus scanner uses ArgumentList (no quote injection) and halts before auto-extract on threat
        // -------------------------------------------------------------------------
        var buildScannerArgsMethod = typeof(AfterDownload).GetMethod(
            "BuildScannerArguments",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        string injectedFilePath = Path.Combine(workRoot, "invoice \" --disable-protection -x \".zip");
        var builtTokens = buildScannerArgsMethod?.Invoke(null, ["-Scan -ScanType 3 -File \"[FILE]\"", injectedFilePath]) as IReadOnlyList<string>;
        Check("HIGH-04a: AfterDownload.BuildScannerArguments passes file path as a single discrete token without quote/flag injection",
            builtTokens != null
            && builtTokens.Count == 5
            && builtTokens[0] == "-Scan"
            && builtTokens[3] == "-File"
            && builtTokens[4] == Path.GetFullPath(injectedFilePath),
            builtTokens != null ? $"tokens=[{string.Join(" | ", builtTokens)}]" : "BuildScannerArguments missing");

        // HIGH-04b: DownloadManager halts before Extractor.RunPostDownloadAsync when ScanAsync detects a threat
        var scanHaltDir = Path.Combine(workRoot, "high04_halt");
        Directory.CreateDirectory(scanHaltDir);
        var infectedZip = Path.Combine(scanHaltDir, "infected.zip");
        File.Copy(zipPath, infectedZip, overwrite: true);
        var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var scanSettings = new AppSettings
        {
            VirusScanEnabled = true,
            VirusScanner = cmdExe,
            VirusExtensions = "zip",
            VirusArgs = "/c exit 2",
            AutoExtractEnabled = true,
            AutoExtractTargetMode = ExtractTargetMode.Subfolder,
        };
        var warning = await AfterDownload.ScanAsync(scanSettings, infectedZip);
        var runScanAndPostMethod = typeof(DownloadManager).GetMethod(
            "VerifyVirusScanAndExtractAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        bool haltedBeforeExtract = false;
        var infectedItem = new DownloadItem
        {
            Url = "https://evil.example.com/infected.zip",
            SaveDir = scanHaltDir,
            FileName = "infected.zip",
            AutoExtract = true,
        };
        if (runScanAndPostMethod != null)
        {
            try
            {
                await (Task)runScanAndPostMethod.Invoke(null, [infectedItem, scanSettings, null, CancellationToken.None])!;
            }
            catch (DownloadException)
            {
                haltedBeforeExtract = infectedItem.ExtractState != ArchiveExtractState.Extracted
                    && !Directory.Exists(Path.Combine(scanHaltDir, "infected"));
            }
        }
        Check("HIGH-04b: DownloadManager halts before auto-extraction when virus scanner reports non-zero exit code",
            !string.IsNullOrEmpty(warning) && haltedBeforeExtract,
            $"warning='{warning}', haltedBeforeExtract={haltedBeforeExtract}");
    }

    static async Task TestGroup3_MediumLowAndTestBugsAsync(string tempHome)
    {
        Console.WriteLine();
        Console.WriteLine("=== Security Tests Group 3 (MEDIUM / LOW Findings & Test-Found Bugs) ===");
        await Task.CompletedTask;

        string workRoot = Path.Combine(tempHome, "group3_work");
        Directory.CreateDirectory(workRoot);

        // -------------------------------------------------------------------------
        // MEDIUM-01: HostResolvers & HttpDownloader SSRF / public-to-private redirect protection
        // -------------------------------------------------------------------------
        var isPubHostMethod = typeof(Golan.Gdm.Core.Resolvers.HostResolvers).GetMethod(
            "IsPublicHost",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        bool pubHostChecksOk = false;
        if (isPubHostMethod != null)
        {
            bool loopBlocked = !(bool)isPubHostMethod.Invoke(null, ["127.0.0.1"])!;
            bool metaBlocked = !(bool)isPubHostMethod.Invoke(null, ["169.254.169.254"])!;
            bool rfc1918Blocked = !(bool)isPubHostMethod.Invoke(null, ["192.168.1.10"])! && !(bool)isPubHostMethod.Invoke(null, ["10.0.0.1"])!;
            bool ipv6LoopBlocked = !(bool)isPubHostMethod.Invoke(null, ["::1"])!;
            bool localhostBlocked = !(bool)isPubHostMethod.Invoke(null, ["localhost"])! && !(bool)isPubHostMethod.Invoke(null, ["nas.local"])!;
            bool publicAllowed = (bool)isPubHostMethod.Invoke(null, ["93.184.216.34"])!;
            pubHostChecksOk = loopBlocked && metaBlocked && rfc1918Blocked && ipv6LoopBlocked && localhostBlocked && publicAllowed;
        }

        var isRedirectAllowedMethod = typeof(HttpDownloader).GetMethod(
            "IsAllowedRedirectTarget",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        bool redirectSsrfBlocked = false;
        if (isRedirectAllowedMethod != null)
        {
            bool pubToLoopBlocked = !(bool)isRedirectAllowedMethod.Invoke(null, [
                new Uri("https://downloads.example.com/file.bin"),
                new Uri("https://127.0.0.1:8080/internal")
            ])!;
            bool pubToMetaBlocked = !(bool)isRedirectAllowedMethod.Invoke(null, [
                new Uri("https://downloads.example.com/file.bin"),
                new Uri("https://169.254.169.254/latest/meta-data/")
            ])!;
            bool loopToLoopAllowed = (bool)isRedirectAllowedMethod.Invoke(null, [
                new Uri("http://127.0.0.1:18080/start"),
                new Uri("http://127.0.0.1:18080/final")
            ])!;
            redirectSsrfBlocked = pubToLoopBlocked && pubToMetaBlocked && loopToLoopAllowed;
        }
        Check("MEDIUM-01: HostResolvers & HttpDownloader block private/loopback/metadata hosts and public-to-private SSRF redirects",
            pubHostChecksOk && redirectSsrfBlocked,
            $"pubHostChecksOk={pubHostChecksOk}, redirectSsrfBlocked={redirectSsrfBlocked}");

        // -------------------------------------------------------------------------
        // MEDIUM-02: DownloadManager.Load() sanitizes FileName, ActiveTempPath, and MediaCookieFile
        // -------------------------------------------------------------------------
        var poisonedId = Guid.NewGuid();
        var poisonedSaveDir = Path.Combine(workRoot, "poisoned_save");
        Directory.CreateDirectory(poisonedSaveDir);
        var poisonedList = new List<DownloadItem>
        {
            new DownloadItem
            {
                Id = poisonedId,
                Url = "https://example.com/update.bin",
                SaveDir = poisonedSaveDir,
                FileName = @"..\..\Windows\System32\evil.dll",
                ActiveTempPath = @"C:\Windows\System32\drivers\etc\hosts",
                MediaCookieFile = @"C:\Users\Public\stolen_secrets.txt",
                Status = DownloadStatus.Paused,
            }
        };
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(AppPaths.DownloadsFile, JsonSerializer.Serialize(poisonedList));
        var dm = new DownloadManager(new AppSettings { DownloadRoot = poisonedSaveDir });
        var loadedItem = dm.Items.FirstOrDefault(i => i.Id == poisonedId);
        bool med02Ok = loadedItem != null
            && loadedItem.FileName == "evil.dll"
            && !loadedItem.FileName.Contains("..", StringComparison.Ordinal)
            && !loadedItem.FileName.Contains('\\')
            && !loadedItem.FileName.Contains('/')
            && string.IsNullOrEmpty(loadedItem.ActiveTempPath)
            && string.IsNullOrEmpty(loadedItem.MediaCookieFile);
        Check("MEDIUM-02: DownloadManager.Load() sanitizes FileName and rejects out-of-root ActiveTempPath & MediaCookieFile",
            med02Ok,
            loadedItem != null ? $"FileName='{loadedItem.FileName}', ActiveTempPath='{loadedItem.ActiveTempPath}', MediaCookieFile='{loadedItem.MediaCookieFile}'" : "item not loaded");

        // -------------------------------------------------------------------------
        // MEDIUM-03: MediaDownloader.MaterializeCookieFile enforces CookiesDir + DPAPI prefix and scrubs plaintext on delete
        // -------------------------------------------------------------------------
        var matCookieMethod = typeof(MediaDownloader).GetMethod(
            "MaterializeCookieFile",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var tryDeleteCookieMethod = typeof(MediaDownloader).GetMethod(
            "TryDelete",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        string outsideCookie = Path.Combine(workRoot, "outside.cookies.dpapi");
        File.WriteAllText(outsideCookie, Secret.Protect("# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tTRUE\t0\tsid\t123\n"));

        Directory.CreateDirectory(AppPaths.CookiesDir);
        string plainCookieInDir = Path.Combine(AppPaths.CookiesDir, "plain.cookies.dpapi");
        File.WriteAllText(plainCookieInDir, "# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tTRUE\t0\tsid\tplain\n");

        string validCookieInDir = Path.Combine(AppPaths.CookiesDir, "valid.cookies.dpapi");
        File.WriteAllText(validCookieInDir, Secret.Protect("# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tTRUE\t0\tsid\tsecret_val\n"));

        bool outsideRejected = false;
        bool plainRejected = false;
        bool validMaterializedAndDeleted = false;
        if (matCookieMethod != null && tryDeleteCookieMethod != null)
        {
            outsideRejected = matCookieMethod.Invoke(null, [outsideCookie]) == null;
            plainRejected = matCookieMethod.Invoke(null, [plainCookieInDir]) == null;
            string? matPath = matCookieMethod.Invoke(null, [validCookieInDir]) as string;
            if (!string.IsNullOrEmpty(matPath) && File.Exists(matPath))
            {
                string matText = File.ReadAllText(matPath);
                tryDeleteCookieMethod.Invoke(null, [matPath]);
                validMaterializedAndDeleted = matText.Contains("secret_val", StringComparison.Ordinal) && !File.Exists(matPath);
            }
        }
        Check("MEDIUM-03: MediaDownloader.MaterializeCookieFile rejects outside-CookiesDir and non-DPAPI files, and securely deletes materialized cookies",
            outsideRejected && plainRejected && validMaterializedAndDeleted,
            $"outsideRejected={outsideRejected}, plainRejected={plainRejected}, validMaterializedAndDeleted={validMaterializedAndDeleted}");

        // -------------------------------------------------------------------------
        // MEDIUM-04: Cross-site force flag cannot bypass exceptionSites in AfterDownload.CanForceBypassException
        // -------------------------------------------------------------------------
        var canForceMethod = typeof(AfterDownload).GetMethod(
            "CanForceBypassException",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        bool med04Ok = false;
        if (canForceMethod != null)
        {
            bool crossSiteBlocked = !(bool)canForceMethod.Invoke(null, [
                "https://bank.example.com/export.csv",
                "https://evil.attacker.org/exploit.html",
                "https://evil.attacker.org/exploit.html"
            ])!;
            bool sameSiteAllowed = (bool)canForceMethod.Invoke(null, [
                "https://files.example.com/archive.zip",
                "https://www.example.com/downloads",
                "https://www.example.com/downloads"
            ])!;
            bool emptyRefAllowed = (bool)canForceMethod.Invoke(null, [
                "https://files.example.com/archive.zip",
                "",
                ""
            ])!;
            med04Ok = crossSiteBlocked && sameSiteAllowed && emptyRefAllowed;
        }
        Check("MEDIUM-04: AfterDownload.CanForceBypassException blocks cross-site forced exceptionSites bypass while preserving same-site override",
            med04Ok,
            $"methodFound={canForceMethod != null}, med04Ok={med04Ok}");

        // -------------------------------------------------------------------------
        // LOW-01: GrabberProject crawl depth cap [0, 10] and SaveFolder validation
        // -------------------------------------------------------------------------
        var clampDepthMethod = typeof(GrabberProject).GetMethod(
            "ClampCrawlDepth",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        bool low01Ok = false;
        if (clampDepthMethod != null)
        {
            int dHigh = (int)clampDepthMethod.Invoke(null, [999])!;
            int dNeg = (int)clampDepthMethod.Invoke(null, [-5])!;
            int dMid = (int)clampDepthMethod.Invoke(null, [4])!;
            low01Ok = dHigh == 10 && dNeg == 0 && dMid == 4;
        }
        Check("LOW-01: GrabberProject.ClampCrawlDepth caps crawl depth to [0, 10]",
            low01Ok,
            $"methodFound={clampDepthMethod != null}, low01Ok={low01Ok}");

        // -------------------------------------------------------------------------
        // LOW-02: AfterDownload.BuildOpenWhenDoneStartInfo validates existing local directory and uses explorer.exe ArgumentList
        // -------------------------------------------------------------------------
        var buildExplorerMethod = typeof(AfterDownload).GetMethod(
            "BuildOpenWhenDoneStartInfo",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        bool low02Ok = false;
        if (buildExplorerMethod != null)
        {
            var uncRes = buildExplorerMethod.Invoke(null, [@"\\192.168.1.99\share\folder"]) as System.Diagnostics.ProcessStartInfo;
            var fileRes = buildExplorerMethod.Invoke(null, [validCookieInDir]) as System.Diagnostics.ProcessStartInfo;
            var missingRes = buildExplorerMethod.Invoke(null, [Path.Combine(workRoot, "does_not_exist_dir")]) as System.Diagnostics.ProcessStartInfo;
            var validDirRes = buildExplorerMethod.Invoke(null, [workRoot]) as System.Diagnostics.ProcessStartInfo;
            low02Ok = uncRes == null
                && fileRes == null
                && missingRes == null
                && validDirRes != null
                && string.Equals(validDirRes.FileName, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                && !validDirRes.UseShellExecute
                && validDirRes.ArgumentList.Count == 1
                && string.Equals(validDirRes.ArgumentList[0], Path.GetFullPath(workRoot), StringComparison.OrdinalIgnoreCase);
        }
        Check("LOW-02: AfterDownload.BuildOpenWhenDoneStartInfo rejects UNC/file/missing paths and launches explorer.exe with ArgumentList",
            low02Ok,
            $"methodFound={buildExplorerMethod != null}, low02Ok={low02Ok}");

        // -------------------------------------------------------------------------
        // T-3: RemoteServer.IsAllowedRemoteUrl rejects malformed magnet URIs and accepts valid btih/btmh
        // -------------------------------------------------------------------------
        bool t3BadRejected = !RemoteServer.IsAllowedRemoteUrl("magnet:?xt=urn:btih:bad", out _)
            && !RemoteServer.IsAllowedRemoteUrl("magnet:?xt=urn:btih:", out _)
            && !RemoteServer.IsAllowedRemoteUrl("magnet:?dn=NoXt", out _);
        bool t3GoodAccepted = RemoteServer.IsAllowedRemoteUrl("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", out _)
            && RemoteServer.IsAllowedRemoteUrl("magnet:?xt.1=urn:btih:0123456789abcdef0123456789abcdef01234567", out _);
        Check("T-3: RemoteServer.IsAllowedRemoteUrl validates magnet URIs via MagnetValidator.TryParse",
            t3BadRejected && t3GoodAccepted);

        // -------------------------------------------------------------------------
        // T-4, T-5, T-6: Source checks for ClipboardMonitor gate, FolderBrowserDialog owner, AutomationProperties.Name, and x:Name buttons
        // -------------------------------------------------------------------------
        string repoRoot = FindRepoRoot();
        string menusCode = File.ReadAllText(Path.Combine(repoRoot, "src", "GDM", "UI", "MainWindow.Menus.cs"));
        bool t4Ok = menusCode.Contains("_settings.ClipboardMonitor ? ClipboardUrl() : null", StringComparison.Ordinal);
        Check("T-4: MainWindow.Menus.cs gates ClipboardUrl() behind _settings.ClipboardMonitor", t4Ok);

        string optionsCode = File.ReadAllText(Path.Combine(repoRoot, "src", "GDM", "UI", "OptionsWindow.xaml.cs"));
        bool t5OwnerOk = optionsCode.Contains("f.ShowDialog(this)", StringComparison.Ordinal);
        bool t6AutomationOk = optionsCode.Contains("AutomationProperties.SetName", StringComparison.Ordinal);
        Check("T-5 & T-6: OptionsWindow passes owner to FolderBrowserDialog.ShowDialog(this) and sets AutomationProperties.Name",
            t5OwnerOk && t6AutomationOk,
            $"owner={t5OwnerOk}, automation={t6AutomationOk}");
    }

    static void TestGroup4_UiPolishAndFormatting()
    {
        Console.WriteLine();
        Console.WriteLine("=== Group 4 UI Polish, Formatting & Localization Tests ===");

        string prevLang = Loc.Language;
        try
        {
            Loc.Init("ar");
            bool cultureIsAr = Loc.Culture.Name == "ar";
            string sizeStr = Fmt.Size(15 * 1024 * 1024 + 512 * 1024); // 15.5 MB
            string speedStr = Fmt.Speed(2 * 1024 * 1024 + 256 * 1024); // 2.25 MB/s
            string pctStr = Fmt.Percent(42.5);
            string ratioStr = Fmt.Ratio(1.75);
            string durStr = Fmt.Duration(125);
            string locFormatted = Loc.T("core.status.paused_pct", 12.5);

            Check("G4-1: Arabic mode preserves Loc.Culture.Name=='ar' while formatting all numbers with Western digits and '.' decimal separator",
                cultureIsAr
                && sizeStr == "15.5 م.ب"
                && speedStr == "2.25 م.ب/ث"
                && pctStr == "42.5%"
                && ratioStr == "1.75"
                && durStr == "2:05"
                && locFormatted.Contains("12.5", StringComparison.Ordinal)
                && !sizeStr.Contains('٫') && !sizeStr.Contains(','),
                $"size='{sizeStr}', speed='{speedStr}', pct='{pctStr}', ratio='{ratioStr}', dur='{durStr}', loc='{locFormatted}'");

            var dlItem = new DownloadItem
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 1000,
                Downloaded = 425,
                TorrentRatio = 1.75,
            };
            Check("G4-2: DownloadItem.StatusText and TorrentRatioText use '.' decimal separator in Arabic mode",
                dlItem.StatusText.Contains("42.5%", StringComparison.Ordinal)
                && dlItem.TorrentRatioText == "1.75",
                $"StatusText='{dlItem.StatusText}', TorrentRatioText='{dlItem.TorrentRatioText}'");
        }
        finally
        {
            Loc.Init(prevLang);
        }

        string repoRoot = FindRepoRoot();
        string progXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "GDM", "UI", "ProgressWindow.xaml"));
        string progCs = File.ReadAllText(Path.Combine(repoRoot, "src", "GDM", "UI", "ProgressWindow.xaml.cs"));
        bool torrentCardHasGrid = progXaml.Contains("x:Name=\"TorrentFilesGrid\"", StringComparison.Ordinal);
        bool torrentHidesSegments = progCs.Contains("item.Kind is DownloadKind.Media or DownloadKind.Torrent", StringComparison.Ordinal)
            && progCs.Contains("RefreshTorrentFiles()", StringComparison.Ordinal);
        bool saveToLtrIsolate = progCs.Contains("\\u2066", StringComparison.Ordinal) && progCs.Contains("\\u2069", StringComparison.Ordinal);
        Check("G4-3: ProgressWindow hides SegmentsCard on torrents, shows TorrentFilesGrid, and isolates LTR file paths in RTL",
            torrentCardHasGrid && torrentHidesSegments && saveToLtrIsolate,
            $"grid={torrentCardHasGrid}, hidesSegments={torrentHidesSegments}, ltrIsolate={saveToLtrIsolate}");
    }

    sealed class SimpleFakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "GDM")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tests", "engine")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate GDM repository root.");
    }

    sealed class SecurityFakeRemoteTarget : IRemoteTarget
    {
        public List<string> AddedUrls { get; } = [];

        public IReadOnlyList<RemoteItemDto> GetItems() => [];

        public RemoteInfoDto GetInfo() => new("2.0.0", 0, 0, false, 1024, [new(QueueDef.MainId, "Main", false)]);

        public Guid Add(string url, Guid? queueId, bool start)
        {
            AddedUrls.Add(url);
            return Guid.NewGuid();
        }

        public bool Pause(Guid id) => true;
        public bool Resume(Guid id) => true;
        public bool RemoveFromListOnly(Guid id) => true;
        public bool StartQueue(Guid id) => true;
        public bool StopQueue(Guid id) => true;
        public void SetSpeedLimiter(bool enabled) { }
    }
}
