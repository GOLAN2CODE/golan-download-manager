using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Golan.Gdm.Core;

/// <summary>
/// Real downloads from real servers (run on demand: EngineTest --internet [outDir] [names...]).
/// Each file is fetched once with curl over one connection as the reference, then by Golan's engine
/// in full, then again with a pause at ~40 %, a "restart of Golan" (state saved and loaded) and a resume.
/// Every Golan copy must be byte-identical to the reference.
/// </summary>
static class Tests_Internet
{
    sealed record Case(string Name, string Url, string What);

    static readonly Case[] Cases =
    [
        new("anydesk", "https://download.anydesk.com/AnyDesk.exe", "Cloudflare, answers 429 to many connections"),
        new("python", "https://www.python.org/ftp/python/3.12.7/python-3.12.7-amd64.exe", "Fastly CDN"),
        new("ytdlp-github", "https://github.com/yt-dlp/yt-dlp/releases/download/2024.10.22/yt-dlp.exe", "GitHub release: redirect to a signed link that expires"),
        new("7zip-github", "https://www.7-zip.org/a/7z2409-x64.exe", "two redirects ending on GitHub"),
        new("firefox", "https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=en-US", "redirect, name only in the final address"),
        new("vlc-mirror", "https://get.videolan.org/vlc/3.0.21/win64/vlc-3.0.21-win64.exe", "https page redirecting to a plain-http mirror"),
        new("vcredist", "https://aka.ms/vs/17/release/vc_redist.x64.exe", "Microsoft short link to its CDN"),
        new("thinkbroadband", "http://ipv4.download.thinkbroadband.com/20MB.zip", "plain http test file"),
        new("ovh", "https://proof.ovh.net/files/10Mb.dat", "OVH test file"),
    ];

    static string Sha(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs))[..16];
    }

    /// <summary>Runs like Golan's download list does: after "busy" or a browser check it waits and continues by itself.</summary>
    static async Task RunLikeGolanAsync(DownloadItem item, SpeedLimiter limiter, CancellationToken ct)
    {
        for (int retry = 1; ; retry++)
        {
            try
            {
                await new HttpDownloader(item, limiter).RunAsync(ct);
                return;
            }
            catch (DownloadException ex) when (ex.Kind is FailKind.Rejected or FailKind.Challenge && retry <= DownloadManager.MaxBusyAutoRetries)
            {
                var wait = ex.Kind == FailKind.Challenge ? DownloadManager.ChallengeAutoRetryDelay(retry) : DownloadManager.BusyAutoRetryDelay(retry);
                Console.WriteLine($"  ...{(ex.Kind == FailKind.Challenge ? "browser check" : "server busy")} at {item.Downloaded:N0} bytes: waiting {wait.TotalSeconds:0}s, then continuing by itself");
                await Task.Delay(wait, ct);
            }
        }
    }

    static string Mb(double bytesPerSecond) => (bytesPerSecond / 1048576).ToString("0.00") + " MB/s";

    public static async Task<int> RunAsync(string outDir, string[] only)
    {
        var settings = new AppSettings();
        settings.Normalize();
        HttpDownloader.Configure(settings);
        var limiter = new SpeedLimiter();
        int fails = 0;
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Real internet downloads into {outDir}");

        foreach (var c in Cases.Where(c => only.Length == 0 || only.Contains(c.Name)))
        {
            var dir = Path.Combine(outDir, c.Name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Console.WriteLine($"\n=== {c.Name}: {c.What}");

            // 1. Reference: curl, one connection.
            var refPath = Path.Combine(dir, "reference.bin");
            var sw = Stopwatch.StartNew();
            var curl = Process.Start(new ProcessStartInfo("curl.exe", $"-sS -L --fail --retry 3 -o \"{refPath}\" \"{c.Url}\"") { UseShellExecute = false, RedirectStandardError = true })!;
            string curlErr = await curl.StandardError.ReadToEndAsync();
            await curl.WaitForExitAsync();
            sw.Stop();
            if (curl.ExitCode != 0 || !File.Exists(refPath))
            {
                Console.WriteLine($"  SKIP  reference download failed: {curlErr.Trim()}");
                continue;
            }
            long size = new FileInfo(refPath).Length;
            string refSha = Sha(refPath);
            double refSpeed = size / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            Console.WriteLine($"  reference: {size:N0} bytes in {sw.Elapsed.TotalSeconds:0.0}s ({Mb(refSpeed)}) sha={refSha}");

            // 2. Golan, full download.
            var full = new DownloadItem { Url = c.Url, SaveDir = Path.Combine(dir, "full"), MaxConnections = 8 };
            Directory.CreateDirectory(full.SaveDir);
            sw.Restart();
            string fullErr = "";
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
            {
                try { await RunLikeGolanAsync(full, limiter, cts.Token); }
                catch (Exception ex) { fullErr = ex.GetType().Name + ": " + ex.Message; }
            }
            sw.Stop();
            bool fullOk = fullErr == "" && File.Exists(full.FullPath) && Sha(full.FullPath) == refSha;
            double fullSpeed = size / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            Console.WriteLine($"  {(fullOk ? "PASS" : "FAIL")}  full: {full.FileName} in {sw.Elapsed.TotalSeconds:0.0}s ({Mb(fullSpeed)}, x{fullSpeed / refSpeed:0.0} vs one connection) " +
                              $"connections<= {full.MaxConnections} {(fullErr != "" ? "error=" + fullErr : "")}");
            if (!fullOk) fails++;

            // A rate-limited site (Cloudflare) counts our reference + full download: give it the quiet minute a real user's
            // single download would have, so the next phase measures Golan and not this test's own request burst.
            if (c.Name == "anydesk") await Task.Delay(TimeSpan.FromSeconds(150));

            // 3. Golan, pause at ~40 %, restart (state saved and loaded), resume.
            var part = new DownloadItem { Url = c.Url, SaveDir = Path.Combine(dir, "resume"), MaxConnections = 8 };
            Directory.CreateDirectory(part.SaveDir);
            string resumeErr = "";
            long paused = 0, minAfter = long.MaxValue;
            using (var cts = new CancellationTokenSource())
            {
                var task = RunLikeGolanAsync(part, limiter, cts.Token);
                var wait = Stopwatch.StartNew();
                while (!task.IsCompleted && wait.Elapsed < TimeSpan.FromMinutes(5) && part.Downloaded < size * 0.4) await Task.Delay(20);
                var stopWatch = Stopwatch.StartNew();
                cts.Cancel();
                try { await task; } catch (OperationCanceledException) { } catch (Exception ex) { resumeErr = "before pause: " + ex.Message; }
                paused = part.Downloaded;
                Console.WriteLine($"  pause: stopped in {stopWatch.ElapsedMilliseconds} ms at {paused:N0} bytes ({100.0 * paused / size:0}%)");
            }
            var restored = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(part, Json.Options), Json.Options)!;
            if (resumeErr == "")
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var task = RunLikeGolanAsync(restored, limiter, cts.Token);
                while (!task.IsCompleted) { minAfter = Math.Min(minAfter, restored.Downloaded); await Task.Delay(5); }
                try { await task; } catch (Exception ex) { resumeErr = ex.GetType().Name + ": " + ex.Message; }
            }
            bool resumedFromMiddle = paused > 0 && minAfter >= paused / 2;
            bool resumeOk = resumeErr == "" && File.Exists(restored.FullPath) && Sha(restored.FullPath) == refSha && resumedFromMiddle;
            Console.WriteLine($"  {(resumeOk ? "PASS" : "FAIL")}  pause + restart + resume: continued from the middle={resumedFromMiddle} identical={(File.Exists(restored.FullPath) && Sha(restored.FullPath) == refSha)} {(resumeErr != "" ? "error=" + resumeErr : "")}");
            if (!resumeOk) fails++;
        }

        Console.WriteLine($"\n[INTERNET Summary] {(fails == 0 ? "all passed" : fails + " failed")}");
        return fails;
    }
}
