using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Golan.Gdm.Core;

namespace Golan.Gdm;

public static class Tests_P2
{
    public static async Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        return await RunInternalAsync(baseUrl, servedDir, outDir, port).WaitAsync(TimeSpan.FromSeconds(120));
    }

    static async Task RunOnStaAsync(Func<Task> scenario, int timeoutSec = 25)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var disp = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(disp));
            var frame = new DispatcherFrame();
            _ = disp.InvokeAsync(async () =>
            {
                try
                {
                    await scenario().WaitAsync(TimeSpan.FromSeconds(timeoutSec));
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            disp.InvokeShutdown();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSec + 5));
    }

    static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(20);
    }

    static void InvokePrivateTick(DownloadManager mgr)
    {
        typeof(DownloadManager).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(mgr, null);
    }

    static void StopManagerTimer(DownloadManager mgr)
    {
        if (typeof(DownloadManager).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mgr) is DispatcherTimer t)
            t.Stop();
        if (typeof(DownloadManager).GetField("_retryTimers", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mgr) is System.Collections.IDictionary dict)
        {
            foreach (var val in dict.Values)
                if (val is DispatcherTimer rt) rt.Stop();
            dict.Clear();
        }
    }

    sealed class MiniHttpServer : IDisposable
    {
        readonly TcpListener _listener;
        readonly CancellationTokenSource _cts = new();
        readonly Func<string, Dictionary<string, string>, (int Code, Dictionary<string, string> Headers, byte[] Body)> _handler;
        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public MiniHttpServer(Func<string, Dictionary<string, string>, (int, Dictionary<string, string>, byte[])> handler)
        {
            _handler = handler;
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
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch { break; }
            }
        }

        async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buf = new byte[8192];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read));
                    if (n <= 0) return;
                    read += n;
                    if (Encoding.ASCII.GetString(buf, 0, read).Contains("\r\n\r\n")) break;
                }
                var reqText = Encoding.ASCII.GetString(buf, 0, read);
                var lines = reqText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var firstParts = lines[0].Split(' ');
                var method = firstParts[0];
                var rawPath = firstParts.Length > 1 ? firstParts[1] : "/";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int idx = line.IndexOf(':');
                    if (idx > 0) headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }

                var (code, respHeaders, body) = _handler(rawPath, headers);
                var reason = code switch
                {
                    200 => "OK",
                    206 => "Partial Content",
                    302 => "Found",
                    401 => "Unauthorized",
                    403 => "Forbidden",
                    404 => "Not Found",
                    500 => "Internal Server Error",
                    503 => "Service Unavailable",
                    _ => "Status"
                };
                var sb = new StringBuilder($"HTTP/1.1 {code} {reason}\r\nConnection: close\r\n");
                foreach (var (k, v) in respHeaders) sb.Append($"{k}: {v}\r\n");
                sb.Append("\r\n");
                var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
                await stream.WriteAsync(headBytes);
                if (!string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) && body.Length > 0)
                    await stream.WriteAsync(body);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }

    static async Task<int> RunInternalAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        int passes = 0, fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [P2] {name} {detail}");
            if (ok) passes++; else fails++;
        }

        var workRoot = Path.Combine(Path.GetTempPath(), "gdm-p2-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        // Ensure sample.zip exists in servedDir if servedDir is provided
        if (!string.IsNullOrWhiteSpace(servedDir) && Directory.Exists(servedDir))
        {
            var zipPath = Path.Combine(servedDir, "sample.zip");
            if (!File.Exists(zipPath))
            {
                using var z = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                for (int i = 0; i < 25; i++)
                {
                    var e = z.CreateEntry($"folder{i % 3}/file{i}.txt");
                    using var w = new StreamWriter(e.Open());
                    w.Write(new string('x', 1000 + i));
                }
            }
        }

        // Back up %APPDATA%\Golan\GDM files and cookies so tests never clobber user data.
        Directory.CreateDirectory(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.CookiesDir);
        var backupFiles = new[]
        {
            AppPaths.DownloadsFile,
            AppPaths.SettingsFile,
            Path.Combine(AppPaths.DataDir, "quota.json"),
            Path.Combine(AppPaths.DataDir, "grabber.json")
        };
        var backups = new Dictionary<string, byte[]?>();
        foreach (var f in backupFiles)
            backups[f] = File.Exists(f) ? File.ReadAllBytes(f) : null;
        var existingCookies = Directory.GetFiles(AppPaths.CookiesDir)
            .ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

        try
        {
            var payload64K = RandomNumberGenerator.GetBytes(64 * 1024);
            var payloadSha = Convert.ToHexString(SHA256.HashData(payload64K));
            int transient503Count = 0;
            int always503Hits = 0;
            int forbidden403Hits = 0;
            int slowSyncVersion = 1;
            int lastModVersion = 1;
            int sizeOnlyVersion = 1;

            using var srv = new MiniHttpServer((rawPath, reqHeaders) =>
            {
                var path = rawPath.Split('?')[0];
                if (path.StartsWith("/ok/"))
                {
                    var name = Path.GetFileName(path);
                    return (200, new()
                    {
                        ["Content-Length"] = payload64K.Length.ToString(),
                        ["Content-Type"] = "application/octet-stream",
                        ["Content-Disposition"] = $"attachment; filename=\"{name}\""
                    }, payload64K);
                }
                if (path == "/slow.bin")
                {
                    Thread.Sleep(250);
                    return (200, new()
                    {
                        ["Content-Length"] = payload64K.Length.ToString(),
                        ["Content-Type"] = "application/octet-stream"
                    }, payload64K);
                }
                if (path == "/veryslow.bin")
                {
                    Thread.Sleep(1500);
                    return (200, new()
                    {
                        ["Content-Length"] = payload64K.Length.ToString(),
                        ["Content-Type"] = "application/octet-stream"
                    }, payload64K);
                }
                if (path == "/transient503.bin")
                {
                    if (Interlocked.Increment(ref transient503Count) == 1)
                        return (503, new() { ["Content-Length"] = "0", ["Retry-After"] = "1" }, []);
                    return (200, new()
                    {
                        ["Content-Length"] = payload64K.Length.ToString(),
                        ["Content-Type"] = "application/octet-stream"
                    }, payload64K);
                }
                if (path == "/always503.bin")
                {
                    Interlocked.Increment(ref always503Hits);
                    return (503, new() { ["Content-Length"] = "0", ["Retry-After"] = "0" }, []);
                }
                if (path == "/forbidden.bin")
                {
                    Interlocked.Increment(ref forbidden403Hits);
                    return (403, new() { ["Content-Length"] = "0" }, []);
                }
                if (path == "/sync-etag.bin")
                {
                    var body = Encoding.UTF8.GetBytes($"sync-content-v{slowSyncVersion}");
                    return (200, new()
                    {
                        ["Content-Length"] = body.Length.ToString(),
                        ["ETag"] = $"\"etag-v{slowSyncVersion}\"",
                        ["Accept-Ranges"] = "bytes"
                    }, body);
                }
                if (path == "/sync-lastmod.bin")
                {
                    var body = Encoding.UTF8.GetBytes("lastmod-fixed-size");
                    var lm = lastModVersion == 1
                        ? "Wed, 21 Oct 2020 07:28:00 GMT"
                        : "Thu, 22 Oct 2020 09:00:00 GMT";
                    return (200, new()
                    {
                        ["Content-Length"] = body.Length.ToString(),
                        ["Last-Modified"] = lm
                    }, body);
                }
                if (path == "/sync-sizeonly.bin")
                {
                    var body = new byte[sizeOnlyVersion == 1 ? 1024 : 2048];
                    return (200, new()
                    {
                        ["Content-Length"] = body.Length.ToString()
                    }, body);
                }
                if (path == "/resumable.bin")
                {
                    return (206, new()
                    {
                        ["Content-Length"] = "1",
                        ["Content-Range"] = $"bytes 0-0/{payload64K.Length}",
                        ["Accept-Ranges"] = "bytes"
                    }, [payload64K[0]]);
                }
                return (404, new() { ["Content-Length"] = "0" }, []);
            });

            // -------------------------------------------------------------------------
            // 1. Repro 6 (D-1) & M1/M4 Queue Tests (Fix P2-1)
            // -------------------------------------------------------------------------
            await RunOnStaAsync(async () =>
            {
                File.Delete(AppPaths.DownloadsFile);
                File.Delete(Path.Combine(AppPaths.DataDir, "quota.json"));
                var qDir = Path.Combine(workRoot, "m1");
                Directory.CreateDirectory(qDir);

                var s = new AppSettings { ProxyMode = "none", DownloadRoot = qDir, UseCategoryFolders = false, MaxConnections = 2 };
                s.Normalize();
                s.MainQueue.MaxSimultaneous = 2;

                var mgr = new DownloadManager(s);
                try
                {
                    // Repro 6 (D-1): ItemCompleted observes non-null QueueId for queued download
                    var qItem = mgr.CreateHttp($"{srv.BaseUrl}/ok/repro-queue-done.zip", fileName: "repro-queue-done.zip");
                    qItem.SaveDir = qDir;
                    Guid? queueIdInsideCompletedEvent = null;
                    Action<DownloadItem>? handler = null;
                    var tcs = new TaskCompletionSource<bool>();
                    handler = completed =>
                    {
                        if (completed.Id == qItem.Id)
                        {
                            queueIdInsideCompletedEvent = completed.QueueId;
                            tcs.TrySetResult(true);
                        }
                    };
                    mgr.ItemCompleted += handler;
                    mgr.Add(qItem, startNow: false, QueueDef.MainId);
                    mgr.StartQueue(QueueDef.MainId);
                    await WaitUntilAsync(() => tcs.Task.IsCompleted);
                    mgr.ItemCompleted -= handler;
                    Check("repro D-1: ItemCompleted observes non-null QueueId for queued download",
                        queueIdInsideCompletedEvent == QueueDef.MainId && qItem.QueueId == null,
                        $"QueueId in ItemCompleted={(queueIdInsideCompletedEvent?.ToString() ?? "null")}, after={qItem.QueueId?.ToString() ?? "null"}");
                    await mgr.RemoveAsync(qItem, deleteFile: true);

                    // M1: MoveInQueue + QueueScheduler.Tick + QueueFinished
                    var d1 = mgr.CreateHttp($"{srv.BaseUrl}/ok/q1.bin"); d1.SaveDir = qDir;
                    var d2 = mgr.CreateHttp($"{srv.BaseUrl}/ok/q2.bin"); d2.SaveDir = qDir;
                    var d3 = mgr.CreateHttp($"{srv.BaseUrl}/ok/q3.bin"); d3.SaveDir = qDir;
                    mgr.Add(d1, startNow: false, QueueDef.MainId);
                    mgr.Add(d2, startNow: false, QueueDef.MainId);
                    mgr.Add(d3, startNow: false, QueueDef.MainId);

                    mgr.MoveInQueue(d3, -1);
                    var orderAfterMove = mgr.QueueItems(QueueDef.MainId).Select(i => i.FileName).ToArray();
                    Check("M1: MoveInQueue(-1) swaps queue positions", orderAfterMove.SequenceEqual(["q1.bin", "q3.bin", "q2.bin"]));

                    bool qStarted = false, qFinished = false;
                    mgr.QueueStarted += q => { if (q.Id == QueueDef.MainId) qStarted = true; };
                    mgr.QueueFinished += q => { if (q.Id == QueueDef.MainId) qFinished = true; };

                    s.MainQueue.ScheduleEnabled = true;
                    s.MainQueue.Daily = true;
                    s.MainQueue.StartTime = "03:00";
                    s.MainQueue.LastStart = null;
                    var sched = new QueueScheduler(mgr);
                    sched.Tick(DateTime.Today.AddHours(3).AddMinutes(5));

                    Check("M1: QueueScheduler.Tick starts MainQueue when due", qStarted && mgr.IsQueueRunning(QueueDef.MainId));

                    await WaitUntilAsync(() => qFinished && mgr.Items.All(i => i.Status == DownloadStatus.Completed));
                    Check("M1: MainQueue pumps all 3 items and fires QueueFinished", qFinished && !mgr.IsQueueRunning(QueueDef.MainId));
                    Check("M1: All 3 queued downloads match SHA-256",
                        mgr.Items.All(i => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(i.FullPath))) == payloadSha));

                    // StopQueue puts interrupted running items back to Queued (RequeueOnStop)
                    var slow = mgr.CreateHttp($"{srv.BaseUrl}/slow.bin"); slow.SaveDir = qDir;
                    mgr.Add(slow, startNow: false, QueueDef.MainId);
                    mgr.StartQueue(QueueDef.MainId);
                    await Task.Delay(40);
                    mgr.StopQueue(QueueDef.MainId);
                    await WaitUntilAsync(() => !mgr.IsRunning(slow));
                    Check("M1: StopQueue returns interrupted active download to Queued state",
                        slow.Status == DownloadStatus.Queued && !mgr.IsQueueRunning(QueueDef.MainId));
                    await mgr.RemoveAsync(slow, deleteFile: true);

                    // M1: Deleting a custom queue while its item is downloading must not fail with NullReferenceException
                    var customQ = new QueueDef { Name = "TempQueue" };
                    s.Queues.Add(customQ);
                    var delItem = mgr.CreateHttp($"{srv.BaseUrl}/slow.bin", fileName: "deleted-q.bin");
                    delItem.SaveDir = qDir;
                    mgr.Add(delItem, startNow: false, customQ.Id);
                    mgr.StartQueue(customQ.Id);
                    await Task.Delay(30);
                    s.Queues.Remove(customQ);
                    await WaitUntilAsync(() => !mgr.IsRunning(delItem));
                    Check("M1: Deleting custom queue during download does not throw NullReferenceException",
                        delItem.Status == DownloadStatus.Completed && delItem.QueueId == null,
                        $"Status={delItem.Status}");
                    await mgr.RemoveAsync(delItem, deleteFile: true);
                }
                finally
                {
                    StopManagerTimer(mgr);
                }
            });

            // -------------------------------------------------------------------------
            // 2. M4 Synchronization Queue & Custom Periodic Queue Retention (Fix P2-1)
            // -------------------------------------------------------------------------
            await RunOnStaAsync(async () =>
            {
                File.Delete(AppPaths.DownloadsFile);
                var syncDir = Path.Combine(workRoot, "m4");
                Directory.CreateDirectory(syncDir);

                var s = new AppSettings { ProxyMode = "none", DownloadRoot = syncDir, UseCategoryFolders = false, MaxConnections = 1 };
                s.Normalize();
                var mgr = new DownloadManager(s);
                try
                {
                    lastModVersion = 1;
                    var lmItem = mgr.CreateHttp($"{srv.BaseUrl}/sync-lastmod.bin");
                    var lmProbe = await HttpDownloader.ProbeAsync(lmItem, CancellationToken.None);
                    lmItem.LastModified = lmProbe.LastModified;
                    lmItem.TotalSize = lmProbe.Size;
                    Check("M4: HasChangedAsync false when Last-Modified unchanged",
                        !await HttpDownloader.HasChangedAsync(lmItem, CancellationToken.None));
                    lastModVersion = 2;
                    Check("M4: HasChangedAsync true when Last-Modified changes",
                        await HttpDownloader.HasChangedAsync(lmItem, CancellationToken.None));

                    sizeOnlyVersion = 1;
                    var szItem = mgr.CreateHttp($"{srv.BaseUrl}/sync-sizeonly.bin");
                    var szProbe = await HttpDownloader.ProbeAsync(szItem, CancellationToken.None);
                    szItem.TotalSize = szProbe.Size;
                    Check("M4: HasChangedAsync false when Content-Length matches",
                        !await HttpDownloader.HasChangedAsync(szItem, CancellationToken.None));
                    sizeOnlyVersion = 2;
                    Check("M4: HasChangedAsync true when Content-Length changes",
                        await HttpDownloader.HasChangedAsync(szItem, CancellationToken.None));

                    slowSyncVersion = 1;
                    var syncItem = mgr.CreateHttp($"{srv.BaseUrl}/sync-etag.bin", fileName: "sync-etag.bin");
                    syncItem.SaveDir = syncDir;
                    mgr.Add(syncItem, startNow: false, QueueDef.SyncId);
                    mgr.StartQueue(QueueDef.SyncId);
                    await WaitUntilAsync(() => syncItem.Status == DownloadStatus.Completed && !mgr.IsQueueRunning(QueueDef.SyncId));
                    Check("M4: SyncQueue initial download retains QueueDef.SyncId",
                        syncItem.QueueId == QueueDef.SyncId && File.ReadAllText(syncItem.FullPath) == "sync-content-v1");

                    slowSyncVersion = 2;
                    mgr.StartQueue(QueueDef.SyncId);
                    await WaitUntilAsync(() => !mgr.IsQueueRunning(QueueDef.SyncId) && File.Exists(syncItem.FullPath) && File.ReadAllText(syncItem.FullPath) == "sync-content-v2");
                    Check("M4: StartQueue(SyncId) re-downloads changed file v2",
                        File.ReadAllText(syncItem.FullPath) == "sync-content-v2" && syncItem.QueueId == QueueDef.SyncId);

                    var customSyncQ = new QueueDef { Name = "CustomPeriodic", Periodic = true };
                    s.Queues.Add(customSyncQ);
                    var customSyncItem = mgr.CreateHttp($"{srv.BaseUrl}/ok/custom-sync.bin", fileName: "custom-sync.bin");
                    customSyncItem.SaveDir = syncDir;
                    mgr.Add(customSyncItem, startNow: false, customSyncQ.Id);
                    mgr.StartQueue(customSyncQ.Id);
                    await WaitUntilAsync(() => customSyncItem.Status == DownloadStatus.Completed);
                    Check("M4: Custom queue with Periodic=true retains item.QueueId after completion",
                        customSyncItem.QueueId == customSyncQ.Id,
                        $"QueueId={customSyncItem.QueueId?.ToString() ?? "null"}");
                }
                finally
                {
                    StopManagerTimer(mgr);
                }
            });

            // -------------------------------------------------------------------------
            // 3. Repro 7 (D-3) & M2 Quota Tests (Fix P2-3)
            // -------------------------------------------------------------------------
            await RunOnStaAsync(async () =>
            {
                File.Delete(AppPaths.DownloadsFile);
                var quotaPath = Path.Combine(AppPaths.DataDir, "quota.json");
                File.Delete(quotaPath);
                var qDir = Path.Combine(workRoot, "m2");
                Directory.CreateDirectory(qDir);

                // Repro 7 (D-3): Fast download completing between 500ms ticks records bytes in QuotaUsedBytes
                var fastSettings = new AppSettings { ProxyMode = "none", DownloadRoot = qDir, UseCategoryFolders = false, QuotaEnabled = true, QuotaMB = 10, QuotaHours = 1 };
                fastSettings.Normalize();
                var fastMgr = new DownloadManager(fastSettings);
                try
                {
                    var fastItem = fastMgr.CreateHttp($"{srv.BaseUrl}/ok/repro-quota-fast.bin", fileName: "repro-quota-fast.bin");
                    fastItem.SaveDir = qDir;
                    var tcs = new TaskCompletionSource<bool>();
                    fastMgr.ItemCompleted += _ => tcs.TrySetResult(true);
                    fastMgr.Add(fastItem, startNow: true);
                    await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    await WaitUntilAsync(() => !fastMgr.IsRunning(fastItem));
                    Check("repro D-3: fast download completing between 500ms ticks records full bytes in QuotaUsedBytes",
                        fastMgr.QuotaUsedBytes >= fastItem.TotalSize && fastItem.TotalSize > 0,
                        $"QuotaUsedBytes={fastMgr.QuotaUsedBytes}, fileSize={fastItem.TotalSize}");
                    await fastMgr.RemoveAsync(fastItem, deleteFile: true);
                }
                finally
                {
                    StopManagerTimer(fastMgr);
                }

                // M2: QuotaBlocked on Load(), Start(item) blocked, StopQueue clears _quotaPausedQueues, QuotaEnabled=false unblocks
                File.Delete(AppPaths.DownloadsFile);
                var s = new AppSettings
                {
                    ProxyMode = "none",
                    DownloadRoot = qDir,
                    UseCategoryFolders = false,
                    QuotaEnabled = true,
                    QuotaMB = 1,
                    QuotaHours = 1
                };
                s.Normalize();

                var seedQuota = JsonSerializer.Serialize(new[]
                {
                    new { At = DateTime.Now.AddMinutes(-5), Bytes = 2L * 1024 * 1024 }
                }, Json.Options);
                File.WriteAllText(quotaPath, seedQuota);

                var mgr = new DownloadManager(s);
                try
                {
                    Check("M2: DownloadManager.Load reads recent usage from quota.json",
                        mgr.QuotaUsedBytes == 2L * 1024 * 1024);
                    Check("M2: QuotaBlocked is true immediately after Load() when quota.json exceeds QuotaMB",
                        mgr.QuotaBlocked);

                    var qStoppedWhileBlocked = mgr.CreateHttp($"{srv.BaseUrl}/veryslow.bin", fileName: "qstopped.bin");
                    qStoppedWhileBlocked.SaveDir = qDir;
                    mgr.Add(qStoppedWhileBlocked, startNow: false, QueueDef.MainId);
                    mgr.StartQueue(QueueDef.MainId);
                    InvokePrivateTick(mgr);
                    Check("M2: QuotaBlocked stays true on Tick()", mgr.QuotaBlocked);

                    mgr.StopQueue(QueueDef.MainId);
                    Check("M2: StopQueue clears MainQueue while QuotaBlocked", !mgr.IsQueueRunning(QueueDef.MainId));

                    var directItem = mgr.CreateHttp($"{srv.BaseUrl}/ok/direct-under-quota.bin");
                    directItem.SaveDir = qDir;
                    mgr.Items.Add(directItem);
                    mgr.Start(directItem);
                    bool ranWhileBlocked = mgr.IsRunning(directItem);
                    await WaitUntilAsync(() => !mgr.IsRunning(directItem));
                    Check("M2: Start(item) does not bypass QuotaBlocked",
                        !ranWhileBlocked && directItem.Status == DownloadStatus.Paused,
                        $"ranWhileBlocked={ranWhileBlocked}, status={directItem.Status}");

                    s.QuotaMB = 2;
                    InvokePrivateTick(mgr);
                    Check("M2: QuotaBlocked stays true at 100% of limit (hysteresis >= 95%)", mgr.QuotaBlocked);

                    s.QuotaMB = 10;
                    InvokePrivateTick(mgr);
                    Check("M2: QuotaBlocked becomes false when usage drops below 95% of limit", !mgr.QuotaBlocked);
                    Check("M2: Unblocking quota does not restart a queue explicitly stopped via StopQueue",
                        !mgr.IsQueueRunning(QueueDef.MainId));
                    await mgr.RemoveAsync(qStoppedWhileBlocked, deleteFile: true);

                    // Also test disabling QuotaEnabled while blocked restores _quotaPausedQueues
                    var qHold = mgr.CreateHttp($"{srv.BaseUrl}/veryslow.bin", fileName: "qhold.bin");
                    qHold.SaveDir = qDir;
                    mgr.Add(qHold, startNow: false, QueueDef.MainId);
                    mgr.StartQueue(QueueDef.MainId);
                    s.QuotaMB = 1;
                    InvokePrivateTick(mgr);
                    bool blockedAgain = mgr.QuotaBlocked;
                    s.QuotaEnabled = false;
                    InvokePrivateTick(mgr);
                    Check("M2: Disabling QuotaEnabled while QuotaBlocked unblocks and restores running queue",
                        blockedAgain && !mgr.QuotaBlocked && mgr.IsQueueRunning(QueueDef.MainId));
                    mgr.StopQueue(QueueDef.MainId);
                    await mgr.RemoveAsync(qHold, deleteFile: true);
                }
                finally
                {
                    StopManagerTimer(mgr);
                }
            });

            // -------------------------------------------------------------------------
            // 4. M3 Retry & D-2 CheckQueueFinished Tests (Fix P2-2)
            // -------------------------------------------------------------------------
            await RunOnStaAsync(async () =>
            {
                File.Delete(AppPaths.DownloadsFile);
                File.Delete(Path.Combine(AppPaths.DataDir, "quota.json"));
                var rDir = Path.Combine(workRoot, "m3");
                Directory.CreateDirectory(rDir);

                var s = new AppSettings { ProxyMode = "none", DownloadRoot = rDir, UseCategoryFolders = false, MaxConnections = 2 };
                s.Normalize();
                s.MainQueue.Retries = 3;
                s.MainQueue.MaxSimultaneous = 2;

                var mgr = new DownloadManager(s);
                try
                {
                    // 1) Transient 503 recovers automatically via HttpDownloader retry
                    var transient = mgr.CreateHttp($"{srv.BaseUrl}/transient503.bin");
                    transient.SaveDir = rDir;
                    await new HttpDownloader(transient, new SpeedLimiter()).RunAsync(CancellationToken.None);
                    Check("M3: HttpDownloader recovers from transient HTTP 503",
                        File.Exists(transient.FullPath) && new FileInfo(transient.FullPath).Length == payload64K.Length);

                    // 2) Permanent HTTP 403 Forbidden (FailKind.Expired) is not retried
                    forbidden403Hits = 0;
                    var perm403 = mgr.CreateHttp($"{srv.BaseUrl}/forbidden.bin", fileName: "forbidden.bin");
                    perm403.SaveDir = rDir;
                    var goodItem = mgr.CreateHttp($"{srv.BaseUrl}/slow.bin", fileName: "good.bin");
                    goodItem.SaveDir = rDir;

                    mgr.Add(perm403, startNow: false, QueueDef.MainId);
                    mgr.Add(goodItem, startNow: false, QueueDef.MainId);

                    mgr.StartQueue(QueueDef.MainId);
                    await WaitUntilAsync(() => goodItem.Status == DownloadStatus.Completed && !mgr.IsRunning(perm403));

                    Check("M3: Permanent HTTP 403 (FailKind.Expired) fails immediately with RetryCount == 0",
                        perm403.Status == DownloadStatus.Error && perm403.RetryCount == 0 && forbidden403Hits == 1,
                        $"Status={perm403.Status}, RetryCount={perm403.RetryCount}, hits={forbidden403Hits}");

                    await mgr.RemoveAsync(perm403, deleteFile: true);
                    await mgr.RemoveAsync(goodItem, deleteFile: true);

                    // 3) D-2: Retryable DownloadException enters ScheduleRetry and keeps queue running in CheckQueueFinished
                    var retryItem = mgr.CreateHttp($"{srv.BaseUrl}/ok/retry-target.bin", fileName: "retry-target.bin");
                    retryItem.SaveDir = rDir;
                    mgr.Add(retryItem, startNow: false, QueueDef.MainId);
                    typeof(DownloadManager).GetField("_runningQueues", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(mgr)!.GetType().GetMethod("Add")!.Invoke(
                            typeof(DownloadManager).GetField("_runningQueues", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mgr),
                            [QueueDef.MainId]);

                    retryItem.RetryCount = 1;
                    retryItem.Status = DownloadStatus.Paused;
                    typeof(DownloadManager).GetMethod("ScheduleRetry", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(mgr, [retryItem, null]); // (item, delay = default)
                    typeof(DownloadManager).GetMethod("CheckQueueFinished", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(mgr, [s.MainQueue]);

                    Check("D-2: CheckQueueFinished does not finish queue while item is waiting in ScheduleRetry (_retryTimers)",
                        mgr.IsQueueRunning(QueueDef.MainId),
                        $"IsQueueRunning={mgr.IsQueueRunning(QueueDef.MainId)}");

                    mgr.StopQueue(QueueDef.MainId);
                    await mgr.RemoveAsync(retryItem, deleteFile: true);
                }
                finally
                {
                    StopManagerTimer(mgr);
                }
            });

            // -------------------------------------------------------------------------
            // 5. Repro 8 (D-4) & RelocateAsync UNC Validation (Fix P2-4)
            // -------------------------------------------------------------------------
            await RunOnStaAsync(async () =>
            {
                File.Delete(AppPaths.DownloadsFile);
                var d4Dir = Path.Combine(workRoot, "d4");
                Directory.CreateDirectory(d4Dir);
                var dmSettings = new AppSettings { ProxyMode = "none", DownloadRoot = d4Dir, UseCategoryFolders = false };
                dmSettings.Normalize();
                var dm = new DownloadManager(dmSettings);
                try
                {
                    var earlyItem = dm.CreateHttp($"{srv.BaseUrl}/veryslow.bin", fileName: "repro-early-later.bin");
                    earlyItem.SaveDir = d4Dir;
                    dm.Add(earlyItem, startNow: true);
                    await Task.Delay(80);
                    bool wasRunningBeforeRelocate = dm.IsRunning(earlyItem);
                    await dm.RelocateAsync(earlyItem, earlyItem.SaveDir, earlyItem.FileName, nameLocked: false, resumeIfRunning: false);
                    bool stillRunningAfterLater = dm.IsRunning(earlyItem);
                    Check("repro D-4: RelocateAsync(resumeIfRunning: false) stops early-started download even when path is unchanged",
                        wasRunningBeforeRelocate && !stillRunningAfterLater,
                        $"wasRunning={wasRunningBeforeRelocate}, stillRunning={stillRunningAfterLater}");

                    // Unconfigured UNC path rejected before Directory.CreateDirectory
                    bool uncBlocked = false;
                    try
                    {
                        await dm.RelocateAsync(earlyItem, @"\\127.0.0.1\unconfigured_share\folder", "repro-early-later.bin", nameLocked: true, resumeIfRunning: false);
                    }
                    catch (Exception ex)
                    {
                        uncBlocked = ex is DownloadException;
                    }
                    Check("P2-4: RelocateAsync blocks unconfigured UNC path before Directory.CreateDirectory", uncBlocked);
                    await dm.RemoveAsync(earlyItem, deleteFile: true);
                }
                finally
                {
                    StopManagerTimer(dm);
                }
            });

            // -------------------------------------------------------------------------
            // 6. MediaCookieFile Preservation on Retry & Orphan Cookie Purge (Fix P2-5)
            // -------------------------------------------------------------------------
            await RunOnStaAsync(async () =>
            {
                File.Delete(AppPaths.DownloadsFile);
                var orphanCookie = Path.Combine(AppPaths.CookiesDir, "orphan-test-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(orphanCookie, "dummy-orphan-cookie");

                var p5Dir = Path.Combine(workRoot, "p5");
                Directory.CreateDirectory(p5Dir);
                var s = new AppSettings { ProxyMode = "none", DownloadRoot = p5Dir, UseCategoryFolders = false };
                s.Normalize();
                s.MainQueue.Retries = 2;
                var mgr = new DownloadManager(s);
                try
                {
                    Check("P2-5: DownloadManager.Load purges orphan cookie files even when downloads.json does not exist",
                        !File.Exists(orphanCookie));

                    var retryCookie = Path.Combine(AppPaths.CookiesDir, "retry-cookie-" + Guid.NewGuid().ToString("N") + ".txt");
                    File.WriteAllText(retryCookie, "cookie-for-retry");
                    var cookieItem = mgr.CreateHttp($"{srv.BaseUrl}/resumable.bin", fileName: "cookie-retry.bin");
                    cookieItem.SaveDir = p5Dir;
                    cookieItem.NameLocked = true;
                    cookieItem.MediaCookieFile = retryCookie;
                    mgr.Add(cookieItem, startNow: false, QueueDef.MainId);

                    // Lock TempPath so StartFreshAsync fails immediately with IOException (retryable)
                    using (var lockFs = new FileStream(cookieItem.TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    {
                        mgr.StartQueue(QueueDef.MainId);
                        await WaitUntilAsync(() => cookieItem.RetryCount >= 1 && !mgr.IsRunning(cookieItem));
                        Check("P2-5: MediaCookieFile is preserved across retryable failure while retries remain",
                            cookieItem.RetryCount == 1 && File.Exists(retryCookie),
                            $"RetryCount={cookieItem.RetryCount}, cookieExists={File.Exists(retryCookie)}");
                    }
                    mgr.StopQueue(QueueDef.MainId);
                    await mgr.RemoveAsync(cookieItem, deleteFile: true);
                    Check("P2-5: MediaCookieFile is deleted on RemoveAsync", !File.Exists(retryCookie));
                }
                finally
                {
                    StopManagerTimer(mgr);
                    MediaDownloader.TryDelete(orphanCookie);
                }
            });

            // -------------------------------------------------------------------------
            // 7. RemoveAsync Shared Path Guard & Bounded Shutdown (Fix P2-6)
            // -------------------------------------------------------------------------
            await RunOnStaAsync(async () =>
            {
                File.Delete(AppPaths.DownloadsFile);
                var p6Dir = Path.Combine(workRoot, "p6");
                Directory.CreateDirectory(p6Dir);
                var s = new AppSettings { ProxyMode = "none", DownloadRoot = p6Dir, UseCategoryFolders = false };
                s.Normalize();
                var mgr = new DownloadManager(s);
                try
                {
                    var oldItem = mgr.CreateHttp($"{srv.BaseUrl}/ok/shared.bin", fileName: "shared.bin");
                    oldItem.SaveDir = p6Dir;
                    var newItem = mgr.CreateHttp($"{srv.BaseUrl}/ok/shared.bin", fileName: "shared.bin");
                    newItem.SaveDir = p6Dir;

                    mgr.Items.Add(oldItem);
                    mgr.Items.Add(newItem);
                    File.WriteAllText(newItem.FullPath, "keep-full");
                    File.WriteAllText(newItem.TempPath, "keep-temp");

                    await mgr.RemoveAsync(oldItem, deleteFile: true);

                    Check("P2-6: RemoveAsync does not delete FullPath or TempPath when shared with another item in Items",
                        File.Exists(newItem.FullPath) && File.Exists(newItem.TempPath));

                    await mgr.RemoveAsync(newItem, deleteFile: true);

                    var shutdownItem = mgr.CreateHttp($"{srv.BaseUrl}/veryslow.bin", fileName: "shutdown-active.bin");
                    shutdownItem.SaveDir = p6Dir;
                    mgr.Add(shutdownItem, startNow: true);
                    await Task.Delay(50);
                    var swShutdown = Stopwatch.StartNew();
                    mgr.Shutdown();
                    swShutdown.Stop();
                    Check("P2-6: Shutdown cancels active downloads and completes within 5s",
                        swShutdown.ElapsedMilliseconds < 5000 && shutdownItem.Status == DownloadStatus.Paused,
                        $"elapsed={swShutdown.ElapsedMilliseconds}ms, status={shutdownItem.Status}");
                }
                finally
                {
                    StopManagerTimer(mgr);
                }
            });

            // -------------------------------------------------------------------------
            // 8. Repro 9 (S-1a), Repro 10 (S-1b, S-1c) & M1 Scheduler Tests (Fix P2-7)
            // -------------------------------------------------------------------------
            {
                var disabledQ = new QueueDef { ScheduleEnabled = false, Daily = true, StartTime = "04:00" };
                Check("M1/P2-7: QueueScheduler.ShouldStart returns false when ScheduleEnabled is false",
                    !QueueScheduler.ShouldStart(disabledQ, DateTime.Today.AddHours(4).AddMinutes(5)));

                var overnightQ = new QueueDef
                {
                    ScheduleEnabled = true,
                    Daily = true,
                    StartTime = "22:00",
                    StopEnabled = true,
                    StopTime = "06:00",
                    RepeatEnabled = true,
                    RepeatHours = 1,
                    RepeatMinutes = 0,
                    LastStart = new DateTime(2026, 10, 19, 23, 30, 0),
                };
                bool repeatsAfterMidnight = QueueScheduler.ShouldStart(overnightQ, new DateTime(2026, 10, 20, 0, 35, 0));
                Check("repro S-1a: overnight queue (22:00->06:00) continues repeating between 00:00 and 06:00",
                    repeatsAfterMidnight);

                var overnightQ2 = new QueueDef
                {
                    ScheduleEnabled = true,
                    Daily = true,
                    StartTime = "22:00",
                    StopEnabled = true,
                    StopTime = "06:00",
                    RepeatEnabled = true,
                    RepeatHours = 2,
                    LastStart = DateTime.Today.AddDays(-1).AddHours(22)
                };
                Check("M1: overnight schedule (22:00->06:00) with RepeatHours=2 triggers after midnight at 00:30",
                    QueueScheduler.ShouldStart(overnightQ2, DateTime.Today.AddMinutes(30)));

                var shortWindowQ = new QueueDef
                {
                    ScheduleEnabled = true,
                    Daily = true,
                    StartTime = "02:00",
                    StopEnabled = true,
                    StopTime = "02:30",
                };
                bool startsAfterStop = QueueScheduler.ShouldStart(shortWindowQ, new DateTime(2026, 10, 19, 2, 45, 0));
                Check("repro S-1b: QueueScheduler does not start at 02:45 when StopTime is 02:30",
                    !startsAfterStop);

                var onceQ = new QueueDef
                {
                    ScheduleEnabled = true,
                    Daily = false,
                    OnceDate = new DateTime(2026, 10, 19),
                    StartTime = "10:00",
                    StopEnabled = true,
                    StopTime = "18:00",
                    RepeatEnabled = true,
                    RepeatHours = 2,
                    LastStart = new DateTime(2026, 10, 19, 16, 0, 0),
                };
                bool repeatsNextDay = QueueScheduler.ShouldStart(onceQ, new DateTime(2026, 10, 20, 12, 0, 0));
                Check("repro S-1c: one-time schedule (Daily=false) does not repeat on subsequent days after OnceDate",
                    !repeatsNextDay);

                var onceOvernightQ = new QueueDef
                {
                    ScheduleEnabled = true,
                    Daily = false,
                    OnceDate = new DateTime(2026, 10, 19),
                    StartTime = "22:00",
                    StopEnabled = true,
                    StopTime = "06:00",
                };
                Check("P2-7: one-time overnight schedule (22:00->06:00) does not start on second night (OnceDate+1 at 22:30)",
                    !QueueScheduler.ShouldStart(onceOvernightQ, new DateTime(2026, 10, 20, 22, 30, 0)));

                var onceRepeatQ = new QueueDef
                {
                    ScheduleEnabled = true,
                    Daily = false,
                    OnceDate = DateTime.Today.AddDays(-3),
                    StartTime = "02:00",
                    RepeatEnabled = true,
                    RepeatHours = 1,
                    LastStart = DateTime.Today.AddDays(-3).AddHours(2)
                };
                Check("M1: one-time queue without StopEnabled does not repeat on subsequent days after OnceDate",
                    !QueueScheduler.ShouldStart(onceRepeatQ, DateTime.Today.AddHours(10)));
            }

            // -------------------------------------------------------------------------
            // 9. M5 AppSettings.LoginFor Security Parity & Lazy Default Names (Fix P2-8)
            // -------------------------------------------------------------------------
            {
                var s = new AppSettings { AutoStartExtensions = ".PDF; zip, .MSI" };
                s.CategoryDefs.Add(new CategoryDef { Key = "custom-cad", Name = "CAD", Extensions = "dwg dxf", BuiltIn = false });
                s.ServerLimits.Add(new ServerLimit { Pattern = "*.example.com", MaxConnections = 4 });
                s.Logins.Add(new SiteLogin { Pattern = "https://secure.example.com/area", User = "alice", Password = "secret" });
                s.Normalize();

                Check("M5: Normalize preserves custom categories while keeping 'general' last",
                    s.CategoryDefs[^1].Key == "general" && s.CategoryDefs.Any(c => c.Key == "custom-cad"));
                Check("M5: AutoStarts matches configured extensions case-insensitively",
                    s.AutoStarts("manual.pdf") && s.AutoStarts("archive.ZIP") && !s.AutoStarts("movie.mp4"));

                var clone = s.Clone();
                clone.CategoryDefs[0].Extensions = "mutated";
                clone.ServerLimits[0].MaxConnections = 99;
                clone.Sounds["complete"] = "C:\\beep.wav";
                Check("M5: AppSettings.Clone() creates a deep copy isolated from mutations",
                    s.CategoryDefs[0].Extensions != "mutated" && s.ServerLimits[0].MaxConnections == 4 && s.Sounds["complete"] == "");
                Check("M5: AppSettings.Clone() preserves Categories.All pointing to original CategoryDefs",
                    ReferenceEquals(Categories.All, s.CategoryDefs));

                var loginSettings = new AppSettings();
                loginSettings.Logins.Add(new SiteLogin { Pattern = "example.com/private", User = "u1", Password = "p1" });
                loginSettings.Logins.Add(new SiteLogin { Pattern = "*", User = "u2", Password = "p2" });
                loginSettings.Normalize();
                HttpDownloader.Configure(loginSettings);

                var prefixUri = new Uri("https://example.com/private-other/archive.zip");
                var unmatchedHostUri = new Uri("https://unrelated.test/archive.zip");
                var exactMatchUri = new Uri("https://example.com/private/archive.zip");
                Check("M5: AppSettings.LoginFor delegates to MatchSiteLogin (rejects prefix collision & '*' wildcard, matches exact path)",
                    loginSettings.LoginFor(prefixUri) == null &&
                    loginSettings.LoginFor(unmatchedHostUri) == null &&
                    loginSettings.LoginFor(exactMatchUri)?.User == "u1");

                var preInitCat = new CategoryDef { Key = "compressed", BuiltIn = false, Name = "core.category.compressed" };
                var preInitQueue = new QueueDef { Name = "core.queue.main" };
                var stringsField = typeof(Loc).GetField("_strings", BindingFlags.Static | BindingFlags.NonPublic);
                var locTable = stringsField?.GetValue(null) as Dictionary<string, string>;
                var prevCat = locTable != null && locTable.TryGetValue("core.category.compressed", out var vc) ? vc : null;
                var prevQueue = locTable != null && locTable.TryGetValue("core.queue.main", out var vq) ? vq : null;
                try
                {
                    if (locTable != null)
                    {
                        locTable["core.category.compressed"] = "Compressed";
                        locTable["core.queue.main"] = "Main Queue";
                    }
                    Check("P2-8: CategoryDef.Name and QueueDef.Name lazily resolve localized names even when initialized before Loc.Init",
                        preInitCat.Name == "Compressed" && preInitQueue.Name == "Main Queue",
                        $"cat='{preInitCat.Name}', queue='{preInitQueue.Name}'");
                }
                finally
                {
                    if (locTable != null)
                    {
                        if (prevCat == null) locTable.Remove("core.category.compressed"); else locTable["core.category.compressed"] = prevCat;
                        if (prevQueue == null) locTable.Remove("core.queue.main"); else locTable["core.queue.main"] = prevQueue;
                    }
                }
            }
        }
        finally
        {
            foreach (var (file, bytes) in backups)
            {
                try
                {
                    if (bytes == null) File.Delete(file);
                    else File.WriteAllBytes(file, bytes);
                }
                catch { }
            }
            try
            {
                foreach (var f in Directory.GetFiles(AppPaths.CookiesDir))
                    if (!existingCookies.ContainsKey(f)) MediaDownloader.TryDelete(f);
                foreach (var (f, b) in existingCookies)
                    if (!File.Exists(f)) File.WriteAllBytes(f, b);
            }
            catch { }
            try { Directory.Delete(workRoot, recursive: true); } catch { }

            var clean = new AppSettings { ProxyMode = "none" };
            clean.Normalize();
            HttpDownloader.Configure(clean);
        }

        Console.WriteLine($"[P2 Summary] {passes} passed, {fails} failed");
        return fails;
    }
}
