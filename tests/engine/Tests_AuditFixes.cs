using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Resolvers;
using Golan.Gdm.Core.Torrent;
using Golan.Gdm.Integration;

public static class Tests_AuditFixes
{
    static int _passes;
    static int _fails;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [AUDIT] {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
        if (ok) _passes++; else _fails++;
    }

    static string HashBytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    sealed class DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    sealed class ThrowingBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Body must not be buffered when ResponseHeadersRead is used.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new InvalidOperationException("Body must not be buffered when ResponseHeadersRead is used."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class LoopbackHttpProxy : IDisposable
    {
        readonly TcpListener _listener;
        readonly CancellationTokenSource _cts = new();
        readonly string _body;
        public int Port { get; }
        public int Hits;

        public LoopbackHttpProxy(string body)
        {
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(client));
                }
                catch
                {
                    break;
                }
            }
        }

        async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buf = new byte[4096];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read)).ConfigureAwait(false);
                    if (n <= 0) return;
                    read += n;
                    if (Encoding.ASCII.GetString(buf, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                Interlocked.Increment(ref Hits);
                var reqLine = Encoding.ASCII.GetString(buf, 0, read).Split("\r\n")[0];
                bool isFirstCandidate = reqLine.Contains(".sha256 ", StringComparison.OrdinalIgnoreCase);
                byte[] bodyBytes = isFirstCandidate ? Encoding.UTF8.GetBytes(_body) : [];
                int code = isFirstCandidate ? 200 : 404;
                string reason = isFirstCandidate ? "OK" : "Not Found";
                string hdr = $"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr)).ConfigureAwait(false);
                if (bodyBytes.Length > 0)
                    await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore shutdown */ }
            _cts.Dispose();
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        _passes = 0;
        _fails = 0;
        Console.WriteLine("=== Audit Fixes Verification Suite (Bugs 1..12) ===");

        var defaultSettings = new AppSettings { ProxyMode = "none" };
        defaultSettings.Normalize();
        HttpDownloader.Configure(defaultSettings);
        var limiter = new SpeedLimiter();

        string afOutDir = Path.Combine(outDir, "_audit_fixes_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(afOutDir);

        // Prepare deterministic served fixtures
        string payloadPath = Path.Combine(servedDir, "audit-payload.bin");
        var rng = new Random(20261116);
        var payloadBytes = new byte[128 * 1024];
        rng.NextBytes(payloadBytes);
        File.WriteAllBytes(payloadPath, payloadBytes);
        string payloadHash = HashBytes(payloadBytes);

        string bigAuditPath = Path.Combine(servedDir, "audit-big.bin");
        var bigAuditBytes = new byte[2 * 1024 * 1024];
        rng.NextBytes(bigAuditBytes);
        File.WriteAllBytes(bigAuditPath, bigAuditBytes);
        string bigAuditHash = HashBytes(bigAuditBytes);

        string expAuditPath = Path.Combine(servedDir, "audit-exp.bin");
        var expAuditBytes = new byte[1536 * 1024]; // 1.5 MB (~1.2s paced on /expiring/)
        rng.NextBytes(expAuditBytes);
        File.WriteAllBytes(expAuditPath, expAuditBytes);
        string expAuditHash = HashBytes(expAuditBytes);

        string gzArchivePath = Path.Combine(servedDir, "audit-archive.tar.gz");
        byte[] gzRawBytes;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                var innerTar = new byte[64 * 1024];
                Array.Fill(innerTar, (byte)'T');
                gz.Write(innerTar, 0, innerTar.Length);
            }
            gzRawBytes = ms.ToArray();
        }
        File.WriteAllBytes(gzArchivePath, gzRawBytes);
        string gzRawHash = HashBytes(gzRawBytes);

        string sampleZipPath = Path.Combine(servedDir, "sample.zip");
        if (!File.Exists(sampleZipPath))
        {
            using var z = ZipFile.Open(sampleZipPath, ZipArchiveMode.Create);
            for (int i = 0; i < 25; i++)
            {
                var e = z.CreateEntry($"folder{i % 3}/file{i}.txt");
                using var w = new StreamWriter(e.Open());
                w.Write(new string('x', 1000 + i));
            }
        }

        string siteDir = Path.Combine(servedDir, "site");
        Directory.CreateDirectory(siteDir);
        File.WriteAllBytes(Path.Combine(siteDir, "grabbed_doc.pdf"), "%PDF-1.4 audit\n"u8.ToArray());

        try
        {
            // -----------------------------------------------------------------
            // Bug 1: StartFreshAsync preserves validated pre-resolved URL and falls back to _item.Url on failure
            // -----------------------------------------------------------------
            {
                var itemOk = new DownloadItem
                {
                    Url = $"{baseUrl}/html-page",
                    ResolvedUrl = $"{baseUrl}/audit-payload.bin",
                    SaveDir = afOutDir,
                    FileName = "af01-preresolved.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(itemOk, limiter).RunAsync(CancellationToken.None);

                var itemFallback = new DownloadItem
                {
                    Url = $"{baseUrl}/audit-payload.bin",
                    ResolvedUrl = $"{baseUrl}/missing-preresolved-404.bin",
                    SaveDir = afOutDir,
                    FileName = "af01-fallback.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(itemFallback, limiter).RunAsync(CancellationToken.None);

                Check("AF-01: StartFreshAsync uses validated pre-resolved URL before ResetProgress and falls back to Url on probe failure",
                    File.Exists(itemOk.FullPath) && HashFile(itemOk.FullPath) == payloadHash && itemOk.Url == $"{baseUrl}/html-page"
                    && File.Exists(itemFallback.FullPath) && HashFile(itemFallback.FullPath) == payloadHash);
            }

            // -----------------------------------------------------------------
            // Bug 2: Unexpected text/html confirm forms / meta-refresh followed; non-confirm HTML & cross-site rejected with err_web_page
            // -----------------------------------------------------------------
            {
                var confirmItem = new DownloadItem
                {
                    Url = $"{baseUrl}/download?mode=confirm",
                    SaveDir = afOutDir,
                    MaxConnections = 2,
                };
                await new HttpDownloader(confirmItem, limiter).RunAsync(CancellationToken.None);

                var refreshItem = new DownloadItem
                {
                    Url = $"{baseUrl}/download?mode=refresh",
                    SaveDir = afOutDir,
                    FileName = "af02-refresh.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(refreshItem, limiter).RunAsync(CancellationToken.None);

                var errHtmlItem = new DownloadItem
                {
                    Url = $"{baseUrl}/download?mode=error",
                    SaveDir = afOutDir,
                    MaxConnections = 1,
                };
                DownloadException? errHtmlEx = null;
                try { await new HttpDownloader(errHtmlItem, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { errHtmlEx = ex; }

                var crossHtmlItem = new DownloadItem
                {
                    Url = $"{baseUrl}/download?mode=cross",
                    SaveDir = afOutDir,
                    MaxConnections = 1,
                };
                DownloadException? crossHtmlEx = null;
                try { await new HttpDownloader(crossHtmlItem, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { crossHtmlEx = ex; }

                string expectedWebErr = Loc.T("core.http.err_web_page", "127.0.0.1");
                Check("AF-02: Extensionless text/html follows same-site confirm form & meta-refresh and rejects HTML login/cross-site pages",
                    confirmItem.FileName == "confirmed_report.bin" && HashFile(confirmItem.FullPath) == payloadHash
                    && HashFile(refreshItem.FullPath) == payloadHash
                    && errHtmlEx != null && errHtmlEx.Message == expectedWebErr && !File.Exists(errHtmlItem.FullPath)
                    && crossHtmlEx != null && crossHtmlEx.Message == expectedWebErr && !File.Exists(crossHtmlItem.FullPath),
                    $"confirmName={confirmItem.FileName}, errEx={errHtmlEx?.Message}");
            }

            // -----------------------------------------------------------------
            // Bug 3: Content-Encoding: deflate (zlib RFC 1950 + raw RFC 1951) and raw .gz/.tgz preservation
            // -----------------------------------------------------------------
            {
                var zlibItem = new DownloadItem
                {
                    Url = $"{baseUrl}/deflate-zlib/audit-payload.bin",
                    SaveDir = afOutDir,
                    FileName = "af03-zlib.bin",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await new HttpDownloader(zlibItem, limiter).RunAsync(CancellationToken.None);

                var rawDefItem = new DownloadItem
                {
                    Url = $"{baseUrl}/deflate-raw/audit-payload.bin",
                    SaveDir = afOutDir,
                    FileName = "af03-raw.bin",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await new HttpDownloader(rawDefItem, limiter).RunAsync(CancellationToken.None);

                var gzSingleItem = new DownloadItem
                {
                    Url = $"{baseUrl}/gzip-archive/audit-archive.tar.gz",
                    SaveDir = afOutDir,
                    FileName = "af03-single.tar.gz",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await new HttpDownloader(gzSingleItem, limiter).RunAsync(CancellationToken.None);

                var gzSegItem = new DownloadItem
                {
                    Url = $"{baseUrl}/gzip-archive/audit-archive.tar.gz",
                    SaveDir = afOutDir,
                    FileName = "af03-seg.tar.gz",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(gzSegItem, limiter).RunAsync(CancellationToken.None);

                Check("AF-03: Content-Encoding deflate decodes both zlib (0x78) and raw deflate, and .tar.gz preserves raw gzip bytes",
                    HashFile(zlibItem.FullPath) == payloadHash
                    && HashFile(rawDefItem.FullPath) == payloadHash
                    && HashFile(gzSingleItem.FullPath) == gzRawHash
                    && HashFile(gzSegItem.FullPath) == gzRawHash);
            }

            // -----------------------------------------------------------------
            // Bug 4: Referer sent across allowed redirects & ZipPreview (strict-origin-when-cross-origin), Cookie/Auth host-locked, no downgrade
            // -----------------------------------------------------------------
            {
                var prevPiece = HttpDownloader.MinInitialPiece;
                HttpDownloader.MinInitialPiece = 256 * 1024;
                try
                {
                    var cdnItem = new DownloadItem
                    {
                        Url = $"{baseUrl}/redirect?to=" + Uri.EscapeDataString($"http://localhost:{port}/require-referer/audit-big.bin"),
                        Referrer = $"http://u:p@127.0.0.1:{port}/source/page?dl=1#frag",
                        Cookies = "session=secret",
                        CookieHost = "127.0.0.1",
                        Login = "user",
                        Password = "pass",
                        SaveDir = afOutDir,
                        FileName = "af04-cdn.bin",
                        NameLocked = true,
                        MaxConnections = 4,
                    };
                    await new HttpDownloader(cdnItem, limiter).RunAsync(CancellationToken.None);

                    var zipCdnItem = new DownloadItem
                    {
                        Url = $"{baseUrl}/redirect?to=" + Uri.EscapeDataString($"http://localhost:{port}/require-referer/sample.zip"),
                        Referrer = $"http://u:p@127.0.0.1:{port}/source/page?dl=1#frag",
                        Cookies = "session=secret",
                        CookieHost = "127.0.0.1",
                        Login = "user",
                        Password = "pass",
                    };
                    var zipEntries = await ZipPreview.ListAsync(zipCdnItem, CancellationToken.None);

                    string? sameRef = HttpDownloader.ReferrerForRequest("https://u:p@site.example/dl/page?id=7#top", new Uri("https://site.example/files/a.zip"));
                    string? crossRef = HttpDownloader.ReferrerForRequest("https://u:p@site.example/dl/page?id=7#top", new Uri("https://cdn.mirror.net/files/a.zip"));
                    string? downRef = HttpDownloader.ReferrerForRequest("https://site.example/dl/page", new Uri("http://site.example/files/a.zip"));

                    Check("AF-04: Referer sent across cross-host CDN redirect & ZipPreview while Cookie/Auth stay host-locked and downgrade strips Referer",
                        HashFile(cdnItem.FullPath) == bigAuditHash
                        && zipEntries.Count == 25
                        && sameRef == "https://site.example/dl/page?id=7"
                        && crossRef == "https://site.example/"
                        && downRef == null);
                }
                finally
                {
                    HttpDownloader.MinInitialPiece = prevPiece;
                }
            }

            // -----------------------------------------------------------------
            // Bug 5: Range: bytes=0-0 fallback (400/403/405/501), Dropbox & OneDrive HEAD/Range/416 fallbacks, SendFollowHttpsAsync ResponseHeadersRead
            // -----------------------------------------------------------------
            {
                var probe403Item = new DownloadItem { Url = $"{baseUrl}/reject-range/403/audit-payload.bin", SaveDir = afOutDir };
                var probe403 = await HttpDownloader.ProbeAsync(probe403Item, CancellationToken.None);

                var dl403 = new DownloadItem
                {
                    Url = $"{baseUrl}/reject-range/403/audit-payload.bin",
                    SaveDir = afOutDir,
                    FileName = "af05-403.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(dl403, limiter).RunAsync(CancellationToken.None);

                var dl501 = new DownloadItem
                {
                    Url = $"{baseUrl}/reject-range/501/audit-payload.bin",
                    SaveDir = afOutDir,
                    FileName = "af05-501.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(dl501, limiter).RunAsync(CancellationToken.None);

                // DropboxResolver: HEAD -> 404, GET Range 0-0 -> 416 (zero-byte file), plain GET -> 200 OK (0 bytes)
                using var dbxZeroInvoker = new HttpMessageInvoker(new DelegateHttpHandler((req, _) =>
                {
                    if (req.Method == HttpMethod.Head)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req });
                    if (req.Headers.Range != null)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = req });
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = req,
                        Content = new ByteArrayContent([]),
                    };
                    resp.Content.Headers.ContentLength = 0;
                    resp.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"empty.txt\"" };
                    return Task.FromResult(resp);
                }));
                var dbxZero = await new DropboxResolver().ResolveAsync(new Uri("https://www.dropbox.com/s/z1/empty.txt?dl=0"), dbxZeroInvoker, CancellationToken.None);

                // OneDriveResolver: GET Range 0-0 -> 403, plain GET -> 200 OK; and zero-byte 416 -> plain GET -> 200 OK
                using var odInvoker = new HttpMessageInvoker(new DelegateHttpHandler((req, _) =>
                {
                    if (req.Headers.Range != null)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = req });
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = req,
                        Content = new ByteArrayContent(new byte[33]),
                    };
                    resp.Content.Headers.ContentLength = 33;
                    resp.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"od_doc.pdf\"" };
                    return Task.FromResult(resp);
                }));
                var odRes = await new OneDriveResolver().ResolveAsync(new Uri("https://1drv.ms/u/s!AbCdEfGh999"), odInvoker, CancellationToken.None);

                // HostResolvers.SendFollowHttpsAsync with HttpClient uses ResponseHeadersRead (does not read ThrowingBodyStream)
                bool headersReadOnly = false;
                using (var streamClient = new System.Net.Http.HttpClient(new DelegateHttpHandler((req, _) =>
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = req,
                        Content = new StreamContent(new ThrowingBodyStream()),
                    };
                    return Task.FromResult(resp);
                })))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://files.example.com/large.iso");
                    using var resp = await HostResolvers.SendFollowHttpsAsync(streamClient, req, CancellationToken.None);
                    headersReadOnly = resp.StatusCode == HttpStatusCode.OK;
                }

                Check("AF-05: Range 0-0 & HEAD fallbacks succeed on 403/404/416/501 in HttpDownloader, DropboxResolver, OneDriveResolver, and SendFollowHttpsAsync streams headers",
                    probe403.Size == payloadBytes.Length && !probe403.Resumable
                    && HashFile(dl403.FullPath) == payloadHash
                    && HashFile(dl501.FullPath) == payloadHash
                    && dbxZero is { FileName: "empty.txt", Size: 0 }
                    && odRes is { FileName: "od_doc.pdf", Size: 33 }
                    && headersReadOnly);
            }

            // -----------------------------------------------------------------
            // Bug 6: RFC 5987 charset decoding, ReadResponseMeta %25 + raw header recovery, FromUrl literal %, Windows ReservedNames
            // -----------------------------------------------------------------
            {
                string? isoName;
                using (var r1 = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) })
                {
                    r1.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename*=ISO-8859-1''caf%E9.pdf");
                    isoName = HttpDownloader.ExtractContentDispositionFileName(r1);
                }

                string? cp1256Name;
                using (var r2 = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) })
                {
                    r2.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename*=windows-1256''%CA%DE%D1%ED%D1.pdf");
                    cp1256Name = HttpDownloader.ExtractContentDispositionFileName(r2);
                }

                using var dbxPctInvoker = new HttpMessageInvoker(new DelegateHttpHandler((req, _) =>
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new ByteArrayContent([1]) };
                    resp.Content.Headers.ContentLength = 1;
                    resp.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename*=UTF-8''100%25_ok.zip");
                    return Task.FromResult(resp);
                }));
                var dbxPct = await new DropboxResolver().ResolveAsync(new Uri("https://www.dropbox.com/s/p1/raw?dl=0"), dbxPctInvoker, CancellationToken.None);

                using var dbxRawInvoker = new HttpMessageInvoker(new DelegateHttpHandler((req, _) =>
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new ByteArrayContent([1]) };
                    resp.Content.Headers.ContentLength = 1;
                    resp.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=unquoted report.zip");
                    return Task.FromResult(resp);
                }));
                var dbxRaw = await new DropboxResolver().ResolveAsync(new Uri("https://www.dropbox.com/s/p2/raw?dl=0"), dbxRawInvoker, CancellationToken.None);

                string fromLiteralPct = FileNames.FromUrl("https://example.com/dl/100%_done%20v2.zip");
                string[] extraReserved = ["COM0.txt", "LPT0", "COM\u00B9.bin", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3.log"];
                bool allReservedPrefixed = extraReserved.All(n => FileNames.Sanitize(n).StartsWith('_'));

                Check("AF-06: RFC 5987 decodes ISO-8859-1 & windows-1256, ReadResponseMeta keeps %25 & raw filenames, FromUrl keeps literal %, and ReservedNames blocks COM0/LPT0/superscripts",
                    isoName == "caf\u00E9.pdf"
                    && cp1256Name == "\u062A\u0642\u0631\u064A\u0631.pdf"
                    && dbxPct?.FileName == "100%_ok.zip"
                    && dbxRaw?.FileName == "unquoted report.zip"
                    && fromLiteralPct == "100%_done v2.zip"
                    && allReservedPrefixed,
                    $"iso={isoName}, cp1256={cp1256Name}, pct={dbxPct?.FileName}, raw={dbxRaw?.FileName}, url={fromLiteralPct}");
            }

            // -----------------------------------------------------------------
            // Bug 7 (C# side): FileNames.ShouldLockBrowserFileName skips generic placeholders and locks real names
            // -----------------------------------------------------------------
            {
                bool skipsPlaceholders =
                    !FileNames.ShouldLockBrowserFileName(null, "https://example.com/a.zip")
                    && !FileNames.ShouldLockBrowserFileName("download", "https://example.com/dl?id=5")
                    && !FileNames.ShouldLockBrowserFileName("download (2)", "https://example.com/dl?id=5")
                    && !FileNames.ShouldLockBrowserFileName("unknown", "https://example.com/dl?id=5")
                    && !FileNames.ShouldLockBrowserFileName("uc", "https://drive.google.com/uc?id=abc")
                    && !FileNames.ShouldLockBrowserFileName("setup.exe.crdownload", "https://example.com/setup.exe")
                    && !FileNames.ShouldLockBrowserFileName("blob_9912", "https://example.com/files/blob_9912");

                bool locksRealNames =
                    FileNames.ShouldLockBrowserFileName("quarterly-report.pdf", "https://example.com/dl?id=5")
                    && FileNames.ShouldLockBrowserFileName("GolanSetup (1).exe", "https://example.com/dl?id=5")
                    && FileNames.ShouldLockBrowserFileName("Makefile", "https://example.com/dl?id=5");

                Check("AF-07: FileNames.ShouldLockBrowserFileName rejects generic/extensionless placeholders and locks real browser filenames",
                    skipsPlaceholders && locksRealNames);
            }

            // -----------------------------------------------------------------
            // Bug 8: HostGate slot accounting does not inflate on uncontended releases and stays thread-safe under contention/cancellation
            // -----------------------------------------------------------------
            {
                string gateHost = "af08-gate-" + Guid.NewGuid().ToString("N")[..8] + ".test";
                for (int i = 0; i < 50; i++)
                {
                    using var s = await HostGate.AcquireAsync(gateHost, limit: 2, CancellationToken.None);
                }

                var held1 = await HostGate.AcquireAsync(gateHost, limit: 2, CancellationToken.None);
                var held2 = await HostGate.AcquireAsync(gateHost, limit: 2, CancellationToken.None);

                using var cts3 = new CancellationTokenSource();
                var blocked3 = HostGate.AcquireAsync(gateHost, limit: 2, cts3.Token);
                await Task.Delay(60);
                bool blockedProperly = !blocked3.IsCompleted;

                cts3.Cancel();
                bool cancelledCleanly = false;
                try { await blocked3; } catch (OperationCanceledException) { cancelledCleanly = true; }

                var waiter4 = HostGate.AcquireAsync(gateHost, limit: 2, CancellationToken.None);
                await Task.Delay(30);
                bool waiter4BlockedBeforeRelease = !waiter4.IsCompleted;
                held1.Dispose();
                using var acquired4 = await waiter4.WaitAsync(TimeSpan.FromSeconds(3));
                held2.Dispose();
                acquired4.Dispose();

                int activeInGate = 0, maxObservedInGate = 0;
                var parallelTasks = Enumerable.Range(0, 16).Select(async _ =>
                {
                    using var slot = await HostGate.AcquireAsync(gateHost, limit: 2, CancellationToken.None);
                    int cur = Interlocked.Increment(ref activeInGate);
                    int prevMax;
                    do
                    {
                        prevMax = Volatile.Read(ref maxObservedInGate);
                        if (cur <= prevMax) break;
                    } while (Interlocked.CompareExchange(ref maxObservedInGate, cur, prevMax) != prevMax);
                    await Task.Delay(10);
                    Interlocked.Decrement(ref activeInGate);
                });
                await Task.WhenAll(parallelTasks);

                Check("AF-08: HostGate never inflates slot count across uncontended releases, handles cancellation, and enforces max concurrency",
                    blockedProperly && cancelledCleanly && waiter4BlockedBeforeRelease && maxObservedInGate <= 2,
                    $"blocked={blockedProperly}, cancelled={cancelledCleanly}, maxInGate={maxObservedInGate}");
            }

            // -----------------------------------------------------------------
            // Bug 9: Simultaneous 429 across all segment workers retires N-1 atomically and keeps >= 1 worker alive to finish
            // -----------------------------------------------------------------
            {
                var prevPiece = HttpDownloader.MinInitialPiece;
                HttpDownloader.MinInitialPiece = 256 * 1024;
                HttpDownloader.ForgetLearnedLimits();
                try
                {
                    var burstItem = new DownloadItem
                    {
                        Url = $"{baseUrl}/burst429/af09/audit-big.bin",
                        SaveDir = afOutDir,
                        FileName = "af09-burst429.bin",
                        NameLocked = true,
                        MaxConnections = 4,
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await new HttpDownloader(burstItem, limiter).RunAsync(cts.Token);

                    Check("AF-09: Simultaneous 429 burst across 4 workers keeps at least 1 active worker alive and completes download",
                        File.Exists(burstItem.FullPath) && HashFile(burstItem.FullPath) == bigAuditHash,
                        $"learned={HttpDownloader.LearnedHostLimit("127.0.0.1")}");
                }
                finally
                {
                    HttpDownloader.MinInitialPiece = prevPiece;
                    HttpDownloader.ForgetLearnedLimits();
                }
            }

            // -----------------------------------------------------------------
            // Bug 10: Multiple presigned-URL expiries clear _reResolveTask, and ReResolveAsync re-runs HostResolvers for sharing links
            // -----------------------------------------------------------------
            {
                var prevPiece = HttpDownloader.MinInitialPiece;
                HttpDownloader.MinInitialPiece = 256 * 1024;
                try
                {
                    var multiExpItem = new DownloadItem
                    {
                        Url = $"{baseUrl}/multi-expiring/af10/audit-exp.bin",
                        SaveDir = afOutDir,
                        FileName = "af10-multiexp.bin",
                        NameLocked = true,
                        MaxConnections = 2,
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var dlTask = new HttpDownloader(multiExpItem, limiter).RunAsync(cts.Token);

                    async Task<long> TokBytesAsync(string tok)
                        => long.Parse(await HttpDownloader.Client.GetStringAsync($"{baseUrl}/tokbytes?token={tok}"));

                    var wait1 = Stopwatch.StartNew();
                    while (await TokBytesAsync("af10_1") == 0 && !dlTask.IsCompleted && wait1.ElapsedMilliseconds < 8000)
                        await Task.Delay(25);
                    await HttpDownloader.Client.GetStringAsync($"{baseUrl}/revoke?token=af10_1");

                    var wait2 = Stopwatch.StartNew();
                    while (await TokBytesAsync("af10_2") == 0 && !dlTask.IsCompleted && wait2.ElapsedMilliseconds < 8000)
                        await Task.Delay(25);
                    await HttpDownloader.Client.GetStringAsync($"{baseUrl}/revoke?token=af10_2");

                    await dlTask;
                    long tok3Bytes = await TokBytesAsync("af10_3");

                    // Sharing link re-resolution via HostResolvers during ReResolveAsync
                    int resolverHits = 0;
                    int cdnV1RangeHits = 0;
                    using var resolverInvoker = new HttpMessageInvoker(new DelegateHttpHandler((req, _) =>
                    {
                        Interlocked.Increment(ref resolverHits);
                        var resp = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://dl.dropboxusercontent.com/af10-v2/doc.bin"),
                            Content = new ByteArrayContent(payloadBytes),
                        };
                        resp.Content.Headers.ContentLength = payloadBytes.Length;
                        return Task.FromResult(resp);
                    }));

                    using var mockDownloadClient = new System.Net.Http.HttpClient(new DelegateHttpHandler((req, _) =>
                    {
                        var u = req.RequestUri!;
                        int total = payloadBytes.Length;
                        long s = 0, e = total - 1;
                        bool ranged = false;
                        if (req.Headers.Range?.Ranges.FirstOrDefault() is { } rh)
                        {
                            s = rh.From ?? 0;
                            e = rh.To ?? (total - 1);
                            ranged = true;
                        }
                        if (u.AbsolutePath.Contains("af10-v1", StringComparison.Ordinal))
                        {
                            if (ranged && !(s == 0 && e == 0))
                            {
                                if (Interlocked.Increment(ref cdnV1RangeHits) == 1)
                                {
                                    // Return truncated partial stream on first range call, then 403 on retry -> triggers ReResolveAsync
                                    int half = (int)Math.Min(16384, e - s + 1);
                                    var trunc = new HttpResponseMessage(HttpStatusCode.PartialContent)
                                    {
                                        RequestMessage = req,
                                        Content = new ByteArrayContent(payloadBytes.AsSpan((int)s, half).ToArray()),
                                    };
                                    trunc.Content.Headers.ContentRange = new ContentRangeHeaderValue(s, e, total);
                                    trunc.Headers.ETag = new EntityTagHeaderValue("\"af10-etag\"");
                                    return Task.FromResult(trunc);
                                }
                                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = req });
                            }
                        }
                        int len = (int)(e - s + 1);
                        var ok = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                        {
                            RequestMessage = req,
                            Content = new ByteArrayContent(payloadBytes.AsSpan((int)s, len).ToArray()),
                        };
                        if (ranged) ok.Content.Headers.ContentRange = new ContentRangeHeaderValue(s, e, total);
                        ok.Content.Headers.ContentLength = len;
                        ok.Headers.AcceptRanges.Add("bytes");
                        ok.Headers.ETag = new EntityTagHeaderValue("\"af10-etag\"");
                        return Task.FromResult(ok);
                    }));

                    string shareHash = "";
                    try
                    {
                        HttpDownloader.ResolverInvoker = resolverInvoker;
                        HttpDownloader.SetClientForTests(mockDownloadClient);
                        var shareItem = new DownloadItem
                        {
                            Url = "https://www.dropbox.com/s/af10share/doc.bin?dl=0",
                            ResolvedUrl = "https://dl.dropboxusercontent.com/af10-v1/doc.bin",
                            SaveDir = afOutDir,
                            FileName = "af10-share.bin",
                            NameLocked = true,
                            MaxConnections = 1,
                        };
                        await new HttpDownloader(shareItem, limiter).RunAsync(CancellationToken.None);
                        shareHash = HashFile(shareItem.FullPath);
                    }
                    finally
                    {
                        HttpDownloader.ResolverInvoker = null;
                        HttpDownloader.Configure(defaultSettings);
                    }

                    Check("AF-10: Multiple presigned expiries re-resolve repeatedly (af10_1 -> af10_2 -> af10_3) and sharing links re-run HostResolvers",
                        HashFile(multiExpItem.FullPath) == expAuditHash && tok3Bytes > 0
                        && resolverHits >= 1 && shareHash == payloadHash,
                        $"tok3Bytes={tok3Bytes}, resolverHits={resolverHits}");
                }
                finally
                {
                    HttpDownloader.MinInitialPiece = prevPiece;
                }
            }

            // -----------------------------------------------------------------
            // Bug 11: MegaDownloader verifies MAC and finishes without HTTP request when .gdm already reached TotalSize
            // -----------------------------------------------------------------
            {
                byte[] aesKey16 = RandomNumberGenerator.GetBytes(16);
                byte[] nonce8 = RandomNumberGenerator.GetBytes(8);
                byte[] plainMega = new byte[150 * 1024]; // spans 2 MEGA MAC chunks
                rng.NextBytes(plainMega);
                byte[] metaMac = MegaCrypto.ComputeMetaMac(plainMega, aesKey16, nonce8);

                byte[] rawKey32 = new byte[32];
                nonce8.CopyTo(rawKey32.AsSpan(16, 8));
                metaMac.CopyTo(rawKey32.AsSpan(24, 8));
                for (int i = 0; i < 16; i++)
                    rawKey32[i] = (byte)(aesKey16[i] ^ rawKey32[i + 16]);

                int megaHttpCalls = 0;
                using var zeroCallInvoker = new HttpMessageInvoker(new DelegateHttpHandler((_, _) =>
                {
                    Interlocked.Increment(ref megaHttpCalls);
                    throw new InvalidOperationException("MegaDownloader should not hit network when .gdm is already complete.");
                }));

                var megaDoneItem = new DownloadItem
                {
                    Url = "https://mega.nz/file/Af11Test",
                    ResolvedUrl = "https://gfs200.userstorage.mega.co.nz/dl/af11",
                    SaveDir = afOutDir,
                    FileName = "af11-mega.bin",
                    NameLocked = true,
                    TotalSize = plainMega.Length,
                };
                File.WriteAllBytes(megaDoneItem.TempPath, plainMega);

                await new MegaDownloader(megaDoneItem, rawKey32, limiter, zeroCallInvoker, verifyMac: true)
                    .RunAsync(CancellationToken.None);

                // Tampered complete .gdm must still fail MAC check and be deleted without hitting network
                var megaBadItem = new DownloadItem
                {
                    Url = "https://mega.nz/file/Af11Bad",
                    ResolvedUrl = "https://gfs200.userstorage.mega.co.nz/dl/af11bad",
                    SaveDir = afOutDir,
                    FileName = "af11-mega-bad.bin",
                    NameLocked = true,
                    TotalSize = plainMega.Length,
                };
                byte[] tamperedMega = (byte[])plainMega.Clone();
                tamperedMega[^1] ^= 0xFF;
                File.WriteAllBytes(megaBadItem.TempPath, tamperedMega);

                bool badRejected = false;
                try
                {
                    await new MegaDownloader(megaBadItem, rawKey32, limiter, zeroCallInvoker, verifyMac: true)
                        .RunAsync(CancellationToken.None);
                }
                catch (DownloadException ex) when (ex.Kind == FailKind.Mismatch)
                {
                    badRejected = true;
                }

                Check("AF-11: MegaDownloader finalizes already-complete .gdm after MAC verification with 0 HTTP calls and rejects tampered .gdm",
                    megaHttpCalls == 0
                    && File.Exists(megaDoneItem.FullPath)
                    && !File.Exists(megaDoneItem.TempPath)
                    && File.ReadAllBytes(megaDoneItem.FullPath).AsSpan().SequenceEqual(plainMega)
                    && badRejected && !File.Exists(megaBadItem.TempPath) && !File.Exists(megaBadItem.FullPath));
            }

            // -----------------------------------------------------------------
            // Bug 12: DetectLaunchingBrowser handle disposal, Torrent rate overflow cap, SiteGrabber gzip HTML, Integrity proxy
            // -----------------------------------------------------------------
            {
                // 12a: Process handle leak check in DetectLaunchingBrowser
                _ = ExtensionContact.DetectLaunchingBrowser();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                int handlesBefore;
                using (var p0 = Process.GetCurrentProcess()) handlesBefore = p0.HandleCount;
                for (int i = 0; i < 50; i++)
                    _ = ExtensionContact.DetectLaunchingBrowser();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                int handlesAfter;
                using (var p1 = Process.GetCurrentProcess()) handlesAfter = p1.HandleCount;
                int handleDelta = handlesAfter - handlesBefore;

                // 12b: TorrentService speed-limit int overflow protection
                int rateZero = TorrentService.ComputeMaxRateBytesPerSec(0);
                int rate1M = TorrentService.ComputeMaxRateBytesPerSec(1024);
                int rate2G = TorrentService.ComputeMaxRateBytesPerSec(2_097_152);
                int rateHuge = TorrentService.ComputeMaxRateBytesPerSec(10_000_000);
                var engSettings = TorrentService.BuildEngineSettings(new AppSettings
                {
                    SpeedLimitEnabled = true,
                    SpeedLimitKB = 5_000_000,
                });

                // 12c: SiteGrabber decompresses gzip-encoded HTML pages
                var grabProj = new GrabberProject
                {
                    StartUrl = $"{baseUrl}/gzip-html-page",
                    LevelsBase = 1,
                    FileMasks = "*.pdf",
                    SearchThisSiteOnly = true,
                };
                var grabber = new SiteGrabber(grabProj);
                var grabbed = new List<GrabbedFile>();
                grabber.FileFound += f => { lock (grabbed) grabbed.Add(f); };
                await grabber.RunAsync(CancellationToken.None);

                // 12d: Integrity.DiscoverHashAsync honours configured HTTP proxy via HttpDownloader.Configure
                string expectedIsoHex = new string('d', 64);
                HashSpec? proxyDiscovered;
                int proxyHits;
                using (var proxy = new LoopbackHttpProxy($"{expectedIsoHex}  release.iso\n"))
                {
                    var proxySettings = new AppSettings
                    {
                        ProxyMode = "manual",
                        ProxyType = "http",
                        ProxyHost = "127.0.0.1",
                        ProxyPort = proxy.Port,
                        ProxyBypass = "",
                    };
                    proxySettings.Normalize();
                    HttpDownloader.Configure(proxySettings);
                    try
                    {
                        proxyDiscovered = await Integrity.DiscoverHashAsync(
                            "http://sums-origin.invalid/pub/release.iso",
                            "release.iso",
                            client: null,
                            ct: CancellationToken.None);
                        proxyHits = Volatile.Read(ref proxy.Hits);
                    }
                    finally
                    {
                        HttpDownloader.Configure(defaultSettings);
                    }
                }

                Check("AF-12: DetectLaunchingBrowser disposes Process handles, Torrent rate caps at int.MaxValue, SiteGrabber reads gzip HTML, and Integrity honours proxy",
                    handleDelta < 25
                    && rateZero == 0 && rate1M == 1_048_576 && rate2G == int.MaxValue && rateHuge == int.MaxValue
                    && engSettings.MaximumDownloadRate == int.MaxValue
                    && grabbed.Any(f => f.Name == "grabbed_doc.pdf")
                    && proxyHits >= 1 && proxyDiscovered != null && proxyDiscovered.Hex == expectedIsoHex,
                    $"handleDelta={handleDelta}, rateHuge={rateHuge}, grabbed={grabbed.Count}, proxyHits={proxyHits}");
            }
        }
        finally
        {
            HttpDownloader.ResolverInvoker = null;
            HttpDownloader.Configure(defaultSettings);
            HttpDownloader.ForgetLearnedLimits();
            DownloadItem.TempRoot = "";
            Loc.Init("ar");
            try { if (Directory.Exists(afOutDir)) Directory.Delete(afOutDir, recursive: true); } catch { /* ignore cleanup */ }
        }

        Console.WriteLine($"[AUDIT Summary] {_passes} passed, {_fails} failed");
        return _fails;
    }
}
