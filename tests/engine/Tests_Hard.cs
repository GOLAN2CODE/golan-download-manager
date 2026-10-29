using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;

public static class Tests_Hard
{
    static bool _hasRun;
    static int _lastFails;

    const int BlockSize = 65536;
    static readonly byte[][] Blocks = BuildBlocks();

    static byte[][] BuildBlocks()
    {
        var baseBlock = new byte[BlockSize];
        for (int i = 0; i < BlockSize; i++)
            baseBlock[i] = (byte)(((i * 73) ^ ((i >> 8) * 199)) & 0xFF);
        var blocks = new byte[256][];
        for (int k = 0; k < 256; k++)
        {
            var b = new byte[BlockSize];
            for (int i = 0; i < BlockSize; i++)
                b[i] = (byte)(baseBlock[i] ^ k);
            blocks[k] = b;
        }
        return blocks;
    }

    static int BlockKey(long blockIdx, int seed)
        => (int)((blockIdx ^ (blockIdx >> 8) ^ (blockIdx >> 16) ^ seed) & 0xFF);

    public static byte[] GenerateSlice(long start, int length, int seed = 0)
    {
        if (length <= 0) return [];
        var res = new byte[length];
        long pos = start;
        int written = 0;
        while (written < length)
        {
            long bIdx = pos >> 16;
            int off = (int)(pos & 0xFFFF);
            int take = Math.Min(length - written, BlockSize - off);
            Buffer.BlockCopy(Blocks[BlockKey(bIdx, seed)], off, res, written, take);
            pos += take;
            written += take;
        }
        return res;
    }

    public static string ExpectedSha256(long size, int seed = 0)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long pos = 0;
        long rem = size;
        while (rem > 0)
        {
            long bIdx = pos >> 16;
            int off = (int)(pos & 0xFFFF);
            int take = (int)Math.Min(rem, BlockSize - off);
            sha.AppendData(Blocks[BlockKey(bIdx, seed)].AsSpan(off, take));
            pos += take;
            rem -= take;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    static string FileSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    static bool HasMotw(string path)
        => File.Exists(path + ":Zone.Identifier")
           && File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal);

    static bool IsInsideDir(string filePath, string dirPath)
    {
        var fullFile = Path.GetFullPath(filePath);
        var fullDir = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetDirectoryName(fullFile), fullDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
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

        int hardPort = int.TryParse(Environment.GetEnvironmentVariable("GDM_HARD_PORT"), out int hp) ? hp : 18802;
        string hardBase = $"http://127.0.0.1:{hardPort}";
        string hardOut = Path.Combine(outDir, "_hard_tmp");
        if (Directory.Exists(hardOut))
        {
            foreach (var f in Directory.GetFiles(hardOut, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { /* ignore cleanup error */ }
            }
        }
        Directory.CreateDirectory(hardOut);

        Process? spawnedServer = null;
        var origSettings = HttpDownloader.Options;
        var origMinPiece = HttpDownloader.MinInitialPiece;
        HttpDownloader.MinInitialPiece = 256 * 1024; // these scenarios use small files with several connections
        var settings = new AppSettings { ProxyMode = "none" };
        settings.Normalize();
        HttpDownloader.Configure(settings);
        var limiter = new SpeedLimiter();

        async Task<bool> IsHardServerUpAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                var pong = await HttpDownloader.Client.GetStringAsync($"{hardBase}/ping", cts.Token);
                return pong.Trim() == "pong";
            }
            catch { return false; }
        }

        try
        {
            if (!await IsHardServerUpAsync())
            {
                string scriptPath = Path.Combine(servedDir, "..", "hardserver.py");
                if (!File.Exists(scriptPath))
                {
                    var d = new DirectoryInfo(AppContext.BaseDirectory);
                    while (d != null && !File.Exists(Path.Combine(d.FullName, "GDM", "tests", "engine", "hardserver.py")))
                        d = d.Parent;
                    if (d != null) scriptPath = Path.Combine(d.FullName, "GDM", "tests", "engine", "hardserver.py");
                }
                var psi = new ProcessStartInfo("python")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-I");
                psi.ArgumentList.Add(Path.GetFullPath(scriptPath));
                psi.ArgumentList.Add(hardPort.ToString());
                spawnedServer = Process.Start(psi);
                for (int i = 0; i < 30 && !await IsHardServerUpAsync(); i++)
                    await Task.Delay(100);
            }

            await HttpDownloader.Client.GetStringAsync($"{hardBase}/reset");

            DownloadItem NewHardItem(string url, string? name = null, int conns = 4) => new()
            {
                Url = url,
                SaveDir = hardOut,
                MaxConnections = conns,
                FileName = name ?? "",
                NameLocked = name != null,
            };

            async Task<JsonObject> GetStatsAsync(string tag)
            {
                var j = await HttpDownloader.Client.GetStringAsync($"{hardBase}/stats?tag={Uri.EscapeDataString(tag)}");
                return JsonNode.Parse(j)!.AsObject();
            }

            async Task<JsonArray> GetLogAsync(string tag)
            {
                var j = await HttpDownloader.Client.GetStringAsync($"{hardBase}/log?tag={Uri.EscapeDataString(tag)}");
                return JsonNode.Parse(j)!.AsArray();
            }

            bool ValidateCompletedItem(DownloadItem item, long expectedSize, int seed, string? expectedFileName = null)
            {
                if (!File.Exists(item.FullPath)) return false;
                if (File.Exists(item.TempPath)) return false;
                if (!IsInsideDir(item.FullPath, hardOut)) return false;
                if (expectedFileName != null && item.FileName != expectedFileName) return false;
                if (new FileInfo(item.FullPath).Length != expectedSize) return false;
                if (FileSha256(item.FullPath) != ExpectedSha256(expectedSize, seed)) return false;
                if (!HasMotw(item.FullPath)) return false;
                return true;
            }

            async Task RunBlock(string label, Func<Task> body)
            {
                try
                {
                    HttpDownloader.ForgetLearnedLimits(); // every block starts with a server it has not met yet
                    await body();
                }
                catch (Exception ex)
                {
                    Check(label, false, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // =========================================================================
            // 1. Throttling, Stall, Reset, Drop Headers
            // =========================================================================

            // H-01: rate=<bytes/s> per-connection throttling
            await RunBlock("H-01: rate=<bytes/s> throttling finishes with valid SHA-256, MOTW, no temp, and timing bounds", async () =>
            {
                var sw = Stopwatch.StartNew();
                var item = NewHardItem($"{hardBase}/f/65536?rate=131072&tag=h01", "h01-rate.bin", conns: 1);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                sw.Stop();
                bool engineFinishedMerging = item.Status == DownloadStatus.Merging;
                item.Status = DownloadStatus.Completed;
                bool ok = engineFinishedMerging
                          && ValidateCompletedItem(item, 65536, 0, "h01-rate.bin")
                          && item.StatusText == Loc.T("core.status.completed")
                          && sw.Elapsed.TotalSeconds is >= 0.35 and <= 4.0;
                Check("H-01: rate=<bytes/s> throttling finishes with valid SHA-256, MOTW, no temp, and timing bounds",
                    ok, $"elapsed={sw.Elapsed.TotalSeconds:F2}s");
            });

            // H-02: stall=<offset>:<seconds> (mid-body stall + read timeout recovery + permanent stall failure)
            await RunBlock("H-02: stall=<offset>:<seconds> recovers via timeout retry and fails cleanly on permanent stall", async () =>
            {
                var prevReadTimeout = HttpDownloader.ReadTimeout;
                var prevBackoff = HttpDownloader.BaseBackoff;
                var prevMaxRetries = HttpDownloader.MaxRetries;
                try
                {
                    HttpDownloader.ReadTimeout = TimeSpan.FromMilliseconds(450);
                    HttpDownloader.BaseBackoff = TimeSpan.FromMilliseconds(100);
                    HttpDownloader.MaxRetries = 3;

                    // Transient stall > ReadTimeout on first attempt, recovers on retry
                    var sw = Stopwatch.StartNew();
                    var item = NewHardItem($"{hardBase}/f/65536?stall=16384:0.9&stall_n=1&tag=h02_ok", "h02-stall-ok.bin", conns: 1);
                    await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                    sw.Stop();
                    var st = await GetStatsAsync("h02_ok");
                    int stallsDone = st["stalls_done"]!.GetValue<int>();
                    int bodyReqs = st["body_requests"]!.GetValue<int>();

                    // Permanent stall fails with localized timeout/IO message
                    var failItem = NewHardItem($"{hardBase}/f/65536?stall=8192:1.0&norange=1&tag=h02_fail", "h02-stall-fail.bin", conns: 1);
                    HttpDownloader.MaxRetries = 1;
                    string errText = "";
                    try { await new HttpDownloader(failItem, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException ex) { errText = ex.Message; failItem.ErrorMessage = ex.Message; failItem.Status = DownloadStatus.Error; }

                    bool ok = ValidateCompletedItem(item, 65536, 0, "h02-stall-ok.bin")
                              && stallsDone == 1 && bodyReqs >= 2
                              && !string.IsNullOrEmpty(errText)
                              && failItem.StatusText == Loc.T("core.status.error", errText)
                              && !File.Exists(failItem.TempPath) && !File.Exists(failItem.FullPath);
                    Check("H-02: stall=<offset>:<seconds> recovers via timeout retry and fails cleanly on permanent stall",
                        ok, $"stalls={stallsDone}, bodyReqs={bodyReqs}, err={errText}");
                }
                finally
                {
                    HttpDownloader.ReadTimeout = prevReadTimeout;
                    HttpDownloader.BaseBackoff = prevBackoff;
                    HttpDownloader.MaxRetries = prevMaxRetries;
                }
            });

            // H-03: reset=<offset> & reset_n=2 (TCP abort mid-body recovery + permanent failure)
            await RunBlock("H-03: reset=<offset>&reset_n=2 recovers after 2 mid-body TCP resets and fails cleanly when permanent", async () =>
            {
                var prevBackoff = HttpDownloader.BaseBackoff;
                var prevMaxRetries = HttpDownloader.MaxRetries;
                try
                {
                    HttpDownloader.BaseBackoff = TimeSpan.FromMilliseconds(100);
                    var item = NewHardItem($"{hardBase}/f/131072?reset=24576&reset_n=2&tag=h03_ok", "h03-reset-ok.bin", conns: 1);
                    await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                    var st = await GetStatsAsync("h03_ok");
                    int resetsDone = st["resets_done"]!.GetValue<int>();

                    HttpDownloader.MaxRetries = 1;
                    var failItem = NewHardItem($"{hardBase}/f/65536?reset=8192&norange=1&tag=h03_fail", "h03-reset-fail.bin", conns: 1);
                    string errText = "";
                    try { await new HttpDownloader(failItem, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException ex) { errText = ex.Message; }

                    bool ok = ValidateCompletedItem(item, 131072, 0, "h03-reset-ok.bin")
                              && resetsDone == 2
                              && !string.IsNullOrEmpty(errText)
                              && !File.Exists(failItem.TempPath) && !File.Exists(failItem.FullPath);
                    Check("H-03: reset=<offset>&reset_n=2 recovers after 2 mid-body TCP resets and fails cleanly when permanent",
                        ok, $"resetsDone={resetsDone}, err={errText}");
                }
                finally
                {
                    HttpDownloader.BaseBackoff = prevBackoff;
                    HttpDownloader.MaxRetries = prevMaxRetries;
                }
            });

            // H-04: drop_headers (close before sending HTTP headers)
            await RunBlock("H-04: drop_headers recovers after transient closes and surfaces localized error when permanent", async () =>
            {
                var prevBackoff = HttpDownloader.BaseBackoff;
                var prevMaxRetries = HttpDownloader.MaxRetries;
                try
                {
                    HttpDownloader.BaseBackoff = TimeSpan.FromMilliseconds(100);
                    var item = NewHardItem($"{hardBase}/f/32768?drop_headers=1&drop_n=2&tag=h04_ok", "h04-drop-ok.bin", conns: 2);
                    await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                    var st = await GetStatsAsync("h04_ok");
                    int dropsDone = st["drops_done"]!.GetValue<int>();

                    HttpDownloader.MaxRetries = 1;
                    var failItem = NewHardItem($"{hardBase}/f/32768?drop_headers=1&tag=h04_fail", "h04-drop-fail.bin", conns: 1);
                    string errText = "";
                    try { await new HttpDownloader(failItem, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException ex) { errText = ex.Message; }

                    bool ok = ValidateCompletedItem(item, 32768, 0, "h04-drop-ok.bin")
                              && dropsDone == 2
                              && !string.IsNullOrEmpty(errText)
                              && !File.Exists(failItem.TempPath) && !File.Exists(failItem.FullPath);
                    Check("H-04: drop_headers recovers after transient closes and surfaces localized error when permanent",
                        ok, $"dropsDone={dropsDone}, err={errText}");
                }
                finally
                {
                    HttpDownloader.BaseBackoff = prevBackoff;
                    HttpDownloader.MaxRetries = prevMaxRetries;
                }
            });

            // =========================================================================
            // 2. Status Scripting: status=503,503,200, retry_after=<sec|http-date>, cap, 429
            // =========================================================================

            // H-05a: status=503,503,200 with Retry-After seconds
            await RunBlock("H-05a: status=503,503,200 with Retry-After seconds retries and completes with valid SHA-256", async () =>
            {
                var sw = Stopwatch.StartNew();
                var item = NewHardItem($"{hardBase}/f/65536?status=503,503,200&retry_after=1&tag=h05_503", "h05-503.bin", conns: 2);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                sw.Stop();
                var st = await GetStatsAsync("h05_503");
                int reqs = st["requests"]!.GetValue<int>();
                bool ok = ValidateCompletedItem(item, 65536, 0, "h05-503.bin")
                          && reqs >= 3
                          && sw.Elapsed.TotalSeconds is >= 1.5 and <= 8.0;
                Check("H-05a: status=503,503,200 with Retry-After seconds retries and completes with valid SHA-256",
                    ok, $"reqs={reqs}, elapsed={sw.Elapsed.TotalSeconds:F2}s");
            });

            // H-05b: status=429,200 with Retry-After HTTP-date (future & past) & Retry-After cap
            await RunBlock("H-05b: 429 + Retry-After HTTP-date + Retry-After cap honoured + localized 429 error", async () =>
            {
                var prevBackoff = HttpDownloader.BaseBackoff;
                var prevMaxRetryAfter = HttpDownloader.MaxRetryAfter;
                var prevMaxRetries = HttpDownloader.MaxRetries;
                try
                {
                    // Set BaseBackoff to 50ms so a 1s future HTTP-date delay is unmistakably due to Retry-After HTTP-date parsing
                    HttpDownloader.BaseBackoff = TimeSpan.FromMilliseconds(50);

                    // HTTP-date 2s in future (RFC 1123 HTTP-date has 1s resolution, so +2s guarantees >= 1.0s delay after sub-second truncation)
                    var swDate = Stopwatch.StartNew();
                    var itemDate = NewHardItem($"{hardBase}/f/32768?status=429,200&retry_after=date%2B2&tag=h05_date", "h05-date.bin", conns: 1);
                    await new HttpDownloader(itemDate, limiter).RunAsync(CancellationToken.None);
                    swDate.Stop();

                    // HTTP-date in the past (date-5) falls back cleanly without negative delay exception
                    var itemPastDate = NewHardItem($"{hardBase}/f/32768?status=429,200&retry_after=date-5&tag=h05_pastdate", "h05-pastdate.bin", conns: 1);
                    await new HttpDownloader(itemPastDate, limiter).RunAsync(CancellationToken.None);

                    // Huge Retry-After (3600s) capped by MaxRetryAfter (0.6s for fast test) + verify default 60s cap via Backoff
                    bool defaultCapIs60s = HttpDownloader.Backoff(0, TimeSpan.FromSeconds(3600)) == TimeSpan.FromSeconds(60);
                    HttpDownloader.MaxRetryAfter = TimeSpan.FromMilliseconds(600);
                    var swCap = Stopwatch.StartNew();
                    var itemCap = NewHardItem($"{hardBase}/f/32768?status=503,200&retry_after=3600&tag=h05_cap", "h05-cap.bin", conns: 1);
                    await new HttpDownloader(itemCap, limiter).RunAsync(CancellationToken.None);
                    swCap.Stop();

                    // Permanent 429 fails with localized core.http.err_busy
                    HttpDownloader.MaxRetryAfter = TimeSpan.FromMilliseconds(100);
                    HttpDownloader.MaxRetries = 1;
                    var fail429 = NewHardItem($"{hardBase}/f/32768?status=429&retry_after=1&tag=h05_429fail", "h05-429fail.bin", conns: 1);
                    string err429 = "";
                    try { await new HttpDownloader(fail429, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException ex) { err429 = ex.Message; }

                    bool ok = ValidateCompletedItem(itemDate, 32768, 0, "h05-date.bin")
                              && swDate.Elapsed.TotalSeconds is >= 0.85 and <= 4.5
                              && ValidateCompletedItem(itemPastDate, 32768, 0, "h05-pastdate.bin")
                              && ValidateCompletedItem(itemCap, 32768, 0, "h05-cap.bin")
                              && defaultCapIs60s
                              && swCap.Elapsed.TotalSeconds is >= 0.45 and <= 3.0
                              && err429 == Loc.T("core.http.err_busy", 429)
                              && !File.Exists(fail429.TempPath);
                    Check("H-05b: 429 + Retry-After HTTP-date + Retry-After cap honoured + localized 429 error",
                        ok, $"dateElapsed={swDate.Elapsed.TotalSeconds:F2}s, defaultCap60s={defaultCapIs60s}, capElapsed={swCap.Elapsed.TotalSeconds:F2}s, err429={err429}");
                }
                finally
                {
                    HttpDownloader.BaseBackoff = prevBackoff;
                    HttpDownloader.MaxRetryAfter = prevMaxRetryAfter;
                    HttpDownloader.MaxRetries = prevMaxRetries;
                }
            });

            // =========================================================================
            // 3. Range Quirks: norange, range_once, badrange, short, long, noaccept, 416
            // =========================================================================

            // H-06a: norange (ignore Range, always 200 full)
            await RunBlock("H-06a: norange downloads full 200 stream with Resumable=false, valid SHA-256, MOTW", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/65536?norange=1&tag=h06_norange", "h06-norange.bin", conns: 4);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = !item.Resumable && ValidateCompletedItem(item, 65536, 0, "h06-norange.bin");
                Check("H-06a: norange downloads full 200 stream with Resumable=false, valid SHA-256, MOTW", ok);
            });

            // H-06b: range_once (honour Range only on the first request, then 200 full)
            await RunBlock("H-06b: range_once falls back to single stream when subsequent Range returns 200", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/65536?range_once=1&tag=h06_range_once", "h06-range-once.bin", conns: 4);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = ValidateCompletedItem(item, 65536, 0, "h06-range-once.bin");
                Check("H-06b: range_once falls back to single stream when subsequent Range returns 200", ok);
            });

            // H-06c: badrange (Content-Range for a different window)
            await RunBlock("H-06c: badrange (wrong Content-Range window) detected and recovered without corruption", async () =>
            {
                // Server sends mismatched Content-Range window on segment requests; engine detects mismatch and falls back to non-ranged 200 OK
                var item = NewHardItem($"{hardBase}/f/65536?badrange=seg&tag=h06_badrange_seg", "h06-badrange.bin", conns: 4);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = ValidateCompletedItem(item, 65536, 0, "h06-badrange.bin");
                Check("H-06c: badrange (wrong Content-Range window) detected and recovered without corruption", ok);
            });

            // H-06d: short (send fewer bytes than Content-Length then close)
            await RunBlock("H-06d: short body rejected on 200 stream (err_incomplete) and resumed on ranged stream", async () =>
            {
                var prevBackoff = HttpDownloader.BaseBackoff;
                HttpDownloader.BaseBackoff = TimeSpan.FromMilliseconds(50);
                try
                {
                    // Permanent short on non-resumable 200 response must fail with localized err_incomplete
                    var failShort = NewHardItem($"{hardBase}/f/65536?norange=1&short=1&tag=h06_short_fail", "h06-short-fail.bin", conns: 1);
                    string errShort = "";
                    try { await new HttpDownloader(failShort, limiter).RunAsync(CancellationToken.None); }
                    catch (DownloadException ex) { errShort = ex.Message; }

                    // Transient short on ranged server resumes remaining bytes and finishes intact
                    var okShort = NewHardItem($"{hardBase}/f/65536?short=1&short_n=2&tag=h06_short_ok", "h06-short-ok.bin", conns: 1);
                    await new HttpDownloader(okShort, limiter).RunAsync(CancellationToken.None);

                    bool ok = errShort == Loc.T("core.http.err_incomplete")
                              && !File.Exists(failShort.FullPath) && !File.Exists(failShort.TempPath)
                              && ValidateCompletedItem(okShort, 65536, 0, "h06-short-ok.bin");
                    Check("H-06d: short body rejected on 200 stream (err_incomplete) and resumed on ranged stream",
                        ok, $"errShort={errShort}");
                }
                finally
                {
                    HttpDownloader.BaseBackoff = prevBackoff;
                }
            });

            // H-06e: long (send more bytes than requested)
            await RunBlock("H-06e: long body bounded in segmented mode and rejected in single-stream mode", async () =>
            {
                // Segmented download caps each segment to its requested range
                var segLong = NewHardItem($"{hardBase}/f/65536?long=1&tag=h06_long_seg", "h06-long-seg.bin", conns: 2);
                await new HttpDownloader(segLong, limiter).RunAsync(CancellationToken.None);

                // Single-stream 200 OK rejects overflow beyond Content-Length with localized err_stream_exceeded
                var singleLong = NewHardItem($"{hardBase}/f/16384?norange=1&long=1&tag=h06_long_single", "h06-long-single.bin", conns: 1);
                string errLong = "";
                try { await new HttpDownloader(singleLong, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { errLong = ex.Message; }

                bool ok = ValidateCompletedItem(segLong, 65536, 0, "h06-long-seg.bin")
                          && errLong == Loc.T("core.http.err_stream_exceeded")
                          && !File.Exists(singleLong.FullPath) && !File.Exists(singleLong.TempPath);
                Check("H-06e: long body bounded in segmented mode and rejected in single-stream mode",
                    ok, $"errLong={errLong}");
            });

            // H-06f: noaccept (no Accept-Ranges header, but Range requests work)
            await RunBlock("H-06f: noaccept (missing Accept-Ranges header) still uses multi-connection ranges", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/589824?noaccept=1&rate=262144&tag=h06_noaccept", "h06-noaccept.bin", conns: 4);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                var st = await GetStatsAsync("h06_noaccept");
                int maxSeen = st["max"]!.GetValue<int>();
                bool ok = item.Resumable && maxSeen >= 2 && ValidateCompletedItem(item, 589824, 0, "h06-noaccept.bin");
                Check("H-06f: noaccept (missing Accept-Ranges header) still uses multi-connection ranges",
                    ok, $"maxSeen={maxSeen}");
            });

            // H-06g: 416 on valid ranges
            await RunBlock("H-06g: 416 on valid ranges falls back to non-ranged GET or reports localized err_416", async () =>
            {
                // Server returns 416 on Range requests, succeeds when retried without Range
                var itemFallback = NewHardItem($"{hardBase}/f/32768?range416=1&tag=h06_416_fb", "h06-416-fb.bin", conns: 2);
                await new HttpDownloader(itemFallback, limiter).RunAsync(CancellationToken.None);

                // Server returns 416 unconditionally -> surfaces localized err_416
                var itemAlways416 = NewHardItem($"{hardBase}/f/32768?range416=always&tag=h06_416_err", "h06-416-err.bin", conns: 2);
                string err416 = "";
                try { await new HttpDownloader(itemAlways416, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { err416 = ex.Message; }

                bool ok = ValidateCompletedItem(itemFallback, 32768, 0, "h06-416-fb.bin")
                          && err416 == Loc.T("core.http.err_416")
                          && !File.Exists(itemAlways416.TempPath) && !File.Exists(itemAlways416.FullPath);
                Check("H-06g: 416 on valid ranges falls back to non-ranged GET or reports localized err_416",
                    ok, $"err416={err416}");
            });

            // =========================================================================
            // 4. Validators: etag, etag_change_after=N, lastmod, weak ETags, If-Range on/off
            // =========================================================================

            // H-07a: etag_change_after=N with If-Range ON
            await RunBlock("H-07a: etag_change_after=2 (If-Range ON) restarts and saves new version intact", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/524288?etag=v1&etag_change_after=2&ifrange=on&seed=10&tag=h07_etagon", "h07-etag-on.bin", conns: 2);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                // Req 0 (probe) & Req 1 (seg 0) use seed=10/etag=v1; Req 2 (seg 1) switches to seed=11/etag=v1-v2, triggering restart
                bool ok = ValidateCompletedItem(item, 524288, 11, "h07-etag-on.bin");
                Check("H-07a: etag_change_after=2 (If-Range ON) restarts and saves new version intact", ok);
            });

            // H-07b: etag_change_after=N with If-Range OFF (server ignores If-Range and still returns 206 with new ETag)
            await RunBlock("H-07b: etag_change_after=2 (If-Range OFF) detects ETag change on 206 and restarts without corruption", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/524288?etag=v1&etag_change_after=2&ifrange=off&seed=20&tag=h07_etagoff", "h07-etag-off.bin", conns: 2);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = ValidateCompletedItem(item, 524288, 21, "h07-etag-off.bin");
                Check("H-07b: etag_change_after=2 (If-Range OFF) detects ETag change on 206 and restarts without corruption", ok);
            });

            // H-07c: Last-Modified validator (no ETag) with change detection when If-Range OFF
            await RunBlock("H-07c: Last-Modified validator used in If-Range and detects change even with If-Range OFF", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/524288?no_etag=1&lastmod=1&lastmod_change_after=2&ifrange=off&seed=30&tag=h07_lastmod", "h07-lastmod.bin", conns: 2);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                var log = await GetLogAsync("h07_lastmod");
                bool sentIfRangeDate = log.Any(n => (n?["if_range"]?.GetValue<string>() ?? "").Contains("GMT", StringComparison.Ordinal));
                bool ok = sentIfRangeDate && ValidateCompletedItem(item, 524288, 31, "h07-lastmod.bin");
                Check("H-07c: Last-Modified validator used in If-Range and detects change even with If-Range OFF",
                    ok, $"sentIfRangeDate={sentIfRangeDate}");
            });

            // H-07d: Weak ETag (W/"x") is never sent in If-Range
            await RunBlock("H-07d: Weak ETag W/\"x\" ignored as strong validator and never sent in If-Range", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/65536?weak_etag=1&lastmod=1&tag=h07_weak", "h07-weak.bin", conns: 2);
                item.ETag = "W/\"preexisting-weak\"";
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                var log = await GetLogAsync("h07_weak");
                bool anyWeakIfRange = log.Any(n => (n?["if_range"]?.GetValue<string>() ?? "").StartsWith("W/", StringComparison.OrdinalIgnoreCase));
                bool ok = string.IsNullOrEmpty(item.ETag) && !anyWeakIfRange && ValidateCompletedItem(item, 65536, 0, "h07-weak.bin");
                Check("H-07d: Weak ETag W/\"x\" ignored as strong validator and never sent in If-Range",
                    ok, $"anyWeakIfRange={anyWeakIfRange}, item.ETag='{item.ETag}'");
            });

            // =========================================================================
            // 5. Framing: chunked, nolength, gzip, head405, zero, segment boundaries
            // =========================================================================

            // H-08a: chunked (Transfer-Encoding: chunked, no Content-Length)
            await RunBlock("H-08a: chunked transfer encoding (no Content-Length) saves exact SHA-256", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/70000?chunked=1&seed=5&tag=h08_chunked", "h08-chunked.bin", conns: 4);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = !item.Resumable && item.TotalSize == 70000 && ValidateCompletedItem(item, 70000, 5, "h08-chunked.bin");
                Check("H-08a: chunked transfer encoding (no Content-Length) saves exact SHA-256", ok);
            });

            // H-08b: nolength (close-delimited 200 OK without Content-Length)
            await RunBlock("H-08b: close-delimited nolength stream saves exact SHA-256", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/50000?nolength=1&seed=6&tag=h08_nolength", "h08-nolength.bin", conns: 4);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = !item.Resumable && item.TotalSize == 50000 && ValidateCompletedItem(item, 50000, 6, "h08-nolength.bin");
                Check("H-08b: close-delimited nolength stream saves exact SHA-256", ok);
            });

            // H-08c: gzip (Content-Encoding: gzip on the wire -> decoded once to exact original bytes)
            await RunBlock("H-08c: Content-Encoding gzip on the wire requests identity and decodes original bytes once", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/90000?gzip=1&seed=7&tag=h08_gzip", "h08-gzip.bin", conns: 4);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                var log = await GetLogAsync("h08_gzip");
                bool requestedIdentity = log.Any(n => (n?["accept_encoding"]?.GetValue<string>() ?? "").Contains("identity", StringComparison.OrdinalIgnoreCase));
                bool ok = requestedIdentity && item.TotalSize == 90000 && ValidateCompletedItem(item, 90000, 7, "h08-gzip.bin");
                Check("H-08c: Content-Encoding gzip on the wire requests identity and decodes original bytes once",
                    ok, $"requestedIdentity={requestedIdentity}, totalSize={item.TotalSize}");
            });

            // H-08d: head405 (HEAD returns 405 Method Not Allowed)
            await RunBlock("H-08d: head405 (HEAD not allowed) works for both ProbeAsync and RunAsync", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/32768?head405=1&seed=8&tag=h08_head405", "h08-head405.bin", conns: 2);
                var probe = await HttpDownloader.ProbeAsync(item, CancellationToken.None);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = probe.Size == 32768 && probe.Resumable && ValidateCompletedItem(item, 32768, 8, "h08-head405.bin");
                Check("H-08d: head405 (HEAD not allowed) works for both ProbeAsync and RunAsync", ok);
            });

            // H-08e: zero (0-byte file where Range: bytes=0-0 returns 416)
            await RunBlock("H-08e: zero (0-byte file) handles 416 on 0-0 probe and creates 0-byte file with MOTW", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/0?zero=1&tag=h08_zero", "h08-zero.bin", conns: 4);
                var probe = await HttpDownloader.ProbeAsync(item, CancellationToken.None);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = probe.Size == 0 && !item.Resumable && ValidateCompletedItem(item, 0, 0, "h08-zero.bin");
                Check("H-08e: zero (0-byte file) handles 416 on 0-0 probe and creates 0-byte file with MOTW", ok);
            });

            // H-08f: sizes at segment boundaries (1, MinSplit-1, MinSplit, MinSplit+1, 2*MinSplit-1, 2*MinSplit, 2*MinSplit+1)
            await RunBlock("H-08f: sizes at segment boundaries (1, MinSplit±1, 2*MinSplit±1) all match SHA-256", async () =>
            {
                long[] sizes = [1, 262143, 262144, 262145, 524287, 524288, 524289];
                bool allOk = true;
                string failedSize = "";
                foreach (var sz in sizes)
                {
                    var bItem = NewHardItem($"{hardBase}/f/{sz}?seed=9&tag=h08_b_{sz}", $"h08-boundary-{sz}.bin", conns: 4);
                    await new HttpDownloader(bItem, limiter).RunAsync(CancellationToken.None);
                    if (!ValidateCompletedItem(bItem, sz, 9, $"h08-boundary-{sz}.bin"))
                    {
                        allOk = false;
                        failedSize = sz.ToString();
                        break;
                    }
                }
                Check("H-08f: sizes at segment boundaries (1, MinSplit±1, 2*MinSplit±1) all match SHA-256",
                    allOk, failedSize);
            });

            // =========================================================================
            // 6. Redirects: 301/302/303/307/308 chains, relative, loop, cross-port/host, downgrade, Unicode
            // =========================================================================

            // H-09a: Redirect chain 301/302/303/307/308 (length 5)
            await RunBlock("H-09a: redirect chain 301->302->303->307->308 follows all 5 hops and keeps original Url", async () =>
            {
                string origUrl = $"{hardBase}/f/32768?redirects=5&redirect_codes=301,302,303,307,308&seed=12&tag=h09_chain";
                var item = NewHardItem(origUrl, "h09-chain.bin", conns: 2);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                var st = await GetStatsAsync("h09_chain");
                bool ok = item.Url == origUrl
                          && st["requests"]!.GetValue<int>() >= 6
                          && ValidateCompletedItem(item, 32768, 12, "h09-chain.bin");
                Check("H-09a: redirect chain 301->302->303->307->308 follows all 5 hops and keeps original Url", ok);
            });

            // H-09b: Relative Location header
            await RunBlock("H-09b: relative Location headers resolved accurately across multi-hop redirects", async () =>
            {
                var item = NewHardItem($"{hardBase}/f/32768?redirects=3&redirect_rel=1&seed=13&tag=h09_rel", "h09-rel.bin", conns: 2);
                await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
                bool ok = ValidateCompletedItem(item, 32768, 13, "h09-rel.bin");
                Check("H-09b: relative Location headers resolved accurately across multi-hop redirects", ok);
            });

            // H-09c: Redirect loop & missing Location header
            await RunBlock("H-09c: redirect loop (err_too_many_redirects) and missing Location (err_redirect_missing) rejected", async () =>
            {
                var loopItem = NewHardItem($"{hardBase}/f/4096?redirect_loop=1&tag=h09_loop", "h09-loop.bin", conns: 1);
                string loopErr = "";
                try { await new HttpDownloader(loopItem, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { loopErr = ex.Message; }

                var noLocItem = NewHardItem($"{hardBase}/f/4096?redirect_missing_loc=1&tag=h09_noloc", "h09-noloc.bin", conns: 1);
                string noLocErr = "";
                try { await new HttpDownloader(noLocItem, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { noLocErr = ex.Message; }

                bool ok = loopErr == Loc.T("core.http.err_too_many_redirects")
                          && noLocErr == Loc.T("core.http.err_redirect_missing")
                          && !File.Exists(loopItem.TempPath) && !File.Exists(noLocItem.TempPath);
                Check("H-09c: redirect loop (err_too_many_redirects) and missing Location (err_redirect_missing) rejected",
                    ok, $"loopErr={loopErr}, noLocErr={noLocErr}");
            });

            // H-09d: Redirect to another host AND another port drops Cookies and Authorization
            await RunBlock("H-09d: redirect to another host or another port drops both Cookies and Authorization", async () =>
            {
                // Cross-host redirect (127.0.0.1 -> localhost)
                string targetCrossHost = $"http://localhost:{hardPort}/f/32768?require_no_cookie=1&require_no_auth=1&seed=14&tag=h09_xhost_dst";
                var xHostItem = NewHardItem($"{hardBase}/f/32768?redirect_to={Uri.EscapeDataString(targetCrossHost)}&tag=h09_xhost_src", "h09-xhost.bin", conns: 2);
                xHostItem.Cookies = "sid=secret_cookie";
                xHostItem.CookieHost = "127.0.0.1";
                xHostItem.Login = "admin";
                xHostItem.Password = "secret_pass";
                await new HttpDownloader(xHostItem, limiter).RunAsync(CancellationToken.None);

                // Cross-port redirect on the SAME host (127.0.0.1:hardPort -> 127.0.0.1:port)
                using var portListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                portListener.Start();
                int altPort = ((IPEndPoint)portListener.LocalEndpoint).Port;
                int leakedOnAltPort = 0;
                var altTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        System.Net.Sockets.TcpClient client;
                        try { client = await portListener.AcceptTcpClientAsync(); }
                        catch { break; }
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            await using (var ns = client.GetStream())
                            using (var sr = new StreamReader(ns, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true))
                            {
                                string? line = await sr.ReadLineAsync();
                                if (string.IsNullOrEmpty(line)) return;
                                bool hasCookieOrAuth = false;
                                string rangeHdr = "";
                                while (!string.IsNullOrEmpty(line = await sr.ReadLineAsync()))
                                {
                                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) ||
                                        line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                                        hasCookieOrAuth = true;
                                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                                        rangeHdr = line[6..].Trim();
                                }
                                if (hasCookieOrAuth) Volatile.Write(ref leakedOnAltPort, 1);
                                var data = GenerateSlice(0, 16384, 15);
                                if (hasCookieOrAuth)
                                {
                                    var bad = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
                                    await ns.WriteAsync(bad);
                                    return;
                                }
                                int s = 0, e = data.Length - 1;
                                if (rangeHdr.StartsWith("bytes="))
                                {
                                    var sp = rangeHdr[6..].Split('-');
                                    s = int.Parse(sp[0]);
                                    e = string.IsNullOrEmpty(sp[1]) ? data.Length - 1 : int.Parse(sp[1]);
                                    var hdr = System.Text.Encoding.ASCII.GetBytes(
                                        $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {s}-{e}/{data.Length}\r\nContent-Length: {e - s + 1}\r\nAccept-Ranges: bytes\r\n\r\n");
                                    await ns.WriteAsync(hdr);
                                    await ns.WriteAsync(data.AsMemory(s, e - s + 1));
                                }
                                else
                                {
                                    var hdr = System.Text.Encoding.ASCII.GetBytes(
                                        $"HTTP/1.1 200 OK\r\nContent-Length: {data.Length}\r\n\r\n");
                                    await ns.WriteAsync(hdr);
                                    await ns.WriteAsync(data);
                                }
                            }
                        });
                    }
                });

                string targetCrossPort = $"http://127.0.0.1:{altPort}/file.bin";
                var xPortItem = NewHardItem($"{hardBase}/f/16384?redirect_to={Uri.EscapeDataString(targetCrossPort)}&tag=h09_xport", "h09-xport.bin", conns: 2);
                xPortItem.Cookies = "sid=secret_cookie";
                xPortItem.CookieHost = "127.0.0.1";
                xPortItem.Login = "admin";
                xPortItem.Password = "secret_pass";
                settings.Logins = [new SiteLogin { Pattern = $"127.0.0.1:{hardPort}", User = "admin", Password = "secret_pass" }];
                try
                {
                    await new HttpDownloader(xPortItem, limiter).RunAsync(CancellationToken.None);
                }
                finally
                {
                    settings.Logins = [];
                    portListener.Stop();
                }

                bool leaked = Volatile.Read(ref leakedOnAltPort) != 0;
                bool ok = ValidateCompletedItem(xHostItem, 32768, 14, "h09-xhost.bin")
                          && !leaked
                          && ValidateCompletedItem(xPortItem, 16384, 15, "h09-xport.bin");
                Check("H-09d: redirect to another host or another port drops both Cookies and Authorization",
                    ok, $"leakedOnAltPort={leaked}");
            });

            // H-09e: https->http downgrade simulation via header check & redirect rejection
            await RunBlock("H-09e: https->http downgrade blocks redirect and strips Cookie, Authorization, and Referer", async () =>
            {
                // 1) Redirect from https origUri to http target is rejected
                bool downgradeRedirectBlocked = !HttpDownloader.IsAllowedRedirectTarget(
                    new Uri("https://example.com/file.bin"),
                    new Uri("http://example.com/file.bin"));

                // 2) Segment request where item.Url is https:// and ResolvedUrl is http:// drops Cookie, Authorization, and Referer
                var dgItem = NewHardItem($"https://127.0.0.1:{hardPort}/f/32768?require_no_cookie=1&require_no_auth=1&require_no_referer=1&seed=16&tag=h09_dg", "h09-dg.bin", conns: 2);
                dgItem.Cookies = "sid=top_secret";
                dgItem.CookieHost = "127.0.0.1";
                dgItem.Login = "user";
                dgItem.Password = "pass";
                dgItem.Referrer = $"https://127.0.0.1:{hardPort}/page.html";
                dgItem.ResolvedUrl = $"{hardBase}/f/32768?require_no_cookie=1&require_no_auth=1&require_no_referer=1&seed=16&tag=h09_dg";
                dgItem.Resumable = true;
                dgItem.TotalSize = 32768;
                dgItem.Segments = [new Segment { Start = 0, End = 32767 }];
                File.WriteAllBytes(dgItem.TempPath, []);
                await new HttpDownloader(dgItem, limiter).RunAsync(CancellationToken.None);

                bool ok = downgradeRedirectBlocked && ValidateCompletedItem(dgItem, 32768, 16, "h09-dg.bin");
                Check("H-09e: https->http downgrade blocks redirect and strips Cookie, Authorization, and Referer",
                    ok, $"redirectBlocked={downgradeRedirectBlocked}");
            });

            // H-09f: Location with Unicode / percent-encoding
            await RunBlock("H-09f: Location header with percent-encoded and raw UTF-8 Arabic names resolved & named properly", async () =>
            {
                var pctItem = NewHardItem($"{hardBase}/f/16384?redirects=1&redirect_unicode=pct&seed=17&tag=h09_upct", null, conns: 2);
                await new HttpDownloader(pctItem, limiter).RunAsync(CancellationToken.None);

                var rawItem = NewHardItem($"{hardBase}/f/16384?redirects=1&redirect_unicode=raw&seed=18&tag=h09_uraw", null, conns: 2);
                await new HttpDownloader(rawItem, limiter).RunAsync(CancellationToken.None);

                bool ok = ValidateCompletedItem(pctItem, 16384, 17, "ملف_عربي.bin")
                          && ValidateCompletedItem(rawItem, 16384, 18, "ملف_مباشر.bin");
                Check("H-09f: Location header with percent-encoded and raw UTF-8 Arabic names resolved & named properly",
                    ok, $"pct='{pctItem.FileName}', raw='{rawItem.FileName}'");
            });

            // =========================================================================
            // 7. Content-Disposition quirks & malicious filenames
            // =========================================================================

            // H-10a: Content-Disposition standard, bare filename=, quoted with ';', RFC 5987 Arabic, and both (filename* wins)
            await RunBlock("H-10a: Content-Disposition (filename=, bare, quoted ';', RFC 5987 Arabic, filename* precedence)", async () =>
            {
                var cases = new (string Mode, string ExpectedName, int Seed)[]
                {
                    ("simple", "simple_report.bin", 40),
                    ("no_disp", "bare_disposition.bin", 41),
                    ("semi", "part1;part2;final.bin", 42),
                    ("rfc5987", "تقرير_جولان_2026.bin", 43),
                    ("both", "الاسم_المفضل.bin", 44),
                };
                bool allOk = true;
                string detail = "";
                foreach (var c in cases)
                {
                    var it = NewHardItem($"{hardBase}/f/8192?cd_mode={c.Mode}&seed={c.Seed}&tag=h10_{c.Mode}", null, conns: 2);
                    await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                    if (!ValidateCompletedItem(it, 8192, c.Seed, c.ExpectedName))
                    {
                        allOk = false;
                        detail = $"mode={c.Mode} got='{it.FileName}' expected='{c.ExpectedName}'";
                        break;
                    }
                }
                Check("H-10a: Content-Disposition (filename=, bare, quoted ';', RFC 5987 Arabic, filename* precedence)",
                    allOk, detail);
            });

            // H-10b: Malicious Content-Disposition (../../evil.exe, C:\x, CON, trailing dots, 300-char names)
            await RunBlock("H-10b: Malicious Content-Disposition (../../evil.exe, C:\\x, CON, trailing dots, 300-char) sanitized & contained", async () =>
            {
                var evilCases = new (string Mode, Func<string, bool> NameCheck, int Seed)[]
                {
                    ("evil_traversal", n => n == "evil.exe" && !n.Contains("..") && !n.Contains('/') && !n.Contains('\\'), 50),
                    ("evil_drive", n => n == "x" && !n.Contains(':') && !n.Contains('\\'), 51),
                    ("evil_con", n => n == "_CON", 52),
                    ("evil_dots", n => n == "trailing_dots.bin" && !n.EndsWith('.'), 53),
                    ("evil_long", n => n.Length == 180 && n.EndsWith(".bin", StringComparison.Ordinal) && !n.EndsWith("..bin", StringComparison.Ordinal), 54),
                };
                bool allOk = FileNames.Sanitize("CON.tar.gz") == "_CON.tar.gz";
                string detail = allOk ? "" : "CON.tar.gz was not prefixed";
                foreach (var c in evilCases)
                {
                    var it = NewHardItem($"{hardBase}/f/8192?cd_mode={c.Mode}&seed={c.Seed}&tag=h10_{c.Mode}", null, conns: 2);
                    await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                    if (!ValidateCompletedItem(it, 8192, c.Seed) || !c.NameCheck(it.FileName))
                    {
                        allOk = false;
                        detail = $"mode={c.Mode} got='{it.FileName}' (len={it.FileName.Length})";
                        break;
                    }
                }
                Check("H-10b: Malicious Content-Disposition (../../evil.exe, C:\\x, CON, trailing dots, 300-char) sanitized & contained",
                    allOk, detail);
            });

            // =========================================================================
            // 8. Pause / Cancel & Resume across quirks (resumable vs restart)
            // =========================================================================

            // H-11a: Resume after mid-download cancellation for resumable quirks (throttled, noaccept, lastmod, weak_etag)
            await RunBlock("H-11a: Pause & resume mid-download preserves partial progress on resumable quirks (rate, noaccept, lastmod, weak_etag)", async () =>
            {
                var resumableQuirks = new (string Name, string Query, int Seed)[]
                {
                    ("rate", "rate=32768", 60),
                    ("noaccept", "noaccept=1&rate=32768", 61),
                    ("lastmod", "no_etag=1&lastmod=1&rate=32768", 62),
                    ("weak_etag", "weak_etag=1&lastmod=1&rate=32768", 63),
                };
                bool allOk = true;
                string detail = "";
                foreach (var q in resumableQuirks)
                {
                    string tag = $"h11_res_{q.Name}";
                    var it = NewHardItem($"{hardBase}/f/131072?{q.Query}&seed={q.Seed}&tag={tag}", $"h11-{q.Name}.bin", conns: 2);
                    using (var cts = new CancellationTokenSource())
                    {
                        var task = new HttpDownloader(it, limiter).RunAsync(cts.Token);
                        while (it.Downloaded < 8192) await Task.Delay(15);
                        cts.Cancel();
                        try { await task; } catch (OperationCanceledException) { /* expected */ }
                    }
                    long partialBefore = it.Downloaded;
                    // Remove rate limit on resume so it finishes fast while keeping same validator/quirk
                    await HttpDownloader.Client.GetStringAsync($"{hardBase}/mutate?tag={tag}&rate=0");
                    await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                    var log = await GetLogAsync(tag);
                    bool resumedFromMid = partialBefore > 0 && log.Any(n =>
                    {
                        var r = n?["range"]?.GetValue<string>() ?? "";
                        return r.StartsWith("bytes=") && !r.StartsWith("bytes=0-");
                    });
                    if (!resumedFromMid || !ValidateCompletedItem(it, 131072, q.Seed, $"h11-{q.Name}.bin"))
                    {
                        allOk = false;
                        detail = $"quirk={q.Name} partial={partialBefore} resumedMid={resumedFromMid}";
                        break;
                    }
                }
                Check("H-11a: Pause & resume mid-download preserves partial progress on resumable quirks (rate, noaccept, lastmod, weak_etag)",
                    allOk, detail);
            });

            // H-11b: Pause & resume when server changes ETag (If-Range ON & OFF), switches to norange, or returns badrange -> clean restart without corruption
            await RunBlock("H-11b: Pause & resume cleanly restarts (zero corruption) on ETag change (If-Range on/off), norange, and badrange", async () =>
            {
                var restartQuirks = new (string Name, string MutateQuery, int InitialSeed, int FinalSeed)[]
                {
                    ("etag_change_on", "rate=0&etag=v2&seed=71&ifrange=on", 70, 71),
                    ("etag_change_off", "rate=0&etag=v2&seed=73&ifrange=off", 72, 73),
                    ("norange", "rate=0&norange=1&seed=75", 74, 75),
                    ("badrange", "rate=0&badrange=1&seed=77", 76, 77),
                };
                bool allOk = true;
                string detail = "";
                foreach (var q in restartQuirks)
                {
                    string tag = $"h11_rst_{q.Name}";
                    var it = NewHardItem($"{hardBase}/f/131072?rate=32768&etag=v1&seed={q.InitialSeed}&tag={tag}", $"h11-rst-{q.Name}.bin", conns: 2);
                    using (var cts = new CancellationTokenSource())
                    {
                        var task = new HttpDownloader(it, limiter).RunAsync(cts.Token);
                        while (it.Downloaded < 8192) await Task.Delay(15);
                        cts.Cancel();
                        try { await task; } catch (OperationCanceledException) { /* expected */ }
                    }
                    await HttpDownloader.Client.GetStringAsync($"{hardBase}/mutate?tag={tag}&{q.MutateQuery}");
                    await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                    if (!ValidateCompletedItem(it, 131072, q.FinalSeed, $"h11-rst-{q.Name}.bin"))
                    {
                        allOk = false;
                        detail = $"quirk={q.Name} failed SHA-256 after restart";
                        break;
                    }
                }
                Check("H-11b: Pause & resume cleanly restarts (zero corruption) on ETag change (If-Range on/off), norange, and badrange",
                    allOk, detail);
            });

            // =========================================================================
            // 9. Dynamic splitting under fast + throttled connections & per-server limit
            // =========================================================================
            await RunBlock("H-12: dynamic splitting under fast+throttled connections beats single connection and honours per-server limit", async () =>
            {
                // 4,194,304 bytes (4 MB = 16 * MinSplit). First connection is throttled to 2 MB/s (takes ~2.0s alone).
                // With 4 connections (capped by ServerLimit=4 even though item asks for 8), each initial segment is 1 MB (4 * MinSplit);
                // fast connections finish their 1 MB segments in milliseconds and dynamically split the throttled connection's remaining range.
                settings.ServerLimits = [new ServerLimit { Pattern = "127.0.0.1", MaxConnections = 4 }];
                HttpDownloader.Configure(settings);
                try
                {
                    var swSingle = Stopwatch.StartNew();
                    var singleItem = NewHardItem($"{hardBase}/f/4194304?slow_first=2097152&seed=80&tag=h12_single", "h12-single.bin", conns: 1);
                    await new HttpDownloader(singleItem, limiter).RunAsync(CancellationToken.None);
                    swSingle.Stop();

                    var swMulti = Stopwatch.StartNew();
                    var multiItem = NewHardItem($"{hardBase}/f/4194304?slow_first=2097152&seed=80&tag=h12_multi", "h12-multi.bin", conns: 8);
                    await new HttpDownloader(multiItem, limiter).RunAsync(CancellationToken.None);
                    swMulti.Stop();

                    var st = await GetStatsAsync("h12_multi");
                    int maxConnsSeen = st["max"]!.GetValue<int>();
                    int bodyReqs = st["body_requests"]!.GetValue<int>();

                    bool ok = ValidateCompletedItem(singleItem, 4194304, 80, "h12-single.bin")
                              && ValidateCompletedItem(multiItem, 4194304, 80, "h12-multi.bin")
                              && maxConnsSeen is >= 2 and <= 4
                              && bodyReqs >= 5 // initial 4 segments + at least 1 dynamic split of the slow first segment
                              && swMulti.Elapsed < swSingle.Elapsed;
                    Check("H-12: dynamic splitting under fast+throttled connections beats single connection and honours per-server limit",
                        ok, $"single={swSingle.Elapsed.TotalSeconds:F2}s, multi={swMulti.Elapsed.TotalSeconds:F2}s, maxConns={maxConnsSeen}, bodyReqs={bodyReqs}");
                }
                finally
                {
                    settings.ServerLimits = [];
                    HttpDownloader.Configure(settings);
                }
            });

            // =========================================================================
            // 10. Queue stress: 200 x 4 KB files through DownloadManager queue
            // =========================================================================
            await RunBlock("H-13: 200 x 4 KB files through DownloadManager queue complete with bounded memory (<50 MB) and no handle leak", async () =>
            {
                string queueDir = Path.Combine(hardOut, "queue200");
                Directory.CreateDirectory(queueDir);
                var qSettings = new AppSettings
                {
                    ProxyMode = "none",
                    DownloadRoot = queueDir,
                    UseCategoryFolders = false,
                    MaxConnections = 2,
                    VirusScanEnabled = false,
                    SoundsEnabled = false,
                };
                qSettings.Normalize();
                qSettings.MainQueue.MaxSimultaneous = 8;

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long memBefore = GC.GetTotalMemory(true);
                int handlesBefore = Process.GetCurrentProcess().HandleCount;

                var dm = new DownloadManager(qSettings);
                var items200 = new List<DownloadItem>(200);
                var swQueue = Stopwatch.StartNew();
                try
                {
                    for (int i = 0; i < 200; i++)
                    {
                        var qi = dm.CreateHttp($"{hardBase}/f/4096?seed={i}&tag=h13_q", fileName: $"q-{i:D3}.bin");
                        qi.SaveDir = queueDir;
                        qi.NameLocked = true;
                        qi.MaxConnections = 1;
                        dm.Add(qi, startNow: false);
                        items200.Add(qi);
                    }

                    dm.StartQueue(QueueDef.MainId);
                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < TimeSpan.FromSeconds(15))
                    {
                        if (items200.All(x => x.Status is DownloadStatus.Completed or DownloadStatus.Error))
                            break;
                        await Task.Delay(20);
                    }
                    swQueue.Stop();
                }
                finally
                {
                    dm.Shutdown();
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long memAfter = GC.GetTotalMemory(true);
                int handlesAfter = Process.GetCurrentProcess().HandleCount;
                long memDeltaMB = Math.Max(0, memAfter - memBefore) / (1024 * 1024);
                int handleDelta = handlesAfter - handlesBefore;

                bool all200Valid = items200.Count == 200 && items200.Select((it, idx) =>
                    it.Status == DownloadStatus.Completed
                    && File.Exists(it.FullPath)
                    && !File.Exists(it.TempPath)
                    && new FileInfo(it.FullPath).Length == 4096
                    && FileSha256(it.FullPath) == ExpectedSha256(4096, idx)).All(x => x);

                bool noLeftoverTemps = Directory.GetFiles(queueDir, "*.gdm").Length == 0;
                bool memBounded = (memAfter - memBefore) < 50L * 1024 * 1024;
                bool handlesBounded = handleDelta < 150;

                Check("H-13: 200 x 4 KB files through DownloadManager queue complete with bounded memory (<50 MB) and no handle leak",
                    all200Valid && noLeftoverTemps && memBounded && handlesBounded,
                    $"completed={items200.Count(x => x.Status == DownloadStatus.Completed)}/200, time={swQueue.Elapsed.TotalSeconds:F2}s, memDelta={memDeltaMB}MB, handleDelta={handleDelta}");
            });

            // =========================================================================
            // 11. Large offsets (> 4 GB / 5 GB virtual file) — no 32-bit int overflow
            // =========================================================================
            await RunBlock("H-14: 5 GB virtual file resumes at >4.2 GB offset with dynamic splitting and zero 32-bit int overflow", async () =>
            {
                const long fiveGb = 5L * 1024 * 1024 * 1024; // 5,368,709,120 bytes (> uint.MaxValue)
                const long tailLen = 1024 * 1024;            // 1 MB tail above 4.999 GB
                const long resumeOffset = fiveGb - tailLen;  // 5,367,660,544 (> 4.2 GB)

                var bigItem = NewHardItem($"{hardBase}/f/{fiveGb}?seed=99&tag=h14_5gb", "h14-5gb-sparse.bin", conns: 4);
                bigItem.Resumable = true;
                bigItem.TotalSize = fiveGb;
                bigItem.ETag = $"\"{fiveGb}-99\"";
                bigItem.Segments =
                [
                    new Segment { Start = 0, End = resumeOffset - 1, Downloaded = resumeOffset },
                    new Segment { Start = resumeOffset, End = fiveGb - 1, Downloaded = 0 },
                ];

                // Create a sparse .gdm temp file on NTFS so seeking/writing at 5 GB uses only 1 MB of physical disk space
                await using (var fs = new FileStream(bigItem.TempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    MarkFileSparse(fs.SafeFileHandle);
                }

                bool tailHashOk = false;
                long finalLength = 0;
                bool motwOk = false;
                bool noTempOk = false;
                try
                {
                    await new HttpDownloader(bigItem, limiter).RunAsync(CancellationToken.None);
                    finalLength = new FileInfo(bigItem.FullPath).Length;
                    motwOk = HasMotw(bigItem.FullPath);
                    noTempOk = !File.Exists(bigItem.TempPath);

                    var tailBuf = new byte[tailLen];
                    await using (var rfs = new FileStream(bigItem.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        rfs.Position = resumeOffset;
                        await rfs.ReadExactlyAsync(tailBuf);
                    }
                    var expectedTail = GenerateSlice(resumeOffset, (int)tailLen, 99);
                    tailHashOk = tailBuf.AsSpan().SequenceEqual(expectedTail);
                }
                finally
                {
                    try { if (File.Exists(bigItem.FullPath)) File.Delete(bigItem.FullPath); } catch { /* ignore */ }
                    try { if (File.Exists(bigItem.TempPath)) File.Delete(bigItem.TempPath); } catch { /* ignore */ }
                }

                var st = await GetStatsAsync("h14_5gb");
                int rangeReqs = st["range_requests"]!.GetValue<int>();
                bool ok = finalLength == fiveGb && tailHashOk && motwOk && noTempOk && rangeReqs >= 2;
                Check("H-14: 5 GB virtual file resumes at >4.2 GB offset with dynamic splitting and zero 32-bit int overflow",
                    ok, $"finalLen={finalLength}, tailHashOk={tailHashOk}, rangeReqs={rangeReqs}");
            });

            // =========================================================================
            // H-15: "Refresh download address" — the fresh link comes from another server
            // =========================================================================
            async Task<(DownloadItem Item, long Partial)> PartialDownloadAsync(string url, string name)
            {
                var it = NewHardItem(url, name, conns: 2);
                using var cts = new CancellationTokenSource();
                var task = new HttpDownloader(it, limiter).RunAsync(cts.Token);
                while (it.Downloaded < 200 * 1024 && !task.IsCompleted) await Task.Delay(15);
                cts.Cancel();
                try { await task; } catch (OperationCanceledException) { /* expected */ }
                return (it, it.Downloaded);
            }

            // Runs the resumed download and reports the lowest progress seen (a restart drops it to zero).
            async Task<long> ResumeWatchingAsync(DownloadItem it)
            {
                long min = long.MaxValue;
                var task = new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                while (!task.IsCompleted) { min = Math.Min(min, it.Downloaded); await Task.Delay(5); }
                await task;
                return min;
            }

            await RunBlock("H-15a: refreshed address on another server (other ETag, same bytes) resumes after a byte check", async () =>
            {
                var (it, partial) = await PartialDownloadAsync($"{hardBase}/f/1048576?etag=srvA&seed=90&rate=131072&tag=h15a_a", "h15a.bin");
                AddressRefresh.Apply(it, $"{hardBase}/f/1048576?etag=srvB&seed=90&rate=262144&tag=h15a_b", "", "", "");
                long min = await ResumeWatchingAsync(it);
                var log = await GetLogAsync("h15a_b");
                bool sampled = log.Any(n => (n?["range"]?.GetValue<string>() ?? "") == "bytes=0-65535");
                Check("H-15a: refreshed address on another server (other ETag, same bytes) resumes after a byte check",
                    partial >= 200 * 1024 && min > partial / 2 && sampled && it.ETag == "\"srvB\"" && !it.AddressRefreshed
                    && ValidateCompletedItem(it, 1048576, 90, "h15a.bin"),
                    $"partial={partial} min={min} sampled={sampled} etag={it.ETag}");
            });

            await RunBlock("H-15b: refreshed address serving different bytes of the same size starts over and stays intact", async () =>
            {
                var (it, partial) = await PartialDownloadAsync($"{hardBase}/f/1048576?etag=srvA&seed=91&rate=131072&tag=h15b_a", "h15b.bin");
                AddressRefresh.Apply(it, $"{hardBase}/f/1048576?etag=srvB&seed=92&rate=262144&tag=h15b_b", "", "", "");
                long min = await ResumeWatchingAsync(it);
                Check("H-15b: refreshed address serving different bytes of the same size starts over and stays intact",
                    partial >= 200 * 1024 && min < partial / 2 && ValidateCompletedItem(it, 1048576, 92, "h15b.bin"),
                    $"partial={partial} min={min}");
            });

            await RunBlock("H-15c: refreshed address with the very same strong ETag resumes without extra checks", async () =>
            {
                var (it, partial) = await PartialDownloadAsync($"{hardBase}/f/1048576?etag=same1&seed=93&rate=131072&tag=h15c_a", "h15c.bin");
                AddressRefresh.Apply(it, $"{hardBase}/f/1048576?etag=same1&seed=93&rate=262144&tag=h15c_b", "", "", "");
                long min = await ResumeWatchingAsync(it);
                var log = await GetLogAsync("h15c_b");
                bool sampled = log.Any(n => (n?["range"]?.GetValue<string>() ?? "") == "bytes=0-65535");
                Check("H-15c: refreshed address with the very same strong ETag resumes without extra checks",
                    min > partial / 2 && !sampled && ValidateCompletedItem(it, 1048576, 93, "h15c.bin"),
                    $"partial={partial} min={min} sampled={sampled}");
            });

            await RunBlock("H-15e: a plain resume on another mirror (other ETag, same bytes) continues after a byte check", async () =>
            {
                var (it, partial) = await PartialDownloadAsync($"{hardBase}/f/1048576?etag=mirrorA&seed=89&rate=131072&tag=h15e_a", "h15e.bin");
                it.Url = $"{hardBase}/f/1048576?etag=mirrorB&seed=89&rate=262144&tag=h15e_b"; // the redirector picked another mirror
                long min = await ResumeWatchingAsync(it);
                Check("H-15e: a plain resume on another mirror (other ETag, same bytes) continues after a byte check",
                    partial >= 200 * 1024 && min > partial / 2 && it.ETag == "\"mirrorB\"" && ValidateCompletedItem(it, 1048576, 89, "h15e.bin"),
                    $"partial={partial} min={min} etag={it.ETag}");
            });

            await RunBlock("H-15d: refreshed address with another size starts over and saves the new file intact", async () =>
            {
                var (it, partial) = await PartialDownloadAsync($"{hardBase}/f/1048576?etag=srvA&seed=94&rate=131072&tag=h15d_a", "h15d.bin");
                AddressRefresh.Apply(it, $"{hardBase}/f/1000000?etag=srvB&seed=94&rate=262144&tag=h15d_b", "", "", "");
                long min = await ResumeWatchingAsync(it);
                Check("H-15d: refreshed address with another size starts over and saves the new file intact",
                    min < partial / 2 && ValidateCompletedItem(it, 1000000, 94, "h15d.bin"),
                    $"partial={partial} min={min}");
            });

            // =========================================================================
            // H-16: busy servers (429 from rate limiters such as Cloudflare)
            // =========================================================================
            async Task<int> BusyAnswersAsync(string tag) => (await GetStatsAsync(tag))["busy_answers"]?.GetValue<int>() ?? -1;

            await RunBlock("H-16a: a server refusing extra connections: the download finishes intact and the accepted number is remembered", async () =>
            {
                var it = NewHardItem($"{hardBase}/f/4194304?busy_over=2&busy_retry_after=1&seed=95&rate=1048576&tag=h16a", "h16a.bin", conns: 8);
                await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                int busy = await BusyAnswersAsync("h16a");
                var learned = HttpDownloader.LearnedHostLimit("127.0.0.1");
                Check("H-16a: a server refusing extra connections: the download finishes intact and the accepted number is remembered",
                    ValidateCompletedItem(it, 4194304, 95, "h16a.bin") && busy > 0 && learned is >= 1 and <= 2,
                    $"busyAnswers={busy} learned={learned}");

                // The next download from that server starts with no more connections than it accepted: no refusals at all.
                var next = NewHardItem($"{hardBase}/f/4194304?busy_over=3&busy_retry_after=1&seed=96&rate=1048576&tag=h16a2", "h16a2.bin", conns: 8);
                await new HttpDownloader(next, limiter).RunAsync(CancellationToken.None);
                int busy2 = await BusyAnswersAsync("h16a2");
                Check("H-16b: the next download from the same server opens only the accepted connections (no refusals)",
                    ValidateCompletedItem(next, 4194304, 96, "h16a2.bin") && busy2 == 0, $"busyAnswers={busy2}");
            });

            await RunBlock("H-16c: a short ban (every request refused for 6 s) is waited out on the last connection", async () =>
            {
                var it = NewHardItem($"{hardBase}/f/1048576?busy_over=1&busy_ban=6&busy_retry_after=1&seed=97&rate=262144&tag=h16c", "h16c.bin", conns: 4);
                var sw = Stopwatch.StartNew();
                await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                Check("H-16c: a short ban (every request refused for 6 s) is waited out on the last connection",
                    ValidateCompletedItem(it, 1048576, 97, "h16c.bin") && sw.Elapsed.TotalSeconds >= 5,
                    $"elapsed={sw.Elapsed.TotalSeconds:F1}s busyAnswers={await BusyAnswersAsync("h16c")}");
            });

            await RunBlock("H-16d: pause while waiting on a busy server stops at once and keeps the downloaded part", async () =>
            {
                var it = NewHardItem($"{hardBase}/f/1048576?busy_over=1&busy_ban=60&busy_retry_after=30&seed=98&rate=131072&tag=h16d", "h16d.bin", conns: 4);
                using var cts = new CancellationTokenSource();
                var task = new HttpDownloader(it, limiter).RunAsync(cts.Token);
                var waitText = Loc.T("core.http.seg_busy_wait", 30);
                var until = Stopwatch.StartNew();
                while (until.Elapsed < TimeSpan.FromSeconds(10) && !task.IsCompleted)
                {
                    bool waiting;
                    lock (it.SegLock) waiting = it.Segments.Any(s => s.Info == waitText);
                    if (waiting) break;
                    await Task.Delay(20);
                }
                bool sawWait;
                lock (it.SegLock) sawWait = it.Segments.Any(s => s.Info == waitText);
                var sw = Stopwatch.StartNew();
                cts.Cancel();
                bool cancelled = false;
                try { await task; } catch (OperationCanceledException) { cancelled = true; }
                Check("H-16d: pause while waiting on a busy server stops at once and keeps the downloaded part",
                    sawWait && cancelled && sw.Elapsed.TotalSeconds < 1.5 && File.Exists(it.TempPath),
                    $"sawWait={sawWait} cancelled={cancelled} stopIn={sw.ElapsedMilliseconds}ms");
            });

            await RunBlock("H-17a: a browser check (Cloudflare challenge) stops at once, keeps the downloaded part and is never hammered", async () =>
            {
                var it = NewHardItem($"{hardBase}/f/2097152?busy_over=1&busy_ban=30&busy_challenge=1&busy_retry_after=1&seed=99&rate=262144&tag=h17a", "h17a.bin", conns: 4);
                var sw = Stopwatch.StartNew();
                DownloadException? err = null;
                try { await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { err = ex; }
                int refused = await BusyAnswersAsync("h17a");
                Check("H-17a: a browser check (Cloudflare challenge) stops at once, keeps the downloaded part and is never hammered",
                    err is { Kind: FailKind.Challenge } && err.Message == Loc.T("core.http.err_challenge", "127.0.0.1") && sw.Elapsed.TotalSeconds < 15
                    && refused <= 5 && File.Exists(it.TempPath) && !File.Exists(it.FullPath),
                    $"kind={err?.Kind} refused={refused} elapsed={sw.Elapsed.TotalSeconds:F1}s");
            });

            await RunBlock("H-17d: after a browser check the download waits about two minutes by itself and the site gets one connection", async () =>
            {
                var dmSettings = new AppSettings { ProxyMode = "none", DownloadRoot = hardOut, TempDir = "" };
                dmSettings.Normalize();
                var dm = new DownloadManager(dmSettings);
                try
                {
                    var it = dm.CreateHttp($"{hardBase}/f/2097152?busy_over=1&busy_ban=60&busy_challenge=1&seed=101&rate=262144&tag=h17d", fileName: "h17d.bin");
                    it.SaveDir = hardOut;
                    it.NameLocked = true;
                    it.MaxConnections = 4;
                    dm.Add(it, startNow: true);
                    var until = Stopwatch.StartNew();
                    while (until.Elapsed < TimeSpan.FromSeconds(20) && !(it.Status == DownloadStatus.Paused && it.RetryCount == 1) && it.Status != DownloadStatus.Error)
                        await Task.Delay(20);
                    bool waiting = it.Status == DownloadStatus.Paused && it.RetryCount == 1
                                   && it.ErrorMessage == Loc.T("core.error.challenge_retry", 120, 1, DownloadManager.MaxBusyAutoRetries);
                    bool kept = it.Downloaded > 0 && File.Exists(it.TempPath);
                    var learned = HttpDownloader.LearnedHostLimit("127.0.0.1");
                    dm.Pause(it);
                    Check("H-17d: after a browser check the download waits about two minutes by itself and the site gets one connection",
                        waiting && kept && learned == 1 && it.RetryCount == 0,
                        $"status={it.Status} msg='{it.ErrorMessage}' kept={it.Downloaded} learned={learned}");
                }
                finally { dm.Shutdown(); }
            });

            await RunBlock("H-18: a small file (8.5 MB like AnyDesk) starts with few connections: at least 2 MB each", async () =>
            {
                var prev = HttpDownloader.MinInitialPiece;
                HttpDownloader.MinInitialPiece = 2 * 1024 * 1024;
                try
                {
                    var it = NewHardItem($"{hardBase}/f/8553912?seed=102&rate=2097152&tag=h18", "h18.bin", conns: 8);
                    await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                    var st = await GetStatsAsync("h18");
                    int requests = st["requests"]!.GetValue<int>();
                    Check("H-18: a small file (8.5 MB like AnyDesk) starts with few connections: at least 2 MB each",
                        ValidateCompletedItem(it, 8553912, 102, "h18.bin") && st["max"]!.GetValue<int>() <= 5 && requests <= 9,
                        $"maxConns={st["max"]} requests={requests}");
                }
                finally { HttpDownloader.MinInitialPiece = prev; }
            });

            await RunBlock("H-17b: a mirror that shows browsers a web page: Golan asks again as a download tool and gets the real file", async () =>
            {
                var it = NewHardItem($"{hardBase}/files/setup.exe?size=524288&page_for_browsers=1&seed=100&tag=h17b", null, conns: 4);
                await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None);
                Check("H-17b: a mirror that shows browsers a web page: Golan asks again as a download tool and gets the real file",
                    ValidateCompletedItem(it, 524288, 100) && it.UserAgent == HttpDownloader.ToolUserAgent && it.FileName.EndsWith(".exe"),
                    $"name={it.FileName} ua={it.UserAgent}");
            });

            await RunBlock("H-17c: a web page where a file was expected is never saved as that file", async () =>
            {
                var it = NewHardItem($"{hardBase}/files/tool.zip?size=4096&mime=text/html&tag=h17c", null, conns: 1);
                DownloadException? err = null;
                try { await new HttpDownloader(it, limiter).RunAsync(CancellationToken.None); }
                catch (DownloadException ex) { err = ex; }
                Check("H-17c: a web page where a file was expected is never saved as that file",
                    err != null && err.Message == Loc.T("core.http.err_web_page", "127.0.0.1") && !File.Exists(it.FullPath)
                    && HttpDownloader.IsPageInsteadOfFile("text/html", "page.html") == false
                    && HttpDownloader.IsPageInsteadOfFile("text/html; charset=utf-8", "vlc.exe")
                    && !HttpDownloader.IsPageInsteadOfFile("application/octet-stream", "vlc.exe")
                    && !HttpDownloader.IsPageInsteadOfFile("text/html", "download"),
                    $"err={err?.Message}");
            });

            await RunBlock("H-16e: a download that still hears 'busy' is paused and retried later by itself; pausing stops that for good", async () =>
            {
                var prevBusy = HttpDownloader.MaxBusyRetries;
                var prevRetries = HttpDownloader.MaxRetries;
                var prevMaxRetryAfter = HttpDownloader.MaxRetryAfter;
                HttpDownloader.MaxBusyRetries = 1;
                HttpDownloader.MaxRetries = 0;
                HttpDownloader.MaxRetryAfter = TimeSpan.FromMilliseconds(100);
                var dmSettings = new AppSettings { ProxyMode = "none", DownloadRoot = hardOut, TempDir = "" };
                dmSettings.Normalize();
                var dm = new DownloadManager(dmSettings);
                try
                {
                    var it = dm.CreateHttp($"{hardBase}/f/65536?status=429&retry_after=1&tag=h16e", fileName: "h16e.bin");
                    it.SaveDir = hardOut;
                    it.NameLocked = true;
                    it.MaxConnections = 1;
                    dm.Add(it, startNow: true);
                    var until = Stopwatch.StartNew();
                    while (until.Elapsed < TimeSpan.FromSeconds(15) && !(it.Status == DownloadStatus.Paused && it.RetryCount == 1) && it.Status != DownloadStatus.Error)
                        await Task.Delay(20);
                    bool waitsToRetry = it.Status == DownloadStatus.Paused && it.RetryCount == 1
                                        && it.ErrorMessage == Loc.T("core.error.busy_retry", 30, 1, DownloadManager.MaxBusyAutoRetries);
                    dm.Pause(it);
                    bool pausedForGood = it.Status == DownloadStatus.Paused && it.RetryCount == 0 && it.ErrorMessage == "";
                    Check("H-16e: a download that still hears 'busy' is paused and retried later by itself; pausing stops that for good",
                        waitsToRetry && pausedForGood && DownloadManager.BusyAutoRetryDelay(9) == TimeSpan.FromSeconds(120),
                        $"status={it.Status} retry={it.RetryCount} msg='{it.ErrorMessage}'");
                }
                finally
                {
                    dm.Shutdown();
                    HttpDownloader.MaxBusyRetries = prevBusy;
                    HttpDownloader.MaxRetries = prevRetries;
                    HttpDownloader.MaxRetryAfter = prevMaxRetryAfter;
                }
            });
        }
        finally
        {
            HttpDownloader.Configure(origSettings);
            HttpDownloader.MinInitialPiece = origMinPiece;
            if (spawnedServer != null)
            {
                try { if (!spawnedServer.HasExited) spawnedServer.Kill(true); } catch { /* ignore */ }
                spawnedServer.Dispose();
            }
        }

        _lastFails = fails;
        return fails;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    static void MarkFileSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        const uint FSCTL_SET_SPARSE = 0x000900C4;
        DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
}
