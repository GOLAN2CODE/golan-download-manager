using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Resolvers;
using Golan.Gdm.Integration;

public static class Tests_LinksAndVideo
{
    static int _passes;
    static int _fails;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [LINKS-VIDEO] {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
        if (ok) _passes++; else _fails++;
    }

    static string HashBytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Forwards public https://&lt;host&gt;/&lt;path&gt; resolver requests to local testserver.py /fakehost/&lt;host&gt;/&lt;path&gt;
    /// while preserving original RequestUri so HostResolvers.IsSafePublicHttpsUri remains unmodified.
    /// </summary>
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

    /// <summary>Minimal loopback passive FTP server supporting USER/PASS, SIZE, REST, EPSV, RETR, and QUIT.</summary>
    sealed class MiniFtpServer : IDisposable
    {
        readonly TcpListener _controlListener;
        readonly CancellationTokenSource _cts = new();
        readonly Dictionary<string, byte[]> _files;
        readonly string _requiredUser;
        readonly string _requiredPass;
        public int Port { get; }

        public MiniFtpServer(Dictionary<string, byte[]> files, string requiredUser = "anonymous", string requiredPass = "")
        {
            _files = files;
            _requiredUser = requiredUser;
            _requiredPass = requiredPass;
            _controlListener = new TcpListener(IPAddress.Loopback, 0);
            _controlListener.Start();
            Port = ((IPEndPoint)_controlListener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
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
            try
            {
                using (control)
                await using (var net = control.GetStream())
                using (var reader = new StreamReader(net, Encoding.UTF8, false, 4096, leaveOpen: true))
                await using (var writer = new StreamWriter(net, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
                {
                    await writer.WriteLineAsync("220 MiniFtpServer Ready").ConfigureAwait(false);
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
                            case "PBSZ":
                            case "PROT":
                                await writer.WriteLineAsync("200 OK").ConfigureAwait(false);
                                break;
                            case "SIZE":
                                if (!authed) { await writer.WriteLineAsync("530 Not logged in").ConfigureAwait(false); break; }
                                if (_files.TryGetValue(arg, out var szBytes))
                                    await writer.WriteLineAsync($"213 {szBytes.Length.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
                                else
                                    await writer.WriteLineAsync("550 File not found").ConfigureAwait(false);
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
                                if (pasvListener == null || !_files.TryGetValue(arg, out var fileBytes))
                                {
                                    await writer.WriteLineAsync("550 Cannot open file").ConfigureAwait(false);
                                    break;
                                }
                                await writer.WriteLineAsync("150 Opening BINARY mode data connection").ConfigureAwait(false);
                                using (var dataClient = await pasvListener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false))
                                await using (var dataStream = dataClient.GetStream())
                                {
                                    int start = (int)Math.Clamp(restOffset, 0, fileBytes.Length);
                                    restOffset = 0;
                                    if (start < fileBytes.Length)
                                        await dataStream.WriteAsync(fileBytes.AsMemory(start), _cts.Token).ConfigureAwait(false);
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
                try { pasvListener?.Stop(); } catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _controlListener.Stop(); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    static byte[] MakeMp4Box(string type, params byte[][] payloads)
    {
        int totalPayload = payloads.Sum(p => p.Length);
        var buf = new byte[8 + totalPayload];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)buf.Length);
        Encoding.ASCII.GetBytes(type, buf.AsSpan(4, 4));
        int pos = 8;
        foreach (var p in payloads)
        {
            p.CopyTo(buf.AsSpan(pos));
            pos += p.Length;
        }
        return buf;
    }

    static byte[] BuildFakeFmp4Init(uint trackId, string handlerFourCc)
    {
        var ftyp = MakeMp4Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\x02\0isomiso6mp41"));

        var mvhdPayload = new byte[100]; // version 0: next_track_ID at offset 96
        BinaryPrimitives.WriteUInt32BigEndian(mvhdPayload.AsSpan(12, 4), 1000); // timescale
        BinaryPrimitives.WriteUInt32BigEndian(mvhdPayload.AsSpan(16, 4), 2000); // duration
        BinaryPrimitives.WriteUInt32BigEndian(mvhdPayload.AsSpan(96, 4), trackId + 1);
        var mvhd = MakeMp4Box("mvhd", mvhdPayload);

        var tkhdPayload = new byte[84]; // version 0: track_ID at offset 12
        tkhdPayload[3] = 0x07; // track_enabled | in_movie | in_preview
        BinaryPrimitives.WriteUInt32BigEndian(tkhdPayload.AsSpan(12, 4), trackId);
        var tkhd = MakeMp4Box("tkhd", tkhdPayload);

        var hdlrPayload = new byte[24];
        Encoding.ASCII.GetBytes(handlerFourCc, hdlrPayload.AsSpan(8, 4));
        var mdia = MakeMp4Box("mdia", MakeMp4Box("hdlr", hdlrPayload));
        var trak = MakeMp4Box("trak", tkhd, mdia);

        var trexPayload = new byte[24]; // version 0: track_ID at offset 4
        BinaryPrimitives.WriteUInt32BigEndian(trexPayload.AsSpan(4, 4), trackId);
        BinaryPrimitives.WriteUInt32BigEndian(trexPayload.AsSpan(8, 4), 1);
        var mvex = MakeMp4Box("mvex", MakeMp4Box("trex", trexPayload));

        var moov = MakeMp4Box("moov", mvhd, trak, mvex);
        return [.. ftyp, .. moov];
    }

    static byte[] BuildFakeFmp4Segment(uint trackId, uint seqNum, byte[] samplePayload)
    {
        var mfhdPayload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(mfhdPayload.AsSpan(4, 4), seqNum);
        var mfhd = MakeMp4Box("mfhd", mfhdPayload);

        var tfhdPayload = new byte[8]; // version 0: track_ID at offset 4
        BinaryPrimitives.WriteUInt32BigEndian(tfhdPayload.AsSpan(4, 4), trackId);
        var traf = MakeMp4Box("traf", MakeMp4Box("tfhd", tfhdPayload));

        var moof = MakeMp4Box("moof", mfhd, traf);
        var mdat = MakeMp4Box("mdat", samplePayload);
        return [.. moof, .. mdat];
    }

    static byte[] EncryptAes128Cbc(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }

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

    public static async Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        _passes = 0;
        _fails = 0;
        Console.WriteLine("=== Links & Video Parity Suite (Task 2) ===");

        var defaultSettings = new AppSettings { ProxyMode = "none" };
        defaultSettings.Normalize();
        HttpDownloader.Configure(defaultSettings);
        var limiter = new SpeedLimiter();

        string lvOutDir = Path.Combine(outDir, "_links_video_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(lvOutDir);

        // Prepare HLS & DASH served fixtures in servedDir
        string hlsDir = Path.Combine(servedDir, "hls");
        string dashDir = Path.Combine(servedDir, "dash");
        Directory.CreateDirectory(hlsDir);
        Directory.CreateDirectory(dashDir);

        byte[] aesKey = [0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F];
        byte[] explicitIv = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F];
        byte[] seq11Iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(seq11Iv.AsSpan(8, 8), 11L); // MEDIA-SEQUENCE=10 -> second segment has sequence 11

        byte[] seg0Plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("HLS-1080P-SEG0-PAYLOAD|", 120)));
        byte[] seg1Plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("HLS-1080P-SEG1-PAYLOAD|", 140)));
        byte[] expectedHls1080 = [.. seg0Plain, .. seg1Plain];

        File.WriteAllBytes(Path.Combine(hlsDir, "enc.key"), aesKey);
        File.WriteAllBytes(Path.Combine(hlsDir, "seg0.enc"), EncryptAes128Cbc(seg0Plain, aesKey, explicitIv));
        File.WriteAllBytes(Path.Combine(hlsDir, "seg1.enc"), EncryptAes128Cbc(seg1Plain, aesKey, seq11Iv));
        File.WriteAllBytes(Path.Combine(hlsDir, "seg360.ts"), Encoding.ASCII.GetBytes("LOW_QUALITY_360P_SEGMENT"));

        byte[] packedBin = new byte[4096];
        Array.Fill(packedBin, (byte)'A', 0, 1024);
        Array.Fill(packedBin, (byte)'X', 1024, 1024);
        Array.Fill(packedBin, (byte)'B', 2048, 1024);
        Array.Fill(packedBin, (byte)'Z', 3072, 1024);
        File.WriteAllBytes(Path.Combine(hlsDir, "packed.bin"), packedBin);
        byte[] expectedByterange = [.. packedBin.AsSpan(0, 1024), .. packedBin.AsSpan(2048, 1024)];

        byte[] v720Marker = Encoding.ASCII.GetBytes("DASH_VIDEO_720P_MARKER_SAMPLE_DATA");
        byte[] v1080Marker = Encoding.ASCII.GetBytes("DASH_VIDEO_1080P_BEST_QUALITY_SAMPLE_DATA");
        byte[] audioMarker = Encoding.ASCII.GetBytes("DASH_AUDIO_AAC_128K_SAMPLE_DATA");

        byte[] v1080Init = BuildFakeFmp4Init(1u, "vide");
        byte[] v1080Seg1 = BuildFakeFmp4Segment(1u, 1u, v1080Marker);
        File.WriteAllBytes(Path.Combine(dashDir, "v_720_init.mp4"), BuildFakeFmp4Init(1u, "vide"));
        File.WriteAllBytes(Path.Combine(dashDir, "v_720_seg1.m4s"), BuildFakeFmp4Segment(1u, 1u, v720Marker));
        File.WriteAllBytes(Path.Combine(dashDir, "v_1080_init.mp4"), v1080Init);
        File.WriteAllBytes(Path.Combine(dashDir, "v_1080_seg1.m4s"), v1080Seg1);
        File.WriteAllBytes(Path.Combine(dashDir, "a_init.mp4"), BuildFakeFmp4Init(1u, "soun"));
        File.WriteAllBytes(Path.Combine(dashDir, "a_seg1.m4s"), BuildFakeFmp4Segment(1u, 1u, audioMarker));

        string auditPayloadPath = Path.Combine(servedDir, "audit-payload.bin");
        if (!File.Exists(auditPayloadPath))
            File.WriteAllBytes(auditPayloadPath, Encoding.ASCII.GetBytes("AUDIT_BINARY_PAYLOAD_FALLBACK"));
        string auditPayloadHash = HashFile(auditPayloadPath);

        try
        {
            // -----------------------------------------------------------------
            // LV-01: HLS master -> 1080p variant selection + AES-128 explicit & sequence IV decryption
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-01", async () =>
            {
                var info = await StreamDownloader.InspectAsync($"{baseUrl}/hls/master.m3u8", "", "", null, CancellationToken.None);
                bool inspectOk = info.Qualities.Any(q => q.Key == "1080") && info.Qualities.Any(q => q.Key == "360");

                var hlsItem = new DownloadItem
                {
                    Url = $"{baseUrl}/hls/master.m3u8",
                    SaveDir = lvOutDir,
                    FileName = "lv01-master.ts",
                    NameLocked = true,
                    MediaQuality = "best",
                    MaxConnections = 2,
                };
                await StreamDownloader.RunAsync(hlsItem, limiter, CancellationToken.None);
                var actual = File.Exists(hlsItem.FullPath) ? File.ReadAllBytes(hlsItem.FullPath) : [];
                Check("LV-01: HLS master selects 1080p variant and decrypts AES-128 explicit + sequence IV segments",
                    inspectOk && actual.AsSpan().SequenceEqual(expectedHls1080));
            });

            // -----------------------------------------------------------------
            // LV-02: HLS #EXT-X-BYTERANGE & #EXT-X-MAP fMP4 initialization segment
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-02", async () =>
            {
                var brItem = new DownloadItem
                {
                    Url = $"{baseUrl}/hls/byterange.m3u8",
                    SaveDir = lvOutDir,
                    FileName = "lv02-byterange.ts",
                    NameLocked = true,
                };
                await StreamDownloader.RunAsync(brItem, limiter, CancellationToken.None);
                var brBytes = File.Exists(brItem.FullPath) ? File.ReadAllBytes(brItem.FullPath) : [];

                var mapItem = new DownloadItem
                {
                    Url = $"{baseUrl}/hls/fmp4.m3u8",
                    SaveDir = lvOutDir,
                    FileName = "lv02-fmp4.mp4",
                    NameLocked = true,
                };
                await StreamDownloader.RunAsync(mapItem, limiter, CancellationToken.None);
                byte[] expectedFmp4 = [.. v1080Init, .. v1080Seg1];
                var mapBytes = File.Exists(mapItem.FullPath) ? File.ReadAllBytes(mapItem.FullPath) : [];

                Check("LV-02: HLS #EXT-X-BYTERANGE slices sub-segments and #EXT-X-MAP prepends fMP4 init segment",
                    brBytes.AsSpan().SequenceEqual(expectedByterange) && mapBytes.AsSpan().SequenceEqual(expectedFmp4));
            });

            // -----------------------------------------------------------------
            // LV-03: HLS live sliding-window rejection & SAMPLE-AES / Widevine DRM rejection
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-03", async () =>
            {
                var liveItem = new DownloadItem { Url = $"{baseUrl}/hls/live.m3u8", SaveDir = lvOutDir, FileName = "lv03-live.ts", NameLocked = true };
                DownloadException? liveEx = null;
                try { await StreamDownloader.RunAsync(liveItem, limiter, CancellationToken.None); }
                catch (DownloadException ex) { liveEx = ex; }

                var drmItem = new DownloadItem { Url = $"{baseUrl}/hls/drm.m3u8", SaveDir = lvOutDir, FileName = "lv03-drm.ts", NameLocked = true };
                DownloadException? drmEx = null;
                try { await StreamDownloader.RunAsync(drmItem, limiter, CancellationToken.None); }
                catch (DownloadException ex) { drmEx = ex; }

                Check("LV-03: HLS live stream without #EXT-X-ENDLIST and HLS SAMPLE-AES/Widevine DRM are rejected clearly",
                    liveEx != null && liveEx.Message == Loc.T("core.stream.err_live")
                    && drmEx != null && drmEx.NonFallback && drmEx.Message == Loc.T("core.media.err_drm")
                    && !File.Exists(drmItem.FullPath));
            });

            // -----------------------------------------------------------------
            // LV-04: Shortener/redirect -> HLS handoff in HttpDownloader & data-download-url interstitial
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-04", async () =>
            {
                var shortItem = new DownloadItem
                {
                    Url = $"{baseUrl}/short/stream",
                    SaveDir = lvOutDir,
                    FileName = "lv04-short-stream.ts",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(shortItem, limiter).RunAsync(CancellationToken.None);
                var shortBytes = File.Exists(shortItem.FullPath) ? File.ReadAllBytes(shortItem.FullPath) : [];

                var btnItem = new DownloadItem
                {
                    Url = $"{baseUrl}/interstitial/button",
                    SaveDir = lvOutDir,
                    FileName = "lv04-button.bin",
                    NameLocked = true,
                    MaxConnections = 2,
                };
                await new HttpDownloader(btnItem, limiter).RunAsync(CancellationToken.None);

                Check("LV-04: Shortener 302 to .m3u8 delegates from HttpDownloader to StreamDownloader and interstitial data-download-url resolves",
                    shortBytes.AsSpan().SequenceEqual(expectedHls1080)
                    && File.Exists(btnItem.FullPath) && HashFile(btnItem.FullPath) == auditPayloadHash);
            });

            // -----------------------------------------------------------------
            // LV-05: MPEG-DASH (.mpd) separate video+audio download, 2-track fMP4 muxing & DASH DRM rejection
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-05", async () =>
            {
                var dashInfo = await StreamDownloader.InspectAsync($"{baseUrl}/dash/manifest.mpd", "", "", null, CancellationToken.None);
                bool dashQualOk = dashInfo.Qualities.Any(q => q.Key == "1080")
                                  && dashInfo.Qualities.Any(q => q.Key == "720")
                                  && dashInfo.Qualities.Any(q => q.AudioOnly);

                var dashItem = new DownloadItem
                {
                    Url = $"{baseUrl}/dash/manifest.mpd",
                    SaveDir = lvOutDir,
                    FileName = "lv05-merged.mp4",
                    NameLocked = true,
                    MediaQuality = "best",
                };
                await StreamDownloader.RunAsync(dashItem, limiter, CancellationToken.None);
                var mergedBytes = File.Exists(dashItem.FullPath) ? File.ReadAllBytes(dashItem.FullPath) : [];
                string mergedAscii = Encoding.Latin1.GetString(mergedBytes);
                int trakCount = 0;
                for (int idx = 0; (idx = mergedAscii.IndexOf("trak", idx, StringComparison.Ordinal)) >= 0; idx += 4)
                    trakCount++;

                bool has1080 = mergedAscii.Contains("DASH_VIDEO_1080P_BEST_QUALITY_SAMPLE_DATA", StringComparison.Ordinal);
                bool no720 = !mergedAscii.Contains("DASH_VIDEO_720P_MARKER_SAMPLE_DATA", StringComparison.Ordinal);
                bool hasAudio = mergedAscii.Contains("DASH_AUDIO_AAC_128K_SAMPLE_DATA", StringComparison.Ordinal);

                var dashDrmItem = new DownloadItem { Url = $"{baseUrl}/dash/drm.mpd", SaveDir = lvOutDir, FileName = "lv05-drm.mp4", NameLocked = true };
                DownloadException? dashDrmEx = null;
                try { await StreamDownloader.RunAsync(dashDrmItem, limiter, CancellationToken.None); }
                catch (DownloadException ex) { dashDrmEx = ex; }

                Check("LV-05: DASH .mpd selects 1080p video + audio, merges into 2-track fMP4, and rejects Widevine ContentProtection",
                    dashQualOk && trakCount >= 2 && has1080 && no720 && hasAudio
                    && dashDrmEx != null && dashDrmEx.NonFallback && dashDrmEx.Message == Loc.T("core.media.err_drm"),
                    $"traks={trakCount}, len={mergedBytes.Length}");
            });

            // -----------------------------------------------------------------
            // LV-06..LV-11: File Host Resolvers against testserver.py (/fakehost/...)
            // -----------------------------------------------------------------
            using (var fakeHandler = new TestServerFakeHostHandler(baseUrl))
            using (var invoker = HostResolvers.CreateIsolatedInvoker(fakeHandler))
            {
                // LV-06: Google Drive large-file confirm (with 'at' token), Google Docs/Sheets export, and folder NonFallback error
                await RunCheckAsync("LV-06", async () =>
                {
                    var gdLarge = await HostResolvers.TryResolveAsync(new Uri("https://drive.google.com/file/d/large999/view?usp=sharing"), invoker, CancellationToken.None);
                    var gdoc = await HostResolvers.TryResolveAsync(new Uri("https://docs.google.com/document/d/doc123/edit?usp=sharing"), invoker, CancellationToken.None);
                    var gdocPdf = await HostResolvers.TryResolveAsync(new Uri("https://docs.google.com/document/d/doc123/edit?format=pdf"), invoker, CancellationToken.None);
                    var gsheet = await HostResolvers.TryResolveAsync(new Uri("https://docs.google.com/spreadsheets/d/sheet456/edit#gid=0"), invoker, CancellationToken.None);
                    DownloadException? gdFolderEx = null;
                    try { await HostResolvers.TryResolveAsync(new Uri("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp"), invoker, CancellationToken.None); }
                    catch (DownloadException ex) { gdFolderEx = ex; }

                    Check("LV-06: Google Drive large-file 'at' confirm, Docs (docx default, pdf on request)/Sheets export, and folder NonFallback rejection",
                        gdLarge != null && gdLarge.FileName == "BigArchive.mkv" && gdLarge.Direct.Query.Contains("at=tok-at-77", StringComparison.Ordinal)
                        && gdoc != null && gdoc.FileName == "Quarterly_Plan.docx"
                        && gdocPdf != null && gdocPdf.FileName == "Quarterly_Plan.pdf"
                        && gsheet != null && gsheet.FileName == "Budget_2026.xlsx"
                        && gdFolderEx != null && gdFolderEx.NonFallback,
                        $"large={gdLarge?.FileName}|{gdLarge?.Direct} doc={gdoc?.FileName} docPdf={gdocPdf?.FileName} sheet={gsheet?.FileName} folderEx={gdFolderEx?.NonFallback}");
                });

                // LV-07: GitHub /releases/latest & /releases/tag/<tag> + VB-06 invariant (/releases/download/... unhandled)
                await RunCheckAsync("LV-07", async () =>
                {
                    var ghLatest = await HostResolvers.TryResolveAsync(new Uri("https://github.com/acme/player/releases/latest"), invoker, CancellationToken.None);
                    var ghTag = await HostResolvers.TryResolveAsync(new Uri("https://github.com/acme/player/releases/tag/v2.3.0"), invoker, CancellationToken.None);
                    bool ghDirectUnhandled = !HostResolvers.All.Any(r => r.CanHandle(new Uri("https://github.com/acme/player/releases/download/v2.4.0/app.zip")));

                    Check("LV-07: GitHub releases/latest & releases/tag pick binary asset via API while keeping releases/download direct",
                        ghLatest != null && ghLatest.FileName == "acme-player-win-x64.zip" && ghLatest.Size == 131072
                        && ghTag != null && ghTag.FileName == "acme-player-v2.3.0.msi"
                        && ghDirectUnhandled);
                });

                // LV-08: SourceForge countdown page rewrite
                await RunCheckAsync("LV-08", async () =>
                {
                    var sf = await HostResolvers.TryResolveAsync(new Uri("https://sourceforge.net/projects/sevenzip/files/7-Zip/24.08/7z2408-x64.exe/download"), invoker, CancellationToken.None);
                    Check("LV-08: SourceForge /projects/.../download rewrites to downloads.sourceforge.net mirror selector",
                        sf != null && sf.FileName == "7z2408-x64.exe"
                        && sf.Direct.ToString() == "https://downloads.sourceforge.net/project/sevenzip/7-Zip/24.08/7z2408-x64.exe?use_mirror=autoselect");
                });

                // LV-09: Pixeldrain single file (/u/<id>) & multi-file list (/l/<id>) NonFallback error
                await RunCheckAsync("LV-09", async () =>
                {
                    var px = await HostResolvers.TryResolveAsync(new Uri("https://pixeldrain.com/u/pxAbc123"), invoker, CancellationToken.None);
                    DownloadException? pxListEx = null;
                    try { await HostResolvers.TryResolveAsync(new Uri("https://pixeldrain.com/l/listAbc123"), invoker, CancellationToken.None); }
                    catch (DownloadException ex) { pxListEx = ex; }

                    Check("LV-09: Pixeldrain /u/<id> resolves direct API download + info metadata and /l/<id> rejects multi-file list",
                        px != null && px.FileName == "ubuntu-clip.mp4" && px.Size == 5242880
                        && px.Direct.ToString() == "https://pixeldrain.com/api/file/pxAbc123?download"
                        && pxListEx != null && pxListEx.NonFallback);
                });

                // LV-10: GoFile guest account token + /contents/<id> + accountToken cookie & multi-file rejection
                await RunCheckAsync("LV-10", async () =>
                {
                    var gf = await HostResolvers.TryResolveAsync(new Uri("https://gofile.io/d/gfShare1"), invoker, CancellationToken.None);
                    DownloadException? gfMultiEx = null;
                    try { await HostResolvers.TryResolveAsync(new Uri("https://gofile.io/d/gfMulti"), invoker, CancellationToken.None); }
                    catch (DownloadException ex) { gfMultiEx = ex; }

                    Check("LV-10: GoFile /d/<id> resolves direct link with guest accountToken cookie and rejects multi-file folder",
                        gf != null && gf.FileName == "dataset.7z" && gf.Size == 2097152
                        && gf.Headers.TryGetValue("Cookie", out var gfCookie) && gfCookie == "accountToken=gf_guest_tok_99"
                        && gfMultiEx != null && gfMultiEx.NonFallback);
                });

                // LV-11: Telegram public post (t.me/<chan>/<id>), MediaFire page, and OneDrive 1drv.ms 401 -> ?download=1 fallback
                await RunCheckAsync("LV-11", async () =>
                {
                    var tg = await HostResolvers.TryResolveAsync(new Uri("https://t.me/gdm_news/42"), invoker, CancellationToken.None);
                    var mf = await HostResolvers.TryResolveAsync(new Uri("https://www.mediafire.com/file/mf123/setup.exe/file"), invoker, CancellationToken.None);
                    var od = await HostResolvers.TryResolveAsync(new Uri("https://1drv.ms/u/s!ABc"), invoker, CancellationToken.None);

                    Check("LV-11: Telegram public post embed, MediaFire button, and OneDrive ?download=1 fallback resolve cleanly",
                        tg != null && tg.FileName == "release_demo.mp4" && tg.Direct.ToString() == "https://cdn4.telesco.pe/file/tg_demo_42.mp4"
                        && mf != null && mf.Direct.ToString() == "https://download1500.mediafire.com/xyz/mf123/setup.exe"
                        && od != null && od.FileName == "Notes.docx");
                });
            }

            // -----------------------------------------------------------------
            // LV-12: Passive FTP Probe, Full Download, REST Partial Resume & 530 Auth Error
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-12", async () =>
            {
                var ftpPayload = new byte[192 * 1024];
                new Random(778899).NextBytes(ftpPayload);
                string ftpHash = HashBytes(ftpPayload);
                var ftpFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["/pub/release.bin"] = ftpPayload,
                };

                using var ftpSrv = new MiniFtpServer(ftpFiles, requiredUser: "gdmuser", requiredPass: "gdmpass");
                string ftpUrl = $"ftp://127.0.0.1:{ftpSrv.Port}/pub/release.bin";

                var probeItem = new DownloadItem { Url = ftpUrl, Login = "gdmuser", Password = "gdmpass" };
                var probe = await FtpDownloader.ProbeAsync(probeItem, CancellationToken.None);

                var dlItem = new DownloadItem
                {
                    Url = ftpUrl,
                    Login = "gdmuser",
                    Password = "gdmpass",
                    SaveDir = lvOutDir,
                    FileName = "lv12-ftp-full.bin",
                    NameLocked = true,
                };
                await new FtpDownloader(dlItem, limiter).RunAsync(CancellationToken.None);

                // Simulate interrupted partial .gdm file (first 64 KB) and resume via FTP REST
                var resumeItem = new DownloadItem
                {
                    Url = ftpUrl,
                    Login = "gdmuser",
                    Password = "gdmpass",
                    SaveDir = lvOutDir,
                    FileName = "lv12-ftp-resume.bin",
                    NameLocked = true,
                };
                File.WriteAllBytes(resumeItem.TempPath, ftpPayload.AsSpan(0, 64 * 1024).ToArray());
                await new FtpDownloader(resumeItem, limiter).RunAsync(CancellationToken.None);

                var badAuthItem = new DownloadItem
                {
                    Url = ftpUrl,
                    Login = "gdmuser",
                    Password = "wrong",
                    SaveDir = lvOutDir,
                    FileName = "lv12-ftp-bad.bin",
                    NameLocked = true,
                };
                DownloadException? ftpAuthEx = null;
                try { await new FtpDownloader(badAuthItem, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { ftpAuthEx = ex; }

                Check("LV-12: FtpDownloader probes SIZE/REST, completes EPSV download, resumes .gdm partial via REST, and reports 530 auth error",
                    probe.Size == ftpPayload.Length && probe.Resumable && probe.FileName == "release.bin"
                    && File.Exists(dlItem.FullPath) && HashFile(dlItem.FullPath) == ftpHash
                    && File.Exists(resumeItem.FullPath) && HashFile(resumeItem.FullPath) == ftpHash
                    && ftpAuthEx != null && ftpAuthEx.StatusCode == 530 && ftpAuthEx.Message == Loc.T("core.ftp.err_auth"));
            });

            // -----------------------------------------------------------------
            // LV-13: RFC 2397 data: URLs (base64 binary, percent-encoded text, invalid rejection)
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-13", async () =>
            {
                byte[] pngSample = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04];
                string dataPngUrl = "data:image/png;base64," + Convert.ToBase64String(pngSample);
                var dataProbe = DataUrlDownloader.Probe(dataPngUrl);

                var dataPngItem = new DownloadItem { Url = dataPngUrl, SaveDir = lvOutDir };
                await DataUrlDownloader.RunAsync(dataPngItem, limiter, CancellationToken.None);

                string dataTxtUrl = "data:text/plain;charset=utf-8,Hello%20Golan%202026%21";
                var dataTxtItem = new DownloadItem { Url = dataTxtUrl, SaveDir = lvOutDir, FileName = "lv13-note.txt", NameLocked = true };
                await DataUrlDownloader.RunAsync(dataTxtItem, limiter, CancellationToken.None);

                var badDataItem = new DownloadItem { Url = "data:image/png;base64", SaveDir = lvOutDir };
                DownloadException? badDataEx = null;
                try { await DataUrlDownloader.RunAsync(badDataItem, limiter, CancellationToken.None); }
                catch (DownloadException ex) { badDataEx = ex; }

                Check("LV-13: DataUrlDownloader decodes base64 PNG & URL-encoded text and rejects malformed data: URIs",
                    dataProbe.Size == pngSample.Length && dataProbe.FileName != null && dataProbe.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(dataPngItem.FullPath) && File.ReadAllBytes(dataPngItem.FullPath).AsSpan().SequenceEqual(pngSample)
                    && File.Exists(dataTxtItem.FullPath) && File.ReadAllText(dataTxtItem.FullPath) == "Hello Golan 2026!"
                    && badDataEx != null && badDataEx.Message == Loc.T("core.data.err_invalid"));
            });

            // -----------------------------------------------------------------
            // LV-14: DRM detector, yt-dlp plain-word error translation, update prompt & "audio" quality args
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-14", async () =>
            {
                bool drmSitesOk = DrmDetector.IsKnownDrmPlatform("https://www.netflix.com/watch/80057281")
                                  && DrmDetector.IsKnownDrmPlatform("https://www.disneyplus.com/video/abc")
                                  && !DrmDetector.IsKnownDrmPlatform("https://www.youtube.com/watch?v=dQw4w9WgXcQ")
                                  && !DrmDetector.IsKnownDrmPlatform("https://vimeo.com/123456");

                bool errMapOk = MediaDownloader.TranslateError("ERROR: [generic] This video is DRM protected (Widevine)") == Loc.T("core.media.err_drm")
                                && MediaDownloader.TranslateError("ERROR: Video unavailable: This video is not available in your country") == Loc.T("core.media.err_geo")
                                && MediaDownloader.TranslateError("ERROR: Sign in to confirm your age. Use --cookies-from-browser") == Loc.T("core.media.err_login")
                                && MediaDownloader.TranslateError("ERROR: HTTP Error 429: Too Many Requests") == Loc.T("core.media.err_429")
                                && MediaDownloader.TranslateError("ERROR: Premieres in 3 hours") == Loc.T("core.media.err_live_upcoming")
                                && MediaDownloader.TranslateError("ERROR: Unable to extract Initial JS player signature function name") == Loc.T("core.media.err_outdated");

                bool updateRecOk = MediaDownloader.IsToolUpdateRecommended("ERROR: Unable to extract player response; please report this issue")
                                   && !MediaDownloader.IsToolUpdateRecommended("ERROR: This video is DRM protected");

                var ytArgs = MediaArgs.Build(new MediaOptions { Quality = "audio" }, outputTemplate: Path.Combine(lvOutDir, "%(title)s.%(ext)s"), ffmpegDir: @"C:\Tools");
                bool audioArgsOk = ytArgs.Contains("-f") && ytArgs.Contains("ba/b") && ytArgs.Contains("-x") && ytArgs.Contains("--audio-format");

                Check("LV-14: DrmDetector, plain-word yt-dlp error translation, outdated tool detection, and 'audio' MediaArgs",
                    drmSitesOk && errMapOk && updateRecOk && audioArgsOk);
            });

            // -----------------------------------------------------------------
            // LV-15: Clipboard capture, URL scheme validation & CLI Media/FTP routing (data: boundary enforced)
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-15", async () =>
            {
                var clipSettings = new AppSettings();
                clipSettings.Normalize();
                bool clipOk = FileNames.ShouldCaptureClipboardUrl("https://drive.google.com/file/d/1AbCdEfGhIjK/view", clipSettings)
                              && FileNames.ShouldCaptureClipboardUrl("https://gofile.io/d/gfShare1", clipSettings)
                              && FileNames.ShouldCaptureClipboardUrl("https://pixeldrain.com/u/pxAbc123", clipSettings)
                              && FileNames.ShouldCaptureClipboardUrl("https://t.me/gdm_news/42", clipSettings)
                              && FileNames.ShouldCaptureClipboardUrl("https://cdn.example.com/vod/master.m3u8?token=1", clipSettings)
                              && FileNames.ShouldCaptureClipboardUrl("ftp://mirror.example.com/pub/linux.iso", clipSettings)
                              && FileNames.ShouldCaptureClipboardUrl("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", clipSettings)
                              && !FileNames.ShouldCaptureClipboardUrl("https://news.ycombinator.com/item?id=1", clipSettings);

                bool startupOk = FileNames.IsDownloadableUrl("ftp://mirror.example.com/pub/file.zip")
                                 && FileNames.IsDownloadableUrl("ftps://mirror.example.com/pub/file.zip")
                                 && FileNames.IsDownloadableUrl("data:text/plain;base64,SGVsbG8=")
                                 && FileNames.IsNetworkDownloadUrl("ftp://mirror.example.com/pub/file.zip")
                                 && FileNames.IsNetworkDownloadUrl("ftps://mirror.example.com/pub/file.zip")
                                 && !FileNames.IsNetworkDownloadUrl("data:text/plain;base64,SGVsbG8=")
                                 && CliParser.IsValidDownloadUrl("ftp://mirror.example.com/pub/file.zip")
                                 && CliParser.IsValidDownloadUrl("ftps://mirror.example.com/pub/file.zip")
                                 && !CliParser.IsValidDownloadUrl("data:text/plain;base64,SGVsbG8=");

                var cliSettings = new AppSettings { DownloadRoot = lvOutDir, TempDir = lvOutDir };
                cliSettings.Normalize();
                var mgr = new DownloadManager(cliSettings);
                try
                {
                    var cliTarget = new DownloadManagerCliTarget(mgr);
                    var (ytOk, _) = await cliTarget.AddDownloadAsync(new CliAddRequest(
                        Url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                        Folder: lvOutDir,
                        StartNow: false));
                    var (audOk, _) = await cliTarget.AddDownloadAsync(new CliAddRequest(
                        Url: "https://vimeo.com/987654321",
                        Folder: lvOutDir,
                        StartNow: false,
                        Quality: "audio"));
                    var (ftpCliOk, _) = await cliTarget.AddDownloadAsync(new CliAddRequest(
                        Url: "ftp://mirror.example.com/pub/archive.tar.gz",
                        Folder: lvOutDir,
                        StartNow: false));
                    var (dataCliOk, _) = await cliTarget.AddDownloadAsync(new CliAddRequest(
                        Url: "data:text/plain;base64,SGVsbG8=",
                        Folder: lvOutDir,
                        StartNow: false));

                    var ytItem = mgr.Items.FirstOrDefault(i => i.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase));
                    var audItem = mgr.Items.FirstOrDefault(i => i.Url.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase));

                    Check("LV-15: Clipboard captures hosts/streams/FTP/magnet, CLI accepts FTP/FTPS/YouTube/audio while rejecting data: URLs",
                        clipOk && startupOk && ytOk && audOk && ftpCliOk && !dataCliOk
                        && ytItem != null && ytItem.Kind == DownloadKind.Media
                        && audItem != null && audItem.Kind == DownloadKind.Media && audItem.MediaQuality == "audio" && audItem.Category == "music");
                }
                finally
                {
                    mgr.Shutdown();
                }
            });

            // -----------------------------------------------------------------
            // LV-16: FTP Pattern/Import reachability, sharing-size override & plain-word network errors
            // -----------------------------------------------------------------
            await RunCheckAsync("LV-16", async () =>
            {
                var expFtp = Transfer.ExpandBatch("ftp://127.0.0.1/pub/disk*.iso", false, "1", "2", 2)
                    .Where(FileNames.IsNetworkDownloadUrl)
                    .ToList();
                bool patternFtpOk = expFtp.Count == 2 && expFtp[0] == "ftp://127.0.0.1/pub/disk01.iso";

                var impSettings = new AppSettings { DownloadRoot = lvOutDir, TempDir = lvOutDir };
                impSettings.Normalize();
                var impMgr = new DownloadManager(impSettings);
                try
                {
                    string jsonImpPath = Path.Combine(lvOutDir, "lv16-import.json");
                    File.WriteAllText(jsonImpPath, """
                    {
                      "format": "gdm-export",
                      "items": [
                        { "url": "ftp://127.0.0.1/pub/one.bin", "fileName": "one.bin" },
                        { "url": "ftps://127.0.0.1/pub/two.bin", "fileName": "two.bin" },
                        { "url": "file:///C:/Windows/win.ini", "fileName": "win.ini" },
                        { "url": "data:text/plain;base64,SGVsbG8=", "fileName": "hello.txt" }
                      ]
                    }
                    """);
                    var impJson = Transfer.Import(jsonImpPath, impMgr);

                    string txtImpPath = Path.Combine(lvOutDir, "lv16-import.txt");
                    File.WriteAllText(txtImpPath, "ftp://127.0.0.1/pub/three.bin\nftps://127.0.0.1/pub/four.bin\nfile:///C:/nope.bin");
                    var impTxt = Transfer.Import(txtImpPath, impMgr);
                    bool importFtpOk = impJson.Count == 2 && impTxt.Count == 2;

                    using var fakeHost = new TestServerFakeHostHandler(baseUrl);
                    using var invoker = HostResolvers.CreateIsolatedInvoker(fakeHost);
                    var shareItem = new DownloadItem
                    {
                        Url = "https://pixeldrain.com/u/pxAbc123",
                        SaveDir = lvOutDir,
                        TotalSize = 4096, // Stale pre-resolve HTML page probe size
                    };
                    bool shareResolved = await impMgr.TryResolveSharingItemAsync(shareItem, invoker, CancellationToken.None);
                    bool shareSizeOk = shareResolved && shareItem.TotalSize == 5242880 && shareItem.FileName == "ubuntu-clip.mp4";

                    var probeListener = new TcpListener(IPAddress.Loopback, 0);
                    probeListener.Start();
                    int closedPort = ((IPEndPoint)probeListener.LocalEndpoint).Port;
                    probeListener.Stop();

                    DownloadException? ftpRefusedEx = null;
                    try
                    {
                        await FtpDownloader.ProbeAsync(new DownloadItem { Url = $"ftp://127.0.0.1:{closedPort}/missing.bin" }, CancellationToken.None);
                    }
                    catch (DownloadException ex)
                    {
                        ftpRefusedEx = ex;
                    }
                    bool ftpPlainErrOk = ftpRefusedEx != null
                                         && ftpRefusedEx.Retryable
                                         && ftpRefusedEx.Message == Loc.T("core.err.network");

                    Check("LV-16: FTP/FTPS works in Pattern/Import, TryResolveSharingItemAsync refreshes stale TotalSize, and refused FTP fails with plain words",
                        patternFtpOk && importFtpOk && shareSizeOk && ftpPlainErrOk);
                }
                finally
                {
                    impMgr.Shutdown();
                }
            });

            // LV-17: the media tool runner (ffmpeg remux/merge) drains a chatty stderr instead of hanging,
            // and cancelling a download stops the tool instead of leaving it running on the file.
            await RunCheckAsync("LV-17", async () =>
            {
                var chatty = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                chatty.ArgumentList.Add("/c");
                chatty.ArgumentList.Add("for /L %i in (1,1,4000) do @echo corrupt packet in stream %i, skipping 1>&2");
                var runTask = StreamDownloader.RunToolProcessAsync(chatty, CancellationToken.None);
                bool drained = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(60))) == runTask
                               && runTask.IsCompletedSuccessfully && runTask.Result == 0;

                var slow = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                slow.ArgumentList.Add("/c");
                slow.ArgumentList.Add("ping -n 60 127.0.0.1 >nul");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                int? slowPid = null;
                bool cancelled = false;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { await StreamDownloader.RunToolProcessAsync(slow, cts.Token, pid => slowPid = pid); }
                catch (OperationCanceledException) { cancelled = true; }
                bool fast = sw.Elapsed < TimeSpan.FromSeconds(20);
                bool gone = false;
                if (slowPid is int p)
                {
                    try { using var pr = System.Diagnostics.Process.GetProcessById(p); gone = pr.WaitForExit(5000); }
                    catch (ArgumentException) { gone = true; }
                }

                Check("LV-17: media tool runner drains chatty stderr without hanging, and cancel kills the tool",
                    drained && cancelled && fast && gone,
                    $"drained={drained}, cancelled={cancelled}, fast={fast}, pid={slowPid}, gone={gone}");
            });
        }
        finally
        {
            try { Directory.Delete(lvOutDir, true); } catch { /* best-effort cleanup */ }
        }

        Console.WriteLine($"[LINKS-VIDEO Summary] {_passes} passed, {_fails} failed");
        return _fails;
    }
}
