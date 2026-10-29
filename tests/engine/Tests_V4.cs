using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Golan.Gdm.Core;

public static class Tests_V4
{
    static int _passed;
    static int _failed;
    static readonly List<string> _failures = [];

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
                    302 => "Found",
                    404 => "Not Found",
                    500 => "Internal Server Error",
                    _ => "Status",
                };
                var sb = new StringBuilder($"HTTP/1.1 {code} {reason}\r\nConnection: close\r\n");
                foreach (var (k, v) in respHeaders) sb.Append($"{k}: {v}\r\n");
                sb.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
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

    public static async Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Console.WriteLine("=== Package V4 Verification Suite (F8, F9, F10, F11) ===");
        var workRoot = Path.Combine(Path.GetTempPath(), "gdm-v4-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        try
        {
            await Test_F8_IntegrityAsync(workRoot);
            await Test_F9_ExtractorAsync(workRoot);
            Test_F10_SpeedRules();
            await Test_F11_SpeedHistoryAndStatsAsync(workRoot);
            Test_I18nParity();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] Tests_V4 unhandled exception: {ex}");
            _failed++;
        }
        finally
        {
            try { Directory.Delete(workRoot, true); } catch { }
        }

        Console.WriteLine($"\nTests_V4 Result: {_passed} passed, {_failed} failed");
        if (_failures.Count > 0)
        {
            Console.WriteLine("Tests_V4 Failures:");
            foreach (var f in _failures) Console.WriteLine($"  - {f}");
        }
        Console.WriteLine();
        return _failed;
    }

    // -------------------------------------------------------------------------
    // F8: Integrity Hash (HashSpec, ComputeAsync, Verify, Sums Discovery)
    // -------------------------------------------------------------------------
    static async Task Test_F8_IntegrityAsync(string workRoot)
    {
        Console.WriteLine("F8. Integrity Hash (HashSpec, ComputeAsync, Verify, Sums Discovery)");

        const string abcMd5 = "900150983cd24fb0d6963f7d28e17f72";
        const string abcSha1 = "a9993e364706816aba3e25717850c26c9cd0d89d";
        const string abcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        const string abcSha512 = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f";
        const string emptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        // F8-01: Explicit prefixes (case-insensitive, with whitespace)
        var pMd5 = HashSpec.Parse("  MD5:" + abcMd5.ToUpperInvariant() + " ");
        var pSha1 = HashSpec.Parse("sha-1:" + abcSha1);
        var pSha1b = HashSpec.Parse("SHA1:" + abcSha1.ToUpperInvariant());
        var pSha256 = HashSpec.Parse("sha256:" + abcSha256);
        var pSha256b = HashSpec.Parse("SHA-256:" + abcSha256.ToUpperInvariant());
        var pSha512 = HashSpec.Parse("sha512:" + abcSha512);
        var pSha512b = HashSpec.Parse("SHA-512:" + abcSha512.ToUpperInvariant());

        Check("F8-01: HashSpec.Parse explicit prefixes (md5, sha1, sha-1, sha256, sha-256, sha512, sha-512)",
            pMd5 is { Algorithm: var a1, Hex: abcMd5 } && a1 == HashAlgorithmName.MD5 && pMd5.Bytes.Length == 16 &&
            pSha1 is { Algorithm: var a2, Hex: abcSha1 } && a2 == HashAlgorithmName.SHA1 &&
            pSha1b is { Algorithm: var a3, Hex: abcSha1 } && a3 == HashAlgorithmName.SHA1 &&
            pSha256 is { Algorithm: var a4, Hex: abcSha256 } && a4 == HashAlgorithmName.SHA256 &&
            pSha256b is { Algorithm: var a5, Hex: abcSha256 } && a5 == HashAlgorithmName.SHA256 &&
            pSha512 is { Algorithm: var a6, Hex: abcSha512 } && a6 == HashAlgorithmName.SHA512 &&
            pSha512b is { Algorithm: var a7, Hex: abcSha512 } && a7 == HashAlgorithmName.SHA512);

        // F8-02: Auto-detection by hex length (32, 40, 64, 128)
        var dMd5 = HashSpec.Parse(abcMd5.ToUpperInvariant());
        var dSha1 = HashSpec.Parse(abcSha1);
        var dSha256 = HashSpec.Parse(abcSha256.ToUpperInvariant());
        var dSha512 = HashSpec.Parse(abcSha512);
        Check("F8-02: HashSpec.Parse auto-detects algorithm by length (32 MD5, 40 SHA-1, 64 SHA-256, 128 SHA-512)",
            dMd5?.Algorithm == HashAlgorithmName.MD5 && dMd5.Hex == abcMd5 &&
            dSha1?.Algorithm == HashAlgorithmName.SHA1 && dSha1.Hex == abcSha1 &&
            dSha256?.Algorithm == HashAlgorithmName.SHA256 && dSha256.Hex == abcSha256 &&
            dSha512?.Algorithm == HashAlgorithmName.SHA512 && dSha512.Hex == abcSha512);

        // F8-03: Invalid inputs rejected
        Check("F8-03: HashSpec.Parse rejects null, empty, non-hex, wrong length, and prefix/length mismatch",
            HashSpec.Parse(null) == null &&
            HashSpec.Parse("") == null &&
            HashSpec.Parse("   ") == null &&
            HashSpec.Parse("abc") == null &&
            HashSpec.Parse(new string('a', 31)) == null &&
            HashSpec.Parse(new string('a', 48)) == null &&
            HashSpec.Parse(new string('g', 64)) == null &&
            HashSpec.Parse("crc32:12345678") == null &&
            HashSpec.Parse("sha256:" + abcMd5) == null &&
            HashSpec.Parse("md5:" + abcSha256) == null &&
            HashSpec.Parse("sha256:") == null);

        // F8-04: ComputeAsync on NIST vectors ("abc" and "")
        var f8Dir = Path.Combine(workRoot, "f8");
        Directory.CreateDirectory(f8Dir);
        var abcFile = Path.Combine(f8Dir, "abc.txt");
        var emptyFile = Path.Combine(f8Dir, "empty.txt");
        File.WriteAllBytes(abcFile, "abc"u8.ToArray());
        File.WriteAllBytes(emptyFile, []);

        var abcHashes = await Integrity.ComputeAsync(abcFile, Integrity.AllAlgorithms);
        var emptyHashes = await Integrity.ComputeAsync(emptyFile, [HashAlgorithmName.SHA256]);
        Check("F8-04: Integrity.ComputeAsync computes exact NIST vectors for 'abc' (MD5/SHA-1/SHA-256/SHA-512) and empty file",
            abcHashes.TryGetValue(HashAlgorithmName.MD5, out var hMd5) && hMd5 == abcMd5 &&
            abcHashes.TryGetValue(HashAlgorithmName.SHA1, out var hSha1) && hSha1 == abcSha1 &&
            abcHashes.TryGetValue(HashAlgorithmName.SHA256, out var hSha256) && hSha256 == abcSha256 &&
            abcHashes.TryGetValue(HashAlgorithmName.SHA512, out var hSha512) && hSha512 == abcSha512 &&
            emptyHashes.TryGetValue(HashAlgorithmName.SHA256, out var hEmpty) && hEmpty == emptySha256);

        // F8-05: Multi-MB streaming (2.5 MB), FileShare.Read while open elsewhere, progress reporting
        var bigFile = Path.Combine(f8Dir, "multi_mb.bin");
        var bigBytes = new byte[2 * 1024 * 1024 + 512 * 1024];
        for (int i = 0; i < bigBytes.Length; i++) bigBytes[i] = (byte)((i * 31 + 7) & 0xFF);
        File.WriteAllBytes(bigFile, bigBytes);
        var expectedBigSha256 = Convert.ToHexString(SHA256.HashData(bigBytes)).ToLowerInvariant();

        var progressValues = new List<double>();
        var progress = new SynchronousProgress<double>(p => progressValues.Add(p));
        Dictionary<HashAlgorithmName, string> bigResult;
        await using (var writerOpen = new FileStream(bigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            bigResult = await Integrity.ComputeAsync(bigFile, [HashAlgorithmName.SHA256], progress);
        }
        Check("F8-05: Integrity.ComputeAsync streams 2.5 MB with FileShare.Read and reports monotonic progress up to 1.0",
            bigResult.TryGetValue(HashAlgorithmName.SHA256, out var actualBig) && actualBig == expectedBigSha256 &&
            progressValues.Count >= 3 && Math.Abs(progressValues[^1] - 1.0) < 0.001);

        // F8-06: Cancellation mid-stream
        using var cts = new CancellationTokenSource();
        var cancelProgress = new SynchronousProgress<double>(p => { if (p > 0.2) cts.Cancel(); });
        bool cancelled = false;
        try
        {
            await Integrity.ComputeAsync(bigFile, Integrity.AllAlgorithms, cancelProgress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check("F8-06: Integrity.ComputeAsync cancels promptly on CancellationToken", cancelled);

        // F8-07: Integrity.Verify (constant-time) & VerifyItemAsync (mismatch never deletes file)
        var okItem = new DownloadItem { SaveDir = f8Dir, FileName = "abc.txt", ExpectedHash = "SHA256:" + abcSha256.ToUpperInvariant(), Status = DownloadStatus.Completed };
        bool verifiedOk = await Integrity.VerifyItemAsync(okItem);
        var badItem = new DownloadItem { SaveDir = f8Dir, FileName = "abc.txt", ExpectedHash = abcSha256[..^1] + (abcSha256[^1] == '0' ? '1' : '0'), Status = DownloadStatus.Completed };
        bool verifiedBad = await Integrity.VerifyItemAsync(badItem);

        Check("F8-07: Integrity.VerifyItemAsync sets Verified/Mismatch states and NEVER deletes file on mismatch",
            verifiedOk && okItem.HashState == HashVerifyState.Verified && okItem.HashStatusText == Loc.T("hash.status.verified") &&
            !verifiedBad && badItem.HashState == HashVerifyState.Mismatch && badItem.HashStatusText == Loc.T("hash.status.mismatch") &&
            File.Exists(abcFile));

        // F8-08: ParseSums (GNU double-space, GNU binary *, BSD, single-hash, path-traversal lines ignored)
        var gnuSums = $"""
            # SHA256SUMS comment
            {emptySha256}  other.iso
            {abcSha256}  ../../abc.txt
            {abcSha256}  abc.txt
            """;
        var gnuBinarySums = $"{abcSha256} *abc.txt\r\n";
        var bsdSums = $"SHA256 (other.txt) = {emptySha256}\nSHA256 (abc.txt) = {abcSha256.ToUpperInvariant()}\n";
        var singleHashSums = $"{abcSha256}\n";

        Check("F8-08: Integrity.ParseSums parses GNU, GNU binary (*), BSD, and single-hash formats while ignoring traversal names",
            Integrity.ParseSums(gnuSums, "abc.txt")?.Hex == abcSha256 &&
            Integrity.ParseSums(gnuBinarySums, "abc.txt")?.Hex == abcSha256 &&
            Integrity.ParseSums(bsdSums, "abc.txt")?.Hex == abcSha256 &&
            Integrity.ParseSums(singleHashSums, "abc.txt")?.Hex == abcSha256 &&
            Integrity.ParseSums($"{abcSha256}  ../evil.txt", "evil.txt") == null);

        // F8-09: DiscoverHashAsync over loopback HTTP (fallback order, cross-host redirect blocked, >64KB cap blocked)
        using var srv = new MiniHttpServer((rawPath, _) =>
        {
            return rawPath switch
            {
                "/dl/archive.zip.sha256" => (404, new(), []),
                "/dl/archive.zip.sha256sum" => (404, new(), []),
                "/dl/SHA256SUMS" => (200, new() { ["Content-Type"] = "text/plain" }, Encoding.UTF8.GetBytes($"{emptySha256}  other.zip\n{abcSha256} *archive.zip\n")),
                "/redir/file.zip.sha256" => (302, new() { ["Location"] = $"http://localhost:18734/dl/SHA256SUMS" }, []),
                "/huge/big.zip.sha256" => (200, new() { ["Content-Length"] = "100000" }, new byte[100000]),
                _ => (404, new(), []),
            };
        });

        var discovered = await Integrity.DiscoverHashAsync($"{srv.BaseUrl}/dl/archive.zip", "archive.zip");
        var crossHostBlocked = await Integrity.DiscoverHashAsync($"{srv.BaseUrl}/redir/file.zip", "file.zip");
        var oversizeBlocked = await Integrity.DiscoverHashAsync($"{srv.BaseUrl}/huge/big.zip", "big.zip");
        Check("F8-09: Integrity.DiscoverHashAsync finds SHA256SUMS in same folder and blocks cross-host redirects and >64KB responses",
            discovered?.Hex == abcSha256 && crossHostBlocked == null && oversizeBlocked == null);
    }

    // -------------------------------------------------------------------------
    // F9: Automatic Archive Extraction (Plan, Zip-Slip, Bomb Guard, 7-Zip, Multi-Part)
    // -------------------------------------------------------------------------
    static async Task Test_F9_ExtractorAsync(string workRoot)
    {
        Console.WriteLine("F9. Auto-Extract (Plan, Zip-Slip, Bomb Guard, Existing Rules, 7-Zip Listing, Multi-Part)");
        var f9Dir = Path.Combine(workRoot, "f9");
        Directory.CreateDirectory(f9Dir);

        // F9-01: Extractor.Plan target directory & compound extensions
        var p1 = Extractor.Plan(Path.Combine(f9Dir, "bundle.zip"), ExtractTargetMode.Subfolder, ExistingFileRule.Rename);
        var p2 = Extractor.Plan(Path.Combine(f9Dir, "source.tar.gz"), ExtractTargetMode.Subfolder, ExistingFileRule.Rename);
        var p3 = Extractor.Plan(Path.Combine(f9Dir, "movie.part01.rar"), ExtractTargetMode.Subfolder, ExistingFileRule.Rename);
        var p4 = Extractor.Plan(Path.Combine(f9Dir, "backup.7z.001"), ExtractTargetMode.Subfolder, ExistingFileRule.Rename);
        var pSame = Extractor.Plan(Path.Combine(f9Dir, "bundle.zip"), ExtractTargetMode.SameFolder, ExistingFileRule.Skip);

        Directory.CreateDirectory(Path.Combine(f9Dir, "bundle"));
        var pUnique = Extractor.Plan(Path.Combine(f9Dir, "bundle.zip"), ExtractTargetMode.Subfolder, ExistingFileRule.Rename);

        Check("F9-01: Extractor.Plan strips compound extensions, handles Subfolder vs SameFolder, and picks unique subfolder when target exists",
            p1.TargetDir == Path.Combine(f9Dir, "bundle") && !p1.Uses7Zip &&
            p2.TargetDir == Path.Combine(f9Dir, "source") && p2.Uses7Zip &&
            p3.TargetDir == Path.Combine(f9Dir, "movie") && p3.Uses7Zip &&
            p4.TargetDir == Path.Combine(f9Dir, "backup") && p4.Uses7Zip &&
            pSame.TargetDir == f9Dir &&
            pUnique.TargetDir == Path.Combine(f9Dir, "bundle (1)"));

        // F9-02: ValidateEntryPath against zip-slip, rooted paths, ADS, and Windows reserved names
        var targetRoot = Path.Combine(f9Dir, "safe_out");
        Check("F9-02: Extractor.ValidateEntryPath accepts safe nested paths and blocks .., rooted paths, ADS (:), and reserved names",
            Extractor.ValidateEntryPath(targetRoot, "sub/readme.txt") == Path.Combine(targetRoot, "sub", "readme.txt") &&
            Extractor.ValidateEntryPath(targetRoot, "../evil.exe") == null &&
            Extractor.ValidateEntryPath(targetRoot, "a/../../evil.exe") == null &&
            Extractor.ValidateEntryPath(targetRoot, "..\\safe_out_evil\\x.txt") == null &&
            Extractor.ValidateEntryPath(targetRoot, "/etc/passwd") == null &&
            Extractor.ValidateEntryPath(targetRoot, "\\Windows\\win.ini") == null &&
            Extractor.ValidateEntryPath(targetRoot, "C:\\evil.exe") == null &&
            Extractor.ValidateEntryPath(targetRoot, "file.txt:Zone.Identifier") == null &&
            Extractor.ValidateEntryPath(targetRoot, "CON.txt") == null &&
            Extractor.ValidateEntryPath(targetRoot, "sub/NUL") == null &&
            Extractor.ValidateEntryPath(targetRoot, "COM1.log") == null);

        // F9-03: Crafted zip-slip archive rejected before escaping target folder
        var slipZip = Path.Combine(f9Dir, "slip.zip");
        using (var fs = File.Create(slipZip))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e1 = za.CreateEntry("../escaped_v4.txt");
            using (var w = new StreamWriter(e1.Open())) w.Write("pwned");
        }
        var slipTarget = Path.Combine(f9Dir, "slip_out");
        var slipRes = await Extractor.ExtractZipAsync(slipZip, slipTarget);
        Check("F9-03: Extractor.ExtractZipAsync blocks crafted zip-slip archive and writes nothing outside target",
            slipRes.State == ArchiveExtractState.Failed && !File.Exists(Path.Combine(f9Dir, "escaped_v4.txt")));

        // F9-04: Zip bomb guard (both total uncompressed size cap and entry count cap)
        var bombZip = Path.Combine(f9Dir, "bomb.zip");
        using (var fs = File.Create(bombZip))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            for (int i = 0; i < 6; i++)
            {
                var e = za.CreateEntry($"zero_{i}.bin", CompressionLevel.SmallestSize);
                using var s = e.Open();
                s.Write(new byte[200 * 1024]); // 6 * 200 KB = 1.2 MB uncompressed
            }
        }
        var bombSizeTarget = Path.Combine(f9Dir, "bomb_size_out");
        var bombEntriesTarget = Path.Combine(f9Dir, "bomb_entries_out");
        var bombSizeRes = await Extractor.ExtractZipAsync(bombZip, bombSizeTarget, maxTotalBytes: 500 * 1024, maxEntries: 100);
        var bombEntriesRes = await Extractor.ExtractZipAsync(bombZip, bombEntriesTarget, maxTotalBytes: 10 * 1024 * 1024, maxEntries: 4);
        Check("F9-04: Extractor.ExtractZipAsync enforces uncompressed byte cap and entry count cap",
            bombSizeRes.State == ArchiveExtractState.Failed &&
            bombEntriesRes.State == ArchiveExtractState.Failed);

        // F9-05: Existing file rules (Rename, Skip, Overwrite)
        var ruleZip = Path.Combine(f9Dir, "rules.zip");
        using (var fs = File.Create(ruleZip))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = za.CreateEntry("hello.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("NEW_CONTENT");
        }

        var dirRename = Path.Combine(f9Dir, "rule_rename");
        Directory.CreateDirectory(dirRename);
        File.WriteAllText(Path.Combine(dirRename, "hello.txt"), "OLD_CONTENT");
        var resRename = await Extractor.ExtractZipAsync(ruleZip, dirRename, ExistingFileRule.Rename);

        var dirSkip = Path.Combine(f9Dir, "rule_skip");
        Directory.CreateDirectory(dirSkip);
        File.WriteAllText(Path.Combine(dirSkip, "hello.txt"), "OLD_CONTENT");
        var resSkip = await Extractor.ExtractZipAsync(ruleZip, dirSkip, ExistingFileRule.Skip);

        var dirOverwrite = Path.Combine(f9Dir, "rule_overwrite");
        Directory.CreateDirectory(dirOverwrite);
        File.WriteAllText(Path.Combine(dirOverwrite, "hello.txt"), "OLD_CONTENT");
        var resOverwrite = await Extractor.ExtractZipAsync(ruleZip, dirOverwrite, ExistingFileRule.Overwrite);

        Check("F9-05: Extractor.ExtractZipAsync honors Rename, Skip, and Overwrite rules for existing files",
            resRename.State == ArchiveExtractState.Extracted &&
            File.ReadAllText(Path.Combine(dirRename, "hello.txt")) == "OLD_CONTENT" &&
            File.Exists(Path.Combine(dirRename, "hello (1).txt")) &&
            File.ReadAllText(Path.Combine(dirRename, "hello (1).txt")) == "NEW_CONTENT" &&
            resSkip.State == ArchiveExtractState.Extracted &&
            File.ReadAllText(Path.Combine(dirSkip, "hello.txt")) == "OLD_CONTENT" &&
            !File.Exists(Path.Combine(dirSkip, "hello (1).txt")) &&
            resOverwrite.State == ArchiveExtractState.Extracted &&
            File.ReadAllText(Path.Combine(dirOverwrite, "hello.txt")) == "NEW_CONTENT");

        // F9-06 & F9-07: 7-Zip listing parser (-slt -ba), pre-extract validation, and ArgumentList builder
        var sample7zListing = """
            Path = docs
            Folder = +
            Size = 0
            Encrypted = -

            Path = docs\report.pdf
            Folder = -
            Size = 12345
            Encrypted = +
            Method = LZMA2:16 7zAES
            """;
        var parsed7z = Extractor.Parse7zListing(sample7zListing);
        var argsRename = Extractor.Build7zExtractArgs(@"C:\dl\arc.7z", @"C:\dl\arc", ExistingFileRule.Rename, "MySecretPass");
        var argsSkip = Extractor.Build7zExtractArgs(@"C:\dl\arc.7z", @"C:\dl\arc", ExistingFileRule.Skip, null);
        var argsOverwrite = Extractor.Build7zExtractArgs(@"C:\dl\arc.7z", @"C:\dl\arc", ExistingFileRule.Overwrite, null);

        var evil7zListing = Extractor.Parse7zListing("Path = ..\\evil.exe\nFolder = -\nSize = 100\nEncrypted = -\n");
        bool evil7zRejected = false;
        try { Extractor.Validate7zListing(evil7zListing, targetRoot, archiveSize: 1024); }
        catch { evil7zRejected = true; }

        var symlink7zListing = Extractor.Parse7zListing("Path = sub\nFolder = -\nSize = 0\nSymbolic Link = ../..\nEncrypted = -\n");
        bool symlink7zRejected = false;
        try { Extractor.Validate7zListing(symlink7zListing, targetRoot, archiveSize: 1024); }
        catch { symlink7zRejected = true; }

        Check("F9-06: Extractor.Parse7zListing & Validate7zListing parse -slt output, detect encryption, map -aou/-aos/-aoa, and block zip-slip & symlinks before extracting",
            parsed7z.Entries.Count == 2 && parsed7z.IsEncrypted && parsed7z.TotalUncompressedSize == 12345 &&
            argsRename.Contains("-aou") && argsRename.Contains("-pMySecretPass") && argsRename.Contains("-y") &&
            argsSkip.Contains("-aos") && argsOverwrite.Contains("-aoa") &&
            evil7zRejected && symlink7zRejected);

        // F9-08: Find7Zip never executes 7z.exe from the download folder or relative paths
        var fakeDlDir = Path.Combine(f9Dir, "untrusted_dl");
        Directory.CreateDirectory(fakeDlDir);
        var planted7z = Path.Combine(fakeDlDir, "7z.exe");
        File.WriteAllBytes(planted7z, [0x4D, 0x5A]);
        var foundFromUntrusted = Extractor.Find7Zip(customPath: null, forbiddenDir: fakeDlDir, searchDirs: [fakeDlDir, ".\\rel_untrusted"]);
        Check("F9-08: Extractor.Find7Zip refuses to load 7z.exe from the download directory or relative PATH entries",
            foundFromUntrusted == null);

        // F9-09: Multi-part detection & completeness check (+ DoS/overflow volume bound)
        var mpDir = Path.Combine(f9Dir, "multipart");
        Directory.CreateDirectory(mpDir);
        var rar1 = Path.Combine(mpDir, "game.part1.rar");
        var rar2 = Path.Combine(mpDir, "game.part2.rar");
        File.WriteAllText(rar1, "p1");
        File.WriteAllText(rar2, "p2");
        File.WriteAllText(rar2 + ".gdm", "temp"); // part 2 still downloading (.gdm)!
        var mpIncomplete = Extractor.CheckMultiPart(rar1);
        File.Delete(rar2 + ".gdm");
        File.WriteAllText(rar2 + ".\u0061dam", "temp"); // legacy partial extension is ignored
        var mpLegacyIgnored = Extractor.CheckMultiPart(rar1);
        File.Delete(rar2 + ".\u0061dam");
        var mpCompleteFrom1 = Extractor.CheckMultiPart(rar1);
        var mpCompleteFrom2 = Extractor.CheckMultiPart(rar2);

        var sz1 = Path.Combine(mpDir, "data.7z.001");
        var sz3 = Path.Combine(mpDir, "data.7z.003"); // missing .002!
        File.WriteAllText(sz1, "1");
        File.WriteAllText(sz3, "3");
        var szGap = Extractor.CheckMultiPart(sz1);
        File.WriteAllText(Path.Combine(mpDir, "data.7z.002"), "2");
        var szComplete = Extractor.CheckMultiPart(sz3);

        var overflowRar = Path.Combine(mpDir, "dos.part999999999999.rar");
        var hugeRar = Path.Combine(mpDir, "dos.part50000000.rar");
        File.WriteAllText(overflowRar, "x");
        File.WriteAllText(hugeRar, "x");
        var mpOverflow = Extractor.CheckMultiPart(overflowRar);
        var mpHuge = Extractor.CheckMultiPart(hugeRar);

        Check("F9-09: Extractor.CheckMultiPart detects .partN.rar and .7z.001 sets, blocks on missing/incomplete (.gdm) parts, ignores legacy partial extension, resolves first part, and bounds huge part numbers",
            mpIncomplete.IsMultiPart && !mpIncomplete.IsComplete &&
            mpLegacyIgnored.IsMultiPart && mpLegacyIgnored.IsComplete &&
            mpCompleteFrom1.IsMultiPart && mpCompleteFrom1.IsComplete && mpCompleteFrom1.FirstPartPath == rar1 &&
            mpCompleteFrom2.IsMultiPart && mpCompleteFrom2.IsComplete && mpCompleteFrom2.FirstPartPath == rar1 &&
            szGap.IsMultiPart && !szGap.IsComplete &&
            szComplete.IsMultiPart && szComplete.IsComplete && szComplete.FirstPartPath == sz1 &&
            !mpOverflow.IsMultiPart && !mpHuge.IsMultiPart && mpHuge.AllPartPaths.Count == 1);

        // F9-10: F8 -> F9 post-download pipeline (hash mismatch blocks extraction; hash match extracts & optionally deletes archive)
        var postDir = Path.Combine(f9Dir, "post");
        Directory.CreateDirectory(postDir);
        var validZip = Path.Combine(postDir, "pkg.zip");
        File.Copy(ruleZip, validZip, overwrite: true);
        var validZipSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(validZip))).ToLowerInvariant();

        var settingsPost = new AppSettings
        {
            AutoExtractEnabled = true,
            AutoExtractTargetMode = ExtractTargetMode.Subfolder,
            AutoExtractExistingRule = ExistingFileRule.Overwrite,
            AutoExtractDeleteArchive = true,
        };
        var itemBadHash = new DownloadItem
        {
            SaveDir = postDir,
            FileName = "pkg.zip",
            ExpectedHash = new string('0', 64),
            Status = DownloadStatus.Completed,
        };
        await Extractor.RunPostDownloadAsync(itemBadHash, settingsPost);
        bool blockedByMismatch = itemBadHash.HashState == HashVerifyState.Mismatch
                                 && itemBadHash.ExtractState == ArchiveExtractState.None
                                 && File.Exists(validZip)
                                 && !Directory.Exists(Path.Combine(postDir, "pkg"));

        var itemGoodHash = new DownloadItem
        {
            SaveDir = postDir,
            FileName = "pkg.zip",
            ExpectedHash = validZipSha256,
            Status = DownloadStatus.Completed,
        };
        await Extractor.RunPostDownloadAsync(itemGoodHash, settingsPost);
        bool extractedAndDeleted = itemGoodHash.HashState == HashVerifyState.Verified
                                   && itemGoodHash.ExtractState == ArchiveExtractState.Extracted
                                   && File.Exists(Path.Combine(postDir, "pkg", "hello.txt"))
                                   && !File.Exists(validZip);

        Check("F9-10: RunPostDownloadAsync blocks extraction when F8 hash fails, and extracts + deletes archive when F8 hash passes",
            blockedByMismatch && extractedAndDeleted);

        // F9-11: Real 7z.exe round-trip if installed on host (graceful skip otherwise)
        var real7z = Extractor.Find7Zip();
        if (real7z != null)
        {
            var rtDir = Path.Combine(f9Dir, "real7z");
            Directory.CreateDirectory(rtDir);
            var srcFile = Path.Combine(rtDir, "sample.txt");
            File.WriteAllText(srcFile, "7zip-round-trip-ok");
            var arc7z = Path.Combine(rtDir, "test_arc.7z");
            var psi = new ProcessStartInfo(real7z) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add(arc7z);
            psi.ArgumentList.Add(srcFile);
            using (var p = Process.Start(psi)!) await p.WaitForExitAsync();

            var rtRes = await Extractor.ExtractAsync(arc7z, ExtractTargetMode.Subfolder, ExistingFileRule.Overwrite);
            Check("F9-11: Real 7z.exe round-trip lists, validates, and extracts .7z archive",
                rtRes.State == ArchiveExtractState.Extracted &&
                File.Exists(Path.Combine(rtDir, "test_arc", "sample.txt")) &&
                File.ReadAllText(Path.Combine(rtDir, "test_arc", "sample.txt")) == "7zip-round-trip-ok");
        }
        else
        {
            Check("F9-11: Real 7z.exe not installed on host — graceful skip verified", true, "skipped (no 7z.exe)");
        }
    }

    // -------------------------------------------------------------------------
    // F10: Speed Limit by Time of Day (SpeedRules & SpeedRuleController)
    // -------------------------------------------------------------------------
    static void Test_F10_SpeedRules()
    {
        Console.WriteLine("F10. Speed Limit by Time of Day (SpeedRules & SpeedRuleController)");

        // F10-01: Same-day rule & [From, To) exclusivity
        var workRule = new SpeedRule
        {
            Days = DaysMask.Weekdays,
            From = new TimeOnly(9, 0),
            To = new TimeOnly(17, 0),
            LimitKBps = 500,
            Enabled = true,
        };
        var mon = new DateTime(2026, 10, 19); // Monday
        var sat = new DateTime(2026, 10, 24); // Saturday
        Check("F10-01: SpeedRules.Evaluate matches same-day [From, To) with exclusive To and weekday mask",
            SpeedRules.Evaluate([workRule], mon.AddHours(8).AddMinutes(59)) == null &&
            SpeedRules.Evaluate([workRule], mon.AddHours(9)) == 500 &&
            SpeedRules.Evaluate([workRule], mon.AddHours(16).AddMinutes(59)) == 500 &&
            SpeedRules.Evaluate([workRule], mon.AddHours(17)) == null &&
            SpeedRules.Evaluate([workRule], sat.AddHours(12)) == null);

        // F10-02: Overnight range (From > To) belongs strictly to start day
        var friNight = new SpeedRule
        {
            Days = DaysMask.Friday,
            From = new TimeOnly(22, 0),
            To = new TimeOnly(6, 0),
            LimitKBps = 250,
            Enabled = true,
        };
        var fri = new DateTime(2026, 10, 23); // Friday
        Check("F10-02: SpeedRules.Evaluate overnight range (22:00-06:00) belongs to start day (Friday night -> Saturday morning)",
            SpeedRules.Evaluate([friNight], fri.AddHours(2)) == null &&
            SpeedRules.Evaluate([friNight], fri.AddHours(21).AddMinutes(59)) == null &&
            SpeedRules.Evaluate([friNight], fri.AddHours(22)) == 250 &&
            SpeedRules.Evaluate([friNight], sat.AddHours(0)) == 250 &&
            SpeedRules.Evaluate([friNight], sat.AddHours(2)) == 250 &&
            SpeedRules.Evaluate([friNight], sat.AddHours(5).AddMinutes(59)) == 250 &&
            SpeedRules.Evaluate([friNight], sat.AddHours(6)) == null &&
            SpeedRules.Evaluate([friNight], sat.AddHours(23)) == null);

        // F10-03: First enabled rule wins; LimitKBps == 0 (unlimited) vs null
        var rulesPriority = new List<SpeedRule>
        {
            new() { Days = DaysMask.All, From = new(0, 0), To = new(23, 59), LimitKBps = 100, Enabled = false },
            new() { Days = DaysMask.Monday, From = new(12, 0), To = new(13, 0), LimitKBps = 0, Enabled = true },
            workRule,
        };
        Check("F10-03: SpeedRules.Evaluate skips disabled rules, first enabled rule wins, and LimitKBps=0 returns 0 (unlimited)",
            SpeedRules.Evaluate(rulesPriority, mon.AddHours(10)) == 500 &&
            SpeedRules.Evaluate(rulesPriority, mon.AddHours(12).AddMinutes(30)) == 0);

        // F10-04: NextBoundary across same-day, overnight, and multi-day
        var b1 = SpeedRules.NextBoundary([workRule], mon.AddHours(8));
        var b2 = SpeedRules.NextBoundary([workRule], mon.AddHours(10));
        var b3 = SpeedRules.NextBoundary([friNight], fri.AddHours(23));
        var b4 = SpeedRules.NextBoundary([friNight], sat.AddHours(2));
        Check("F10-04: SpeedRules.NextBoundary finds exact transition time for same-day and overnight rules",
            b1 == mon.AddHours(9) &&
            b2 == mon.AddHours(17) &&
            b3 == sat.AddHours(6) &&
            b4 == sat.AddHours(6));

        // F10-05: SpeedRuleController with manual override until NextBoundary
        var now = mon.AddHours(8).AddMinutes(30);
        var settings = new AppSettings { SpeedRules = [workRule] };
        var limiter = new SpeedLimiter();
        using var ctrl = new SpeedRuleController(settings, limiter, clock: () => now, startTimer: false);

        ctrl.Tick();
        bool offAt830 = !limiter.Enabled;

        now = mon.AddHours(9);
        ctrl.Tick();
        bool onAt900 = limiter.Enabled && limiter.BytesPerSecond == 500 * 1024L;

        now = mon.AddHours(10);
        ctrl.SetManualOverride(enabled: false); // user turns off limiter manually
        bool manualOffAt1000 = !limiter.Enabled && ctrl.IsManualOverrideActive && ctrl.ManualOverrideUntil == mon.AddHours(17);

        now = mon.AddHours(12);
        ctrl.Tick();
        bool stillManualAt1200 = !limiter.Enabled && ctrl.IsManualOverrideActive;

        now = mon.AddHours(17); // boundary reached -> override expires!
        ctrl.Tick();
        bool expiredAt1700 = !ctrl.IsManualOverrideActive && !limiter.Enabled;

        Check("F10-05: SpeedRuleController applies scheduled rule, holds manual override until NextBoundary, and expires on boundary",
            offAt830 && onAt900 && manualOffAt1000 && stillManualAt1200 && expiredAt1700);
    }

    // -------------------------------------------------------------------------
    // F11: SpeedHistory Ring Buffer & StatsStore
    // -------------------------------------------------------------------------
    static async Task Test_F11_SpeedHistoryAndStatsAsync(string workRoot)
    {
        Console.WriteLine("F11. SpeedHistory Ring Buffer & StatsStore Daily Buckets");

        // F11-01: Ring buffer wrap-around (600 capacity)
        var hist = new SpeedHistory(600);
        hist.Add(-50); // clamped to 0
        for (int i = 1; i <= 750; i++) hist.Add(i);
        var snap = hist.Snapshot();
        Check("F11-01: SpeedHistory 600-sample ring buffer wraps cleanly in chronological order",
            snap.Length == 600 && snap[0] == 151 && snap[599] == 750 && hist.Max == 750 && hist.Current == 750);

        // F11-02: SpeedHistory thread safety
        var parallelTasks = new List<Task>();
        for (int t = 0; t < 4; t++)
        {
            parallelTasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) hist.Add(i * 1024L);
            }));
            parallelTasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 500; i++) _ = hist.Snapshot();
            }));
        }
        await Task.WhenAll(parallelTasks);
        Check("F11-02: SpeedHistory is thread-safe under concurrent Add and Snapshot",
            hist.Snapshot().Length == 600);

        // F11-03: StatsStore bucket aggregation across midnight
        var f11Dir = Path.Combine(workRoot, "f11");
        Directory.CreateDirectory(f11Dir);
        var statsPath = Path.Combine(f11Dir, "stats.json");
        var day1 = new DateOnly(2026, 10, 18);
        var day2 = new DateOnly(2026, 10, 19);
        var currentDay = day1;

        using (var store = new StatsStore(statsPath, todayProvider: () => currentDay, autoFlushTimer: false))
        {
            store.Record(1000, "CDN.Example.com", "video");
            store.Record(500, "cdn.example.com", "music");
            store.RecordFileCompleted("cdn.example.com", "video");
            store.RecordActiveSecond(seconds: 10);

            currentDay = day2;
            store.Record(2000, "files.org", "compressed");
            store.RecordFileCompleted("files.org", "compressed");
            store.RecordFileCompleted("files.org", "compressed");
            store.RecordActiveSecond(seconds: 5);
            store.Flush();
        }

        // F11-05: Round-trip reload from disk
        using (var reloaded = new StatsStore(statsPath, todayProvider: () => day2, autoFlushTimer: false))
        {
            var buckets = reloaded.GetBuckets();
            var bDay1 = buckets.FirstOrDefault(b => b.Date == "2026-10-18");
            var bDay2 = buckets.FirstOrDefault(b => b.Date == "2026-10-19");
            Check("F11-03 & F11-05: StatsStore aggregates daily buckets across midnight and round-trips through stats.json atomically",
                bDay1 is { Bytes: 1500, Files: 1, ActiveSeconds: 10 } &&
                bDay1.ByCategory.TryGetValue("video", out var vBytes) && vBytes == 1000 &&
                bDay1.ByHost.TryGetValue("cdn.example.com", out var hBytes) && hBytes == 1500 &&
                bDay2 is { Bytes: 2000, Files: 2, ActiveSeconds: 5 } &&
                !File.Exists(statsPath + ".tmp"));

            var qToday = reloaded.Query(StatsRange.Today, day2);
            var q7 = reloaded.Query(StatsRange.Last7Days, day2);
            Check("F11-07: StatsStore.Query computes Today, Last7Days, AverageSpeed, ByCategory, and TopHosts",
                qToday.TotalBytes == 2000 && qToday.TotalFiles == 2 && Math.Abs(qToday.AverageSpeedBytesPerSec - 400.0) < 0.01 &&
                q7.TotalBytes == 3500 && q7.TotalFiles == 3 && Math.Abs(q7.AverageSpeedBytesPerSec - (3500.0 / 15.0)) < 0.01);
        }

        // F11-04: 400-day retention trim & top-50 host cap per day
        var retentionPath = Path.Combine(f11Dir, "retention.json");
        var baseDate = new DateOnly(2026, 10, 18);
        using (var rStore = new StatsStore(retentionPath, todayProvider: () => baseDate, autoFlushTimer: false))
        {
            for (int d = 449; d >= 0; d--)
                rStore.Record(100, "example.com", "general", baseDate.AddDays(-d));
            for (int h = 1; h <= 65; h++)
                rStore.Record(h * 100L, $"host{h:D2}.example.com", "general", baseDate);
            rStore.Flush();

            var buckets = rStore.GetBuckets();
            var todayBucket = buckets.First(b => b.Date == baseDate.ToString("yyyy-MM-dd"));
            Check("F11-04: StatsStore trims retention to 400 days and caps ByHost at top 50 hosts",
                buckets.Count == 400 &&
                buckets[0].Date == baseDate.AddDays(-399).ToString("yyyy-MM-dd") &&
                todayBucket.ByHost.Count <= 50 &&
                todayBucket.ByHost.ContainsKey("host65.example.com"));
        }

        // F11-06: Corrupt stats.json recovery (.bad backup)
        var corruptPath = Path.Combine(f11Dir, "corrupt_stats.json");
        File.WriteAllText(corruptPath, "{{{not-valid-json");
        using (var cStore = new StatsStore(corruptPath, autoFlushTimer: false))
        {
            Check("F11-06: StatsStore recovers from corrupt stats.json and saves .bad copy",
                cStore.GetBuckets().Count == 0 && File.Exists(corruptPath + ".bad"));
        }

        // F11-08: Parallel Record concurrency
        var concPath = Path.Combine(f11Dir, "conc_stats.json");
        using (var concStore = new StatsStore(concPath, todayProvider: () => baseDate, autoFlushTimer: false))
        {
            var workers = Enumerable.Range(0, 16).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    concStore.Record(100, $"h{i % 10}.com", "video", baseDate);
                    concStore.RecordFileCompleted($"h{i % 10}.com", "video", baseDate);
                }
            })).ToArray();
            await Task.WhenAll(workers);
            var summary = concStore.Query(StatsRange.Today, baseDate);
            Check("F11-08: StatsStore parallel Record loses zero updates across 16 threads",
                summary.TotalBytes == 16L * 500 * 100 && summary.TotalFiles == 16 * 500);
        }
    }

    static void Test_I18nParity()
    {
        Console.WriteLine("V4. i18n Key Parity (v4.ar.json vs v4.en.json)");
        Loc.Init("ar");
        var arVerified = Loc.T("hash.status.verified");
        var arMismatch = Loc.T("hash.status.mismatch");
        var arCompute = Loc.T("hash.menu.compute");
        var arSubfolder = Loc.T("extract.target.subfolder");
        Loc.Init("en");
        var enVerified = Loc.T("hash.status.verified");
        Loc.Init("ar");

        var i18nDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GDM", "i18n"));
        if (!Directory.Exists(i18nDir))
            i18nDir = Path.GetFullPath(Path.Combine("..", "..", "src", "GDM", "i18n"));
        using var sAr = File.OpenRead(Path.Combine(i18nDir, "integrity.ar.json"));
        using var sEn = File.OpenRead(Path.Combine(i18nDir, "integrity.en.json"));
        var mapAr = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sAr)!;
        var mapEn = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sEn)!;
        bool exactKeyParity = mapAr.Count > 50 &&
                              mapAr.Keys.OrderBy(k => k).SequenceEqual(mapEn.Keys.OrderBy(k => k)) &&
                              mapAr.Values.All(v => !string.IsNullOrWhiteSpace(v)) &&
                              mapEn.Values.All(v => !string.IsNullOrWhiteSpace(v));

        Check("V4-i18n: v4.ar.json and v4.en.json have exact key parity and required strings («سليم ✓», «لا يطابق ✗», «احسب البصمة», «في مجلد باسم الملف»)",
            exactKeyParity &&
            arVerified == "سليم ✓" && arMismatch == "لا يطابق ✗" && arCompute == "احسب البصمة" && arSubfolder == "في مجلد باسم الملف" &&
            enVerified == "Verified ✓");
    }

    sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
