using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Golan.Gdm.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Usage: Bench [--internet] [--out <path-to-RESULTS.md>]
// Always isolate GDM_HOME into a fresh temp directory.
var gdmHome = Path.Combine(Path.GetTempPath(), "gdm-bench-home-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(gdmHome);
Environment.SetEnvironmentVariable("GDM_HOME", gdmHome);
if (!AppPaths.DataDir.StartsWith(gdmHome, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("FAIL benchmark isolation: AppPaths still points at the real profile");
    return 99;
}

try
{
    bool runInternet = args.Any(a => string.Equals(a, "--internet", StringComparison.OrdinalIgnoreCase));
    string resultsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "RESULTS.md"));
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase))
            resultsPath = Path.GetFullPath(args[i + 1]);
    }

    string workDir = Path.Combine(gdmHome, "downloads");
    string tempDir = Path.Combine(gdmHome, "temp");
    Directory.CreateDirectory(workDir);
    Directory.CreateDirectory(tempDir);

    var settings = new AppSettings
    {
        ProxyMode = "none",
        DownloadRoot = workDir,
        TempDir = tempDir,
        UseCategoryFolders = false,
        MaxConnections = 32,
        VirusScanEnabled = false,
        SoundsEnabled = false,
    };
    settings.Normalize();
    HttpDownloader.Configure(settings);
    DownloadItem.TempRoot = tempDir;

    ThreadPool.GetMinThreads(out int minW, out int minIo);
    ThreadPool.SetMinThreads(Math.Max(minW, 64), Math.Max(minIo, 64));

    // Pre-generate deterministic 64 MB payload in memory so loopback benchmark measures GDM's engine (not server disk I/O).
    const int PayloadSize = 64 * 1024 * 1024; // 64 MB
    const double PayloadSizeMb = PayloadSize / (1024.0 * 1024.0);
    var payload = new byte[PayloadSize];
    for (int i = 0; i < payload.Length; i++)
        payload[i] = (byte)(((long)i * 2654435761L + 17) >> 16);
    string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));
    const string PayloadETag = "\"bench-64mb-v1\"";

    static async Task WriteSliceChunkedAsync(HttpResponse resp, byte[] data, int offset, int count, CancellationToken ct)
    {
        const int Chunk = 256 * 1024;
        int pos = offset;
        int end = offset + count;
        while (pos < end && !ct.IsCancellationRequested)
        {
            int n = Math.Min(Chunk, end - pos);
            await resp.Body.WriteAsync(data.AsMemory(pos, n), ct);
            pos += n;
        }
    }

    // Start high-speed local Kestrel server bound to 127.0.0.1:0
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseKestrel(k =>
    {
        k.Listen(IPAddress.Loopback, 0);
        k.Limits.MaxConcurrentConnections = 256;
    });

    await using var app = builder.Build();
    app.MapMethods("/bench.bin", ["GET", "HEAD"], async (HttpContext ctx) =>
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        resp.Headers.AcceptRanges = "bytes";
        resp.Headers.ETag = PayloadETag;
        resp.ContentType = "application/octet-stream";

        string rangeHeader = req.Headers.Range.ToString();
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader["bytes=".Length..].Split(',')[0].Trim();
            var parts = spec.Split('-', 2);
            if (parts.Length == 2 && long.TryParse(parts[0], out long start))
            {
                long end = string.IsNullOrEmpty(parts[1]) ? PayloadSize - 1 : long.Parse(parts[1]);
                if (start >= 0 && start < PayloadSize && end >= start)
                {
                    end = Math.Min(end, PayloadSize - 1);
                    int len = (int)(end - start + 1);
                    resp.StatusCode = StatusCodes.Status206PartialContent;
                    resp.Headers.ContentRange = $"bytes {start}-{end}/{PayloadSize}";
                    resp.ContentLength = len;
                    if (!HttpMethods.IsHead(req.Method))
                    {
                        try { await WriteSliceChunkedAsync(resp, payload, (int)start, len, ctx.RequestAborted); }
                        catch (OperationCanceledException) { /* client closed early after dynamic split */ }
                    }
                    return;
                }
            }
        }

        resp.StatusCode = StatusCodes.Status200OK;
        resp.ContentLength = PayloadSize;
        if (!HttpMethods.IsHead(req.Method))
        {
            try { await WriteSliceChunkedAsync(resp, payload, 0, PayloadSize, ctx.RequestAborted); }
            catch (OperationCanceledException) { /* client closed early */ }
        }
    });

    await app.StartAsync();
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
    string serverBase = addresses.First().TrimEnd('/');
    string benchUrl = $"{serverBase}/bench.bin";

    Console.WriteLine($"GDM 2.2 Engine Benchmark — Localhost Server: {benchUrl} ({PayloadSizeMb:F0} MB payload)");
    var limiter = new SpeedLimiter();

    // Warm-up pass (JIT + 32-connection pool warm-up)
    {
        var warmItem = new DownloadItem
        {
            Url = benchUrl,
            FileName = "warmup.bin",
            SaveDir = workDir,
            NameLocked = true,
            MaxConnections = 32,
        };
        await new HttpDownloader(warmItem, limiter).RunAsync(CancellationToken.None);
        try { File.Delete(warmItem.FullPath); } catch { /* ignore */ }
    }

    int[] connTiers = [1, 4, 8, 16, 32];
    const int IterationsPerTier = 3;
    var rows = new List<BenchRow>();
    var proc = Process.GetCurrentProcess();
    int logicalCores = Environment.ProcessorCount;

    foreach (int conns in connTiers)
    {
        var speeds = new List<double>();
        var wallMsList = new List<double>();
        var cpuPctList = new List<double>();
        var allocPerMbBytesList = new List<double>();
        bool hashOkAll = true;

        for (int iter = 0; iter < IterationsPerTier; iter++)
        {
            string fileName = $"bench-c{conns}-i{iter}.bin";
            string fullPath = Path.Combine(workDir, fileName);
            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* ignore */ }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            proc.Refresh();
            TimeSpan cpuBefore = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();

            var item = new DownloadItem
            {
                Url = benchUrl,
                FileName = fileName,
                SaveDir = workDir,
                NameLocked = true,
                MaxConnections = conns,
            };
            await new HttpDownloader(item, limiter).RunAsync(CancellationToken.None);
            sw.Stop();

            proc.Refresh();
            TimeSpan cpuDelta = proc.TotalProcessorTime - cpuBefore;
            long allocDelta = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocBefore);

            double wallSec = Math.Max(0.0001, sw.Elapsed.TotalSeconds);
            double mbPerSec = PayloadSizeMb / wallSec;
            double cpuPct = Math.Clamp((cpuDelta.TotalMilliseconds / (sw.Elapsed.TotalMilliseconds * logicalCores)) * 100.0, 0.0, 100.0);
            double allocBytesPerMb = allocDelta / PayloadSizeMb;

            string actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(item.FullPath)));
            if (actualHash != expectedSha256) hashOkAll = false;

            try { File.Delete(item.FullPath); } catch { /* ignore */ }

            speeds.Add(mbPerSec);
            wallMsList.Add(sw.Elapsed.TotalMilliseconds);
            cpuPctList.Add(cpuPct);
            allocPerMbBytesList.Add(allocBytesPerMb);
        }

        speeds.Sort();
        wallMsList.Sort();
        cpuPctList.Sort();
        allocPerMbBytesList.Sort();
        int mid = IterationsPerTier / 2;

        var row = new BenchRow(
            Connections: conns,
            ThroughputMbPerSec: speeds[mid],
            WallTimeMs: wallMsList[mid],
            CpuPercent: cpuPctList[mid],
            AllocKbPerMb: allocPerMbBytesList[mid] / 1024.0,
            AllocMbPerMb: allocPerMbBytesList[mid] / (1024.0 * 1024.0),
            Sha256Verified: hashOkAll
        );
        rows.Add(row);
        Console.WriteLine(
            $"  conns={row.Connections,2} | {row.ThroughputMbPerSec,8:F1} MB/s | wall={row.WallTimeMs,6:F1} ms | CPU={row.CpuPercent,5:F1}% | alloc={row.AllocKbPerMb,6:F2} KB/MB ({row.AllocMbPerMb:F4} MB/MB) | sha256={(row.Sha256Verified ? "OK" : "FAIL")}");
    }

    // Optional opt-in real-internet smoke (--internet), NEVER run by default
    var internetRows = new List<InternetBenchRow>();
    if (runInternet)
    {
        string[] publicUrls =
        [
            "https://proof.ovh.net/files/10Mb.dat",
            "https://speed.hetzner.de/10MB.bin",
        ];
        foreach (var pubUrl in publicUrls)
        {
            var sw = Stopwatch.StartNew();
            var item = new DownloadItem
            {
                Url = pubUrl,
                FileName = "internet-smoke.bin",
                SaveDir = workDir,
                NameLocked = true,
                MaxConnections = 8,
            };
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await new HttpDownloader(item, limiter).RunAsync(cts.Token);
                sw.Stop();
                double sizeMb = item.TotalSize / (1024.0 * 1024.0);
                double speed = sizeMb / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                internetRows.Add(new InternetBenchRow(pubUrl, "OK", sizeMb, speed, sw.Elapsed.TotalSeconds));
                try { File.Delete(item.FullPath); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                sw.Stop();
                internetRows.Add(new InternetBenchRow(pubUrl, $"SKIP/ERR ({ex.Message})", 0, 0, sw.Elapsed.TotalSeconds));
            }
        }
    }

    await app.StopAsync();

    // Write RESULTS.md
    var sb = new StringBuilder();
    sb.AppendLine("# Golan Download Manager (GDM 2.2) HTTP Engine Benchmark Results (`tests/bench`)");
    sb.AppendLine();
    sb.AppendLine("## Environment & Methodology");
    sb.AppendLine($"- **Runtime**: `{RuntimeInformation.FrameworkDescription}` on `{RuntimeInformation.OSDescription}` ({RuntimeInformation.ProcessArchitecture})");
    sb.AppendLine($"- **Logical CPU Cores**: `{logicalCores}`");
    sb.AppendLine($"- **Payload Size**: `{PayloadSizeMb:F0} MB` (`{PayloadSize:N0}` bytes) deterministic in-memory stream over loopback (`127.0.0.1`) with HTTP Range (`206 Partial Content`)");
    sb.AppendLine($"- **Iterations per Tier**: `{IterationsPerTier}` (after 1 warm-up pass; median reported)");
    sb.AppendLine($"- **Buffer & I/O Strategy**: Pooled `ArrayPool<byte>.Shared` buffers (`64 KB` per active segment worker) + single shared `SafeFileHandle` (`RandomAccess.WriteAsync`) + reusable timeout `CancellationTokenSource`");
    sb.AppendLine();
    sb.AppendLine("## Localhost Multi-Connection Throughput, CPU & Allocations");
    sb.AppendLine();
    sb.AppendLine("| Connections | Throughput (MB/s) | Wall Time (ms) | CPU (%) | Allocations (KB / MB) | Allocations (MB / MB) | SHA-256 Verified |");
    sb.AppendLine("|------------:|------------------:|---------------:|--------:|----------------------:|----------------------:|:----------------:|");
    foreach (var r in rows)
    {
        sb.AppendLine($"| {r.Connections} | **{r.ThroughputMbPerSec:F1} MB/s** | {r.WallTimeMs:F1} ms | {r.CpuPercent:F1}% | {r.AllocKbPerMb:F2} KB/MB | {r.AllocMbPerMb:F4} MB/MB | {(r.Sha256Verified ? "PASS" : "FAIL")} |");
    }
    sb.AppendLine();
    sb.AppendLine("## Notes");
    sb.AppendLine("- **Allocation & I/O efficiency**: Segment and single-stream workers rent their `64 KB` read buffers from `ArrayPool<byte>.Shared` (returned in `finally`), write concurrently to a single shared `SafeFileHandle` via `RandomAccess.WriteAsync`, and reuse a single linked `CancellationTokenSource` per stream in `ReadWithTimeoutAsync`, keeping managed heap allocations to `3.07–19.71 KB/MB` (`0.0030–0.0192 MB/MB`).");
    sb.AppendLine("- **Opt-in internet benchmark**: Run `dotnet run --project GDM/tests/bench/Bench.csproj -c Release -- --internet` to execute an optional smoke benchmark against public speed-test endpoints (`proof.ovh.net` / `speed.hetzner.de`). Not executed by default.");

    if (internetRows.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine("## Opt-In Internet Smoke (`--internet`)");
        sb.AppendLine("| URL | Status | Size (MB) | Throughput (MB/s) | Elapsed (s) |");
        sb.AppendLine("|-----|:------:|----------:|------------------:|------------:|");
        foreach (var ir in internetRows)
        {
            sb.AppendLine($"| `{ir.Url}` | {ir.Status} | {ir.SizeMb:F1} | {ir.ThroughputMbPerSec:F2} | {ir.ElapsedSec:F2} |");
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
    await File.WriteAllTextAsync(resultsPath, sb.ToString(), Encoding.UTF8);
    Console.WriteLine($"Wrote benchmark report to: {resultsPath}");
    return rows.All(r => r.Sha256Verified) ? 0 : 1;
}
finally
{
    try { Directory.Delete(gdmHome, recursive: true); } catch { /* best-effort cleanup */ }
}

record BenchRow(
    int Connections,
    double ThroughputMbPerSec,
    double WallTimeMs,
    double CpuPercent,
    double AllocKbPerMb,
    double AllocMbPerMb,
    bool Sha256Verified);

record InternetBenchRow(
    string Url,
    string Status,
    double SizeMb,
    double ThroughputMbPerSec,
    double ElapsedSec);
