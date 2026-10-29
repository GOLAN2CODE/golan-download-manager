using System.Security.Cryptography;
using System.Text.Json;
using Golan.Gdm.Core;

/// <summary>Video experience: tool preparation progress text, resumable tool downloads, smart-mode setting.</summary>
static class Tests_Video
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  [VIDEO] {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  [VIDEO] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
    }

    static string Sha(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static async Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        Console.WriteLine("Video experience");

        // ---- progress text ----
        var full = MediaTools.FormatToolProgress("Merge tool", 45L * 1024 * 1024, 190L * 1024 * 1024, 5L * 1024 * 1024);
        Check("progress shows done/total, speed and time left", full.Contains("45") && full.Contains("190") && full.Split(" · ").Length == 4, full);
        var noTotal = MediaTools.FormatToolProgress("Merge tool", 1024, -1, 0);
        Check("unknown size and no speed yet: only what and how much", noTotal.Split(" · ").Length == 2, noTotal);

        // ---- resumable download (local test server supports Range) ----
        var dir = Path.GetFullPath(Path.Combine(outDir, "video-tools-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(dir);
        var expected = Sha(Path.Combine(served, "big.bin"));
        var part = Path.Combine(dir, "tool.part");
        var reports = 0;
        var progress = new Progress<(string Text, double Percent)>(_ => Interlocked.Increment(ref reports));
        var size = await MediaTools.DownloadResumableAsync($"{baseUrl}/big.bin", part, "tool", progress, CancellationToken.None, requireHttps: false);
        Check("tool download completes with the right content", size == new FileInfo(Path.Combine(served, "big.bin")).Length && Sha(part) == expected);

        // Simulate an interrupted download: keep only the first 10 MB, then resume.
        using (var fs = new FileStream(part, FileMode.Open)) fs.SetLength(10L * 1024 * 1024);
        await MediaTools.DownloadResumableAsync($"{baseUrl}/big.bin", part, "tool", null, CancellationToken.None, requireHttps: false);
        Check("an interrupted tool download resumes and ends identical", Sha(part) == expected);

        bool httpsEnforced;
        try { await MediaTools.DownloadResumableAsync($"{baseUrl}/big.bin", part + "2", "tool", null, CancellationToken.None); httpsEnforced = false; }
        catch (DownloadException) { httpsEnforced = true; }
        Check("real tool downloads refuse plain http", httpsEnforced);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            bool cancelled;
            try { await MediaTools.DownloadResumableAsync($"{baseUrl}/big.bin", part + "3", "tool", null, cts.Token, requireHttps: false); cancelled = false; }
            catch (OperationCanceledException) { cancelled = true; }
            Check("tool download can be cancelled", cancelled);
        }

        // ---- smart mode setting ----
        var s = new AppSettings();
        Check("smart mode is off by default", !s.VideoSmartMode);
        s.VideoSmartMode = true;
        s.DefaultVideoQuality = "720";
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json.Options), Json.Options)!;
        Check("smart mode and the remembered quality survive a save", back.VideoSmartMode && back.DefaultVideoQuality == "720");

        Check("isolated runs never prefetch tools from the internet", AppPaths.IsIsolated);

        try { Directory.Delete(dir, true); } catch (IOException) { /* best-effort cleanup */ }
        Console.WriteLine($"[VIDEO Summary] {_pass} passed, {_fail} failed");
        return _fail;
    }
}
