using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Resolvers;
using Golan.Gdm.Integration;

public static class Tests_ReviewFixes
{
    static int _passes;
    static int _fails;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [REVIEW-FIXES] {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
        if (ok) _passes++; else _fails++;
    }

    static string HashBytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    static async Task RunCheckAsync(string id, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Check($"{id}: unhandled exception", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    sealed class DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    /// <summary>Forwards public https://&lt;host&gt;/&lt;path&gt; resolver requests to local testserver.py /fakehost/&lt;host&gt;/&lt;path&gt;.</summary>
    sealed class TestServerFakeHostHandler(string baseUrl) : HttpMessageHandler
    {
        readonly HttpMessageInvoker _loopback = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        }, disposeHandler: true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var orig = request.RequestUri ?? throw new InvalidOperationException("Missing RequestUri");
            var forwardUri = new Uri($"{baseUrl}/fakehost/{orig.Host.ToLowerInvariant()}{orig.PathAndQuery}");
            using var fwd = new HttpRequestMessage(request.Method, forwardUri);
            foreach (var h in request.Headers)
                fwd.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (request.Content != null)
            {
                var body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                fwd.Content = new ByteArrayContent(body);
                foreach (var ch in request.Content.Headers)
                    fwd.Content.Headers.TryAddWithoutValidation(ch.Key, ch.Value);
            }

            var resp = await _loopback.SendAsync(fwd, cancellationToken).ConfigureAwait(false);
            resp.RequestMessage = request;
            return resp;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _loopback.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Stream that yields a prefix of bytes and then throws an IOException to simulate mid-segment connection drop.</summary>
    sealed class PartialThenThrowStream(byte[] prefix) : Stream
    {
        int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < prefix.Length)
            {
                int take = Math.Min(count, prefix.Length - _pos);
                Buffer.BlockCopy(prefix, _pos, buffer, offset, take);
                _pos += take;
                return take;
            }
            throw new IOException("Simulated mid-segment socket drop");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos < prefix.Length)
            {
                int take = Math.Min(buffer.Length, prefix.Length - _pos);
                prefix.AsMemory(_pos, take).CopyTo(buffer);
                _pos += take;
                return ValueTask.FromResult(take);
            }
            return ValueTask.FromException<int>(new IOException("Simulated mid-segment socket drop"));
        }
    }

    /// <summary>
    /// Configurable loopback passive FTP server that records command order, enforces RFC 3659 immediate-REST-before-RETR,
    /// and supports custom SIZE / overflow / unknown-size responses.
    /// </summary>
    sealed class ReviewFtpServer : IDisposable
    {
        public sealed record VirtualFile(byte[] Data, long? AnnouncedSize = null);

        readonly TcpListener _controlListener;
        readonly CancellationTokenSource _cts = new();
        readonly Dictionary<string, VirtualFile> _files;
        readonly string _requiredUser;
        readonly string _requiredPass;
        readonly object _cmdLock = new();
        readonly List<List<string>> _sessionCommands = [];

        public int Port { get; }

        public ReviewFtpServer(Dictionary<string, VirtualFile> files, string requiredUser = "anonymous", string requiredPass = "")
        {
            _files = files;
            _requiredUser = requiredUser;
            _requiredPass = requiredPass;
            _controlListener = new TcpListener(IPAddress.Loopback, 0);
            _controlListener.Start();
            Port = ((IPEndPoint)_controlListener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public IReadOnlyList<IReadOnlyList<string>> GetRecordedSessions()
        {
            lock (_cmdLock)
                return _sessionCommands.Select(s => (IReadOnlyList<string>)s.ToList()).ToList();
        }

        public void ClearRecordedSessions()
        {
            lock (_cmdLock) _sessionCommands.Clear();
        }

        async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _controlListener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch
                {
                    /* listener stopped */
                    break;
                }
            }
        }

        async Task HandleClientAsync(TcpClient control)
        {
            TcpListener? pasvListener = null;
            var commands = new List<string>();
            lock (_cmdLock) _sessionCommands.Add(commands);

            try
            {
                using (control)
                await using (var net = control.GetStream())
                using (var reader = new StreamReader(net, Encoding.UTF8, false, 4096, leaveOpen: true))
                await using (var writer = new StreamWriter(net, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
                {
                    await writer.WriteLineAsync("220 ReviewFtpServer Ready").ConfigureAwait(false);
                    string currentUser = "";
                    bool authed = false;
                    long restOffset = 0;

                    while (!_cts.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                        if (line == null) break;
                        int sp = line.IndexOf(' ');
                        string cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                        string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();

                        lock (_cmdLock)
                            commands.Add(cmd == "PASS" ? "PASS ***" : line);

                        // RFC 3659 section 5.3: REST must immediately precede RETR; any intervening command resets the marker.
                        if (cmd != "REST" && cmd != "RETR")
                            restOffset = 0;

                        switch (cmd)
                        {
                            case "USER":
                                currentUser = arg;
                                await writer.WriteLineAsync("331 Password required").ConfigureAwait(false);
                                break;
                            case "PASS":
                                if (string.Equals(currentUser, _requiredUser, StringComparison.Ordinal)
                                    && (string.IsNullOrEmpty(_requiredPass) || string.Equals(arg, _requiredPass, StringComparison.Ordinal)))
                                {
                                    authed = true;
                                    await writer.WriteLineAsync("230 Logged in").ConfigureAwait(false);
                                }
                                else
                                {
                                    await writer.WriteLineAsync("530 Login incorrect").ConfigureAwait(false);
                                }
                                break;
                            case "OPTS":
                            case "TYPE":
                                await writer.WriteLineAsync("200 OK").ConfigureAwait(false);
                                break;
                            case "SIZE":
                                if (!authed) { await writer.WriteLineAsync("530 Not logged in").ConfigureAwait(false); break; }
                                if (_files.TryGetValue(arg, out var vf))
                                {
                                    long sz = vf.AnnouncedSize ?? vf.Data.Length;
                                    if (sz >= 0)
                                        await writer.WriteLineAsync($"213 {sz.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
                                    else
                                        await writer.WriteLineAsync("550 SIZE unavailable").ConfigureAwait(false);
                                }
                                else
                                {
                                    await writer.WriteLineAsync("550 File not found").ConfigureAwait(false);
                                }
                                break;
                            case "REST":
                                if (!long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out restOffset) || restOffset < 0)
                                    restOffset = 0;
                                await writer.WriteLineAsync($"350 Restarting at {restOffset}").ConfigureAwait(false);
                                break;
                            case "EPSV":
                                pasvListener?.Stop();
                                pasvListener = new TcpListener(IPAddress.Loopback, 0);
                                pasvListener.Start();
                                int dataPort = ((IPEndPoint)pasvListener.LocalEndpoint).Port;
                                await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{dataPort}|)").ConfigureAwait(false);
                                break;
                            case "RETR":
                                if (!authed) { await writer.WriteLineAsync("530 Not logged in").ConfigureAwait(false); break; }
                                if (pasvListener == null || !_files.TryGetValue(arg, out var retrFile))
                                {
                                    await writer.WriteLineAsync("550 Cannot open file").ConfigureAwait(false);
                                    break;
                                }
                                await writer.WriteLineAsync("150 Opening BINARY mode data connection").ConfigureAwait(false);
                                using (var dataClient = await pasvListener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false))
                                await using (var dataStream = dataClient.GetStream())
                                {
                                    int start = (int)Math.Clamp(restOffset, 0, retrFile.Data.Length);
                                    restOffset = 0;
                                    if (start < retrFile.Data.Length)
                                    {
                                        try { await dataStream.WriteAsync(retrFile.Data.AsMemory(start), _cts.Token).ConfigureAwait(false); }
                                        catch (IOException) { /* client closed early upon detecting size overflow */ }
                                    }
                                }
                                pasvListener.Stop();
                                pasvListener = null;
                                await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
                                break;
                            case "QUIT":
                                await writer.WriteLineAsync("221 Goodbye").ConfigureAwait(false);
                                return;
                            default:
                                await writer.WriteLineAsync("502 Command not implemented").ConfigureAwait(false);
                                break;
                        }
                    }
                }
            }
            catch
            {
                /* client disconnected */
            }
            finally
            {
                try { pasvListener?.Stop(); } catch { /* ignore shutdown */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _controlListener.Stop(); } catch { /* ignore shutdown */ }
            _cts.Dispose();
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        _passes = 0;
        _fails = 0;
        Console.WriteLine("=== Review Findings & Regression Suite ===");

        var defaultSettings = new AppSettings { ProxyMode = "none" };
        defaultSettings.Normalize();
        HttpDownloader.Configure(defaultSettings);
        var limiter = new SpeedLimiter();

        string rfOutDir = Path.Combine(outDir, "_review_fixes_" + Guid.NewGuid().ToString("N")[..8]);
        string rfServedSubDir = Path.Combine(servedDir, "rf");
        string rfRunTagPrefix = "rf_" + Guid.NewGuid().ToString("N")[..6];
        Directory.CreateDirectory(rfOutDir);

        var rng = new Random(20261118);
        var sample64K = new byte[64 * 1024];
        rng.NextBytes(sample64K);
        string sample64KHash = HashBytes(sample64K);

        try
        {
            // -----------------------------------------------------------------
            // RF-01: Fix 1 — GoFile / resolver CookieHost sent over HTTPS to CookieHost only (never HTTP, never other host)
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-01", async () =>
            {
                var gfItem = new DownloadItem
                {
                    Url = "https://gofile.io/d/gfShare1",
                    Cookies = "accountToken=gf_guest_tok_99",
                    CookieHost = "store1.gofile.io",
                };

                bool httpsMatchOk = HttpDownloader.CookiesForRequest(gfItem, new Uri("https://store1.gofile.io/download/web/c1/dataset.7z")) == "accountToken=gf_guest_tok_99"
                                    && gfItem.CookiesFor(new Uri("https://store1.gofile.io/download/web/c1/dataset.7z")) == "accountToken=gf_guest_tok_99";
                bool neverHttp = HttpDownloader.CookiesForRequest(gfItem, new Uri("http://store1.gofile.io/download/web/c1/dataset.7z")) == "";
                bool neverOtherHost = HttpDownloader.CookiesForRequest(gfItem, new Uri("https://gofile.io/d/gfShare1")) == ""
                                      && HttpDownloader.CookiesForRequest(gfItem, new Uri("https://store2.gofile.io/download/web/c1/dataset.7z")) == ""
                                      && HttpDownloader.CookiesForRequest(gfItem, new Uri("https://sub.store1.gofile.io/download/web/c1/dataset.7z")) == "";

                var httpOriginItem = new DownloadItem
                {
                    Url = "http://gofile.io/d/gfShare1",
                    Cookies = "accountToken=gf_guest_tok_99",
                    CookieHost = "store1.gofile.io",
                };
                bool httpOrigSafe = HttpDownloader.CookiesForRequest(httpOriginItem, new Uri("http://store1.gofile.io/download/web/c1/dataset.7z")) == ""
                                    && HttpDownloader.CookiesForRequest(httpOriginItem, new Uri("https://store1.gofile.io/download/web/c1/dataset.7z")) == "accountToken=gf_guest_tok_99";

                // End-to-end: TryResolveSharingItemAsync + HttpDownloader sends Cookie to store1.gofile.io, drops it on CDN redirect, and blocks http downgrade
                var mgrSettings = new AppSettings { ProxyMode = "none", DownloadRoot = rfOutDir, TempDir = rfOutDir };
                mgrSettings.Normalize();
                var mgr = new DownloadManager(mgrSettings);
                bool e2eStoreOk = false;
                bool e2eRedirectDropOk = false;
                bool e2eHttpDowngradeBlocked = false;
                bool plainHttpRequested = false;
                try
                {
                    using var fakeHost = new TestServerFakeHostHandler(baseUrl);
                    using var invoker = HostResolvers.CreateIsolatedInvoker(fakeHost);

                    var dlItem = new DownloadItem
                    {
                        Url = "https://gofile.io/d/gfShare1",
                        SaveDir = rfOutDir,
                        FileName = "rf01-gofile.7z",
                        NameLocked = true,
                        MaxConnections = 2,
                    };
                    bool resolvedOk = await mgr.TryResolveSharingItemAsync(dlItem, invoker, CancellationToken.None);

                    using var mockClient = new System.Net.Http.HttpClient(new DelegateHttpHandler((req, _) =>
                    {
                        var u = req.RequestUri!;
                        string cookieHdr = req.Headers.TryGetValues("Cookie", out var cv) ? string.Join("; ", cv) : "";
                        string authHdr = req.Headers.Authorization?.ToString() ?? "";
                        if (u.Scheme != Uri.UriSchemeHttps) plainHttpRequested = true;

                        if (u.Host == "store1.gofile.io" && u.AbsolutePath == "/redir-edge")
                        {
                            if (cookieHdr != "accountToken=gf_guest_tok_99")
                                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = req });
                            var r302 = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = req };
                            r302.Headers.Location = new Uri("https://edge-cdn.gofile.io/final.7z");
                            return Task.FromResult(r302);
                        }
                        if (u.Host == "edge-cdn.gofile.io")
                        {
                            if (!string.IsNullOrEmpty(cookieHdr) || !string.IsNullOrEmpty(authHdr))
                                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = req });
                            var edgeOk = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                RequestMessage = req,
                                Content = new ByteArrayContent(sample64K),
                            };
                            edgeOk.Content.Headers.ContentLength = sample64K.Length;
                            return Task.FromResult(edgeOk);
                        }
                        if (u.Host == "store1.gofile.io" && u.AbsolutePath == "/redir-http")
                        {
                            var rHttp = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = req };
                            rHttp.Headers.Location = new Uri("http://store1.gofile.io/insecure.7z");
                            return Task.FromResult(rHttp);
                        }
                        if (u.Host == "store1.gofile.io")
                        {
                            if (cookieHdr != "accountToken=gf_guest_tok_99" || !string.IsNullOrEmpty(authHdr))
                                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = req });
                            int total = sample64K.Length;
                            long s = 0, e = total - 1;
                            bool ranged = false;
                            if (req.Headers.Range?.Ranges.FirstOrDefault() is { } rh)
                            {
                                s = rh.From ?? 0;
                                e = rh.To ?? (total - 1);
                                ranged = true;
                            }
                            int len = (int)(e - s + 1);
                            var ok = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                            {
                                RequestMessage = req,
                                Content = new ByteArrayContent(sample64K.AsSpan((int)s, len).ToArray()),
                            };
                            if (ranged) ok.Content.Headers.ContentRange = new ContentRangeHeaderValue(s, e, total);
                            ok.Content.Headers.ContentLength = len;
                            ok.Headers.AcceptRanges.Add("bytes");
                            ok.Headers.ETag = new EntityTagHeaderValue("\"rf01-gf\"");
                            return Task.FromResult(ok);
                        }
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req });
                    }));

                    HttpDownloader.SetClientForTests(mockClient);
                    // The fake share declares 2 MB while the mock store serves the 64 KB sample: the size-mismatch
                    // restart re-runs the resolver, which must go to the local test server and not to the mock client.
                    HttpDownloader.ResolverInvoker = invoker;
                    dlItem.Login = "shouldNotLeak";
                    dlItem.Password = "shouldNotLeakPw";
                    await new HttpDownloader(dlItem, limiter).RunAsync(CancellationToken.None);
                    e2eStoreOk = resolvedOk && File.Exists(dlItem.FullPath) && HashFile(dlItem.FullPath) == sample64KHash;

                    var redirItem = new DownloadItem
                    {
                        Url = "https://gofile.io/d/gfShare1",
                        ResolvedUrl = "https://store1.gofile.io/redir-edge",
                        Cookies = "accountToken=gf_guest_tok_99",
                        CookieHost = "store1.gofile.io",
                        SaveDir = rfOutDir,
                        FileName = "rf01-gofile-edge.7z",
                        NameLocked = true,
                        MaxConnections = 1,
                    };
                    await new HttpDownloader(redirItem, limiter).RunAsync(CancellationToken.None);
                    e2eRedirectDropOk = File.Exists(redirItem.FullPath) && HashFile(redirItem.FullPath) == sample64KHash;

                    var downItem = new DownloadItem
                    {
                        Url = "https://gofile.io/d/gfShare1",
                        ResolvedUrl = "https://store1.gofile.io/redir-http",
                        Cookies = "accountToken=gf_guest_tok_99",
                        CookieHost = "store1.gofile.io",
                        SaveDir = rfOutDir,
                        FileName = "rf01-gofile-down.7z",
                        NameLocked = true,
                        MaxConnections = 1,
                    };
                    try { await new HttpDownloader(downItem, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException)
                    {
                        // The downgrade is refused; the engine then falls back to the share page, which fails here.
                        // What matters: nothing was ever requested over plain http and no file was written.
                        e2eHttpDowngradeBlocked = !plainHttpRequested && !File.Exists(downItem.FullPath);
                    }
                }
                finally
                {
                    HttpDownloader.ResolverInvoker = null;
                    HttpDownloader.Configure(defaultSettings);
                    mgr.Shutdown();
                }

                Check("RF-01: Resolver cookie sent over HTTPS to CookieHost, dropped on other hosts/HTTP, and protected against downgrade",
                    httpsMatchOk && neverHttp && neverOtherHost && httpOrigSafe && e2eStoreOk && e2eRedirectDropOk && e2eHttpDowngradeBlocked);
            });

            // -----------------------------------------------------------------
            // RF-02: Fix 2 — FTP userinfo stripped from URLs in logs and Zone.Identifier
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-02", async () =>
            {
                bool stripHelperOk =
                    Log.StripUserInfo("ftp://alice:SuperSecretPw1@127.0.0.1:2121/pub/file.bin") == "ftp://127.0.0.1:2121/pub/file.bin"
                    && Log.StripUserInfo("ftps://bob:p%40ss%3A2@ftp.example.com/dir/a.zip") == "ftps://ftp.example.com/dir/a.zip"
                    && Log.StripUserInfo("http://useronly@example.com/test?a=1") == "http://example.com/test?a=1"
                    && !Log.StripUserInfo("ftp://baduser:bad@pw@bad host/file.bin").Contains("bad@pw", StringComparison.Ordinal)
                    && !Log.StripUserInfo("ftp://baduser:bad@pw@bad host/file.bin").Contains("baduser", StringComparison.Ordinal)
                    && Log.StripUserInfo("https://example.com/dl?email=me@example.com") == "https://example.com/dl?email=me@example.com";

                using var ftpSrv = new ReviewFtpServer(
                    new Dictionary<string, ReviewFtpServer.VirtualFile>(StringComparer.Ordinal)
                    {
                        ["/vault/doc.bin"] = new(sample64K),
                    },
                    requiredUser: "ftp_sec_user",
                    requiredPass: "Ftp_S3cr3t_98765!");

                var motwItem = new DownloadItem
                {
                    Url = $"ftp://ftp_sec_user:Ftp_S3cr3t_98765!@127.0.0.1:{ftpSrv.Port}/vault/doc.bin",
                    Referrer = "http://ref_user:Ref_S3cr3t_54321!@127.0.0.1/index.html",
                    SaveDir = rfOutDir,
                    FileName = "rf02-motw.bin",
                    NameLocked = true,
                };
                await new FtpDownloader(motwItem, limiter).RunAsync(CancellationToken.None);
                string zoneText = File.Exists(motwItem.FullPath + ":Zone.Identifier")
                    ? File.ReadAllText(motwItem.FullPath + ":Zone.Identifier")
                    : "";
                bool motwClean = zoneText.Contains("ZoneId=3", StringComparison.Ordinal)
                                 && !zoneText.Contains("Ftp_S3cr3t_98765!", StringComparison.Ordinal)
                                 && !zoneText.Contains("ftp_sec_user", StringComparison.Ordinal)
                                 && !zoneText.Contains("Ref_S3cr3t_54321!", StringComparison.Ordinal)
                                 && !zoneText.Contains("ref_user", StringComparison.Ordinal);

                var closedListener = new TcpListener(IPAddress.Loopback, 0);
                closedListener.Start();
                int closedPort = ((IPEndPoint)closedListener.LocalEndpoint).Port;
                closedListener.Stop();

                string secretMarker = "NeverLogThisFtpPw_" + Guid.NewGuid().ToString("N")[..8];
                string userMarker = "NeverLogUser_" + Guid.NewGuid().ToString("N")[..6];
                string failUrl = $"ftp://{userMarker}:{secretMarker}@127.0.0.1:{closedPort}/missing.bin";

                var mgrSettings = new AppSettings { ProxyMode = "none", DownloadRoot = rfOutDir, TempDir = rfOutDir };
                mgrSettings.Normalize();
                var mgr = new DownloadManager(mgrSettings);
                try
                {
                    if (Golan.Gdm.Core.Licensing.LicenseGate.CanDownload)
                    {
                        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        mgr.ItemFailed += _ => tcs.TrySetResult(true);
                        var failItem = new DownloadItem
                        {
                            Url = failUrl,
                            SaveDir = rfOutDir,
                            FileName = "rf02-fail.bin",
                            NameLocked = true,
                        };
                        mgr.Add(failItem, startNow: true);
                        await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    }
                    else
                    {
                        Log.Write($"download failed {Log.StripUserInfo(failUrl)}: test");
                    }
                }
                finally
                {
                    mgr.Shutdown();
                }

                string logContent = File.Exists(AppPaths.LogFile) ? File.ReadAllText(AppPaths.LogFile) : "";
                bool logClean = logContent.Contains($"download failed ftp://127.0.0.1:{closedPort}/missing.bin:", StringComparison.Ordinal)
                                && !logContent.Contains(secretMarker, StringComparison.Ordinal)
                                && !logContent.Contains(userMarker, StringComparison.Ordinal);

                Check("RF-02: Log.StripUserInfo removes credentials and neither gdm.log nor FTP Zone.Identifier leaks FTP passwords",
                    stripHelperOk && motwClean && logClean);
            });

            // -----------------------------------------------------------------
            // RF-03: Fix 3 — FtpDownloader SIZE overflow, MaxDownloadSize, FTPS PROT P refusal, REST order, and .gdm adoption
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-03", async () =>
            {
                var overflowData = new byte[32 * 1024];
                rng.NextBytes(overflowData);

                using var ftpSrv = new ReviewFtpServer(
                    new Dictionary<string, ReviewFtpServer.VirtualFile>(StringComparer.Ordinal)
                    {
                        ["/pub/exact.bin"] = new(sample64K),
                        ["/pub/overflow.bin"] = new(overflowData, AnnouncedSize: 4096),
                        ["/pub/nosize.bin"] = new(overflowData, AnnouncedSize: -1),
                    },
                    requiredUser: "u",
                    requiredPass: "p");

                // 3(c) + 3(d) positive: item.Downloaded > 0 adopts .gdm and sends REST after EPSV immediately before RETR
                ftpSrv.ClearRecordedSessions();
                var resumeItem = new DownloadItem
                {
                    Url = $"ftp://127.0.0.1:{ftpSrv.Port}/pub/exact.bin",
                    Login = "u",
                    Password = "p",
                    SaveDir = rfOutDir,
                    FileName = "rf03-resume.bin",
                    NameLocked = true,
                    Downloaded = 24 * 1024,
                };
                File.WriteAllBytes(resumeItem.TempPath, sample64K.AsSpan(0, 24 * 1024).ToArray());
                await new FtpDownloader(resumeItem, limiter).RunAsync(CancellationToken.None);

                var resumeCmds = ftpSrv.GetRecordedSessions().LastOrDefault() ?? [];
                int epsvIdx = -1, restIdx = -1, retrIdx = -1;
                for (int i = 0; i < resumeCmds.Count; i++)
                {
                    if (resumeCmds[i].StartsWith("EPSV", StringComparison.OrdinalIgnoreCase)) epsvIdx = i;
                    else if (resumeCmds[i].StartsWith("REST ", StringComparison.OrdinalIgnoreCase)) restIdx = i;
                    else if (resumeCmds[i].StartsWith("RETR ", StringComparison.OrdinalIgnoreCase)) retrIdx = i;
                }
                bool restOrderOk = epsvIdx >= 0 && restIdx == epsvIdx + 1 && retrIdx == restIdx + 1
                                   && File.Exists(resumeItem.FullPath) && HashFile(resumeItem.FullPath) == sample64KHash;

                // 3(d) negative: item.Downloaded == 0 discards existing .gdm instead of adopting it
                ftpSrv.ClearRecordedSessions();
                var staleItem = new DownloadItem
                {
                    Url = $"ftp://127.0.0.1:{ftpSrv.Port}/pub/exact.bin",
                    Login = "u",
                    Password = "p",
                    SaveDir = rfOutDir,
                    FileName = "rf03-stale.bin",
                    NameLocked = true,
                    Downloaded = 0,
                };
                var corruptPrefix = new byte[24 * 1024];
                Array.Fill(corruptPrefix, (byte)0xEE);
                File.WriteAllBytes(staleItem.TempPath, corruptPrefix);
                await new FtpDownloader(staleItem, limiter).RunAsync(CancellationToken.None);
                var staleCmds = ftpSrv.GetRecordedSessions().LastOrDefault() ?? [];
                bool staleIgnoredOk = !staleCmds.Any(c => c.StartsWith("REST ", StringComparison.OrdinalIgnoreCase))
                                      && File.Exists(staleItem.FullPath) && HashFile(staleItem.FullPath) == sample64KHash;

                // 3(a): Known SIZE exceeded -> fails with err_stream_exceeded and removes temp/final
                var overItem = new DownloadItem
                {
                    Url = $"ftp://127.0.0.1:{ftpSrv.Port}/pub/overflow.bin",
                    Login = "u",
                    Password = "p",
                    SaveDir = rfOutDir,
                    FileName = "rf03-overflow.bin",
                    NameLocked = true,
                };
                DownloadException? overEx = null;
                try { await new FtpDownloader(overItem, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { overEx = ex; }
                bool knownOverflowRejected = overEx != null
                                             && overEx.Message == Loc.T("core.http.err_stream_exceeded")
                                             && !File.Exists(overItem.FullPath)
                                             && !File.Exists(overItem.TempPath);

                // 3(a): MaxDownloadSize respected for both known SIZE and unknown SIZE
                long prevMaxDl = HttpDownloader.MaxDownloadSize;
                HttpDownloader.MaxDownloadSize = 8192;
                bool maxDlKnownRejected = false;
                bool maxDlStreamRejected = false;
                try
                {
                    var bigKnown = new DownloadItem
                    {
                        Url = $"ftp://127.0.0.1:{ftpSrv.Port}/pub/exact.bin",
                        Login = "u",
                        Password = "p",
                        SaveDir = rfOutDir,
                        FileName = "rf03-maxknown.bin",
                        NameLocked = true,
                    };
                    try { await new FtpDownloader(bigKnown, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException ex) when (ex.Message == Loc.T("core.http.err_file_too_large")) { maxDlKnownRejected = true; }

                    var bigUnknown = new DownloadItem
                    {
                        Url = $"ftp://127.0.0.1:{ftpSrv.Port}/pub/nosize.bin",
                        Login = "u",
                        Password = "p",
                        SaveDir = rfOutDir,
                        FileName = "rf03-maxunknown.bin",
                        NameLocked = true,
                    };
                    try { await new FtpDownloader(bigUnknown, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException ex) when (ex.Message == Loc.T("core.http.err_file_too_large")) { maxDlStreamRejected = !File.Exists(bigUnknown.FullPath); }
                }
                finally
                {
                    HttpDownloader.MaxDownloadSize = prevMaxDl;
                }

                // 3(b): FTPS PROT P refusal throws DownloadException instead of cleartext fallback
                FtpDownloader.EnsureFtpsDataProtection((200, "PROT P OK"));
                bool protRefusedThrows = false;
                try { FtpDownloader.EnsureFtpsDataProtection((534, "Policy requires SSL")); }
                catch (DownloadException ex) when (ex.StatusCode == 534) { protRefusedThrows = true; }

                Check("RF-03: FtpDownloader enforces SIZE/MaxDownloadSize bounds, refuses FTPS without PROT P, sends REST right before RETR, and ignores unpersisted .gdm",
                    restOrderOk && staleIgnoredOk && knownOverflowRejected && maxDlKnownRejected && maxDlStreamRejected && protRefusedThrows,
                    $"epsv={epsvIdx}, rest={restIdx}, retr={retrIdx}, overEx={overEx?.Message}");
            });

            // -----------------------------------------------------------------
            // RF-04: Fix 4 — HttpDownloader.HasChangedAsync returns false for HLS/DASH, FTP/FTPS, and data: items
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-04", async () =>
            {
                var hlsSync = new DownloadItem { Url = $"{baseUrl}/hls/master.m3u8", TotalSize = 555_555, Status = DownloadStatus.Completed };
                var dashSync = new DownloadItem { Url = $"{baseUrl}/dash/manifest.mpd", TotalSize = 777_777, Status = DownloadStatus.Completed };
                var redirStreamSync = new DownloadItem { Url = $"{baseUrl}/short/stream", TotalSize = 888_888, Status = DownloadStatus.Completed };
                var ftpSync = new DownloadItem { Url = "ftp://127.0.0.1:1/pub/file.bin", TotalSize = 12345, Status = DownloadStatus.Completed };
                var ftpsSync = new DownloadItem { Url = "ftps://127.0.0.1:1/pub/file.bin", TotalSize = 12345, Status = DownloadStatus.Completed };
                var dataSync = new DownloadItem { Url = "data:text/plain;base64,SGVsbG8=", TotalSize = 5, Status = DownloadStatus.Completed };

                bool allFalse =
                    !await HttpDownloader.HasChangedAsync(hlsSync, CancellationToken.None)
                    && !await HttpDownloader.HasChangedAsync(dashSync, CancellationToken.None)
                    && !await HttpDownloader.HasChangedAsync(redirStreamSync, CancellationToken.None)
                    && !await HttpDownloader.HasChangedAsync(ftpSync, CancellationToken.None)
                    && !await HttpDownloader.HasChangedAsync(ftpsSync, CancellationToken.None)
                    && !await HttpDownloader.HasChangedAsync(dataSync, CancellationToken.None);

                Check("RF-04: HttpDownloader.HasChangedAsync returns false for HLS, DASH, redirected streams, FTP/FTPS, and data: URLs",
                    allFalse);
            });

            // -----------------------------------------------------------------
            // RF-05: Fix 5 — Size/ETag mismatch restart re-runs resolver (Pixeldrain, Telegram, SourceForge, GoFile)
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-05", async () =>
            {
                using var fakeHost = new TestServerFakeHostHandler(baseUrl);
                using var resolverInvoker = HostResolvers.CreateIsolatedInvoker(fakeHost);

                using var mockClient = new System.Net.Http.HttpClient(new DelegateHttpHandler((req, _) =>
                {
                    var u = req.RequestUri!;
                    // Any direct hit on the HTML share page must return text/html so dropping ResolvedUrl would fail with err_web_page
                    bool isSharePage =
                        (u.Host == "pixeldrain.com" && u.AbsolutePath.StartsWith("/u/", StringComparison.Ordinal))
                        || u.Host == "t.me"
                        || u.Host == "sourceforge.net"
                        || u.Host == "gofile.io";
                    if (isSharePage)
                    {
                        var htmlResp = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = req,
                            Content = new StringContent("<html><body>HTML Share Page</body></html>", Encoding.UTF8, "text/html"),
                        };
                        return Task.FromResult(htmlResp);
                    }

                    if (u.Host == "store1.gofile.io")
                    {
                        string ck = req.Headers.TryGetValues("Cookie", out var cv) ? string.Join("; ", cv) : "";
                        if (ck != "accountToken=gf_guest_tok_99")
                            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = req });
                    }

                    int total = sample64K.Length;
                    long s = 0, e = total - 1;
                    bool ranged = false;
                    if (req.Headers.Range?.Ranges.FirstOrDefault() is { } rh)
                    {
                        s = rh.From ?? 0;
                        e = rh.To ?? (total - 1);
                        ranged = true;
                    }
                    int len = (int)(e - s + 1);
                    var ok = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    {
                        RequestMessage = req,
                        Content = new ByteArrayContent(sample64K.AsSpan((int)s, len).ToArray()),
                    };
                    if (ranged) ok.Content.Headers.ContentRange = new ContentRangeHeaderValue(s, e, total);
                    ok.Content.Headers.ContentLength = len;
                    ok.Headers.AcceptRanges.Add("bytes");
                    ok.Headers.ETag = new EntityTagHeaderValue("\"new-v2-etag\"");
                    return Task.FromResult(ok);
                }));

                (string ShareUrl, string StaleDirect, string OutName)[] cases =
                [
                    ("https://pixeldrain.com/u/pxAbc123", "https://pixeldrain.com/api/file/pxAbc123?download", "rf05-px.mp4"),
                    ("https://t.me/gdm_news/42", "https://cdn4.telesco.pe/file/tg_old.mp4", "rf05-tg.mp4"),
                    ("https://sourceforge.net/projects/sevenzip/files/7-Zip/24.08/7z2408-x64.exe/download", "https://downloads.sourceforge.net/project/sevenzip/7-Zip/24.08/7z2408-x64.exe?use_mirror=autoselect", "rf05-sf.exe"),
                    ("https://gofile.io/d/gfShare1", "https://store1.gofile.io/download/web/c1/dataset.7z", "rf05-gf.7z"),
                ];

                bool allRestartedOk = true;
                try
                {
                    HttpDownloader.ResolverInvoker = resolverInvoker;
                    HttpDownloader.SetClientForTests(mockClient);

                    foreach (var c in cases)
                    {
                        var item = new DownloadItem
                        {
                            Url = c.ShareUrl,
                            ResolvedUrl = c.StaleDirect,
                            SaveDir = rfOutDir,
                            FileName = c.OutName,
                            NameLocked = true,
                            MaxConnections = 2,
                            Resumable = true,
                            TotalSize = 16384, // stale old size triggers Mismatch against 64 KB server file
                            ETag = "\"old-v1-etag\"",
                            Downloaded = 4096,
                            Segments = [new Segment { Start = 0, End = 16383, Downloaded = 4096 }],
                        };
                        if (c.ShareUrl.Contains("gofile.io", StringComparison.Ordinal))
                        {
                            item.Cookies = "accountToken=gf_guest_tok_99";
                            item.CookieHost = "store1.gofile.io";
                        }
                        File.WriteAllBytes(item.TempPath, new byte[4096]);

                        await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                        if (!File.Exists(item.FullPath) || HashFile(item.FullPath) != sample64KHash)
                            allRestartedOk = false;
                    }
                }
                finally
                {
                    HttpDownloader.ResolverInvoker = null;
                    HttpDownloader.Configure(defaultSettings);
                }

                Check("RF-05: Size/ETag mismatch restart re-runs HostResolvers for Pixeldrain, Telegram, SourceForge, and GoFile",
                    allRestartedOk);
            });

            // -----------------------------------------------------------------
            // RF-06: StreamDownloader per-segment retry (503 then 200) and progress rollback on failed try
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-06", async () =>
            {
                byte[] seg0 = new byte[2048];
                byte[] seg1 = new byte[2048];
                Array.Fill(seg0, (byte)'0');
                Array.Fill(seg1, (byte)'1');
                byte[] expectedJoined = [.. seg0, .. seg1];

                int seg0Calls = 0;
                int seg1Calls = 0;
                long downloadedAtSeg1RetryStart = -1;

                var hlsItem = new DownloadItem
                {
                    Url = "http://127.0.0.1/retry/index.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf06-retry.ts",
                    NameLocked = true,
                    MaxConnections = 1,
                };

                using var mockClient = new System.Net.Http.HttpClient(new DelegateHttpHandler((req, _) =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    if (path == "/retry/index.m3u8")
                    {
                        const string m3u = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nseg0.ts\n#EXTINF:4.0,\nseg1.ts\n#EXT-X-ENDLIST\n";
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = req,
                            Content = new StringContent(m3u, Encoding.UTF8, "application/vnd.apple.mpegurl"),
                        });
                    }
                    if (path == "/retry/seg0.ts")
                    {
                        if (Interlocked.Increment(ref seg0Calls) == 1)
                            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = req });
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = req,
                            Content = new ByteArrayContent(seg0),
                        });
                    }
                    if (path == "/retry/seg1.ts")
                    {
                        int n = Interlocked.Increment(ref seg1Calls);
                        if (n == 1)
                        {
                            // Stream 1500 bytes then drop -> counted bytes must be subtracted before attempt 2
                            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                RequestMessage = req,
                                Content = new StreamContent(new PartialThenThrowStream(seg1[..1500])),
                            });
                        }
                        downloadedAtSeg1RetryStart = hlsItem.Downloaded;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = req,
                            Content = new ByteArrayContent(seg1),
                        });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req });
                }));

                try
                {
                    HttpDownloader.SetClientForTests(mockClient);
                    await StreamDownloader.RunAsync(hlsItem, limiter, CancellationToken.None);
                }
                finally
                {
                    HttpDownloader.Configure(defaultSettings);
                }

                var outBytes = File.Exists(hlsItem.FullPath) ? File.ReadAllBytes(hlsItem.FullPath) : [];
                Check("RF-06: StreamDownloader retries 503/dropped segment and rolls back partial bytes without double counting",
                    seg0Calls == 2 && seg1Calls == 2
                    && downloadedAtSeg1RetryStart == seg0.Length
                    && hlsItem.Downloaded == expectedJoined.Length
                    && outBytes.AsSpan().SequenceEqual(expectedJoined),
                    $"seg0Calls={seg0Calls}, seg1Calls={seg1Calls}, dlBeforeRetry={downloadedAtSeg1RetryStart}, finalDl={hlsItem.Downloaded}");
            });

            // -----------------------------------------------------------------
            // RF-07: StreamDownloader refuses public-host manifest whose segment points at 127.0.0.1 (no internet)
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-07", async () =>
            {
                int networkHits = 0;
                using var zeroOrManifestClient = new System.Net.Http.HttpClient(new DelegateHttpHandler((req, _) =>
                {
                    Interlocked.Increment(ref networkHits);
                    if (req.RequestUri!.Host == "cdn.public-stream.example" && req.RequestUri.AbsolutePath == "/vod/playlist.m3u8")
                    {
                        const string ssrfM3u = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nhttps://127.0.0.1/internal.ts\n#EXT-X-ENDLIST\n";
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = req,
                            Content = new StringContent(ssrfM3u, Encoding.UTF8, "application/vnd.apple.mpegurl"),
                        });
                    }
                    throw new InvalidOperationException($"Blocked unexpected network call to {req.RequestUri}");
                }));

                bool directCheckHttpBlocked = false;
                bool directCheckHttpsBlocked = false;
                bool runCheckBlocked = false;
                int hitsAfterDirect = -1;
                int hitsAfterRun = -1;

                try
                {
                    HttpDownloader.SetClientForTests(zeroOrManifestClient);
                    var pubItem = new DownloadItem { Url = "https://cdn.public-stream.example/vod/playlist.m3u8", SaveDir = rfOutDir };

                    try
                    {
                        await StreamDownloader.FetchBytesAsync(pubItem, new Uri("http://127.0.0.1:8080/secret.ts"), null, null, null, trackProgress: true, CancellationToken.None);
                    }
                    catch (DownloadException ex) when (ex.Message == Loc.T("core.http.err_redirect_invalid"))
                    {
                        directCheckHttpBlocked = true;
                    }

                    try
                    {
                        await StreamDownloader.FetchBytesAsync(pubItem, new Uri("https://127.0.0.1/secret.ts"), null, null, null, trackProgress: true, CancellationToken.None);
                    }
                    catch (DownloadException ex) when (ex.Message == Loc.T("core.http.err_redirect_invalid"))
                    {
                        directCheckHttpsBlocked = true;
                    }
                    hitsAfterDirect = Volatile.Read(ref networkHits);

                    try
                    {
                        await StreamDownloader.RunAsync(pubItem, limiter, CancellationToken.None);
                    }
                    catch (DownloadException ex) when (ex.Message == Loc.T("core.http.err_redirect_invalid"))
                    {
                        runCheckBlocked = true;
                    }
                    hitsAfterRun = Volatile.Read(ref networkHits);
                }
                finally
                {
                    HttpDownloader.Configure(defaultSettings);
                }

                Check("RF-07: StreamDownloader refuses public-host manifest segments targeting 127.0.0.1 before sending any segment request",
                    directCheckHttpBlocked && directCheckHttpsBlocked && hitsAfterDirect == 0 && runCheckBlocked && hitsAfterRun == 1);
            });

            // -----------------------------------------------------------------
            // RF-08: MergeTracksAsync throws instead of concatenating when neither merger works
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-08", async () =>
            {
                string badV = Path.Combine(rfOutDir, "rf08-bad-video.part");
                string badA = Path.Combine(rfOutDir, "rf08-bad-audio.part");
                string badOut = Path.Combine(rfOutDir, "rf08-merged.mp4");
                File.WriteAllBytes(badV, "NOT_ISO_BMFF_VIDEO_STREAM"u8.ToArray());
                File.WriteAllBytes(badA, "NOT_ISO_BMFF_AUDIO_STREAM"u8.ToArray());

                DownloadException? mergeEx = null;
                try
                {
                    await StreamDownloader.MergeTracksAsync(badV, badA, badOut, CancellationToken.None);
                }
                catch (DownloadException ex)
                {
                    mergeEx = ex;
                }

                Check("RF-08: MergeTracksAsync throws err_ffmpeg instead of concatenating unmergeable tracks",
                    mergeEx != null && mergeEx.Message == Loc.T("core.media.err_ffmpeg") && !File.Exists(badOut));
            });

            // -----------------------------------------------------------------
            // RF-09: EnsureStreamFileName replaces .m3u8 or .mpd extension on NameLocked items
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-09", () =>
            {
                var srcUri = new Uri("https://cdn.example.com/streams/master.m3u8");

                var lockedM3u8 = new DownloadItem { FileName = "lesson_01.m3u8", NameLocked = true };
                StreamDownloader.EnsureStreamFileName(lockedM3u8, srcUri, ".ts");

                var lockedM3u8Upper = new DownloadItem { FileName = "Lesson_02.M3U8", NameLocked = true };
                StreamDownloader.EnsureStreamFileName(lockedM3u8Upper, srcUri, ".mp4");

                var lockedMpd = new DownloadItem { FileName = "concert.mpd", NameLocked = true };
                StreamDownloader.EnsureStreamFileName(lockedMpd, new Uri("https://cdn.example.com/dash/manifest.mpd"), ".mp4");

                var lockedMpdAudio = new DownloadItem { FileName = "podcast.MPD", NameLocked = true };
                StreamDownloader.EnsureStreamFileName(lockedMpdAudio, new Uri("https://cdn.example.com/dash/manifest.mpd"), ".m4a");

                var lockedMkv = new DownloadItem { FileName = "keep_container.mkv", NameLocked = true };
                StreamDownloader.EnsureStreamFileName(lockedMkv, srcUri, ".mp4");

                Check("RF-09: EnsureStreamFileName replaces locked .m3u8/.mpd extensions with container extension and preserves other locked extensions",
                    lockedM3u8.FileName == "lesson_01.ts"
                    && lockedM3u8Upper.FileName == "Lesson_02.mp4"
                    && lockedMpd.FileName == "concert.mp4"
                    && lockedMpdAudio.FileName == "podcast.m4a"
                    && lockedMkv.FileName == "keep_container.mkv");
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------
            // RF-10: DASH padWidth clamp (<= 20) and total segment cap (MaxDashSegments = 200_000)
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-10", () =>
            {
                string normalPad = StreamDownloader.ExpandDashVars("seg_$Number%05d$.m4s", "v1", 1000, 42, 0);
                string hugePad = StreamDownloader.ExpandDashVars("seg_$Number%0999999999d$_$Time%0888888d$.m4s", "v1", 1000, 42, 7);
                string expectedHugePad = $"seg_{"42".PadLeft(20, '0')}_{"7".PadLeft(20, '0')}.m4s";

                var sbMpd = new StringBuilder();
                sbMpd.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
                sbMpd.AppendLine("""<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT300000S">""");
                sbMpd.AppendLine("""  <Period duration="PT300000S">""");
                sbMpd.AppendLine("""    <AdaptationSet contentType="video" mimeType="video/mp4">""");
                sbMpd.AppendLine("""      <SegmentTemplate media="seg_$Number$.m4s" startNumber="1" timescale="1">""");
                sbMpd.AppendLine("""        <SegmentTimeline>""");
                for (int i = 0; i < 11; i++) // 11 * 20,000 = 220,000 > MaxDashSegments (200,000)
                    sbMpd.AppendLine("""          <S d="1" r="19999"/>""");
                sbMpd.AppendLine("""        </SegmentTimeline>""");
                sbMpd.AppendLine("""      </SegmentTemplate>""");
                sbMpd.AppendLine("""      <Representation id="1080" bandwidth="2000000" width="1920" height="1080"/>""");
                sbMpd.AppendLine("""    </AdaptationSet>""");
                sbMpd.AppendLine("""  </Period>""");
                sbMpd.AppendLine("""</MPD>""");

                DownloadException? capEx = null;
                try
                {
                    _ = StreamDownloader.ParseDashMpd(new Uri("https://cdn.example.com/dash/hostile.mpd"), sbMpd.ToString());
                }
                catch (DownloadException ex)
                {
                    capEx = ex;
                }

                Check("RF-10: DASH ExpandDashVars clamps huge padWidth to 20 and ParseDashMpd rejects >200,000 SegmentTimeline entries",
                    normalPad == "seg_00042.m4s"
                    && hugePad == expectedHugePad
                    && capEx != null && capEx.Message == Loc.T("core.stream.err_manifest"));
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------
            // RF-11: GitHubResolver asset choice never mistakes "darwin" (macOS) for Windows "win"
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-11", async () =>
            {
                var assetsNode = JsonNode.Parse("""
                [
                  { "name": "SHA256SUMS.txt", "size": 256, "browser_download_url": "https://objects.githubusercontent.com/org/SHA256SUMS.txt" },
                  { "name": "app-v1.0-darwin-x64.tar.gz", "size": 10000, "browser_download_url": "https://objects.githubusercontent.com/org/app-v1.0-darwin-x64.tar.gz" },
                  { "name": "app-v1.0-darwin-arm64.tar.gz", "size": 10000, "browser_download_url": "https://objects.githubusercontent.com/org/app-v1.0-darwin-arm64.tar.gz" },
                  { "name": "app-v1.0-linux-amd64.tar.gz", "size": 10000, "browser_download_url": "https://objects.githubusercontent.com/org/app-v1.0-linux-amd64.tar.gz" },
                  { "name": "app-v1.0-windows-arm64.zip", "size": 11000, "browser_download_url": "https://objects.githubusercontent.com/org/app-v1.0-windows-arm64.zip" },
                  { "name": "app-v1.0-windows-x64.zip", "size": 12345, "browser_download_url": "https://objects.githubusercontent.com/org/app-v1.0-windows-x64.zip" }
                ]
                """)!.AsArray();

                var picked = GitHubResolver.PickReleaseAsset(assetsNode);
                string? pickedName = picked?["name"]?.GetValue<string>();

                var exeVsDarwin = JsonNode.Parse("""
                [
                  { "name": "tool-darwin-universal.pkg", "size": 9000, "browser_download_url": "https://objects.githubusercontent.com/org/tool-darwin-universal.pkg" },
                  { "name": "tool-installer.exe", "size": 15000, "browser_download_url": "https://objects.githubusercontent.com/org/tool-installer.exe" }
                ]
                """)!.AsArray();
                string? pickedExe = GitHubResolver.PickReleaseAsset(exeVsDarwin)?["name"]?.GetValue<string>();

                using var ghInvoker = HostResolvers.CreateIsolatedInvoker(new DelegateHttpHandler((req, _) =>
                {
                    var body = new JsonObject
                    {
                        ["tag_name"] = "v1.0",
                        ["assets"] = JsonNode.Parse(assetsNode.ToJsonString()),
                    }.ToJsonString();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = req,
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
                }));
                var resolved = await new GitHubResolver().ResolveAsync(new Uri("https://github.com/org/app/releases/latest"), ghInvoker, CancellationToken.None);

                Check("RF-11: GitHubResolver skips darwin assets and picks the Windows x64 release asset",
                    pickedName == "app-v1.0-windows-x64.zip"
                    && pickedExe == "tool-installer.exe"
                    && resolved is { FileName: "app-v1.0-windows-x64.zip", Size: 12345 });
            });

            // -----------------------------------------------------------------
            // RF-12: BrowserProfiles.ReadDetailedState folder manifest.json fallback & UiaBrowserDriver.IsReplaceableAddress truth table
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-12", () =>
            {
                string extDir = Path.Combine(rfOutDir, "unpacked-ext");
                string profNoVer = Path.Combine(rfOutDir, "prof-no-ver");
                string profWithVer = Path.Combine(rfOutDir, "prof-with-ver");
                string profNotInstalled = Path.Combine(rfOutDir, "prof-not-installed");
                Directory.CreateDirectory(extDir);
                Directory.CreateDirectory(profNoVer);
                Directory.CreateDirectory(profWithVer);
                Directory.CreateDirectory(profNotInstalled);

                File.WriteAllText(Path.Combine(extDir, "manifest.json"), """{"manifest_version":3,"name":"Golan","version":"2.5.1"}""");

                var prefsNoVer = new JsonObject
                {
                    ["extensions"] = new JsonObject
                    {
                        ["ui"] = new JsonObject { ["developer_mode"] = true },
                        ["settings"] = new JsonObject
                        {
                            [BrowserIntegration.ExtensionId] = new JsonObject
                            {
                                ["path"] = extDir,
                                ["state"] = 1,
                            }
                        }
                    }
                };
                File.WriteAllText(Path.Combine(profNoVer, "Preferences"), prefsNoVer.ToJsonString());

                var prefsWithVer = new JsonObject
                {
                    ["extensions"] = new JsonObject
                    {
                        ["ui"] = new JsonObject { ["developer_mode"] = true },
                        ["settings"] = new JsonObject
                        {
                            [BrowserIntegration.ExtensionId] = new JsonObject
                            {
                                ["path"] = extDir,
                                ["state"] = 1,
                                ["version"] = "2.4.0",
                            }
                        }
                    }
                };
                File.WriteAllText(Path.Combine(profWithVer, "Preferences"), prefsWithVer.ToJsonString());
                File.WriteAllText(Path.Combine(profNotInstalled, "Preferences"), """{"extensions":{"ui":{"developer_mode":true},"settings":{}}}""");

                var stateFallback = BrowserProfiles.ReadDetailedState(profNoVer, BrowserIntegration.ExtensionId, extDir);
                var stateProfileWins = BrowserProfiles.ReadDetailedState(profWithVer, BrowserIntegration.ExtensionId, extDir);
                var stateNotInst = BrowserProfiles.ReadDetailedState(profNotInstalled, BrowserIntegration.ExtensionId, extDir);

                bool profileFallbackOk =
                    stateFallback is { DeveloperMode: true, Installed: true, Disabled: false, Version: "2.5.1" }
                    && stateProfileWins is { DeveloperMode: true, Installed: true, Disabled: false, Version: "2.4.0" }
                    && stateNotInst is { DeveloperMode: true, Installed: false, Disabled: false, Version: null };

                bool truthTableOk =
                    UiaBrowserDriver.IsReplaceableAddress("", "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("   ", "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress(null!, "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("about:blank", "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("about:newtab", "about:debugging#/runtime/this-firefox")
                    && UiaBrowserDriver.IsReplaceableAddress("about:home", "about:debugging#/runtime/this-firefox")
                    && UiaBrowserDriver.IsReplaceableAddress("chrome://newtab/", "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("edge://newtab", "edge://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("chrome://new-tab-page/", "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("chrome://extensions", "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("chrome://extensions/?id=fkjplbaglahdiphiodkgidkdfkbebgfh", "chrome://extensions/")
                    && UiaBrowserDriver.IsReplaceableAddress("about:debugging#/runtime/this-firefox", "about:debugging#/runtime/this-firefox")
                    && UiaBrowserDriver.IsReplaceableAddress("about:debugging", "about:debugging?tab=1")
                    && !UiaBrowserDriver.IsReplaceableAddress("https://example.com/article", "chrome://extensions/")
                    && !UiaBrowserDriver.IsReplaceableAddress("chrome://settings", "chrome://extensions/")
                    && !UiaBrowserDriver.IsReplaceableAddress("about:preferences", "about:debugging#/runtime/this-firefox")
                    && !UiaBrowserDriver.IsReplaceableAddress("https://example.com", "");

                Check("RF-12: BrowserProfiles.ReadDetailedState falls back to folder manifest.json version and IsReplaceableAddress matches truth table",
                    profileFallbackOk && truthTableOk);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------
            // RF-13: StreamDownloader resume (HLS, changed piece list restart, AES-128 IV/sequence, DASH two-track, RemoveAsync sidecar cleanup, live stream)
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-13", async () =>
            {
                async Task<string[]> GetRfHitsAsync(string tag)
                {
                    var raw = await HttpDownloader.Client.GetStringAsync($"{baseUrl}/rf-ctl?op=hits&tag={Uri.EscapeDataString(tag)}");
                    return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
                Task ResetRfHitsAsync(string tag)
                    => HttpDownloader.Client.GetStringAsync($"{baseUrl}/rf-ctl?op=reset&tag={Uri.EscapeDataString(tag)}");
                Task HoldRfSubAsync(string tag, string sub)
                    => HttpDownloader.Client.GetStringAsync($"{baseUrl}/rf-ctl?op=hold&tag={Uri.EscapeDataString(tag)}&sub={Uri.EscapeDataString(sub)}");
                Task UnholdRfSubAsync(string tag, string sub)
                    => HttpDownloader.Client.GetStringAsync($"{baseUrl}/rf-ctl?op=unhold&tag={Uri.EscapeDataString(tag)}&sub={Uri.EscapeDataString(sub)}");

                async Task WaitForRfHitAsync(string tag, string expectedSub, int timeoutMs = 4000)
                {
                    var swWait = System.Diagnostics.Stopwatch.StartNew();
                    while (swWait.ElapsedMilliseconds < timeoutMs)
                    {
                        var hits = await GetRfHitsAsync(tag);
                        if (hits.Contains(expectedSub, StringComparer.Ordinal))
                            return;
                        await Task.Delay(15);
                    }
                    throw new TimeoutException($"Timed out waiting for hit '{expectedSub}' under tag '{tag}'");
                }

                byte[][] segs = new byte[4][];
                for (int i = 0; i < 4; i++)
                {
                    segs[i] = new byte[8192];
                    rng.NextBytes(segs[i]);
                }
                byte[] expected4 = [.. segs[0], .. segs[1], .. segs[2], .. segs[3]];
                string expected4Hash = HashBytes(expected4);

                // 13a: HLS VOD pause after 2 pieces -> resume requests ONLY seg2 & seg3, restores progress, deletes sidecar on completion
                string tHls = rfRunTagPrefix + "_hls";
                string dHls = Path.Combine(rfServedSubDir, tHls);
                Directory.CreateDirectory(dHls);
                for (int i = 0; i < 4; i++)
                    File.WriteAllBytes(Path.Combine(dHls, $"seg{i}.ts"), segs[i]);
                const string m3u4 = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nseg0.ts\n#EXTINF:4.0,\nseg1.ts\n#EXTINF:4.0,\nseg2.ts\n#EXTINF:4.0,\nseg3.ts\n#EXT-X-ENDLIST\n";
                File.WriteAllText(Path.Combine(dHls, "index.m3u8"), m3u4);

                var hlsItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tHls}/index.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf13-hls-resume.ts",
                    NameLocked = true,
                    MaxConnections = 1,
                };

                await HoldRfSubAsync(tHls, "seg2.ts");
                using (var cts1 = new CancellationTokenSource())
                {
                    var run1 = StreamDownloader.RunAsync(hlsItem, limiter, cts1.Token);
                    await WaitForRfHitAsync(tHls, "seg2.ts");
                    cts1.Cancel();
                    await UnholdRfSubAsync(tHls, "seg2.ts");
                    try { await run1; } catch (OperationCanceledException) { /* expected pause */ }
                }

                bool pausedStateExists = File.Exists(hlsItem.TempPath)
                                         && File.Exists(hlsItem.TempPath + ".state")
                                         && hlsItem.Downloaded == segs[0].Length + segs[1].Length;

                // Reset in-memory Downloaded to 0 so Run 2 proves sidecar restores progress numbers
                hlsItem.Downloaded = 0;
                await ResetRfHitsAsync(tHls);
                await StreamDownloader.RunAsync(hlsItem, limiter, CancellationToken.None);
                var resumeHits = await GetRfHitsAsync(tHls);

                bool hlsResumeOk = pausedStateExists
                                   && !resumeHits.Contains("seg0.ts", StringComparer.Ordinal)
                                   && !resumeHits.Contains("seg1.ts", StringComparer.Ordinal)
                                   && resumeHits.Contains("seg2.ts", StringComparer.Ordinal)
                                   && resumeHits.Contains("seg3.ts", StringComparer.Ordinal)
                                   && File.Exists(hlsItem.FullPath)
                                   && HashFile(hlsItem.FullPath) == expected4Hash
                                   && hlsItem.Downloaded == expected4.Length
                                   && !File.Exists(hlsItem.TempPath)
                                   && !File.Exists(hlsItem.TempPath + ".state");

                // 13b: Changed piece list (changed next-piece URL and changed piece count) deletes old temp data and starts fresh
                string tChanged = rfRunTagPrefix + "_chg";
                string dChanged = Path.Combine(rfServedSubDir, tChanged);
                Directory.CreateDirectory(dChanged);
                byte[] seg2Alt = new byte[8192];
                rng.NextBytes(seg2Alt);
                for (int i = 0; i < 4; i++)
                    File.WriteAllBytes(Path.Combine(dChanged, $"seg{i}.ts"), segs[i]);
                File.WriteAllBytes(Path.Combine(dChanged, "seg2_v2.ts"), seg2Alt);
                File.WriteAllText(Path.Combine(dChanged, "index.m3u8"), m3u4);

                var chgUrlItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tChanged}/index.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf13-changed-url.ts",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await HoldRfSubAsync(tChanged, "seg2.ts");
                using (var ctsChg = new CancellationTokenSource())
                {
                    var runChg = StreamDownloader.RunAsync(chgUrlItem, limiter, ctsChg.Token);
                    await WaitForRfHitAsync(tChanged, "seg2.ts");
                    ctsChg.Cancel();
                    await UnholdRfSubAsync(tChanged, "seg2.ts");
                    try { await runChg; } catch (OperationCanceledException) { /* expected pause */ }
                }
                // Mutate next piece URL in manifest (seg2.ts -> seg2_v2.ts)
                File.WriteAllText(Path.Combine(dChanged, "index.m3u8"), m3u4.Replace("seg2.ts", "seg2_v2.ts", StringComparison.Ordinal));
                await ResetRfHitsAsync(tChanged);
                await StreamDownloader.RunAsync(chgUrlItem, limiter, CancellationToken.None);
                var chgUrlHits = await GetRfHitsAsync(tChanged);
                string expectedAltHash = HashBytes([.. segs[0], .. segs[1], .. seg2Alt, .. segs[3]]);
                bool changedNextUrlRestarts = chgUrlHits.Contains("seg0.ts", StringComparer.Ordinal)
                                              && chgUrlHits.Contains("seg1.ts", StringComparer.Ordinal)
                                              && chgUrlHits.Contains("seg2_v2.ts", StringComparer.Ordinal)
                                              && HashFile(chgUrlItem.FullPath) == expectedAltHash;

                // Mutate piece count (4 -> 3 pieces)
                File.WriteAllText(Path.Combine(dChanged, "index.m3u8"), m3u4);
                var chgCountItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tChanged}/index.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf13-changed-count.ts",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await HoldRfSubAsync(tChanged, "seg2.ts");
                using (var ctsCnt = new CancellationTokenSource())
                {
                    var runCnt = StreamDownloader.RunAsync(chgCountItem, limiter, ctsCnt.Token);
                    await WaitForRfHitAsync(tChanged, "seg2.ts");
                    ctsCnt.Cancel();
                    await UnholdRfSubAsync(tChanged, "seg2.ts");
                    try { await runCnt; } catch (OperationCanceledException) { /* expected pause */ }
                }
                const string m3u3 = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nseg0.ts\n#EXTINF:4.0,\nseg1.ts\n#EXTINF:4.0,\nseg2.ts\n#EXT-X-ENDLIST\n";
                File.WriteAllText(Path.Combine(dChanged, "index.m3u8"), m3u3);
                await ResetRfHitsAsync(tChanged);
                await StreamDownloader.RunAsync(chgCountItem, limiter, CancellationToken.None);
                var chgCountHits = await GetRfHitsAsync(tChanged);
                bool changedCountRestarts = chgCountHits.Contains("seg0.ts", StringComparer.Ordinal)
                                            && HashFile(chgCountItem.FullPath) == HashBytes([.. segs[0], .. segs[1], .. segs[2]]);

                // 13c: AES-128 HLS resume (explicit IV on piece 0 + sequence-based IVs from #EXT-X-MEDIA-SEQUENCE:40 on pieces 1..3)
                string tAes = rfRunTagPrefix + "_aes";
                string dAes = Path.Combine(rfServedSubDir, tAes);
                Directory.CreateDirectory(dAes);
                byte[] aesKey = RandomNumberGenerator.GetBytes(16);
                byte[] explicitIv0 = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
                File.WriteAllBytes(Path.Combine(dAes, "enc.key"), aesKey);

                static byte[] EncryptCbc(byte[] plain, byte[] k, byte[] iv)
                {
                    using var aes = Aes.Create();
                    aes.KeySize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = k;
                    return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
                }
                static byte[] SeqIv(long seq)
                {
                    var iv = new byte[16];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8, 8), seq);
                    return iv;
                }

                File.WriteAllBytes(Path.Combine(dAes, "seg0.enc"), EncryptCbc(segs[0], aesKey, explicitIv0));
                for (int i = 1; i < 4; i++)
                    File.WriteAllBytes(Path.Combine(dAes, $"seg{i}.enc"), EncryptCbc(segs[i], aesKey, SeqIv(40 + i)));

                string aesM3u =
                    "#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:40\n" +
                    $"#EXT-X-KEY:METHOD=AES-128,URI=\"/rf/{tAes}/enc.key\",IV=0x00112233445566778899AABBCCDDEEFF\n" +
                    "#EXTINF:4.0,\nseg0.enc\n" +
                    $"#EXT-X-KEY:METHOD=AES-128,URI=\"/rf/{tAes}/enc.key\"\n" +
                    "#EXTINF:4.0,\nseg1.enc\n#EXTINF:4.0,\nseg2.enc\n#EXTINF:4.0,\nseg3.enc\n#EXT-X-ENDLIST\n";
                File.WriteAllText(Path.Combine(dAes, "index.m3u8"), aesM3u);

                var aesItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tAes}/index.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf13-aes-resume.ts",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await HoldRfSubAsync(tAes, "seg2.enc");
                using (var ctsAes = new CancellationTokenSource())
                {
                    var runAes = StreamDownloader.RunAsync(aesItem, limiter, ctsAes.Token);
                    await WaitForRfHitAsync(tAes, "seg2.enc");
                    ctsAes.Cancel();
                    await UnholdRfSubAsync(tAes, "seg2.enc");
                    try { await runAes; } catch (OperationCanceledException) { /* expected pause */ }
                }
                await ResetRfHitsAsync(tAes);
                await StreamDownloader.RunAsync(aesItem, limiter, CancellationToken.None);
                var aesResumeHits = await GetRfHitsAsync(tAes);
                bool aesResumeOk = !aesResumeHits.Contains("seg0.enc", StringComparer.Ordinal)
                                   && !aesResumeHits.Contains("seg1.enc", StringComparer.Ordinal)
                                   && aesResumeHits.Contains("seg2.enc", StringComparer.Ordinal)
                                   && aesResumeHits.Contains("seg3.enc", StringComparer.Ordinal)
                                   && HashFile(aesItem.FullPath) == expected4Hash
                                   && !File.Exists(aesItem.TempPath + ".state");

                // 13d: DASH resume + DownloadManager.RemoveAsync deletes sidecar + live stream clears stale sidecar
                string tDash = rfRunTagPrefix + "_dash";
                string dDash = Path.Combine(rfServedSubDir, tDash);
                Directory.CreateDirectory(dDash);
                byte[] dInit = "DASH_INIT_HDR_"u8.ToArray();
                File.WriteAllBytes(Path.Combine(dDash, "a_init.mp4"), dInit);
                for (int i = 1; i <= 3; i++)
                    File.WriteAllBytes(Path.Combine(dDash, $"a_seg{i}.m4s"), segs[i - 1]);
                string dashMpd =
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT6S\">\n" +
                    "  <Period duration=\"PT6S\">\n" +
                    "    <AdaptationSet contentType=\"audio\" mimeType=\"audio/mp4\">\n" +
                    $"      <SegmentTemplate initialization=\"/rf/{tDash}/a_init.mp4\" media=\"/rf/{tDash}/a_seg$Number$.m4s\" startNumber=\"1\" duration=\"2\" timescale=\"1\"/>\n" +
                    "      <Representation id=\"aud1\" bandwidth=\"128000\"/>\n" +
                    "    </AdaptationSet>\n" +
                    "  </Period>\n" +
                    "</MPD>\n";
                File.WriteAllText(Path.Combine(dDash, "manifest.mpd"), dashMpd);

                var dashItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tDash}/manifest.mpd",
                    MediaQuality = "m4a",
                    SaveDir = rfOutDir,
                    FileName = "rf13-dash-resume.m4a",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await HoldRfSubAsync(tDash, "a_seg2.m4s");
                using (var ctsDash = new CancellationTokenSource())
                {
                    var runDash = StreamDownloader.RunAsync(dashItem, limiter, ctsDash.Token);
                    await WaitForRfHitAsync(tDash, "a_seg2.m4s");
                    ctsDash.Cancel();
                    await UnholdRfSubAsync(tDash, "a_seg2.m4s");
                    try { await runDash; } catch (OperationCanceledException) { /* expected pause */ }
                }
                await ResetRfHitsAsync(tDash);
                await StreamDownloader.RunAsync(dashItem, limiter, CancellationToken.None);
                var dashHits = await GetRfHitsAsync(tDash);
                bool dashResumeOk = !dashHits.Contains("a_init.mp4", StringComparer.Ordinal)
                                    && !dashHits.Contains("a_seg1.m4s", StringComparer.Ordinal)
                                    && dashHits.Contains("a_seg2.m4s", StringComparer.Ordinal)
                                    && dashHits.Contains("a_seg3.m4s", StringComparer.Ordinal)
                                    && HashFile(dashItem.FullPath) == HashBytes([.. dInit, .. segs[0], .. segs[1], .. segs[2]])
                                    && !File.Exists(dashItem.TempPath + ".state");

                // RemoveAsync deletes partial + sidecar
                var mgrSettings = new AppSettings { ProxyMode = "none", DownloadRoot = rfOutDir, TempDir = "" };
                mgrSettings.Normalize();
                var mgr = new DownloadManager(mgrSettings);
                bool removeCleansSidecar;
                try
                {
                    var remStreamItem = new DownloadItem
                    {
                        Url = $"{baseUrl}/rf/{tHls}/index.m3u8",
                        SaveDir = rfOutDir,
                        FileName = "rf13-remove-sidecar.ts",
                        NameLocked = true,
                        MaxConnections = 1,
                    };
                    await HoldRfSubAsync(tHls, "seg1.ts");
                    using (var ctsRem = new CancellationTokenSource())
                    {
                        var runRem = StreamDownloader.RunAsync(remStreamItem, limiter, ctsRem.Token);
                        await WaitForRfHitAsync(tHls, "seg1.ts");
                        ctsRem.Cancel();
                        await UnholdRfSubAsync(tHls, "seg1.ts");
                        try { await runRem; } catch (OperationCanceledException) { /* expected pause */ }
                    }
                    string remTmp = remStreamItem.TempPath;
                    bool hadSidecarBeforeRemove = File.Exists(remTmp) && File.Exists(remTmp + ".state");
                    mgr.Items.Add(remStreamItem);
                    await mgr.RemoveAsync(remStreamItem, deleteFile: false);
                    removeCleansSidecar = hadSidecarBeforeRemove && !File.Exists(remTmp) && !File.Exists(remTmp + ".state");
                }
                finally
                {
                    mgr.Shutdown();
                }

                // Separate video + audio tracks resume each track's own .vpart / .apart file
                bool twoTrackResumeOk = true;
                string dashFixtureDir = Path.Combine(servedDir, "dash");
                if (File.Exists(Path.Combine(dashFixtureDir, "v_1080_init.mp4"))
                    && File.Exists(Path.Combine(dashFixtureDir, "v_1080_seg1.m4s"))
                    && File.Exists(Path.Combine(dashFixtureDir, "a_init.mp4"))
                    && File.Exists(Path.Combine(dashFixtureDir, "a_seg1.m4s")))
                {
                    string tDual = rfRunTagPrefix + "_dual";
                    string dDual = Path.Combine(rfServedSubDir, tDual);
                    Directory.CreateDirectory(dDual);
                    File.Copy(Path.Combine(dashFixtureDir, "v_1080_init.mp4"), Path.Combine(dDual, "v_init.mp4"), true);
                    File.Copy(Path.Combine(dashFixtureDir, "v_1080_seg1.m4s"), Path.Combine(dDual, "v_seg1.m4s"), true);
                    File.Copy(Path.Combine(dashFixtureDir, "v_1080_seg1.m4s"), Path.Combine(dDual, "v_seg2.m4s"), true);
                    File.Copy(Path.Combine(dashFixtureDir, "a_init.mp4"), Path.Combine(dDual, "a_init.mp4"), true);
                    File.Copy(Path.Combine(dashFixtureDir, "a_seg1.m4s"), Path.Combine(dDual, "a_seg1.m4s"), true);
                    File.Copy(Path.Combine(dashFixtureDir, "a_seg1.m4s"), Path.Combine(dDual, "a_seg2.m4s"), true);

                    string dualMpd =
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                        "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT4S\">\n" +
                        "  <Period duration=\"PT4S\">\n" +
                        "    <AdaptationSet contentType=\"video\" mimeType=\"video/mp4\">\n" +
                        $"      <SegmentTemplate initialization=\"/rf/{tDual}/v_init.mp4\" media=\"/rf/{tDual}/v_seg$Number$.m4s\" startNumber=\"1\" duration=\"2\" timescale=\"1\"/>\n" +
                        "      <Representation id=\"1080\" bandwidth=\"4000000\" width=\"1920\" height=\"1080\"/>\n" +
                        "    </AdaptationSet>\n" +
                        "    <AdaptationSet contentType=\"audio\" mimeType=\"audio/mp4\">\n" +
                        $"      <SegmentTemplate initialization=\"/rf/{tDual}/a_init.mp4\" media=\"/rf/{tDual}/a_seg$Number$.m4s\" startNumber=\"1\" duration=\"2\" timescale=\"1\"/>\n" +
                        "      <Representation id=\"aud1\" bandwidth=\"128000\"/>\n" +
                        "    </AdaptationSet>\n" +
                        "  </Period>\n" +
                        "</MPD>\n";
                    File.WriteAllText(Path.Combine(dDual, "manifest.mpd"), dualMpd);

                    var dualItem = new DownloadItem
                    {
                        Url = $"{baseUrl}/rf/{tDual}/manifest.mpd",
                        SaveDir = rfOutDir,
                        FileName = "rf13-dual-resume.mp4",
                        NameLocked = true,
                        MaxConnections = 1,
                    };
                    // Hold on a_seg2.m4s -> video (2/2) is done and audio (1/2) is done
                    await HoldRfSubAsync(tDual, "a_seg2.m4s");
                    using (var ctsDual = new CancellationTokenSource())
                    {
                        var runDual = StreamDownloader.RunAsync(dualItem, limiter, ctsDual.Token);
                        await WaitForRfHitAsync(tDual, "a_seg2.m4s");
                        ctsDual.Cancel();
                        await UnholdRfSubAsync(tDual, "a_seg2.m4s");
                        try { await runDual; } catch (OperationCanceledException) { /* expected pause */ }
                    }
                    bool hadBothTrackSidecars = File.Exists(dualItem.TempPath + ".vpart")
                                                && File.Exists(dualItem.TempPath + ".vpart.state")
                                                && File.Exists(dualItem.TempPath + ".apart")
                                                && File.Exists(dualItem.TempPath + ".apart.state");

                    await ResetRfHitsAsync(tDual);
                    await StreamDownloader.RunAsync(dualItem, limiter, CancellationToken.None);
                    var dualHits = await GetRfHitsAsync(tDual);
                    twoTrackResumeOk = hadBothTrackSidecars
                                       && !dualHits.Contains("v_init.mp4", StringComparer.Ordinal)
                                       && !dualHits.Contains("v_seg1.m4s", StringComparer.Ordinal)
                                       && !dualHits.Contains("v_seg2.m4s", StringComparer.Ordinal)
                                       && !dualHits.Contains("a_init.mp4", StringComparer.Ordinal)
                                       && !dualHits.Contains("a_seg1.m4s", StringComparer.Ordinal)
                                       && dualHits.Contains("a_seg2.m4s", StringComparer.Ordinal)
                                       && File.Exists(dualItem.FullPath)
                                       && new FileInfo(dualItem.FullPath).Length > 0
                                       && !File.Exists(dualItem.TempPath + ".vpart")
                                       && !File.Exists(dualItem.TempPath + ".vpart.state")
                                       && !File.Exists(dualItem.TempPath + ".apart")
                                       && !File.Exists(dualItem.TempPath + ".apart.state");
                }

                // Live stream (no #EXT-X-ENDLIST) wipes stale partial/sidecar and refuses with err_live
                var liveItem = new DownloadItem
                {
                    Url = $"{baseUrl}/hls/live.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf13-live.ts",
                    NameLocked = true,
                };
                File.WriteAllBytes(liveItem.TempPath, [1, 2, 3]);
                File.WriteAllText(liveItem.TempPath + ".state", "{}");
                bool liveRefusedAndCleaned = false;
                try { await StreamDownloader.RunAsync(liveItem, limiter, CancellationToken.None); }
                catch (DownloadException ex) when (ex.Message == Loc.T("core.stream.err_live"))
                {
                    liveRefusedAndCleaned = !File.Exists(liveItem.TempPath) && !File.Exists(liveItem.TempPath + ".state");
                }

                Check("RF-13: StreamDownloader resumes HLS, AES-128 HLS, and single/dual-track DASH from next piece, restarts on changed piece list, and cleans sidecars",
                    hlsResumeOk && changedNextUrlRestarts && changedCountRestarts && aesResumeOk && dashResumeOk && removeCleansSidecar && twoTrackResumeOk && liveRefusedAndCleaned,
                    $"hls={hlsResumeOk}, chgUrl={changedNextUrlRestarts}, chgCnt={changedCountRestarts}, aes={aesResumeOk}, dash={dashResumeOk}, dual={twoTrackResumeOk}, rmSidecar={removeCleansSidecar}, live={liveRefusedAndCleaned}");
            });

            // -----------------------------------------------------------------
            // RF-14: Large stream piece (~40 MB) streams via temp file on disk, undoes bytes on mid-piece retry, decrypts AES-128, and deletes temp piece file
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-14", async () =>
            {
                string tBig = rfRunTagPrefix + "_big";
                string dBig = Path.Combine(rfServedSubDir, tBig);
                Directory.CreateDirectory(dBig);

                int bigLen = 40 * 1024 * 1024; // 40 MB (> 32 MB threshold)
                byte[] bigSeg0 = new byte[bigLen];
                new Random(20261119).NextBytes(bigSeg0);
                byte[] smallSeg1 = new byte[64 * 1024];
                new Random(20261120).NextBytes(smallSeg1);

                string expectedCombinedHash;
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    sha.AppendData(bigSeg0);
                    sha.AppendData(smallSeg1);
                    expectedCombinedHash = Convert.ToHexString(sha.GetHashAndReset());
                }

                File.WriteAllBytes(Path.Combine(dBig, "big0.ts"), bigSeg0);
                File.WriteAllBytes(Path.Combine(dBig, "small1.ts"), smallSeg1);
                const string bigM3u = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nbig0.ts\n#EXTINF:2.0,\nsmall1.ts\n#EXT-X-ENDLIST\n";
                File.WriteAllText(Path.Combine(dBig, "index.m3u8"), bigM3u);

                // Drop first attempt at 35 MB (after spilling to disk) so retry must undo all 35 MB and delete attempt 1's temp piece file
                int dropAt = 35 * 1024 * 1024;
                await HttpDownloader.Client.GetStringAsync($"{baseUrl}/rf-ctl?op=drop_once&tag={Uri.EscapeDataString(tBig)}&sub=big0.ts&at={dropAt}");

                var bigItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tBig}/index.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf14-large-piece.ts",
                    NameLocked = true,
                    MaxConnections = 1,
                };

                bool observedPieceTempOnDisk = false;
                using var observerCts = new CancellationTokenSource();
                var observerTask = Task.Run(async () =>
                {
                    while (!observerCts.IsCancellationRequested)
                    {
                        try
                        {
                            if (Directory.GetFiles(rfOutDir, "*.piece.*.tmp").Length > 0)
                            {
                                observedPieceTempOnDisk = true;
                                break;
                            }
                        }
                        catch { /* directory transient scan */ }
                        await Task.Delay(5);
                    }
                });

                try
                {
                    await StreamDownloader.RunAsync(bigItem, limiter, CancellationToken.None);
                }
                finally
                {
                    observerCts.Cancel();
                    try { await observerTask; } catch { /* observer stopped */ }
                }

                var bigHitsRaw = await HttpDownloader.Client.GetStringAsync($"{baseUrl}/rf-ctl?op=hits&tag={Uri.EscapeDataString(tBig)}");
                int big0Requests = bigHitsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Count(x => x == "big0.ts");
                long expectedTotalBytes = (long)bigLen + smallSeg1.Length;
                bool hashMatches = File.Exists(bigItem.FullPath) && HashFile(bigItem.FullPath) == expectedCombinedHash;
                bool noDoubleCount = bigItem.Downloaded == expectedTotalBytes;
                bool noLeftoverTempPieces = Directory.GetFiles(rfOutDir, "*.piece.*.tmp", SearchOption.AllDirectories).Length == 0;

                // AES-128 encrypted >32 MB piece growing past 32 MB with unknown Content-Length (?chunked=1)
                int aesPlainLen = 34 * 1024 * 1024 + 13; // non-block-aligned > 32 MB plaintext to exercise PKCS#7 unpadding on disk path
                byte[] aesPlain = new byte[aesPlainLen];
                new Random(20261121).NextBytes(aesPlain);
                string expectedAesPlainHash = HashBytes(aesPlain);
                byte[] k16 = RandomNumberGenerator.GetBytes(16);
                byte[] iv16 = RandomNumberGenerator.GetBytes(16);
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = k16;
                    File.WriteAllBytes(Path.Combine(dBig, "big_aes.enc"), aes.EncryptCbc(aesPlain, iv16, PaddingMode.PKCS7));
                }
                File.WriteAllBytes(Path.Combine(dBig, "big_aes.key"), k16);
                string aesBigM3u =
                    "#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-TARGETDURATION:10\n" +
                    $"#EXT-X-KEY:METHOD=AES-128,URI=\"/rf/{tBig}/big_aes.key\",IV=0x{Convert.ToHexString(iv16)}\n" +
                    "#EXTINF:10.0,\nbig_aes.enc?chunked=1\n#EXT-X-ENDLIST\n";
                File.WriteAllText(Path.Combine(dBig, "aes_big.m3u8"), aesBigM3u);

                var aesBigItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tBig}/aes_big.m3u8",
                    SaveDir = rfOutDir,
                    FileName = "rf14-large-aes.ts",
                    NameLocked = true,
                    MaxConnections = 1,
                };
                await StreamDownloader.RunAsync(aesBigItem, limiter, CancellationToken.None);
                bool aesBigOk = File.Exists(aesBigItem.FullPath)
                                && HashFile(aesBigItem.FullPath) == expectedAesPlainHash
                                && Directory.GetFiles(rfOutDir, "*.piece.*.tmp", SearchOption.AllDirectories).Length == 0;

                Check("RF-14: ~40 MB stream piece streams via temp file on disk, rolls back bytes on retry, decrypts >32 MB AES-128, and deletes temp piece files",
                    observedPieceTempOnDisk && big0Requests == 2 && hashMatches && noDoubleCount && noLeftoverTempPieces && aesBigOk,
                    $"diskObserved={observedPieceTempOnDisk}, big0Calls={big0Requests}, dl={bigItem.Downloaded}/{expectedTotalBytes}, leftover={Directory.GetFiles(rfOutDir, "*.piece.*.tmp").Length}, aesBig={aesBigOk}");
            });

            // -----------------------------------------------------------------
            // RF-15: First-run decision logic for all four combinations of FirstRun / StartHidden and sequential Welcome -> ExtensionOffer order
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-15", async () =>
            {
                var pTrueFalse = ExtensionOfferPolicy.DecideStartupFlow(firstRun: true, startHidden: false);
                var pTrueTrue = ExtensionOfferPolicy.DecideStartupFlow(firstRun: true, startHidden: true);
                var pFalseFalse = ExtensionOfferPolicy.DecideStartupFlow(firstRun: false, startHidden: false);
                var pFalseTrue = ExtensionOfferPolicy.DecideStartupFlow(firstRun: false, startHidden: true);

                bool fourCombosOk =
                    pTrueFalse is { ShowWelcome: true, CheckExtensionOffer: true, ClearFirstRunAfterWelcome: true, OfferAppInstall: true }
                    && pTrueTrue is { ShowWelcome: false, CheckExtensionOffer: false, ClearFirstRunAfterWelcome: false, OfferAppInstall: false }
                    && pFalseFalse is { ShowWelcome: false, CheckExtensionOffer: false, ClearFirstRunAfterWelcome: false, OfferAppInstall: true }
                    && pFalseTrue is { ShowWelcome: false, CheckExtensionOffer: false, ClearFirstRunAfterWelcome: false, OfferAppInstall: false };

                var fakeChrome = new BrowserIntegration.BrowserInfo("Google Chrome", @"Software\Google\Chrome", @"C:\Fake\chrome.exe", "chrome://extensions");
                var events = new List<string>();
                int openDialogs = 0;
                int maxSimultaneousDialogs = 0;
                bool firstRunDuringWelcome = false;
                bool firstRunAtOfferCheck = true;

                var settings = new AppSettings { FirstRun = true };

                // 1) (FirstRun=true, StartHidden=true): must NOT open anything and must NOT clear FirstRun
                await ExtensionOfferPolicy.ExecuteStartupFlowAsync(
                    settings,
                    startHidden: true,
                    showWelcome: () => events.Add("welcome"),
                    checkExtensionOfferAsync: _ => { events.Add("checkOffer"); return Task.FromResult(((IReadOnlyList<BrowserIntegration.BrowserInfo>)[fakeChrome], true)); },
                    showExtensionOffer: _ => events.Add("extensionOffer"),
                    offerAppInstall: () => events.Add("appInstall"),
                    saveSettings: false);
                bool hiddenPreservesFirstRun = events.Count == 0 && settings.FirstRun;

                // 2) (FirstRun=true, StartHidden=false) with eligible browser extension offer:
                //    Welcome and extension offer BOTH run once sequentially, never stacked, and FirstRun clears only after Welcome
                await ExtensionOfferPolicy.ExecuteStartupFlowAsync(
                    settings,
                    startHidden: false,
                    showWelcome: () =>
                    {
                        openDialogs++;
                        maxSimultaneousDialogs = Math.Max(maxSimultaneousDialogs, openDialogs);
                        firstRunDuringWelcome = settings.FirstRun;
                        events.Add("welcome");
                        openDialogs--;
                    },
                    checkExtensionOfferAsync: offerSnapshot =>
                    {
                        firstRunAtOfferCheck = settings.FirstRun;
                        bool should = ExtensionOfferPolicy.ShouldOffer(offerSnapshot, [fakeChrome], _ => false);
                        events.Add("checkOffer");
                        return Task.FromResult(((IReadOnlyList<BrowserIntegration.BrowserInfo>)[fakeChrome], should));
                    },
                    showExtensionOffer: _ =>
                    {
                        openDialogs++;
                        maxSimultaneousDialogs = Math.Max(maxSimultaneousDialogs, openDialogs);
                        ExtensionOfferPolicy.MarkShown(settings, save: false);
                        ExtensionOfferPolicy.RecordChoice(settings, installNow: false, save: false);
                        events.Add("extensionOffer");
                        openDialogs--;
                    },
                    offerAppInstall: () =>
                    {
                        openDialogs++;
                        maxSimultaneousDialogs = Math.Max(maxSimultaneousDialogs, openDialogs);
                        events.Add("appInstall");
                        openDialogs--;
                    },
                    saveSettings: false);

                bool realFirstRunOk = events.SequenceEqual(["welcome", "checkOffer", "extensionOffer", "appInstall"])
                                      && firstRunDuringWelcome
                                      && !firstRunAtOfferCheck
                                      && !settings.FirstRun
                                      && maxSimultaneousDialogs == 1;

                // 3) (FirstRun=false, StartHidden=false): Welcome and ExtensionOffer never repeat; only OfferAppInstall runs
                events.Clear();
                await ExtensionOfferPolicy.ExecuteStartupFlowAsync(
                    settings,
                    startHidden: false,
                    showWelcome: () => events.Add("welcome"),
                    checkExtensionOfferAsync: _ => { events.Add("checkOffer"); return Task.FromResult(((IReadOnlyList<BrowserIntegration.BrowserInfo>)[fakeChrome], true)); },
                    showExtensionOffer: _ => events.Add("extensionOffer"),
                    offerAppInstall: () => events.Add("appInstall"),
                    saveSettings: false);
                bool secondVisibleRunOk = events.SequenceEqual(["appInstall"]);

                // 4) (FirstRun=false, StartHidden=true): nothing runs
                events.Clear();
                await ExtensionOfferPolicy.ExecuteStartupFlowAsync(
                    settings,
                    startHidden: true,
                    showWelcome: () => events.Add("welcome"),
                    checkExtensionOfferAsync: _ => { events.Add("checkOffer"); return Task.FromResult(((IReadOnlyList<BrowserIntegration.BrowserInfo>)[fakeChrome], true)); },
                    showExtensionOffer: _ => events.Add("extensionOffer"),
                    offerAppInstall: () => events.Add("appInstall"),
                    saveSettings: false);
                bool secondHiddenRunOk = events.Count == 0;

                // 5) If Welcome fails before finishing, FirstRun stays true
                var abortedSettings = new AppSettings { FirstRun = true };
                try
                {
                    await ExtensionOfferPolicy.ExecuteStartupFlowAsync(
                        abortedSettings,
                        startHidden: false,
                        showWelcome: () => throw new InvalidOperationException("Welcome aborted"),
                        checkExtensionOfferAsync: _ => Task.FromResult(((IReadOnlyList<BrowserIntegration.BrowserInfo>)[], false)),
                        showExtensionOffer: _ => { },
                        offerAppInstall: () => { },
                        saveSettings: false);
                }
                catch (InvalidOperationException) { /* expected abort */ }
                bool abortKeepsFirstRun = abortedSettings.FirstRun;

                Check("RF-15: Startup first-run helper handles all 4 FirstRun/StartHidden combinations, shows both Welcome and extension offer without stacking, and clears FirstRun only after Welcome",
                    fourCombosOk && hiddenPreservesFirstRun && realFirstRunOk && secondVisibleRunOk && secondHiddenRunOk && abortKeepsFirstRun);
            });

            // -----------------------------------------------------------------
            // RF-16: Intentional web page downloads (manual .html/.htm/.xhtml/.mht saved; browser-captured .html and manual non-html refused)
            // -----------------------------------------------------------------
            await RunCheckAsync("RF-16", async () =>
            {
                string tHtml = rfRunTagPrefix + "_html";
                string dHtml = Path.Combine(rfServedSubDir, tHtml);
                Directory.CreateDirectory(dHtml);

                byte[] articleHtml = "<!DOCTYPE html><html><head><title>Golan Manual Page</title></head><body><p>Saved HTML document.</p></body></html>"u8.ToArray();
                string articleHash = HashBytes(articleHtml);
                File.WriteAllBytes(Path.Combine(dHtml, "article.html"), articleHtml);
                File.WriteAllBytes(Path.Combine(dHtml, "cli_doc.htm"), articleHtml);

                string expectedWebErr = Loc.T("core.http.err_web_page", "127.0.0.1");

                // 1) Manual .html link is saved
                var manualHtmlItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tHtml}/article.html",
                    SaveDir = rfOutDir,
                    AddedManually = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(manualHtmlItem, limiter).RunAsync(CancellationToken.None);
                bool manualHtmlSaved = File.Exists(manualHtmlItem.FullPath)
                                       && manualHtmlItem.FileName == "article.html"
                                       && HashFile(manualHtmlItem.FullPath) == articleHash;

                // CLI add path sets AddedManually = true and saves .htm
                var cliSettings = new AppSettings { ProxyMode = "none", DownloadRoot = rfOutDir, TempDir = rfOutDir };
                cliSettings.Normalize();
                var cliMgr = new DownloadManager(cliSettings);
                bool cliHtmlSaved;
                try
                {
                    var cliTarget = new DownloadManagerCliTarget(cliMgr);
                    var (cliOk, _) = await cliTarget.AddDownloadAsync(new CliAddRequest(
                        Url: $"{baseUrl}/rf/{tHtml}/cli_doc.htm",
                        Folder: rfOutDir,
                        StartNow: false));
                    var cliItem = cliMgr.Items.FirstOrDefault();
                    bool flagSetByCli = cliOk && cliItem is { AddedManually: true };
                    if (cliItem != null)
                        await new HttpDownloader(cliItem, limiter).RunAsync(CancellationToken.None);
                    cliHtmlSaved = flagSetByCli && cliItem != null && File.Exists(cliItem.FullPath) && HashFile(cliItem.FullPath) == articleHash;
                }
                finally
                {
                    cliMgr.Shutdown();
                }

                // Manual non-html URL with user-locked .xhtml name is also saved
                var lockedXhtmlItem = new DownloadItem
                {
                    Url = $"{baseUrl}/html-page",
                    SaveDir = rfOutDir,
                    FileName = "rf16-locked.xhtml",
                    NameLocked = true,
                    AddedManually = true,
                    MaxConnections = 1,
                };
                await new HttpDownloader(lockedXhtmlItem, limiter).RunAsync(CancellationToken.None);
                bool lockedXhtmlSaved = File.Exists(lockedXhtmlItem.FullPath) && new FileInfo(lockedXhtmlItem.FullPath).Length > 0;

                // 2) Browser-captured .html link (AddedManually == false) is still refused with err_web_page
                var browserHtmlItem = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tHtml}/article.html",
                    SaveDir = rfOutDir,
                    FileName = "rf16-browser-captured.html",
                    NameLocked = true,
                    AddedManually = false,
                    MaxConnections = 1,
                };
                DownloadException? browserEx = null;
                try { await new HttpDownloader(browserHtmlItem, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { browserEx = ex; }
                bool browserHtmlRefused = browserEx != null
                                          && browserEx.Message == expectedWebErr
                                          && !File.Exists(browserHtmlItem.FullPath);

                var browserUnlockedHtml = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tHtml}/article.html",
                    SaveDir = rfOutDir,
                    AddedManually = false,
                    MaxConnections = 1,
                };
                DownloadException? browserUnlockedEx = null;
                try { await new HttpDownloader(browserUnlockedHtml, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { browserUnlockedEx = ex; }
                bool browserUnlockedRefused = browserUnlockedEx != null && browserUnlockedEx.Message == expectedWebErr;

                // 3) Manual link to a non-html name that returns HTML is still refused with err_web_page
                var manualNonHtmlUrl = new DownloadItem
                {
                    Url = $"{baseUrl}/html-page",
                    SaveDir = rfOutDir,
                    FileName = "rf16-expected-archive.zip",
                    NameLocked = true,
                    AddedManually = true,
                    MaxConnections = 1,
                };
                DownloadException? nonHtmlEx1 = null;
                try { await new HttpDownloader(manualNonHtmlUrl, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { nonHtmlEx1 = ex; }

                var manualLockedNonHtmlOnHtmlUrl = new DownloadItem
                {
                    Url = $"{baseUrl}/rf/{tHtml}/article.html",
                    SaveDir = rfOutDir,
                    FileName = "rf16-locked-binary.bin",
                    NameLocked = true,
                    AddedManually = true,
                    MaxConnections = 1,
                };
                DownloadException? nonHtmlEx2 = null;
                try { await new HttpDownloader(manualLockedNonHtmlOnHtmlUrl, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { nonHtmlEx2 = ex; }

                bool manualNonHtmlRefused = nonHtmlEx1 != null && nonHtmlEx1.Message == expectedWebErr && !File.Exists(manualNonHtmlUrl.FullPath)
                                            && nonHtmlEx2 != null && nonHtmlEx2.Message == expectedWebErr && !File.Exists(manualLockedNonHtmlOnHtmlUrl.FullPath);

                // 4) Sharing-host resolver and sync-queue items are never treated as intentional web page downloads
                bool exclusionsHold =
                    !HttpDownloader.IsIntentionalWebPageDownload(
                        new DownloadItem { Url = "https://gofile.io/d/gfShare1", FileName = "share.html", NameLocked = true, AddedManually = true },
                        new Uri("https://gofile.io/d/gfShare1"))
                    && !HttpDownloader.IsIntentionalWebPageDownload(
                        new DownloadItem { Url = "https://example.com/page.html", AddedManually = true, QueueId = QueueDef.SyncId },
                        new Uri("https://example.com/page.html"))
                    && !HttpDownloader.IsIntentionalWebPageDownload(
                        new DownloadItem { Url = "https://example.com/page.html", AddedManually = true, TriggeredBySync = true },
                        new Uri("https://example.com/page.html"));

                Check("RF-16: Manual .html/.htm/.xhtml links save HTML body while browser-captured .html, manual non-html names, resolvers, and sync queue still refuse with err_web_page",
                    manualHtmlSaved && cliHtmlSaved && lockedXhtmlSaved && browserHtmlRefused && browserUnlockedRefused && manualNonHtmlRefused && exclusionsHold,
                    $"manual={manualHtmlSaved}, cli={cliHtmlSaved}, xhtml={lockedXhtmlSaved}, browserRefused={browserHtmlRefused && browserUnlockedRefused}, nonHtmlRefused={manualNonHtmlRefused}, excl={exclusionsHold}");
            });
        }
        finally
        {
            HttpDownloader.ResolverInvoker = null;
            HttpDownloader.Configure(defaultSettings);
            DownloadItem.TempRoot = "";
            try { if (Directory.Exists(rfOutDir)) Directory.Delete(rfOutDir, recursive: true); } catch { /* best-effort cleanup */ }
            try
            {
                if (Directory.Exists(rfServedSubDir))
                {
                    foreach (var d in Directory.GetDirectories(rfServedSubDir, rfRunTagPrefix + "*"))
                        Directory.Delete(d, recursive: true);
                }
            }
            catch { /* best-effort served fixture cleanup */ }
        }

        Console.WriteLine($"[REVIEW-FIXES Summary] {_passes} passed, {_fails} failed");
        return _fails;
    }
}
