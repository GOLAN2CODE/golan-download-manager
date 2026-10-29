using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Golan.Gdm.Core;

public static class Tests_P4
{
    static int _passed;
    static int _failed;
    static readonly List<string> _failures = [];
    static int? _cachedResult;

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}{(detail != null ? $" ({detail})" : "")}");
        }
        else
        {
            _failed++;
            _failures.Add(name + (detail != null ? $" ({detail})" : ""));
            Console.WriteLine($"  [FAIL] {name}{(detail != null ? $" ({detail})" : "")}");
        }
    }

    // [ModuleInitializer] (called from Program.cs instead)
    internal static void Init()
    {
        var args = Environment.GetCommandLineArgs();
        bool p4Only = args.Any(a => string.Equals(a, "--p4-only", StringComparison.OrdinalIgnoreCase));
        if (!p4Only && args.Length < 4) return;

        // Return immediately from [ModuleInitializer] so the CLR releases the <Module>..cctor()
        // loader lock before any async/ThreadPool or Process.OutputDataReceived callbacks run.
        Console.SetOut(new P4BootstrapWriter(Console.Out, args, p4Only));
    }

    sealed class P4BootstrapWriter(TextWriter inner, string[] args, bool p4Only) : TextWriter
    {
        bool _ran;
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            if (!_ran)
            {
                _ran = true;
                Console.SetOut(inner);
                RunBootstrap();
            }
            inner.WriteLine(value);
        }

        void RunBootstrap()
        {
            string servedDir = args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "gdm-p4-served");
            string outDir = args.Length >= 3 ? args[2] : Path.Combine(Path.GetTempPath(), "gdm-p4-out");
            int port = args.Length >= 4 && int.TryParse(args[3], out var p) ? p : 48004;
            string baseUrl = $"http://127.0.0.1:{port}";

            Directory.CreateDirectory(servedDir);
            Directory.CreateDirectory(outDir);
            EnsureSampleZip(servedDir);

            int fails = RunAsync(baseUrl, servedDir, outDir, port).GetAwaiter().GetResult();

            var clean = new AppSettings { ProxyMode = "none" };
            clean.Normalize();
            HttpDownloader.Configure(clean);

            if (p4Only)
                Environment.Exit(fails == 0 ? 0 : 1);
            else if (fails > 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Environment.ExitCode = 1;
        }
    }

    static void EnsureSampleZip(string servedDir)
    {
        string zipServed = Path.Combine(servedDir, "sample.zip");
        if (File.Exists(zipServed)) return;
        using var zf = ZipFile.Open(zipServed, ZipArchiveMode.Create);
        for (int i = 0; i < 25; i++)
        {
            var e = zf.CreateEntry($"folder_{i % 3}/item_{i:D2}.txt", CompressionLevel.Optimal);
            using var s = new StreamWriter(e.Open());
            s.Write(new string((char)('A' + (i % 26)), 500 + i * 37));
        }
    }

    static void StopManagerTimer(DownloadManager mgr)
    {
        if (typeof(DownloadManager).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mgr) is DispatcherTimer t)
            t.Stop();
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

    public static async Task<int> RunAsync(string baseUrl, string servedDir, string outDir, int port)
    {
        if (_cachedResult.HasValue) return _cachedResult.Value;

        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Console.WriteLine("=== Package P4 Verification Suite ===");
        var workRoot = Path.Combine(Path.GetTempPath(), "gdm-p4-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        Directory.CreateDirectory(AppPaths.DataDir);
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

        try
        {
            var repoRoot = FindRepoRoot();
            var productAsm = LoadOrBuildProductAssembly(repoRoot);
            await Test_P4_1_VerifyYtDlpHashFailClosed(workRoot);
            await Test_P4_2_MediaDownloaderFixes(workRoot);
            await Test_P4_3_ExtrasFixes(baseUrl, port, workRoot);
            await Test_P4_4_GrabberFixes(workRoot);
            Test_P4_5_GrabberWindowFixes(productAsm);
            await Test_P4_6_VideoWindowFixes(productAsm, repoRoot, workRoot);
            Test_P4_7_BatchWindowFixes(productAsm);
            Test_P4_8_OptionsSchedulerProgressFixes(productAsm, repoRoot);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] Tests_P4 unhandled exception: {ex}");
            _failed++;
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
            try { Directory.Delete(workRoot, true); } catch { }
        }

        Console.WriteLine($"\nTests_P4 Result: {_passed} passed, {_failed} failed");
        if (_failures.Count > 0)
        {
            Console.WriteLine("Tests_P4 Failures:");
            foreach (var f in _failures) Console.WriteLine($"  - {f}");
        }
        Console.WriteLine();

        _cachedResult = _failed;
        return _failed;
    }

    // -------------------------------------------------------------------------
    // P4-1: MediaTools.VerifyYtDlpHashAsync fails closed & deletes binary
    // -------------------------------------------------------------------------
    static async Task Test_P4_1_VerifyYtDlpHashFailClosed(string workRoot)
    {
        Console.WriteLine("P4-1. MediaTools.VerifyYtDlpHashAsync fail-closed");
        var dummyBin = Path.Combine(workRoot, "yt-dlp-unverified.exe");
        File.WriteAllText(dummyBin, "unverified binary content");

        bool threwDownloadException = false;
        try
        {
            var verifyMethod = typeof(MediaTools).GetMethod("VerifyYtDlpHashAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            var task = (Task)verifyMethod.Invoke(null, [dummyBin, new CancellationToken(canceled: true)])!;
            await task;
        }
        catch (DownloadException)
        {
            threwDownloadException = true;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is DownloadException)
        {
            threwDownloadException = true;
        }
        catch
        {
            // Other exception types do not satisfy DownloadException requirement
        }

        Check("P4-1: VerifyYtDlpHashAsync throws DownloadException and deletes unverified binary when hash fetch fails",
            threwDownloadException && !File.Exists(dummyBin),
            $"threw={threwDownloadException}, fileExists={File.Exists(dummyBin)}");
    }

    // -------------------------------------------------------------------------
    // P4-2: MediaDownloader proxy/UNC/NA/GDMF/PathRegistry fixes + M10
    // -------------------------------------------------------------------------
    static async Task Test_P4_2_MediaDownloaderFixes(string workRoot)
    {
        Console.WriteLine("P4-2. MediaDownloader proxy, UNC, GDMP|NA, GDMF|, PathRegistry & M10");
        var proxyForTargetMethod = typeof(MediaDownloader).GetMethod("ProxyForTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        var commonArgsMethod = typeof(MediaDownloader).GetMethod("CommonArgs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var newStartInfoMethod = typeof(MediaDownloader).GetMethod("NewStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!;

        // 1) Repro 11 (M-3): ProxyForTarget respects host:port bypass matching HttpDownloader.IsProxyBypassed
        var proxySettings = new AppSettings
        {
            ProxyMode = "manual",
            ProxyType = "http",
            ProxyHost = "127.0.0.1",
            ProxyPort = 8888,
            ProxyBypass = "127.0.0.1:8080",
        };
        proxySettings.Normalize();
        HttpDownloader.Configure(proxySettings);
        var proxyResult = (string?)proxyForTargetMethod.Invoke(null, ["http://127.0.0.1:8080/video.mp4"]);
        bool httpBypassed = HttpDownloader.IsProxyBypassed("http://127.0.0.1:8080/video.mp4");
        Check("P4-2 (repro M-3): MediaDownloader.ProxyForTarget respects host:port bypass and returns empty string",
            httpBypassed && proxyResult == "",
            $"IsProxyBypassed={httpBypassed}, ProxyForTarget={(proxyResult == null ? "null" : $"\"{proxyResult}\"")}");

        // 2) SOCKS4 support & ProxyMode="none" -> --proxy "" and NO_PROXY="*"
        var socks4Settings = new AppSettings
        {
            ProxyMode = "manual",
            ProxyType = "socks4",
            ProxyHost = "127.0.0.1",
            ProxyPort = 1080,
            ProxyBypass = "localhost"
        };
        socks4Settings.Normalize();
        HttpDownloader.Configure(socks4Settings);
        var socks4Proxy = (string?)proxyForTargetMethod.Invoke(null, ["https://example.com/video.mp4"]);
        Check("P4-2: MediaDownloader.ProxyForTarget supports socks4 scheme",
            socks4Proxy == "socks4://127.0.0.1:1080",
            $"actual={socks4Proxy}");

        var noneSettings = new AppSettings { ProxyMode = "none" };
        noneSettings.Normalize();
        HttpDownloader.Configure(noneSettings);
        var noneProxy = (string?)proxyForTargetMethod.Invoke(null, ["https://example.com/video.mp4"]);
        var psi = (ProcessStartInfo)newStartInfoMethod.Invoke(null, [new[] { "--version" }, "https://example.com/video.mp4"])!;
        var cArgs = InvokeCommonArgs(commonArgsMethod, "UA", "https://ref.test/", null, "https://example.com/video.mp4");
        int proxyFlagIdx = cArgs.IndexOf("--proxy");
        bool hasEmptyProxyArg = proxyFlagIdx >= 0 && proxyFlagIdx + 1 < cArgs.Count && cArgs[proxyFlagIdx + 1] == "";
        psi.Environment.TryGetValue("NO_PROXY", out var noProxyEnv);
        Check("P4-2: ProxyMode=none passes --proxy \"\" in CommonArgs and sets NO_PROXY=* in NewStartInfo",
            noneProxy == "" && hasEmptyProxyArg && noProxyEnv == "*",
            $"noneProxy={(noneProxy ?? "null")}, hasEmptyProxyArg={hasEmptyProxyArg}, NO_PROXY={noProxyEnv}");

        // 3) User-configured UNC root allowed vs unconfigured UNC rejected
        var uncSettings = new AppSettings { ProxyMode = "none", DownloadRoot = @"\\fileserver\GdmDownloads" };
        uncSettings.Normalize();
        HttpDownloader.Configure(uncSettings);
        var isUnderMethod = typeof(MediaDownloader).GetMethod("IsUnderConfiguredRoot", BindingFlags.Static | BindingFlags.NonPublic);
        bool configuredUncAllowed = isUnderMethod != null && (bool)isUnderMethod.Invoke(null, [@"\\fileserver\GdmDownloads\Video"])!;
        bool unconfiguredUncBlocked = isUnderMethod != null && !(bool)isUnderMethod.Invoke(null, [@"\\evil.example.com\share\Video"])!;
        Check("P4-2: MediaDownloader allows UNC SaveDir under user-configured DownloadRoot and blocks unconfigured UNC shares",
            configuredUncAllowed && unconfiguredUncBlocked,
            $"configuredUncAllowed={configuredUncAllowed}, unconfiguredUncBlocked={unconfiguredUncBlocked}");

        HttpDownloader.Configure(noneSettings);

        // 4) M10 helpers + RunAsync protocol (GDMS|, GDMP|NA guard, GDMM, GDMF| path validation, PathRegistry.Release)
        var mDir = Path.Combine(workRoot, "m10");
        Directory.CreateDirectory(mDir);

        Check("P4-2 (M10): MediaDownloader.LooksLikeMediaPage distinguishes media hosts from file hosts",
            MediaDownloader.LooksLikeMediaPage("https://www.youtube.com/watch?v=abc")
            && MediaDownloader.LooksLikeMediaPage("https://sub.tiktok.com/@u/video/1")
            && !MediaDownloader.LooksLikeMediaPage("https://cdn.example.com/archive.zip"));

        var tPriv = MediaDownloader.TranslateError("ERROR: Private video. Sign in if you've been granted access");
        var t403 = MediaDownloader.TranslateError("ERROR: HTTP Error 403: Forbidden");
        var tAge = MediaDownloader.TranslateError("ERROR: Sign in to confirm your age. This video may be age-restricted");
        Check("P4-2 (M10): MediaDownloader.TranslateError maps private, 403, and age-restricted stderr messages distinctly",
            !string.IsNullOrEmpty(tPriv) && !string.IsNullOrEmpty(t403) && !string.IsNullOrEmpty(tAge)
            && tPriv != t403 && t403 != tAge);

        var cookiesJson = JsonNode.Parse("""
            [
              { "domain": ".youtube.com", "name": "SID\r\nINJECT", "value": "val\t1", "path": "/", "secure": true, "httpOnly": true, "expirationDate": 1893456000 }
            ]
            """)!.AsArray();
        var cookieFile = MediaDownloader.WriteCookieFile(cookiesJson);
        try
        {
            var rawOnDisk = File.ReadAllText(cookieFile);
            var unprotected = Secret.Unprotect(rawOnDisk);
            Check("P4-2 (M10): WriteCookieFile DPAPI-encrypts cookie file and strips CR/LF/TAB from Netscape fields",
                File.Exists(cookieFile)
                && !rawOnDisk.Contains("SIDINJECT")
                && unprotected.StartsWith("# Netscape HTTP Cookie File")
                && unprotected.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1893456000\tSIDINJECT\tval1"));
        }
        finally
        {
            MediaDownloader.TryDelete(cookieFile);
        }

        var pItem = new DownloadItem { SaveDir = mDir, FileName = "clip.mp4" };
        var fPart = Path.Combine(mDir, "clip.mp4.part");
        var fYtdl = Path.Combine(mDir, "clip.mp4.ytdl");
        var fFrag = Path.Combine(mDir, "clip.f137.mp4.part-Frag1");
        var fKeep = Path.Combine(mDir, "unrelated.mp4");
        File.WriteAllText(fPart, "x");
        File.WriteAllText(fYtdl, "x");
        File.WriteAllText(fFrag, "x");
        File.WriteAllText(fKeep, "keep");
        MediaDownloader.DeletePartials(pItem);
        Check("P4-2 (M10): MediaDownloader.DeletePartials removes .part, .ytdl, and .part-Frag files while keeping unrelated files",
            !File.Exists(fPart) && !File.Exists(fYtdl) && !File.Exists(fFrag) && File.Exists(fKeep));

        // End-to-end MediaDownloader.RunAsync protocol test with fake yt-dlp.exe shim
        Directory.CreateDirectory(AppPaths.ToolsDir);
        var ytdlpExe = Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe");
        var ytdlpDll = Path.Combine(AppPaths.ToolsDir, "yt-dlp.dll");
        var ytdlpCfg = Path.Combine(AppPaths.ToolsDir, "yt-dlp.runtimeconfig.json");
        var ytdlpDeps = Path.Combine(AppPaths.ToolsDir, "yt-dlp.deps.json");
        var ffmpegExe = Path.Combine(AppPaths.ToolsDir, "ffmpeg.exe");
        var ytdlpBackup = ytdlpExe + ".p4test.bak";
        bool createdDummyFfmpeg = !File.Exists(ffmpegExe);
        if (File.Exists(ytdlpExe) && !File.Exists(ytdlpBackup)) File.Move(ytdlpExe, ytdlpBackup, overwrite: true);
        if (createdDummyFfmpeg) File.WriteAllBytes(ffmpegExe, [0]);
        try
        {
            var outsideFile = Path.Combine(workRoot, "outside_evil.mp4");
            File.WriteAllBytes(outsideFile, new byte[128]);

            var shimDir = Path.Combine(Path.GetTempPath(), "gdm-p4-ytdlp-shim-v5");
            var shimOutDir = Path.Combine(shimDir, "out");
            var builtShim = Path.Combine(shimOutDir, "yt-dlp.exe");
            if (!File.Exists(builtShim))
            {
                Directory.CreateDirectory(shimDir);
                File.WriteAllText(Path.Combine(shimDir, "shim.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                        <AssemblyName>yt-dlp</AssemblyName>
                        <ImplicitUsings>enable</ImplicitUsings>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(shimDir, "Program.cs"), """
                    using System.Text;
                    Console.OutputEncoding = Encoding.UTF8;
                    int oIdx = Array.IndexOf(args, "-o");
                    string tpl = oIdx >= 0 && oIdx + 1 < args.Length ? args[oIdx + 1] : "out.%(ext)s";
                    string dir = Path.GetDirectoryName(tpl) ?? ".";
                    string parentDir = Path.GetDirectoryName(dir) ?? dir;
                    string outsidePath = Path.Combine(parentDir, "outside_evil.mp4");
                    string finalPath = tpl.Replace("%(title).150B", "Test Media Title").Replace("%(title).180B", "Test Media Title").Replace("%(ext)s", "mp4");
                    Console.WriteLine("GDMS|" + finalPath);
                    Console.WriteLine("GDMP|NA|NA|NA|NA|NA|137");
                    Console.WriteLine("GDMP|2000000|2000000|2000000|500000|2|137");
                    Console.WriteLine("GDMM");
                    File.WriteAllBytes(finalPath, new byte[4096]);
                    Console.WriteLine("GDMF|" + finalPath);
                    Console.WriteLine("GDMF|" + outsidePath);
                    return 0;
                    """);
                var buildPsi = new ProcessStartInfo("dotnet", $"build \"{Path.Combine(shimDir, "shim.csproj")}\" -o \"{shimOutDir}\" -v q")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var bp = Process.Start(buildPsi)!) bp.WaitForExit(20000);
            }

            File.Copy(Path.Combine(shimOutDir, "yt-dlp.exe"), ytdlpExe, overwrite: true);
            File.Copy(Path.Combine(shimOutDir, "yt-dlp.dll"), ytdlpDll, overwrite: true);
            File.Copy(Path.Combine(shimOutDir, "yt-dlp.runtimeconfig.json"), ytdlpCfg, overwrite: true);
            if (File.Exists(Path.Combine(shimOutDir, "yt-dlp.deps.json")))
                File.Copy(Path.Combine(shimOutDir, "yt-dlp.deps.json"), ytdlpDeps, overwrite: true);

            var mediaItem = new DownloadItem
            {
                Url = "https://www.youtube.com/watch?v=test",
                SaveDir = mDir,
                FileName = "placeholder.mp4",
                Kind = DownloadKind.Media,
                MediaQuality = "720"
            };
            bool sawNegativeBytes = false;
            mediaItem.PropertyChanged += (_, _) =>
            {
                if (mediaItem.Downloaded < 0)
                    sawNegativeBytes = true;
            };
            var reservedPlaceholder = Path.Combine(mDir, "placeholder.mp4");
            PathRegistry.Reserve(mediaItem.Id, reservedPlaceholder);

            await MediaDownloader.RunAsync(mediaItem, new SpeedLimiter(), CancellationToken.None);

            var probeId = Guid.NewGuid();
            bool reservationReleased = string.Equals(PathRegistry.Reserve(probeId, reservedPlaceholder), reservedPlaceholder, StringComparison.OrdinalIgnoreCase);
            PathRegistry.Release(probeId);
            Check("P4-2 (M10): MediaDownloader.RunAsync ignores GDMP|NA, rejects GDMF| outside SaveDir, releases PathRegistry, and writes MOTW",
                !sawNegativeBytes
                && File.Exists(mediaItem.FullPath)
                && string.Equals(Path.GetFullPath(mediaItem.SaveDir), Path.GetFullPath(mDir), StringComparison.OrdinalIgnoreCase)
                && mediaItem.FileName == "Test Media Title.mp4"
                && mediaItem.TotalSize == 4096
                && mediaItem.Downloaded == 4096
                && File.Exists(mediaItem.FullPath + ":Zone.Identifier")
                && reservationReleased,
                $"sawNegative={sawNegativeBytes}, fileName={mediaItem.FileName}, saveDir={mediaItem.SaveDir}, released={reservationReleased}");
        }
        finally
        {
            MediaDownloader.TryDelete(ytdlpExe);
            MediaDownloader.TryDelete(ytdlpDll);
            MediaDownloader.TryDelete(ytdlpCfg);
            MediaDownloader.TryDelete(ytdlpDeps);
            if (File.Exists(ytdlpBackup)) File.Move(ytdlpBackup, ytdlpExe, overwrite: true);
            if (createdDummyFfmpeg) MediaDownloader.TryDelete(ffmpegExe);
        }
    }

    static List<string> InvokeCommonArgs(MethodInfo m, string? ua, string? referrer, string? cookieFile, string? url)
    {
        var pars = m.GetParameters();
        var res = pars.Length >= 4
            ? m.Invoke(null, [ua, referrer, cookieFile, url])
            : m.Invoke(null, [ua, referrer, cookieFile]);
        return (List<string>)res!;
    }

    // -------------------------------------------------------------------------
    // P4-3: Extras.cs (ZipPreview cross-origin credential/Referer/Cookie gate + Transfer.Import)
    // -------------------------------------------------------------------------
    static async Task Test_P4_3_ExtrasFixes(string baseUrl, int port, string workRoot)
    {
        Console.WriteLine("P4-3. ZipPreview cross-origin credential/Referer/Cookie gate & Transfer.Import");

        // 1) Repro 5 (H-7): ZipPreview drops per-item Basic Auth on cross-host redirect
        {
            var zipCrossAuth = new DownloadItem
            {
                Url = $"{baseUrl}/redirect?to=" + Uri.EscapeDataString($"http://localhost:{port}/noauth/sample.zip"),
                Login = "user",
                Password = "pass",
            };
            bool zipOk = false;
            string zipErr = "";
            try
            {
                var list = await ZipPreview.ListAsync(zipCrossAuth, CancellationToken.None);
                zipOk = list.Count == 25;
            }
            catch (Exception ex) { zipErr = ex.Message; }
            Check("P4-3 (repro H-7): ZipPreview drops per-item Basic Auth on cross-host redirect",
                zipOk, $"err={zipErr}");
        }

        // 2) ZipPreview.RangeAsync drops Referer and Cookie on cross-host redirect, keeps them on same-host
        {
            using var echoSrv = new MiniHttpServer((_, reqHeaders) =>
            {
                reqHeaders.TryGetValue("Cookie", out var c);
                reqHeaders.TryGetValue("Referer", out var r);
                reqHeaders.TryGetValue("Authorization", out var a);
                var json = JsonSerializer.Serialize(new { cookie = c ?? "", referer = r ?? "", auth = a ?? "" });
                var body = Encoding.UTF8.GetBytes(json);
                return (206, new() { ["Content-Type"] = "application/json", ["Content-Length"] = body.Length.ToString() }, body);
            });
            var rangeMethod = typeof(ZipPreview).GetMethod("RangeAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

            var crossItem = new DownloadItem
            {
                Url = $"http://127.0.0.1:{echoSrv.Port}/sample.zip",
                Referrer = $"http://127.0.0.1:{echoSrv.Port}/origin-page.html",
                Cookies = "SESSION=topsecret",
                CookieHost = "127.0.0.1",
                Login = "user",
                Password = "pw"
            };
            var crossUri = new Uri($"http://localhost:{echoSrv.Port}/echo-headers");
            var crossBytes = await (Task<byte[]>)rangeMethod.Invoke(null, [crossItem, crossUri, 0L, 4096L, CancellationToken.None])!;
            var crossJson = JsonNode.Parse(Encoding.UTF8.GetString(crossBytes))!;
            string leakedCookie = crossJson["cookie"]?.GetValue<string>() ?? "";
            string leakedReferer = crossJson["referer"]?.GetValue<string>() ?? "";
            string leakedAuth = crossJson["auth"]?.GetValue<string>() ?? "";
            Check("P4-3: ZipPreview.RangeAsync strips Referer, Cookie, and Basic Auth when redirected to another host",
                string.IsNullOrEmpty(leakedCookie) && string.IsNullOrEmpty(leakedReferer) && string.IsNullOrEmpty(leakedAuth),
                $"cookie='{leakedCookie}', referer='{leakedReferer}', auth='{leakedAuth}'");

            var sameItem = new DownloadItem
            {
                Url = $"http://127.0.0.1:{echoSrv.Port}/sample.zip",
                Referrer = $"http://127.0.0.1:{echoSrv.Port}/origin-page.html",
                Cookies = "SESSION=topsecret",
                CookieHost = "127.0.0.1"
            };
            var sameUri = new Uri($"http://127.0.0.1:{echoSrv.Port}/echo-headers");
            var sameBytes = await (Task<byte[]>)rangeMethod.Invoke(null, [sameItem, sameUri, 0L, 4096L, CancellationToken.None])!;
            var sameJson = JsonNode.Parse(Encoding.UTF8.GetString(sameBytes))!;
            string sameCookie = sameJson["cookie"]?.GetValue<string>() ?? "";
            string sameReferer = sameJson["referer"]?.GetValue<string>() ?? "";
            Check("P4-3: ZipPreview.RangeAsync attaches same-host Referer and Cookie",
                sameCookie.Contains("SESSION=topsecret") && sameReferer.Contains("/origin-page.html"),
                $"cookie='{sameCookie}', referer='{sameReferer}'");
        }

        // 3) M8: Transfer.Export / Transfer.Import round-trip (NameLocked + balanced parentheses)
        {
            var expDir = Path.Combine(workRoot, "m8");
            Directory.CreateDirectory(expDir);
            var safeDir = Path.Combine(expDir, "SafeDownloads");
            Directory.CreateDirectory(safeDir);

            var s = new AppSettings { DownloadRoot = safeDir, UseCategoryFolders = false };
            s.Normalize();
            var mgr = new DownloadManager(s);
            try
            {
                var httpItem = mgr.CreateHttp("https://example.com/files/archive_(1)", "https://example.com/ref", "sid=secret", "", "custom_name.zip");
                httpItem.NameLocked = true;
                httpItem.SaveDir = safeDir;
                httpItem.Description = "Important archive";
                httpItem.Login = "alice";
                httpItem.Password = "super-secret-pw";

                var mediaItem = new DownloadItem
                {
                    Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                    FileName = "video.mp4",
                    SaveDir = safeDir,
                    Category = "video",
                    Kind = DownloadKind.Media,
                    MediaQuality = "1080",
                    Description = "1080p stream"
                };

                var jsonPath = Path.Combine(expDir, "export.gdm");
                Transfer.Export(jsonPath, [httpItem, mediaItem], text: false);
                var rawJson = File.ReadAllText(jsonPath);
                Check("P4-3 (M8): Transfer.Export JSON writes format 'gdm-export' and omits sensitive Password and Cookies",
                    rawJson.Contains("\"gdm-export\"", StringComparison.Ordinal)
                    && !rawJson.Contains("super-secret-pw") && !rawJson.Contains("sid=secret"));

                var importedJson = Transfer.Import(jsonPath, mgr);
                Check("P4-3 (M8): Transfer.Export/Import JSON round-trips HTTP and Media items with metadata",
                    importedJson.Count == 2
                    && importedJson[0].Url == httpItem.Url
                    && importedJson[0].FileName == "custom_name.zip"
                    && importedJson[0].SaveDir == safeDir
                    && importedJson[0].Description == "Important archive"
                    && importedJson[0].Login == "alice"
                    && importedJson[1].Kind == DownloadKind.Media
                    && importedJson[1].MediaQuality == "1080");

                Check("P4-3 (M8): Transfer.Import JSON sets NameLocked=true when fileName was explicitly exported",
                    importedJson[0].NameLocked);

                var unsafeJsonPath = Path.Combine(expDir, "unsafe.json");
                File.WriteAllText(unsafeJsonPath, """
                    {
                      "format": "gdm-export",
                      "items": [
                        { "url": "https://example.com/a.exe", "fileName": "a.exe", "saveDir": "C:\\Windows\\System32", "mediaQuality": "invalid-quality" }
                      ]
                    }
                    """);
                var unsafeImported = Transfer.Import(unsafeJsonPath, mgr);
                Check("P4-3 (M8): Transfer.Import rejects C:\\Windows\\System32 SaveDir and sanitizes invalid mediaQuality to 'best'",
                    unsafeImported.Count == 1
                    && !unsafeImported[0].SaveDir.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase)
                    && unsafeImported[0].MediaQuality == "best");

                var txtPath = Path.Combine(expDir, "export.txt");
                File.WriteAllLines(txtPath, [
                    "https://example.com/files/archive_(1)",
                    "(https://example.com/files/wrapped_(2).zip).",
                    mediaItem.Url
                ]);
                var importedTxt = Transfer.Import(txtPath, mgr);
                Check("P4-3 (M8): Transfer.Import text mode preserves balanced ')' in URLs while trimming unmatched trailing ').'",
                    importedTxt.Count == 3
                    && importedTxt[0].Url == "https://example.com/files/archive_(1)"
                    && importedTxt[1].Url == "https://example.com/files/wrapped_(2).zip"
                    && importedTxt[2].Url == mediaItem.Url,
                    $"u0={importedTxt[0].Url}, u1={importedTxt[1].Url}");
            }
            finally
            {
                StopManagerTimer(mgr);
                try { if (Directory.Exists(safeDir)) Directory.Delete(safeDir, true); } catch { }
            }
        }
    }

    // -------------------------------------------------------------------------
    // P4-4: SiteGrabber MinSizeKB, MainDomain ccTLD, and thread-safe GrabberStats (M9)
    // -------------------------------------------------------------------------
    static async Task Test_P4_4_GrabberFixes(string workRoot)
    {
        Console.WriteLine("P4-4. SiteGrabber MinSizeKB, MainDomain ccTLD & GrabberProject (M9)");
        using var srv = new MiniHttpServer((rawPath, _) =>
        {
            var path = rawPath.Split('?')[0];
            if (path == "/sub/index.html")
            {
                var html = """
                    <html><body>
                      <a href="/sub/keep/unknown-size.pdf">Unknown Size PDF First</a>
                      <a href="/sub/keep/doc1.pdf">Keep PDF</a>
                      <a href="/sub/keep/thumb_1.pdf">Thumb PDF</a>
                      <a href="/sub/skipfile/doc2.pdf">Skipped Path PDF</a>
                      <a href="/sub/keep/dup.pdf">Dup 1</a>
                      <a href="/sub/AllowedPage/page2.html">Allowed Subpage</a>
                      <a href="/sub/SkipPage/page3.html">Skipped Subpage</a>
                      <a href="/parent.html">Parent Page</a>
                      <a href="/sub/keep/oversize.pdf">Oversize PDF</a>
                    </body></html>
                    """;
                var b = Encoding.UTF8.GetBytes(html);
                return (200, new() { ["Content-Type"] = "text/html", ["Content-Length"] = b.Length.ToString() }, b);
            }
            if (path == "/sub/AllowedPage/page2.html")
            {
                var html = """
                    <html><body>
                      <a href="/sub/keep/from_allowed.pdf">Allowed Child PDF</a>
                      <a href="/sub/keep/dup.pdf">Dup 2</a>
                    </body></html>
                    """;
                var b = Encoding.UTF8.GetBytes(html);
                return (200, new() { ["Content-Type"] = "text/html", ["Content-Length"] = b.Length.ToString() }, b);
            }
            if (path == "/sub/SkipPage/page3.html")
            {
                var html = """<html><body><a href="/sub/keep/from_skipped.pdf">Should Not Crawl</a></body></html>""";
                var b = Encoding.UTF8.GetBytes(html);
                return (200, new() { ["Content-Type"] = "text/html", ["Content-Length"] = b.Length.ToString() }, b);
            }
            if (path == "/parent.html")
            {
                var html = """<html><body><a href="/sub/keep/from_parent.pdf">Parent PDF</a></body></html>""";
                var b = Encoding.UTF8.GetBytes(html);
                return (200, new() { ["Content-Type"] = "text/html", ["Content-Length"] = b.Length.ToString() }, b);
            }
            if (path == "/sub/keep/unknown-size.pdf")
            {
                return (200, new() { ["Content-Type"] = "application/pdf" }, Encoding.UTF8.GetBytes("tiny"));
            }
            if (path == "/sub/keep/oversize.pdf")
            {
                return (200, new() { ["Content-Type"] = "application/pdf", ["Content-Length"] = (500 * 1024).ToString() }, new byte[16]);
            }
            if (path.EndsWith(".pdf"))
            {
                return (200, new() { ["Content-Type"] = "application/pdf", ["Content-Length"] = (20 * 1024).ToString() }, new byte[16]);
            }
            return (404, new() { ["Content-Length"] = "0" }, []);
        });

        var s = new AppSettings { MaxConnections = 2, ProxyMode = "none" };
        s.Normalize();
        HttpDownloader.Configure(s);

        var proj = new GrabberProject
        {
            Name = "FilterTest:Project*1",
            StartUrl = $"{srv.BaseUrl}/sub/index.html",
            LevelsBase = 1,
            SearchThisSiteOnly = true,
            DontExploreParent = true,
            FileMasks = "*.pdf",
            FileSkipMasks = "*thumb*",
            FilesOnly = "*/keep/*",
            FilesSkip = "*oversize*",
            ExploreOnly = "*/AllowedPage/*",
            ExploreSkip = "*/SkipPage/*",
            HideDuplicates = true,
            MinSizeKB = 10,
            MaxSizeKB = 100
        };

        var foundFiles = new List<GrabbedFile>();
        var grabber = new SiteGrabber(proj);
        grabber.FileFound += f => { lock (foundFiles) foundFiles.Add(f); };
        await grabber.RunAsync(CancellationToken.None);
        var names = foundFiles.Select(f => f.Name).ToList();

        Check("P4-4 (M9): SiteGrabber discovers matching files on start page and ExploreOnly child page even after rejecting an earlier link",
            names.Contains("doc1.pdf") && names.Contains("from_allowed.pdf"),
            $"found=[{string.Join(", ", names)}]");
        Check("P4-4 (M9): SiteGrabber enforces FileSkipMasks, FilesOnly, and FilesSkip filters",
            !names.Contains("thumb_1.pdf") && !names.Contains("doc2.pdf") && !names.Contains("oversize.pdf"));
        Check("P4-4 (M9): SiteGrabber enforces ExploreSkip and DontExploreParent filters",
            !names.Contains("from_skipped.pdf") && !names.Contains("from_parent.pdf"));
        Check("P4-4 (M9): SiteGrabber HideDuplicates=true deduplicates identical file URLs across pages",
            names.Count(n => n == "dup.pdf") == 1);
        Check("P4-4 (M9): SiteGrabber with MinSizeKB > 0 rejects files whose Content-Length is 0/unknown",
            !names.Contains("unknown-size.pdf"));

        var mainDomainMethod = typeof(SiteGrabber).GetMethod("MainDomain", BindingFlags.Static | BindingFlags.NonPublic)!;
        var d1 = (string)mainDomainMethod.Invoke(null, ["site-a.co.uk"])!;
        var d2 = (string)mainDomainMethod.Invoke(null, ["site-b.co.uk"])!;
        var dSub = (string)mainDomainMethod.Invoke(null, ["cdn.site-a.co.uk"])!;
        Check("P4-4 (M9): SiteGrabber.MainDomain handles multi-part ccTLDs (.co.uk) without collapsing unrelated domains",
            !string.Equals(d1, d2, StringComparison.OrdinalIgnoreCase) && string.Equals(d1, dSub, StringComparison.OrdinalIgnoreCase),
            $"d1='{d1}', d2='{d2}', dSub='{dSub}'");

        try
        {
            proj.Save();
            var loadedProj = GrabberProject.Load(proj.Name);
            Check("P4-4 (M9): GrabberProject.Save, List, and Load persist project filters and sanitize Name",
                loadedProj != null && loadedProj.StartUrl == proj.StartUrl && loadedProj.FileSkipMasks == "*thumb*" && GrabberProject.List().Count > 0);
        }
        finally
        {
            var savedFile = Path.Combine(GrabberProject.Folder, FileNames.Sanitize(proj.Name) + ".json");
            MediaDownloader.TryDelete(savedFile);
        }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "GDM", "src", "GDM")))
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    static Assembly LoadOrBuildProductAssembly(string repoRoot)
    {
        var binDir = Path.Combine(repoRoot, "GDM", "src", "GDM", "bin");
        var existing = Directory.Exists(binDir)
            ? Directory.GetFiles(binDir, "GDM.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (existing == null)
        {
            var csproj = Path.Combine(repoRoot, "GDM", "src", "GDM", "GDM.csproj");
            var psi = new ProcessStartInfo("dotnet", $"build \"{csproj}\" -nologo -v q")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi)!) p.WaitForExit(30000);
            existing = Directory.GetFiles(binDir, "GDM.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();
        }
        return Assembly.Load(File.ReadAllBytes(existing));
    }

    // -------------------------------------------------------------------------
    // P4-5: GrabberWindow credential scoping & queue MaxSimultaneous preservation
    // -------------------------------------------------------------------------
    static void Test_P4_5_GrabberWindowFixes(Assembly productAsm)
    {
        Console.WriteLine("P4-5. GrabberWindow credential scoping & QueueDef.MainId MaxSimultaneous");
        var grabberWinType = productAsm.GetType("Golan.Gdm.UI.GrabberWindow")!;
        var shouldCopyMethod = grabberWinType.GetMethod("ShouldCopyProjectCredentials", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        bool sameHostOk = false;
        bool crossHostBlocked = false;
        bool downgradeBlocked = false;
        if (shouldCopyMethod != null)
        {
            sameHostOk = (bool)shouldCopyMethod.Invoke(null, ["https://example.com/start", "https://example.com/files/a.zip", "alice"])!;
            crossHostBlocked = !(bool)shouldCopyMethod.Invoke(null, ["https://example.com/start", "https://cdn.other.com/a.zip", "alice"])!;
            downgradeBlocked = !(bool)shouldCopyMethod.Invoke(null, ["https://example.com/start", "http://example.com/a.zip", "alice"])!;
        }
        Check("P4-5: GrabberWindow copies project Login/Password only to same-host links without HTTPS->HTTP downgrade",
            shouldCopyMethod != null && sameHostOk && crossHostBlocked && downgradeBlocked,
            $"methodFound={shouldCopyMethod != null}, sameHostOk={sameHostOk}, crossHostBlocked={crossHostBlocked}, downgradeBlocked={downgradeBlocked}");
    }

    // -------------------------------------------------------------------------
    // P4-6: VideoWindow CookieMatchesUri & LoadThumbnailAsync HTTP-only guard
    // -------------------------------------------------------------------------
    static async Task Test_P4_6_VideoWindowFixes(Assembly productAsm, string repoRoot, string workRoot)
    {
        Console.WriteLine("P4-6. VideoWindow CookieMatchesUri & LoadThumbnailAsync HTTP-only guard");
        var videoWinType = productAsm.GetType("Golan.Gdm.UI.VideoWindow")!;
        var cookieMatchMethod = videoWinType.GetMethod("CookieMatchesUri", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        static JsonNode MakeCookie(string domain, string path, bool secure, bool hostOnly) => new JsonObject
        {
            ["domain"] = domain,
            ["name"] = "SID",
            ["value"] = "123",
            ["path"] = path,
            ["secure"] = secure,
            ["hostOnly"] = hostOnly
        };

        var hostOnlyCookie = MakeCookie("example.com", "/", false, true);
        var domainCookie = MakeCookie(".example.com", "/app", false, false);

        bool hostOnlyExact = (bool)cookieMatchMethod.Invoke(null, [hostOnlyCookie, new Uri("https://example.com/video")])!;
        bool hostOnlySub = (bool)cookieMatchMethod.Invoke(null, [hostOnlyCookie, new Uri("https://sub.example.com/video")])!;
        bool domainSubExactPath = (bool)cookieMatchMethod.Invoke(null, [domainCookie, new Uri("https://sub.example.com/app/stream")])!;
        bool pathPrefixMismatch = (bool)cookieMatchMethod.Invoke(null, [domainCookie, new Uri("https://sub.example.com/application")])!;

        Check("P4-6: VideoWindow.CookieMatchesUri enforces hostOnly and '/' path boundary",
            hostOnlyExact && !hostOnlySub && domainSubExactPath && !pathPrefixMismatch,
            $"hostOnlyExact={hostOnlyExact}, hostOnlySub={hostOnlySub}, domainSubExactPath={domainSubExactPath}, pathPrefixMismatch={pathPrefixMismatch}");

        var png1x1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var localPng = Path.Combine(workRoot, "local_thumb.png");
        File.WriteAllBytes(localPng, png1x1);
        var loadThumbMethod = videoWinType.GetMethod("LoadThumbnailAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        bool thumbRejectedNonHttp = false;
        if (loadThumbMethod != null)
        {
            var localThumb = await (Task<System.Windows.Media.Imaging.BitmapImage?>)loadThumbMethod.Invoke(null, [localPng])!;
            var fileThumb = await (Task<System.Windows.Media.Imaging.BitmapImage?>)loadThumbMethod.Invoke(null, [new Uri(localPng).AbsoluteUri])!;
            thumbRejectedNonHttp = localThumb == null && fileThumb == null;
        }
        Check("P4-6: VideoWindow.LoadThumbnailAsync rejects non-HTTP local and file:// paths",
            thumbRejectedNonHttp,
            $"staticMethodFound={loadThumbMethod != null}, thumbRejectedNonHttp={thumbRejectedNonHttp}");

        var xamlText = File.ReadAllText(Path.Combine(repoRoot, "GDM", "src", "GDM", "UI", "VideoWindow.xaml"));
        Check("P4-6: VideoWindow.xaml playlist Title TextBlock binds FlowDirection via TextDir converter",
            xamlText.Contains("Text=\"{Binding Title}\"") && xamlText.Contains("FlowDirection=\"{Binding Title, Converter={StaticResource TextDir}}\""));
    }

    // -------------------------------------------------------------------------
    // P4-7: BatchWindow cross-host cookie preservation with CookieHost = linkUri.Host
    // -------------------------------------------------------------------------
    static void Test_P4_7_BatchWindowFixes(Assembly productAsm)
    {
        Console.WriteLine("P4-7. BatchWindow cross-host cookie scoping");
        var batchWinType = productAsm.GetType("Golan.Gdm.UI.BatchWindow")!;
        var resolveMethod = batchWinType.GetMethod("ResolveCookiesForLink", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        bool crossCdnPreserved = false;
        bool downgradeCleared = false;
        if (resolveMethod != null)
        {
            var cdnRes = ((string Cookies, string CookieHost))resolveMethod.Invoke(null, ["https://cdn.example.net/file.zip", "https://portal.example.com/list", "SESSION=abc"])!;
            crossCdnPreserved = cdnRes.Cookies == "SESSION=abc" && cdnRes.CookieHost == "cdn.example.net";

            var downRes = ((string Cookies, string CookieHost))resolveMethod.Invoke(null, ["http://portal.example.com/file.zip", "https://portal.example.com/list", "SESSION=abc"])!;
            downgradeCleared = string.IsNullOrEmpty(downRes.Cookies) && string.IsNullOrEmpty(downRes.CookieHost);
        }
        Check("P4-7: BatchWindow preserves cookies for cross-host CDN links with CookieHost=linkUri.Host and blocks HTTPS->HTTP downgrade",
            resolveMethod != null && crossCdnPreserved && downgradeCleared,
            $"methodFound={resolveMethod != null}, crossCdnPreserved={crossCdnPreserved}, downgradeCleared={downgradeCleared}");
    }

    // -------------------------------------------------------------------------
    // P4-8: OptionsWindow CopySettings, Firefox XPI localization, ProgressWindow SegmentRow
    // -------------------------------------------------------------------------
    static void Test_P4_8_OptionsSchedulerProgressFixes(Assembly productAsm, string repoRoot)
    {
        Console.WriteLine("P4-8. OptionsWindow CopySettings, Firefox XPI path & ProgressWindow SegmentRow");
        var optionsWinType = productAsm.GetType("Golan.Gdm.UI.OptionsWindow")!;
        var appSettingsType = productAsm.GetType("Golan.Gdm.Core.AppSettings")!;
        var copyMethod = optionsWinType.GetMethod("CopySettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        var src = Activator.CreateInstance(appSettingsType)!;
        var dst = Activator.CreateInstance(appSettingsType)!;

        void SetProp(object obj, string name, object val) => appSettingsType.GetProperty(name)!.SetValue(obj, val);
        T GetProp<T>(object obj, string name) => (T)appSettingsType.GetProperty(name)!.GetValue(obj)!;

        SetProp(src, "MaxConnections", 16);
        SetProp(src, "SortBy", "Size");
        SetProp(src, "SortDescending", false);
        SetProp(src, "HideCategories", true);
        SetProp(src, "SmallToolbar", true);
        SetProp(src, "DarkMode", true);
        SetProp(src, "ShowDropTarget", true);
        SetProp(src, "DropTargetLeft", 300.0);
        SetProp(src, "DropTargetTop", 400.0);

        SetProp(dst, "MaxConnections", 4);
        SetProp(dst, "SortBy", "AddedAt");
        SetProp(dst, "SortDescending", true);
        SetProp(dst, "HideCategories", false);
        SetProp(dst, "SmallToolbar", false);
        SetProp(dst, "DarkMode", false);
        SetProp(dst, "ShowDropTarget", false);
        SetProp(dst, "DropTargetLeft", 12.0);
        SetProp(dst, "DropTargetTop", 34.0);

        copyMethod.Invoke(null, [dst, src]);

        Check("P4-8: OptionsWindow.CopySettings copies option fields while preserving MainWindow view-state properties",
            GetProp<int>(dst, "MaxConnections") == 16
            && GetProp<string>(dst, "SortBy") == "AddedAt"
            && GetProp<bool>(dst, "SortDescending")
            && !GetProp<bool>(dst, "HideCategories")
            && !GetProp<bool>(dst, "SmallToolbar")
            && !GetProp<bool>(dst, "DarkMode")
            && !GetProp<bool>(dst, "ShowDropTarget")
            && GetProp<double>(dst, "DropTargetLeft") == 12.0
            && GetProp<double>(dst, "DropTargetTop") == 34.0,
            $"MaxConn={GetProp<int>(dst, "MaxConnections")}, SortBy={GetProp<string>(dst, "SortBy")}, DarkMode={GetProp<bool>(dst, "DarkMode")}, DropLeft={GetProp<double>(dst, "DropTargetLeft")}");

        var enTable = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(repoRoot, "GDM", "src", "GDM", "i18n", "windows.en.json"))) ?? [];
        var arTable = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(repoRoot, "GDM", "src", "GDM", "i18n", "windows.ar.json"))) ?? [];
        enTable.TryGetValue("win.options.browsers.firefox_xpi", out var xpiEn);
        arTable.TryGetValue("win.options.browsers.firefox_xpi", out var xpiAr);
        var optCode = string.Concat(Directory.GetFiles(Path.Combine(repoRoot, "GDM", "src", "GDM", "UI"), "OptionsWindow*.cs").Select(File.ReadAllText));
        var smallCode = File.ReadAllText(Path.Combine(repoRoot, "GDM", "src", "GDM", "UI", "SmallWindows.cs"));
        Check("P4-8: Firefox XPI label is localized in both English and Arabic and displayed in OptionsWindow & WelcomeWindow",
            !string.IsNullOrWhiteSpace(xpiEn) && !string.IsNullOrWhiteSpace(xpiAr)
            && optCode.Contains("BrowserIntegration.FirefoxXpiPath") && smallCode.Contains("BrowserIntegration.FirefoxXpiPath"),
            $"en='{xpiEn}', ar='{xpiAr}'");

        var progressWinType = productAsm.GetType("Golan.Gdm.UI.ProgressWindow")!;
        var segRowType = productAsm.GetType("Golan.Gdm.UI.SegmentRow") ?? progressWinType.GetNestedType("SegmentRow", BindingFlags.NonPublic | BindingFlags.Public);
        bool segRowObservable = segRowType != null && typeof(INotifyPropertyChanged).IsAssignableFrom(segRowType);
        Check("P4-8: ProgressWindow.SegmentRow implements INotifyPropertyChanged for in-place refresh",
            segRowObservable);
    }
}
