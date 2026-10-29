using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Golan.Gdm.Core;

public static class Tests_P1
{
    static bool _hasRun;
    static int _lastFails;

    // [ModuleInitializer] (called from Program.cs instead: an async run inside a module initializer deadlocks)
    internal static void Init()
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        // cmdArgs[0] is the assembly path; standard invocation is: EngineTest.dll <servedDir> <outDir> <port> [--p1-only]
        bool p1Only = cmdArgs.Any(a => string.Equals(a, "--p1-only", StringComparison.OrdinalIgnoreCase));
        var posArgs = cmdArgs.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (posArgs.Length < 3 || !int.TryParse(posArgs[2], out int port)) return;

        string servedDir = posArgs[0];
        string outDir = posArgs[1];
        string baseUrl = $"http://127.0.0.1:{port}";

        int fails = RunAsync(baseUrl, servedDir, outDir, port).GetAwaiter().GetResult();
        if (p1Only)
        {
            Environment.Exit(fails);
        }
        else if (fails > 0)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (Environment.ExitCode == 0) Environment.ExitCode = fails;
            };
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        if (_hasRun) return _lastFails;
        _hasRun = true;

        int fails = 0, passes = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} {detail}".TrimEnd());
            if (ok) passes++; else fails++;
        }

        string p1OutDir = Path.Combine(outDir, "_p1_tmp");
        Directory.CreateDirectory(p1OutDir);

        var bigPath = Path.Combine(servedDir, "big.bin");
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        var expectedBigHash = Hash(bigPath);

        var defaultSettings = new AppSettings { ProxyMode = "none" };
        defaultSettings.Normalize();
        HttpDownloader.Configure(defaultSettings);
        var limiter = new SpeedLimiter();

        try
        {
            // ---------- P1-1a (Repro 1 / H-1a): Paused download .gdm preserved on same-name collision ----------
            {
                var itemA = new DownloadItem
                {
                    Url = $"{baseUrl}/big.bin",
                    SaveDir = p1OutDir,
                    FileName = "repro-collision.bin",
                    NameLocked = true,
                    MaxConnections = 4,
                };
                using (var cts = new CancellationTokenSource())
                {
                    var runA = new HttpDownloader(itemA, limiter).RunAsync(cts.Token);
                    while (itemA.Downloaded < 4 * 1024 * 1024) await Task.Delay(15);
                    cts.Cancel();
                    try { await runA; } catch (OperationCanceledException) { }
                }

                string itemATemp = itemA.TempPath;
                bool itemATempExistedBefore = File.Exists(itemATemp);
                long itemATempSizeBefore = itemATempExistedBefore ? new FileInfo(itemATemp).Length : 0;

                var itemB = new DownloadItem
                {
                    Url = $"{baseUrl}/big.bin",
                    SaveDir = p1OutDir,
                    FileName = "repro-collision.bin",
                    NameLocked = true,
                    MaxConnections = 4,
                };
                // Simulate DownloadManager.Add / RunAsync claiming itemB's initial FullPath
                PathRegistry.Claim(itemB.Id, itemB.FullPath);
                await new HttpDownloader(itemB, limiter).RunAsync(CancellationToken.None);

                bool itemATempIntact = File.Exists(itemATemp) && new FileInfo(itemATemp).Length >= itemATempSizeBefore && itemATempSizeBefore > 0;
                Check("P1-1a (H-1a): paused .gdm preserved and second item renamed",
                    itemATempIntact && itemB.FileName == "repro-collision (1).bin" && Hash(itemB.FullPath) == expectedBigHash,
                    $"itemATempIntact={itemATempIntact}, itemB={itemB.FileName}");

                PathRegistry.Release(itemA.Id);
                PathRegistry.Release(itemB.Id);
                try { File.Delete(itemATemp); } catch { }
            }

            // ---------- P1-1b (Repro 2 / H-1b): Active download collision gets numbered filename without IOException ----------
            {
                var itemA = new DownloadItem
                {
                    Url = $"{baseUrl}/big.bin",
                    SaveDir = p1OutDir,
                    FileName = "repro-active-collision.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                    SpeedLimitKB = 2048,
                };
                using var ctsA = new CancellationTokenSource();
                var runA = new HttpDownloader(itemA, limiter).RunAsync(ctsA.Token);
                while (itemA.Downloaded < 512 * 1024 || !File.Exists(itemA.TempPath)) await Task.Delay(15);

                var itemB = new DownloadItem
                {
                    Url = $"{baseUrl}/big.bin",
                    SaveDir = p1OutDir,
                    FileName = "repro-active-collision.bin",
                    NameLocked = true,
                    MaxConnections = 4,
                };
                PathRegistry.Claim(itemB.Id, itemB.FullPath);
                Exception? itemBEx = null;
                try
                {
                    await new HttpDownloader(itemB, limiter).RunAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    itemBEx = ex;
                }

                ctsA.Cancel();
                try { await runA; } catch (OperationCanceledException) { }

                Check("P1-1b (H-1b): active collision numbers second item without IOException",
                    itemBEx == null && itemB.FileName == "repro-active-collision (1).bin" && File.Exists(itemB.FullPath) && Hash(itemB.FullPath) == expectedBigHash,
                    itemBEx != null ? $"ex={itemBEx.GetType().Name}: {itemBEx.Message}" : $"itemB={itemB.FileName}");

                PathRegistry.Release(itemA.Id);
                PathRegistry.Release(itemB.Id);
                try { File.Delete(itemA.TempPath); } catch { }
            }

            // ---------- P1-2 (Repro 3 / H-2): Concurrent workers coordinate on presigned URL 403 expiration ----------
            // ---------- P1-3 (H-3): Dynamic segment splitting when worker finishes early ----------
            // ---------- P1-4 (H-4): Referer stripped on cross-host redirect and https->http downgrade ----------
            // ---------- P1-5 (SPEC-11): ProbeAsync and HasChangedAsync retry on transient 503 ----------
            await using (var auxServer = new P1AuxServer())
            {
                // P1-2: Multi-worker presigned URL expiration
                var expItem = new DownloadItem
                {
                    Url = $"{auxServer.BaseUrl}/presign-origin.bin",
                    SaveDir = p1OutDir,
                    FileName = "presigned-exp.bin",
                    NameLocked = true,
                    MaxConnections = 4,
                };
                Exception? expEx = null;
                try
                {
                    using var expCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await new HttpDownloader(expItem, limiter).RunAsync(expCts.Token);
                }
                catch (Exception ex)
                {
                    expEx = ex;
                }
                Check("P1-2 (H-2): concurrent workers coordinate on presigned 403 expiration",
                    expEx == null && File.Exists(expItem.FullPath) && Hash(expItem.FullPath) == auxServer.PayloadHash && auxServer.PresignOriginProbes == 2,
                    expEx != null ? $"ex={expEx.Message}" : $"probes={auxServer.PresignOriginProbes}");

                // P1-3: Dynamic segment splitting
                var splitItem = new DownloadItem
                {
                    Url = $"{auxServer.BaseUrl}/split.bin",
                    SaveDir = p1OutDir,
                    FileName = "split-out.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                var prevPiece = HttpDownloader.MinInitialPiece;
                HttpDownloader.MinInitialPiece = 256 * 1024; // the mechanism itself, on a small test file
                try
                {
                    using var splitCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await new HttpDownloader(splitItem, limiter).RunAsync(splitCts.Token);
                }
                finally { HttpDownloader.MinInitialPiece = prevPiece; }
                Check("P1-3 (H-3): dynamic segment splitting splits active segment when worker finishes",
                    auxServer.SplitMidRangeSeen && File.Exists(splitItem.FullPath) && Hash(splitItem.FullPath) == auxServer.PayloadHash,
                    $"splitMidSeen={auxServer.SplitMidRangeSeen}, rangeRequests={auxServer.SplitRangeCount}");

                // P1-4: Referer header security
                var sameHostRefItem = new DownloadItem
                {
                    Url = $"{auxServer.BaseUrl}/ref-check/same.bin",
                    Referrer = $"{auxServer.BaseUrl}/source-page.html",
                    SaveDir = p1OutDir,
                    FileName = "ref-same.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(sameHostRefItem, limiter).RunAsync(CancellationToken.None);

                var crossHostRefItem = new DownloadItem
                {
                    Url = $"{auxServer.BaseUrl}/ref-redirect?to=" + Uri.EscapeDataString($"http://localhost:{auxServer.Port}/ref-check/cross.bin"),
                    Referrer = $"{auxServer.BaseUrl}/secret-page?token=123",
                    SaveDir = p1OutDir,
                    FileName = "ref-cross.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(crossHostRefItem, limiter).RunAsync(CancellationToken.None);

                var downgradeRefItem = new DownloadItem
                {
                    Url = $"https://127.0.0.1:{auxServer.Port}/ref-check/downgrade.bin",
                    ResolvedUrl = $"{auxServer.BaseUrl}/ref-check/downgrade.bin",
                    Referrer = $"https://127.0.0.1:{auxServer.Port}/secure-page",
                    SaveDir = p1OutDir,
                    FileName = "ref-downgrade.bin",
                    NameLocked = true,
                    MaxConnections = 1,
                    Resumable = true,
                    TotalSize = auxServer.Payload.Length,
                    Segments = [new Segment { Start = 0, End = auxServer.Payload.Length - 1 }],
                };
                File.WriteAllBytes(downgradeRefItem.TempPath, []);
                await new HttpDownloader(downgradeRefItem, limiter).RunAsync(CancellationToken.None);

                Check("P1-4 (H-4): Referer sent on same-host but stripped on cross-host redirect and https->http downgrade",
                    auxServer.LastRefererFor("same.bin") == $"{auxServer.BaseUrl}/source-page.html" &&
                    string.IsNullOrEmpty(auxServer.LastRefererFor("cross.bin")) &&
                    string.IsNullOrEmpty(auxServer.LastRefererFor("downgrade.bin")),
                    $"same='{auxServer.LastRefererFor("same.bin")}', cross='{auxServer.LastRefererFor("cross.bin")}', downgrade='{auxServer.LastRefererFor("downgrade.bin")}'");

                // P1-5: ProbeAsync & HasChangedAsync retry on transient 503
                var probeRetryItem = new DownloadItem
                {
                    Url = $"{auxServer.BaseUrl}/flaky-probe.bin",
                    SaveDir = p1OutDir,
                };
                ProbeResult? probeRes = null;
                bool? hasChangedRes = null;
                try
                {
                    probeRes = await HttpDownloader.ProbeAsync(probeRetryItem, CancellationToken.None);
                    probeRetryItem.ETag = probeRes.ETag;
                    probeRetryItem.TotalSize = probeRes.Size;
                    auxServer.ResetFlakyProbe();
                    hasChangedRes = await HttpDownloader.HasChangedAsync(probeRetryItem, CancellationToken.None);
                }
                catch { }
                Check("P1-5 (SPEC-11): ProbeAsync and HasChangedAsync retry transient 503",
                    probeRes != null && probeRes.Size == auxServer.Payload.Length && hasChangedRes == false,
                    $"probeSize={probeRes?.Size}, hasChanged={hasChangedRes}");
            }

            // ---------- P1-6 (Repro 4 / H-5): Paused TempPath preserved across TempRoot change ----------
            {
                string prevTempRoot = DownloadItem.TempRoot;
                string newTempRoot = Path.Combine(p1OutDir, "custom_temp_root");
                Directory.CreateDirectory(newTempRoot);
                try
                {
                    DownloadItem.TempRoot = "";
                    var itemT = new DownloadItem
                    {
                        Url = $"{baseUrl}/big.bin",
                        SaveDir = p1OutDir,
                        FileName = "repro-temproot.bin",
                        NameLocked = true,
                        MaxConnections = 4,
                    };
                    using (var cts = new CancellationTokenSource())
                    {
                        var runT = new HttpDownloader(itemT, limiter).RunAsync(cts.Token);
                        while (itemT.Downloaded < 4 * 1024 * 1024) await Task.Delay(15);
                        cts.Cancel();
                        try { await runT; } catch (OperationCanceledException) { }
                    }

                    string originalTempPath = itemT.TempPath;
                    bool isGdmTemp = originalTempPath.EndsWith(".gdm", StringComparison.OrdinalIgnoreCase);
                    long pausedDownloaded = itemT.Downloaded;
                    DownloadItem.TempRoot = newTempRoot;

                    bool existsAfterTempRootChange = File.Exists(itemT.TempPath) &&
                        string.Equals(itemT.TempPath, originalTempPath, StringComparison.OrdinalIgnoreCase);

                    await new HttpDownloader(itemT, limiter).RunAsync(CancellationToken.None);

                    Check("P1-6 (H-5): paused TempPath (.gdm) preserved when TempRoot changes and resumes cleanly",
                        isGdmTemp && existsAfterTempRootChange && pausedDownloaded > 0 && File.Exists(itemT.FullPath) && Hash(itemT.FullPath) == expectedBigHash && !File.Exists(originalTempPath),
                        $"isGdm={isGdmTemp}, existsAfterChange={existsAfterTempRootChange}, tempPath={itemT.TempPath}");

                    // P1-6b: Legacy partial file extension is ignored and download uses .gdm cleanly
                    DownloadItem.TempRoot = "";
                    var itemLegacy = new DownloadItem
                    {
                        Url = $"{baseUrl}/big.bin",
                        SaveDir = p1OutDir,
                        FileName = "repro-no-legacy-ext.bin",
                        NameLocked = true,
                        MaxConnections = 4,
                    };
                    string legacyPart = itemLegacy.FullPath + ".\u0061dam";
                    File.WriteAllBytes(legacyPart, [1, 2, 3]);
                    itemLegacy.ActiveTempPath = "";
                    bool ignoredLegacyPart = !string.Equals(itemLegacy.TempPath, legacyPart, StringComparison.OrdinalIgnoreCase)
                                             && itemLegacy.TempPath.EndsWith(".gdm", StringComparison.OrdinalIgnoreCase);
                    await new HttpDownloader(itemLegacy, limiter).RunAsync(CancellationToken.None);
                    Check("P1-6b: legacy partial file is ignored and download completes using .gdm",
                        ignoredLegacyPart && File.Exists(itemLegacy.FullPath) && Hash(itemLegacy.FullPath) == expectedBigHash && File.Exists(legacyPart),
                        $"ignoredLegacyPart={ignoredLegacyPart}");
                }
                finally
                {
                    DownloadItem.TempRoot = prevTempRoot;
                }
            }

            // ---------- P1-7 (CRITICAL-2 / H-2): DownloadItem.CookiesFor delegates to CookiesForRequest ----------
            {
                var emptyHostCookie = new DownloadItem
                {
                    Url = "https://site.com/file.zip",
                    Cookies = "session=secret",
                    CookieHost = "",
                };
                var validHostCookie = new DownloadItem
                {
                    Url = "https://site.com/file.zip",
                    Cookies = "session=secret",
                    CookieHost = "site.com",
                };
                Check("P1-7 (H-2): CookiesFor refuses empty CookieHost, cross-host, and https->http downgrade",
                    emptyHostCookie.CookiesFor(new Uri("https://site.com/file.zip")) == "" &&
                    validHostCookie.CookiesFor(new Uri("https://site.com/file.zip")) == "session=secret" &&
                    validHostCookie.CookiesFor(new Uri("https://sub.site.com/file.zip")) == "" &&
                    validHostCookie.CookiesFor(new Uri("http://site.com/file.zip")) == "");
            }

            // ---------- P1-8 & P1-9 (Repro 12 / H-6 / M6 / STD-5 / STD-7): Localization & TextDir ----------
            {
                Loc.Init("en");
                bool rtlEmptyEn = TextDir.IsRtl("");
                bool rtlDigitsEn = TextDir.IsRtl("12345 (1).bin");
                bool rtlArabicText = TextDir.IsRtl("ملف");
                bool rtlLatinText = TextDir.IsRtl("file");

                string err404En = "";
                try
                {
                    await new HttpDownloader(new DownloadItem
                    {
                        Url = $"{baseUrl}/definitely-missing-p1.bin",
                        SaveDir = p1OutDir,
                        FileName = "missing-p1.bin",
                        NameLocked = true,
                    }, limiter).RunAsync(CancellationToken.None);
                }
                catch (DownloadException ex) { err404En = ex.Message; }

                string errUncEn = "";
                try
                {
                    await new HttpDownloader(new DownloadItem
                    {
                        Url = $"{baseUrl}/big.bin",
                        SaveDir = @"\\127.0.0.1\unconfigured_share\folder",
                        FileName = "unc-p1.bin",
                        NameLocked = true,
                    }, limiter).RunAsync(CancellationToken.None);
                }
                catch (DownloadException ex) { errUncEn = ex.Message; }

                bool hasArabicInEn404 = Regex.IsMatch(err404En, @"[\u0600-\u06FF]");
                bool hasArabicInEnUnc = Regex.IsMatch(errUncEn, @"[\u0600-\u06FF]");
                bool rawKeyReturned = err404En.StartsWith("core.", StringComparison.Ordinal) || errUncEn.StartsWith("core.", StringComparison.Ordinal);

                Check("P1-8 (H-6/M6): English mode throws English 404 and UNC errors with zero Arabic chars",
                    err404En.Contains("404") && errUncEn.Length > 0 && !hasArabicInEn404 && !hasArabicInEnUnc && !rawKeyReturned,
                    $"404='{err404En}', unc='{errUncEn}'");

                Loc.Init("ar");
                bool rtlEmptyAr = TextDir.IsRtl("");
                bool neutralArCulture = Loc.Culture.Name == "ar";
                string err404Ar = Loc.T("core.http.err_404");

                Check("P1-9 (STD-5/STD-7): TextDir.IsRtl fallback, neutral 'ar' Culture, and disk i18n loader",
                    !rtlEmptyEn && !rtlDigitsEn && rtlArabicText && !rtlLatinText && rtlEmptyAr && neutralArCulture && err404Ar.Contains("404") && Regex.IsMatch(err404Ar, @"[\u0600-\u06FF]"),
                    $"culture={Loc.Culture.Name}, rtlEmptyEn={rtlEmptyEn}, err404Ar='{err404Ar}'");
            }

            // ---------- Must-Keep #2: User-configured UNC root allowed by EnsureSafeLocalDir ----------
            {
                var uncSettings = new AppSettings
                {
                    ProxyMode = "none",
                    DownloadRoot = @"\\127.0.0.1\user_configured_share\Downloads",
                };
                uncSettings.Normalize();
                HttpDownloader.Configure(uncSettings);
                try
                {
                    var ensureMethod = typeof(HttpDownloader).GetMethod("EnsureSafeLocalDir", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                    bool configuredAllowed = true;
                    try { ensureMethod.Invoke(null, [@"\\127.0.0.1\user_configured_share\Downloads\Subfolder"]); }
                    catch { configuredAllowed = false; }

                    bool unconfiguredRejected = false;
                    try { ensureMethod.Invoke(null, [@"\\127.0.0.1\other_share\Downloads"]); }
                    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is DownloadException) { unconfiguredRejected = true; }

                    Check("P1 Must-Keep #2: user-configured UNC root allowed while unconfigured UNC rejected",
                        configuredAllowed && unconfiguredRejected);
                }
                finally
                {
                    HttpDownloader.Configure(defaultSettings);
                }
            }
        }
        finally
        {
            Loc.Init("ar");
            DownloadItem.TempRoot = "";
            HttpDownloader.Configure(defaultSettings);
            try { if (Directory.Exists(p1OutDir)) Directory.Delete(p1OutDir, recursive: true); } catch { }
        }

        _lastFails = fails;
        return fails;
    }

    sealed class P1AuxServer : IAsyncDisposable
    {
        readonly TcpListener _listener;
        readonly CancellationTokenSource _cts = new();
        readonly Task _acceptLoop;
        readonly object _lock = new();
        readonly Dictionary<string, string> _lastReferer = new(StringComparer.OrdinalIgnoreCase);
        readonly ManualResetEventSlim _splitMidRequested = new(false);
        int _presignV1Hits;
        int _flakyProbeHits;

        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";
        public byte[] Payload { get; }
        public string PayloadHash { get; }
        public int PresignOriginProbes { get; private set; }
        public bool SplitMidRangeSeen => _splitMidRequested.IsSet;
        public int SplitRangeCount { get; private set; }

        public P1AuxServer()
        {
            Payload = new byte[2 * 1024 * 1024]; // 2 MB (>= 8 * MinSplit)
            Random.Shared.NextBytes(Payload);
            PayloadHash = Convert.ToHexString(SHA256.HashData(Payload));

            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string? LastRefererFor(string name)
        {
            lock (_lock) return _lastReferer.GetValueOrDefault(name);
        }

        public void ResetFlakyProbe() => Interlocked.Exchange(ref _flakyProbeHits, 0);

        async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch { break; }
            }
        }

        async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var reqText = await ReadHeadersAsync(stream).ConfigureAwait(false);
                if (string.IsNullOrEmpty(reqText)) return;

                var lines = reqText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                var rawTarget = parts[1];
                var uri = new Uri($"http://127.0.0.1:{Port}{rawTarget}");
                var path = uri.AbsolutePath;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int idx = line.IndexOf(':');
                    if (idx > 0) headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }

                if (path == "/presign-origin.bin")
                {
                    int probeNum;
                    lock (_lock) probeNum = ++PresignOriginProbes;
                    string target = probeNum == 1 ? $"{BaseUrl}/presign-v1.bin" : $"{BaseUrl}/presign-v2.bin";
                    await WriteEmptyResponseAsync(stream, 302, "Found", new Dictionary<string, string> { ["Location"] = target }).ConfigureAwait(false);
                    return;
                }

                if (path == "/presign-v1.bin")
                {
                    var (start, end, hasRange) = ParseRange(headers.GetValueOrDefault("Range"), Payload.Length);
                    if (hasRange && start == 0 && end == 0)
                    {
                        await WriteRangeResponseAsync(stream, 0, 0, Payload.AsMemory(0, 1), "presigned-exp.bin").ConfigureAwait(false);
                        return;
                    }
                    int hit = Interlocked.Increment(ref _presignV1Hits);
                    if (hit <= 4)
                    {
                        // Give all 4 workers time to open their initial segment connections, then drop mid-stream
                        await Task.Delay(60).ConfigureAwait(false);
                        return;
                    }
                    // Subsequent segment retries on v1 get 403 Expired simultaneously
                    await Task.Delay(30).ConfigureAwait(false);
                    await WriteEmptyResponseAsync(stream, 403, "Forbidden").ConfigureAwait(false);
                    return;
                }

                if (path == "/presign-v2.bin")
                {
                    var (start, end, _) = ParseRange(headers.GetValueOrDefault("Range"), Payload.Length);
                    int len = (int)(end - start + 1);
                    await WriteRangeResponseAsync(stream, start, end, Payload.AsMemory((int)start, len), "presigned-exp.bin").ConfigureAwait(false);
                    return;
                }

                if (path == "/split.bin")
                {
                    var (start, end, hasRange) = ParseRange(headers.GetValueOrDefault("Range"), Payload.Length);
                    if (hasRange && !(start == 0 && end == 0))
                    {
                        lock (_lock) SplitRangeCount++;
                        int half = Payload.Length / 2;
                        if (start > half)
                        {
                            _splitMidRequested.Set();
                        }
                        else if (start == half)
                        {
                            // Slow second initial segment: send first 64 KB then wait up to 2.5s for dynamic split
                            int totalLen = (int)(end - start + 1);
                            string hdr = $"HTTP/1.1 206 Partial Content\r\n" +
                                         $"Content-Range: bytes {start}-{end}/{Payload.Length}\r\n" +
                                         $"Content-Length: {totalLen}\r\n" +
                                         $"Accept-Ranges: bytes\r\n" +
                                         $"ETag: \"split-etag\"\r\n" +
                                         $"Connection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr)).ConfigureAwait(false);
                            int firstChunk = Math.Min(65536, totalLen);
                            await stream.WriteAsync(Payload.AsMemory((int)start, firstChunk)).ConfigureAwait(false);
                            await stream.FlushAsync().ConfigureAwait(false);
                            _splitMidRequested.Wait(TimeSpan.FromMilliseconds(2000));
                            if (totalLen > firstChunk)
                            {
                                await stream.WriteAsync(Payload.AsMemory((int)start + firstChunk, totalLen - firstChunk)).ConfigureAwait(false);
                            }
                            return;
                        }
                    }
                    int sliceLen = (int)(end - start + 1);
                    await WriteRangeResponseAsync(stream, start, end, Payload.AsMemory((int)start, sliceLen), "split-out.bin", "\"split-etag\"").ConfigureAwait(false);
                    return;
                }

                if (path == "/ref-redirect")
                {
                    var to = Uri.UnescapeDataString(uri.Query.TrimStart('?').Replace("to=", ""));
                    await WriteEmptyResponseAsync(stream, 302, "Found", new Dictionary<string, string> { ["Location"] = to }).ConfigureAwait(false);
                    return;
                }

                if (path.StartsWith("/ref-check/", StringComparison.OrdinalIgnoreCase))
                {
                    string file = path["/ref-check/".Length..];
                    lock (_lock) _lastReferer[file] = headers.GetValueOrDefault("Referer") ?? "";
                    var (start, end, _) = ParseRange(headers.GetValueOrDefault("Range"), Payload.Length);
                    int len = (int)(end - start + 1);
                    await WriteRangeResponseAsync(stream, start, end, Payload.AsMemory((int)start, len), file).ConfigureAwait(false);
                    return;
                }

                if (path == "/flaky-probe.bin")
                {
                    if (Interlocked.Increment(ref _flakyProbeHits) == 1)
                    {
                        await WriteEmptyResponseAsync(stream, 503, "Service Unavailable", new Dictionary<string, string> { ["Retry-After"] = "1" }).ConfigureAwait(false);
                        return;
                    }
                    var (start, end, _) = ParseRange(headers.GetValueOrDefault("Range"), Payload.Length);
                    int len = (int)(end - start + 1);
                    await WriteRangeResponseAsync(stream, start, end, Payload.AsMemory((int)start, len), "flaky-probe.bin").ConfigureAwait(false);
                    return;
                }

                await WriteEmptyResponseAsync(stream, 404, "Not Found").ConfigureAwait(false);
            }
        }

        static (long start, long end, bool hasRange) ParseRange(string? rangeHeader, int totalLength)
        {
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                var m = Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
                if (m.Success)
                {
                    long s = long.Parse(m.Groups[1].Value);
                    long e = m.Groups[2].Length > 0 ? long.Parse(m.Groups[2].Value) : totalLength - 1;
                    return (s, e, true);
                }
            }
            return (0, totalLength - 1, false);
        }

        static async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (sb.Length < 16384)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, 1)).ConfigureAwait(false);
                if (n == 0) break;
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 && sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n')
                    break;
            }
            return sb.ToString();
        }

        static async Task WriteEmptyResponseAsync(NetworkStream stream, int code, string reason, Dictionary<string, string>? extraHeaders = null)
        {
            var sb = new StringBuilder($"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n");
            if (extraHeaders != null)
                foreach (var (k, v) in extraHeaders) sb.Append($"{k}: {v}\r\n");
            sb.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
        }

        async Task WriteRangeResponseAsync(NetworkStream stream, long start, long end, ReadOnlyMemory<byte> data, string fileName, string etag = "\"p1-etag\"")
        {
            string hdr = $"HTTP/1.1 206 Partial Content\r\n" +
                         $"Content-Range: bytes {start}-{end}/{Payload.Length}\r\n" +
                         $"Content-Length: {data.Length}\r\n" +
                         $"Content-Type: application/octet-stream\r\n" +
                         $"Content-Disposition: attachment; filename=\"{fileName}\"\r\n" +
                         $"Accept-Ranges: bytes\r\n" +
                         $"ETag: {etag}\r\n" +
                         $"Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr)).ConfigureAwait(false);
            await stream.WriteAsync(data).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
            _splitMidRequested.Dispose();
            _cts.Dispose();
        }
    }
}
