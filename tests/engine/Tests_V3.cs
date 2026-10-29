// Package V3 (Feature F6 — BitTorrent & magnet links) comprehensive test suite.
// Covers:
//   V3-1: Magnet URI validation & parsing table (btih hex40, btih base32, btmh, tracker scheme filter, xs/as https filter)
//   V3-2: TorrentPaths.Sanitize & pre-start validation table (.., rooted, UNC, drive, ADS, reserved DOS names, trailing dots/spaces, >200 chars, control chars, >10,000 files, executable detection)
//   V3-3: File selection & priority mapping (DoNotDownload/Low/Normal/High, partial size calculation, Select All/None, executable warning)
//   V3-4: Fast-resume persistence & load round-trip under AppPaths.LocalDir\torrent\
//   V3-5: TorrentState -> DownloadStatus mapping & seeding stop policy (ratio >= 1.0 or time >= 60m, 0 = don't seed, Completed status while seeding)
//   V3-6: magnet: URL protocol registration via IMagnetRegistry / FakeMagnetRegistry
//   V3-7: Loopback swarm integration test (3 MB random file, TorrentCreator, seeder ClientEngine + TorrentRunner on 127.0.0.1, SHA-256 + MOTW verification)
//   V3-8: Settings.V3 defaults & JSON round-trip, DownloadItem torrent properties round-trip, i18n parity (v3.en.json / v3.ar.json)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using MonoTorrent;
using MonoTorrent.Client;

internal static class Tests_V3
{
    private static int _fails;
    private static int _passes;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passes++;
            Console.WriteLine($"  [PASS] {name}" + (detail is null ? "" : $"  ({detail})"));
        }
        else
        {
            _fails++;
            Console.WriteLine($"  [FAIL] {name}" + (detail is null ? "" : $"  ({detail})"));
        }
    }

    private static bool ExpectThrows<TException>(Action act) where TException : Exception
    {
        try
        {
            act();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        _fails = 0;
        _passes = 0;
        Console.WriteLine("\n================ V3 TEST SUITE (F6 BitTorrent & Magnet Links) ================");

        string? savedDownloads = File.Exists(AppPaths.DownloadsFile) ? File.ReadAllText(AppPaths.DownloadsFile) : null;
        string? savedSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;

        // Keep all temporary test artifacts inside outDir so we never touch files outside the workspace.
        string workRoot = Path.Combine(Path.GetFullPath(outDir), "v3-work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        try
        {
            Test_V3_1_MagnetValidation();
            Test_V3_2_PathSanitization(workRoot);
            Test_V3_3_FileSelectionAndPriority();
            Test_V3_4_FastResumePersistence();
            Test_V3_5_StateMappingAndSeedingPolicy();
            Test_V3_6_MagnetProtocolRegistration();
            await Test_V3_7_LoopbackSwarmAsync(workRoot);
            Test_V3_8_SettingsSerializationAndI18n();
        }
        finally
        {
            try
            {
                if (savedDownloads is null)
                {
                    if (File.Exists(AppPaths.DownloadsFile)) File.Delete(AppPaths.DownloadsFile);
                }
                else
                {
                    File.WriteAllText(AppPaths.DownloadsFile, savedDownloads);
                }
            }
            catch { }

            try
            {
                if (savedSettings is null)
                {
                    if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
                }
                else
                {
                    File.WriteAllText(AppPaths.SettingsFile, savedSettings);
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(workRoot))
                    Directory.Delete(workRoot, recursive: true);
            }
            catch { }
        }

        Console.WriteLine($"================ V3 SUMMARY: {_passes} passed, {_fails} failed ================\n");
        return _fails;
    }

    // =========================================================================
    // V3-1: Magnet URI validation & parsing table
    // =========================================================================
    private static void Test_V3_1_MagnetValidation()
    {
        Console.WriteLine("\n=== V3-1: Magnet URI Validation & Parsing ===");

        // V3-1.01: Valid btih 40-char lowercase hex
        {
            string uri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Ubuntu+ISO";
            bool ok = MagnetValidator.TryParse(uri, out var info, out var err);
            Check("V3-1.01 Valid btih 40-char lowercase hex",
                ok && info != null
                   && info.InfoHashHex == "0123456789abcdef0123456789abcdef01234567"
                   && info.DisplayName == "Ubuntu ISO",
                err ?? info?.InfoHashHex);
        }

        // V3-1.02: Valid btih 40-char uppercase hex
        {
            string uri = "magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&dn=Test";
            bool ok = MagnetValidator.TryParse(uri, out var info, out var err);
            Check("V3-1.02 Valid btih 40-char uppercase hex",
                ok && info != null
                   && info.InfoHashHex.Equals("abcdef0123456789abcdef0123456789abcdef01", StringComparison.OrdinalIgnoreCase),
                err ?? info?.InfoHashHex);
        }

        // V3-1.03: Valid btih 32-char Base32 (RFC 4648 A-Z2-7) -> 0102030405060708090a0b0c0d0e0f1011121314
        {
            string uri = "magnet:?xt=urn:btih:AEBAGBAFAYDQQCIKBMGA2DQPCAIREEYU";
            bool ok = MagnetValidator.TryParse(uri, out var info, out var err);
            Check("V3-1.03 Valid btih 32-char Base32",
                ok && info != null
                   && info.InfoHashHex.Equals("0102030405060708090a0b0c0d0e0f1011121314", StringComparison.OrdinalIgnoreCase),
                err ?? info?.InfoHashHex);
        }

        // V3-1.04: Valid btmh v2 multihash hex (1220 + 64 hex)
        {
            string sha256Hex = new string('a', 64);
            string uri = "magnet:?xt=urn:btmh:1220" + sha256Hex + "&dn=HybridV2";
            bool ok = MagnetValidator.TryParse(uri, out var info, out var err);
            Check("V3-1.04 Valid btmh v2 multihash hex",
                ok && info != null && info.InfoHashHex.Contains(sha256Hex, StringComparison.OrdinalIgnoreCase),
                err ?? info?.InfoHashHex);
        }

        // V3-1.05: Reject null, empty, whitespace
        {
            bool r1 = MagnetValidator.TryParse(null, out _, out _);
            bool r2 = MagnetValidator.TryParse("", out _, out _);
            bool r3 = MagnetValidator.TryParse("   \t\r\n ", out _, out _);
            Check("V3-1.05 Reject null, empty, whitespace", !r1 && !r2 && !r3);
        }

        // V3-1.06: Reject non-magnet scheme
        {
            bool r1 = MagnetValidator.TryParse("https://example.com/?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", out _, out _);
            bool r2 = MagnetValidator.TryParse("http://example.com/file.torrent", out _, out _);
            bool r3 = MagnetValidator.TryParse("file:///C:/test.torrent", out _, out _);
            Check("V3-1.06 Reject non-magnet scheme", !r1 && !r2 && !r3);
        }

        // V3-1.07: Reject missing xt parameter
        {
            bool ok = MagnetValidator.TryParse("magnet:?dn=NoHashTorrent&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337", out _, out _);
            Check("V3-1.07 Reject missing xt parameter", !ok);
        }

        // V3-1.08: Reject unsupported URN in xt
        {
            bool ok = MagnetValidator.TryParse("magnet:?xt=urn:ed2k:354B15E68FB8F36D7CD88FF94116CDC1", out _, out _);
            Check("V3-1.08 Reject unsupported URN in xt", !ok);
        }

        // V3-1.09: Reject btih wrong lengths (39, 41, 31, 33)
        {
            bool r39 = MagnetValidator.TryParse("magnet:?xt=urn:btih:" + new string('a', 39), out _, out _);
            bool r41 = MagnetValidator.TryParse("magnet:?xt=urn:btih:" + new string('a', 41), out _, out _);
            bool r31 = MagnetValidator.TryParse("magnet:?xt=urn:btih:" + new string('A', 31), out _, out _);
            bool r33 = MagnetValidator.TryParse("magnet:?xt=urn:btih:" + new string('A', 33), out _, out _);
            Check("V3-1.09 Reject btih wrong lengths (39, 41, 31, 33)", !r39 && !r41 && !r31 && !r33);
        }

        // V3-1.10: Reject btih 40-char with non-hex char ('g')
        {
            bool ok = MagnetValidator.TryParse("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef0123456g", out _, out _);
            Check("V3-1.10 Reject btih 40-char with non-hex char", !ok);
        }

        // V3-1.11: Reject btih 32-char with invalid Base32 chars ('0', '1', '8', '9')
        {
            bool ok = MagnetValidator.TryParse("magnet:?xt=urn:btih:0189GBAFAYDQQCIKBMGA2DQPCAIREEYU", out _, out _);
            Check("V3-1.11 Reject btih 32-char with invalid Base32 chars", !ok);
        }

        // V3-1.12: Reject btmh without 1220 SHA-256 prefix or wrong length
        {
            bool r1 = MagnetValidator.TryParse("magnet:?xt=urn:btmh:1220abcd", out _, out _);
            bool r2 = MagnetValidator.TryParse("magnet:?xt=urn:btmh:9999" + new string('a', 64), out _, out _);
            Check("V3-1.12 Reject btmh with invalid prefix or length", !r1 && !r2);
        }

        // V3-1.13: Tracker filtering: keep udp/http/https, strip file/javascript/ws/ftp/malformed
        {
            string uri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"
                + "&tr=udp%3A%2F%2Ftracker.example.com%3A6969%2Fannounce"
                + "&tr=http%3A%2F%2Ftracker.example.org%3A8080%2Fannounce"
                + "&tr=https%3A%2F%2Ftracker.example.net%2Fannounce"
                + "&tr=file%3A%2F%2F%2FC%3A%2FWindows%2Fwin.ini"
                + "&tr=javascript%3Aalert(1)"
                + "&tr=ws%3A%2F%2Fevil.example.com%2Fws"
                + "&tr=ftp%3A%2F%2Fftp.example.com%2Fannounce"
                + "&tr=not_a_uri";
            bool ok = MagnetValidator.TryParse(uri, out var info, out var err);
            Check("V3-1.13 Tracker scheme filtering (only udp/http/https retained)",
                ok && info != null
                   && info.Trackers.Count == 3
                   && info.Trackers.Any(t => t.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
                   && info.Trackers.Any(t => t.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   && info.Trackers.Any(t => t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)),
                $"count={info?.Trackers.Count}");
        }

        // V3-1.14: xs and as parameters: keep https://, ignore http://, file://, ftp://
        {
            string uri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"
                + "&xs=https%3A%2F%2Ftrusted.example.com%2Ft.torrent"
                + "&xs=http%3A%2F%2Finsecure.example.com%2Ft.torrent"
                + "&as=https%3A%2F%2Fcdn.example.com%2Fseed.bin"
                + "&as=file%3A%2F%2F%2Fetc%2Fpasswd"
                + "&as=ftp%3A%2F%2Ffiles.example.com%2Fseed.bin";
            bool ok = MagnetValidator.TryParse(uri, out var info, out _);
            Check("V3-1.14 xs and as parameters: only https:// retained",
                ok && info != null
                   && info.HttpsSources.Count == 2
                   && info.HttpsSources.All(s => s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)),
                $"count={info?.HttpsSources.Count}");
        }

        // V3-1.15: Magnet URI length limit (> 8192 rejected) and SSRF host filtering on tr/xs/as
        {
            string overlongUri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=" + new string('a', 9000);
            bool rejectedLong = !MagnetValidator.TryParse(overlongUri, out _, out _) && !MagnetValidator.IsMagnetUri(overlongUri);

            string ssrfUri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"
                + "&tr=http%3A%2F%2F127.0.0.1%3A8080%2Fannounce"
                + "&tr=http%3A%2F%2Flocalhost%3A8080%2Fannounce"
                + "&tr=udp%3A%2F%2F0.0.0.0%3A6969%2Fannounce"
                + "&xs=https%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data"
                + "&as=https%3A%2F%2Flocalhost%2Fseed.bin"
                + "&tr=https%3A%2F%2Ftracker.example.com%2Fannounce";
            bool parsedSsrf = MagnetValidator.TryParse(ssrfUri, out var ssrfInfo, out _);
            Check("V3-1.15 Reject >8KB magnet URI and strip loopback/link-local SSRF trackers & webseeds",
                rejectedLong && parsedSsrf && ssrfInfo != null
                && ssrfInfo.Trackers.Count == 1
                && ssrfInfo.Trackers[0].StartsWith("https://tracker.example.com", StringComparison.OrdinalIgnoreCase)
                && ssrfInfo.HttpsSources.Count == 0);
        }
    }

    // =========================================================================
    // V3-2: TorrentPaths.Sanitize & pre-start validation table
    // =========================================================================
    private static void Test_V3_2_PathSanitization(string workRoot)
    {
        Console.WriteLine("\n=== V3-2: Torrent Path Sanitization & Pre-Start Validation ===");
        string root = Path.Combine(workRoot, "downloads");
        Directory.CreateDirectory(root);
        string fullRootWithSep = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // V3-2.01: Valid single file
        {
            string res = TorrentPaths.Sanitize(root, "ubuntu-24.04-desktop-amd64.iso");
            Check("V3-2.01 Valid single file path",
                res == Path.Combine(Path.GetFullPath(root), "ubuntu-24.04-desktop-amd64.iso")
                && res.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase),
                res);
        }

        // V3-2.02: Valid nested multi-file path with forward slashes
        {
            string res = TorrentPaths.Sanitize(root, "album/disc1/01 - track.flac");
            Check("V3-2.02 Valid nested multi-file path",
                res == Path.Combine(Path.GetFullPath(root), "album", "disc1", "01 - track.flac")
                && res.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase),
                res);
        }

        // V3-2.03 .. V3-2.16: Reject unsafe paths table
        (string Id, string Desc, string Input)[] rejectCases =
        [
            ("V3-2.03", "Reject dot-dot traversal at start", "../secrets.txt"),
            ("V3-2.04", "Reject nested dot-dot traversal", "sub/../../outside.txt"),
            ("V3-2.05", "Reject backslash dot-dot traversal", @"sub\..\..\..\Windows\win.ini"),
            ("V3-2.06", "Reject trailing dot-dot segment", "sub/.."),
            ("V3-2.07", "Reject Unix rooted path", "/etc/passwd"),
            ("V3-2.08", "Reject Windows backslash rooted path", @"\Windows\System32\cmd.exe"),
            ("V3-2.09", "Reject UNC path", @"\\attacker.example.com\share\payload.dll"),
            ("V3-2.10", "Reject drive-letter absolute & relative paths", @"C:\evil.exe"),
            ("V3-2.10b", "Reject drive-relative path", "C:evil.exe"),
            ("V3-2.11", "Reject NTFS Alternate Data Stream (ADS)", "readme.txt:Zone.Identifier"),
            ("V3-2.11b", "Reject NTFS ADS on folder or $DATA", "dir:ads/file.txt"),
            ("V3-2.12a", "Reject reserved DOS device name CON", "CON"),
            ("V3-2.12b", "Reject reserved DOS device name with extension prn.txt", "prn.txt"),
            ("V3-2.12c", "Reject reserved DOS device name with multi-ext nul.tar.gz", "nul.tar.gz"),
            ("V3-2.12d", "Reject reserved DOS device name in subfolder COM1", "sub/COM1/file.txt"),
            ("V3-2.12e", "Reject reserved DOS device name LPT9.doc", "LPT9.doc"),
            ("V3-2.13a", "Reject segment with trailing dot", "folder./file.txt"),
            ("V3-2.13b", "Reject segment with trailing space", "folder/file.txt "),
            ("V3-2.13c", "Reject segment with leading space or dot-only", " /file.txt"),
            ("V3-2.14", "Reject overlong segment (> 200 chars)", new string('a', 201) + "/file.txt"),
            ("V3-2.15a", "Reject null byte in path", "good\0evil.exe"),
            ("V3-2.15b", "Reject control char newline in path", "line\nbreak.txt"),
            ("V3-2.16", "Reject invalid filename characters (< > | ? *)", "bad<name>.txt"),
        ];

        foreach (var (id, desc, input) in rejectCases)
        {
            bool threw = ExpectThrows<DownloadException>(() => TorrentPaths.Sanitize(root, input));
            Check($"{id} {desc}", threw, $"input={input.Replace("\0", "\\0").Replace("\n", "\\n")}");
        }

        // V3-2.17: Cap 10,000 files per torrent
        {
            var okList = Enumerable.Range(0, 10_000).Select(i => $"sub/file_{i}.bin").ToList();
            var tooMany = Enumerable.Range(0, 10_001).Select(i => $"sub/file_{i}.bin").ToList();

            TorrentPaths.ValidateFileList(root, okList);
            bool threwTooMany = ExpectThrows<DownloadException>(() => TorrentPaths.ValidateFileList(root, tooMany));
            bool threwEmpty = ExpectThrows<DownloadException>(() => TorrentPaths.ValidateFileList(root, Array.Empty<string>()));
            Check("V3-2.17 Cap 10,000 files per torrent (accepts 10,000; rejects 10,001 and 0)",
                threwTooMany && threwEmpty);
        }

        // V3-2.18: Executable detection table
        {
            string[] dangerous = [".exe", ".msi", ".bat", ".cmd", ".ps1", ".scr", ".vbs", ".js", ".hta", ".com", ".pif", ".jar"];
            string[] safe = [".txt", ".mp4", ".iso", ".flac", ".mkv", ".pdf", ".zip"];

            bool allDangerousDetected = dangerous.All(ext =>
                TorrentPaths.IsExecutable($"folder/installer{ext}") &&
                TorrentPaths.IsExecutable($"folder/INSTALLER{ext.ToUpperInvariant()}") &&
                TorrentPaths.IsExecutable($"folder/installer{ext}."));
            bool allSafeIgnored = safe.All(ext => !TorrentPaths.IsExecutable($"folder/media{ext}"));

            Check("V3-2.18 Executable detection for all 12 dangerous extensions",
                allDangerousDetected && allSafeIgnored);
        }

        // V3-2.19: Allow 195-char segment (<= 200) and valid dotfile (.gitignore), reject bidi override (\u202E)
        {
            string seg195 = new string('x', 191) + ".bin"; // 195 chars
            string res195 = TorrentPaths.Sanitize(root, seg195);
            string resDot = TorrentPaths.Sanitize(root, "repo/.gitignore");
            bool threwBidi = ExpectThrows<DownloadException>(() => TorrentPaths.Sanitize(root, "invoice\u202Efdp.exe"));
            Check("V3-2.19 Allow 195-char segment & .gitignore, reject Unicode bidi override",
                res195.EndsWith(seg195, StringComparison.Ordinal)
                && resDot.EndsWith(".gitignore", StringComparison.Ordinal)
                && threwBidi);
        }

        // V3-2.20: Reject duplicate case-insensitive normalized paths in ValidateFileList
        {
            bool threwDupCase = ExpectThrows<DownloadException>(() =>
                TorrentPaths.ValidateFileList(root, ["sub/payload.exe", "sub/PAYLOAD.exe"]));
            bool threwDupSlash = ExpectThrows<DownloadException>(() =>
                TorrentPaths.ValidateFileList(root, ["sub/file.bin", @"sub\file.bin"]));
            Check("V3-2.20 Reject duplicate case-insensitive normalized paths in ValidateFileList",
                threwDupCase && threwDupSlash);
        }
    }

    // =========================================================================
    // V3-3: File selection & priority mapping
    // =========================================================================
    private static void Test_V3_3_FileSelectionAndPriority()
    {
        Console.WriteLine("\n=== V3-3: File Selection & Priority Mapping ===");

        // V3-3.01: Priority enum bi-directional mapping
        {
            bool ok = TorrentSelection.ToMonoTorrentPriority(TorrentFilePriority.DoNotDownload) == Priority.DoNotDownload
                   && TorrentSelection.ToMonoTorrentPriority(TorrentFilePriority.Low) == Priority.Low
                   && TorrentSelection.ToMonoTorrentPriority(TorrentFilePriority.Normal) == Priority.Normal
                   && TorrentSelection.ToMonoTorrentPriority(TorrentFilePriority.High) == Priority.High
                   && TorrentSelection.FromMonoTorrentPriority(Priority.DoNotDownload) == TorrentFilePriority.DoNotDownload
                   && TorrentSelection.FromMonoTorrentPriority(Priority.Low) == TorrentFilePriority.Low
                   && TorrentSelection.FromMonoTorrentPriority(Priority.Normal) == TorrentFilePriority.Normal
                   && TorrentSelection.FromMonoTorrentPriority(Priority.High) == TorrentFilePriority.High;
            Check("V3-3.01 Priority enum bi-directional mapping", ok);
        }

        // V3-3.02: Partial file selection size calculation
        {
            var entries = new List<TorrentFileEntry>
            {
                new(0, "a.bin", 1000, TorrentFilePriority.High),
                new(1, "b.bin", 2500, TorrentFilePriority.DoNotDownload),
                new(2, "c.bin", 500, TorrentFilePriority.Low),
                new(3, "d.bin", 4000, TorrentFilePriority.Normal),
            };
            long selectedBytes = TorrentSelection.ComputeSelectedSize(entries);
            var selectedIndices = TorrentSelection.GetSelectedIndices(entries);
            Check("V3-3.02 Partial file selection size calculation",
                selectedBytes == 5500 && selectedIndices.SequenceEqual([0, 2, 3]),
                $"selectedBytes={selectedBytes}");
        }

        // V3-3.03: Select None / Select All toggle
        {
            var entries = new List<TorrentFileEntry>
            {
                new(0, "a.bin", 1000, TorrentFilePriority.Normal),
                new(1, "b.bin", 2500, TorrentFilePriority.Normal),
                new(2, "c.bin", 500, TorrentFilePriority.Normal),
                new(3, "d.bin", 4000, TorrentFilePriority.Normal),
            };
            TorrentSelection.SelectNone(entries);
            long noneBytes = TorrentSelection.ComputeSelectedSize(entries);
            bool allSkipped = entries.All(e => !e.Selected && e.Priority == TorrentFilePriority.DoNotDownload);

            TorrentSelection.SelectAll(entries);
            long allBytes = TorrentSelection.ComputeSelectedSize(entries);
            bool allNormal = entries.All(e => e.Selected && e.Priority == TorrentFilePriority.Normal);

            Check("V3-3.03 Select None / Select All toggle",
                noneBytes == 0 && allSkipped && allBytes == 8000 && allNormal,
                $"none={noneBytes}, all={allBytes}");
        }

        // V3-3.04: Executable warning flag on file entries
        {
            var entries = new List<TorrentFileEntry>
            {
                new(0, "movie.mkv", 1_000_000, TorrentFilePriority.Normal),
                new(1, "codec_setup.exe", 50_000, TorrentFilePriority.Normal),
                new(2, "run.bat", 1_200, TorrentFilePriority.DoNotDownload),
                new(3, "subs.srt", 40_000, TorrentFilePriority.Normal),
            };
            bool flagsOk = !entries[0].IsExecutable
                        && entries[1].IsExecutable
                        && entries[2].IsExecutable
                        && !entries[3].IsExecutable;
            bool warnWhenExeSelected = TorrentSelection.HasSelectedExecutable(entries);
            entries[1].Selected = false;
            bool noWarnWhenExeDeselected = !TorrentSelection.HasSelectedExecutable(entries);

            Check("V3-3.04 Executable warning flag on file entries",
                flagsOk && warnWhenExeSelected && noWarnWhenExeDeselected);
        }
    }

    // =========================================================================
    // V3-4: Fast-resume persistence & load round-trip
    // =========================================================================
    private static void Test_V3_4_FastResumePersistence()
    {
        Console.WriteLine("\n=== V3-4: Fast-Resume Persistence & Load Round-Trip ===");

        string infoHashHex = "0123456789abcdef0123456789abcdef01234567";
        var infoHashes = new InfoHashes(InfoHash.FromHex(infoHashHex), null);
        var bitfield = new BitField(32).SetAll(false);
        bitfield[0] = true;
        bitfield[5] = true;
        bitfield[31] = true;
        var unhandled = new BitField(32).SetAll(false);
        var fastResume = new FastResume(infoHashes, bitfield, unhandled);
        byte[] encoded = fastResume.Encode();

        // V3-4.01: Save and load round-trip under AppPaths.LocalDir\torrent\
        {
            string savedPath = TorrentFastResumeStore.SaveFastResume(infoHashHex, encoded);
            bool underLocalDir = savedPath.StartsWith(
                Path.GetFullPath(AppPaths.LocalDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            bool loadedOk = TorrentFastResumeStore.TryLoadFastResume(infoHashHex, out var reloaded)
                         && reloaded != null
                         && FastResume.TryLoad(new MemoryStream(reloaded), out var parsedResume)
                         && parsedResume != null
                         && parsedResume.Bitfield[0]
                         && parsedResume.Bitfield[5]
                         && parsedResume.Bitfield[31]
                         && !parsedResume.Bitfield[1];

            Check("V3-4.01 Fast-resume save & load round-trip under AppPaths.LocalDir",
                underLocalDir && loadedOk, savedPath);
        }

        // V3-4.02: InfoHash path traversal guard
        {
            bool threw1 = ExpectThrows<ArgumentException>(() => TorrentFastResumeStore.SaveFastResume("../evil", encoded));
            bool threw2 = ExpectThrows<ArgumentException>(() => TorrentFastResumeStore.SaveFastResume(@"..\..\evil", encoded));
            bool loadBad = TorrentFastResumeStore.TryLoadFastResume("../evil", out _);
            Check("V3-4.02 Fast-resume infoHash path traversal guard",
                threw1 && threw2 && !loadBad);
        }

        // V3-4.03: Corrupt fast-resume recovery
        {
            string path = TorrentFastResumeStore.GetFastResumeFilePath(infoHashHex);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0xFF, 0x00, 0x64, 0x13]);
            bool ok = TorrentFastResumeStore.TryLoadFastResume(infoHashHex, out _);
            Check("V3-4.03 Corrupt fast-resume recovery returns false without throwing",
                !ok && !File.Exists(path));
        }

        // V3-4.04: DeleteFastResume cleans up on cancel/remove
        {
            string path = TorrentFastResumeStore.SaveFastResume(infoHashHex, encoded);
            bool existedBefore = File.Exists(path);
            TorrentFastResumeStore.DeleteFastResume(infoHashHex);
            bool deletedAfter = !File.Exists(path);
            Check("V3-4.04 DeleteFastResume cleans up fast-resume file",
                existedBefore && deletedAfter);
        }
    }

    // =========================================================================
    // V3-5: TorrentState -> DownloadStatus mapping & seeding stop policy
    // =========================================================================
    private static void Test_V3_5_StateMappingAndSeedingPolicy()
    {
        Console.WriteLine("\n=== V3-5: TorrentState Mapping & Seeding Stop Policy ===");

        // V3-5.01: Metadata & Starting -> Connecting
        Check("V3-5.01 State mapping: Metadata & Starting -> Connecting",
            TorrentPolicy.MapStatus(TorrentState.Metadata, isComplete: false) == DownloadStatus.Connecting &&
            TorrentPolicy.MapStatus(TorrentState.Starting, isComplete: false) == DownloadStatus.Connecting &&
            TorrentPolicy.MapStatus(TorrentState.Hashing, isComplete: false) == DownloadStatus.Connecting);

        // V3-5.02: Downloading -> Downloading
        Check("V3-5.02 State mapping: Downloading -> Downloading",
            TorrentPolicy.MapStatus(TorrentState.Downloading, isComplete: false) == DownloadStatus.Downloading);

        // V3-5.03: Seeding or Complete -> Completed
        Check("V3-5.03 State mapping: Seeding or Complete -> Completed",
            TorrentPolicy.MapStatus(TorrentState.Seeding, isComplete: true) == DownloadStatus.Completed &&
            TorrentPolicy.MapStatus(TorrentState.Downloading, isComplete: true) == DownloadStatus.Completed &&
            TorrentPolicy.MapStatus(TorrentState.Stopped, isComplete: true) == DownloadStatus.Completed);

        // V3-5.04: Stopped/Paused (incomplete) -> Paused, Error -> Error
        Check("V3-5.04 State mapping: Paused/Stopped -> Paused, Error -> Error",
            TorrentPolicy.MapStatus(TorrentState.Paused, isComplete: false) == DownloadStatus.Paused &&
            TorrentPolicy.MapStatus(TorrentState.Stopped, isComplete: false) == DownloadStatus.Paused &&
            TorrentPolicy.MapStatus(TorrentState.Error, isComplete: false) == DownloadStatus.Error);

        // V3-5.05: Seeding stop policy below limits
        Check("V3-5.05 Seeding stop policy: below ratio (0.5 < 1.0) and time (30m < 60m) continues seeding",
            !TorrentPolicy.ShouldStopSeeding(
                downloadedBytes: 10_000_000,
                uploadedBytes: 5_000_000,
                seedingElapsed: TimeSpan.FromMinutes(30),
                ratioLimit: 1.0,
                minutesLimit: 60));

        // V3-5.06: Seeding stop policy reaches ratio >= 1.0
        Check("V3-5.06 Seeding stop policy: stops when ratio >= 1.0",
            TorrentPolicy.ShouldStopSeeding(
                downloadedBytes: 10_000_000,
                uploadedBytes: 10_000_000,
                seedingElapsed: TimeSpan.FromMinutes(12),
                ratioLimit: 1.0,
                minutesLimit: 60));

        // V3-5.07: Seeding stop policy reaches time >= 60m
        Check("V3-5.07 Seeding stop policy: stops when elapsed >= 60 minutes",
            TorrentPolicy.ShouldStopSeeding(
                downloadedBytes: 10_000_000,
                uploadedBytes: 2_000_000,
                seedingElapsed: TimeSpan.FromMinutes(60),
                ratioLimit: 1.0,
                minutesLimit: 60));

        // V3-5.08: Seeding stop policy 0 ratio or 0 minutes means don't seed
        Check("V3-5.08 Seeding stop policy: 0 ratio limit or 0 time limit stops immediately",
            TorrentPolicy.ShouldStopSeeding(10_000_000, 0, TimeSpan.Zero, ratioLimit: 0.0, minutesLimit: 60) &&
            TorrentPolicy.ShouldStopSeeding(10_000_000, 0, TimeSpan.Zero, ratioLimit: 1.0, minutesLimit: 0));

        // V3-5.09: Listen port 0 selects a random port in [49152, 65535]; explicit port is preserved
        int p0 = TorrentService.ResolveListenPort(0);
        int pExplicit = TorrentService.ResolveListenPort(51413);
        Check("V3-5.09 ResolveListenPort(0) picks random port in 49152-65535 and preserves explicit port",
            p0 >= 49152 && p0 <= 65535 && pExplicit == 51413,
            $"p0={p0}, pExplicit={pExplicit}");
    }

    // =========================================================================
    // V3-6: magnet: URL protocol registration via IMagnetRegistry
    // =========================================================================
    private static void Test_V3_6_MagnetProtocolRegistration()
    {
        Console.WriteLine("\n=== V3-6: magnet: Protocol Registration ===");

        var fakeReg = new FakeMagnetRegistry();
        var registrar = new MagnetProtocolRegistrar(fakeReg);
        string exePath = @"C:\Program Files\Golan\GDM\GDM.exe";

        // V3-6.01: Register writes HKCU\Software\Classes\magnet keys and values
        registrar.Register(exePath);
        string? urlProto = fakeReg.GetValue(@"Software\Classes\magnet", "URL Protocol");
        string? icon = fakeReg.GetValue(@"Software\Classes\magnet\DefaultIcon", null);
        string? cmd = fakeReg.GetValue(@"Software\Classes\magnet\shell\open\command", null);
        Check("V3-6.01 Register writes HKCU\\Software\\Classes\\magnet keys & command",
            urlProto == ""
            && icon == $"\"{exePath}\",0"
            && cmd == $"\"{exePath}\" \"%1\""
            && registrar.IsRegistered(exePath),
            cmd);

        // V3-6.02: Unregister removes magnet protocol registration
        registrar.Unregister(exePath);
        Check("V3-6.02 Unregister removes magnet protocol registration",
            !fakeReg.KeyExists(@"Software\Classes\magnet") && !registrar.IsRegistered(exePath));

        // V3-6.03: Quote-injection / relative exePath rejection
        bool threwQuote = ExpectThrows<ArgumentException>(() => registrar.Register("C:\\evil\" --flag.exe"));
        bool threwRel = ExpectThrows<ArgumentException>(() => registrar.Register("relative\\GDM.exe"));
        Check("V3-6.03 Reject quote injection or relative exePath in Register",
            threwQuote && threwRel);

        // V3-6.04: Reject ADS colon, dot-dot traversal, and non-.exe in FormatCommand / Register
        bool threwAds = ExpectThrows<ArgumentException>(() => MagnetProtocolRegistrar.FormatCommand(@"C:\GDM\GDM.exe:stream.exe"));
        bool threwTraversal = ExpectThrows<ArgumentException>(() => MagnetProtocolRegistrar.FormatCommand(@"C:\GDM\..\evil.exe"));
        bool threwNonExe = ExpectThrows<ArgumentException>(() => MagnetProtocolRegistrar.FormatCommand(@"C:\GDM\GDM.bat"));
        Check("V3-6.04 FormatCommand rejects ADS colons, dot-dot traversal, and non-.exe paths",
            threwAds && threwTraversal && threwNonExe);
    }

    // =========================================================================
    // V3-7: Loopback swarm integration test (3 MB file over 127.0.0.1)
    // =========================================================================
    private static async Task Test_V3_7_LoopbackSwarmAsync(string workRoot)
    {
        Console.WriteLine("\n=== V3-7: Loopback Swarm Integration Test (3 MB on 127.0.0.1) ===");

        string seedDir = Path.Combine(workRoot, "seed");
        string leechDir = Path.Combine(workRoot, "leech");
        string seederCache = Path.Combine(workRoot, "seeder_cache");
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(leechDir);
        Directory.CreateDirectory(seederCache);

        // 1. Create a 3 MB random file
        const int fileSize = 3 * 1024 * 1024;
        string fileName = "payload_3mb.bin";
        string seedFilePath = Path.Combine(seedDir, fileName);
        byte[] payload = new byte[fileSize];
        RandomNumberGenerator.Fill(payload);
        await File.WriteAllBytesAsync(seedFilePath, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        // 2. Build a .torrent with MonoTorrent.TorrentCreator
        string torrentPath = Path.Combine(workRoot, "payload_3mb.torrent");
        var creator = new TorrentCreator(TorrentType.V1Only)
        {
            PieceLength = 64 * 1024, // 64 KB pieces
            Private = false
        };
        await creator.CreateAsync(new TorrentFileSource(seedFilePath), torrentPath);

        // 3. Start a seeder ClientEngine bound strictly to 127.0.0.1 with an ephemeral loopback port
        int seederPort = TorrentService.GetFreeLoopbackPort();
        var seederSettings = new EngineSettingsBuilder
        {
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            CacheDirectory = seederCache,
            DhtEndPoint = null,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Loopback, seederPort)
            }
        }.ToSettings();

        using var seederEngine = new ClientEngine(seederSettings);
        var torrent = await Torrent.LoadAsync(torrentPath);
        var seederTorrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = false,
            AllowPeerExchange = false,
            CreateContainingDirectory = false
        }.ToSettings();

        var seederManager = await seederEngine.AddAsync(torrent, seedDir, seederTorrentSettings);
        await seederManager.HashCheckAsync(autoStart: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (seederManager.State != TorrentState.Seeding && !cts.Token.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token);
        }

        // 4. Download with TorrentRunner on 127.0.0.1 (DHT off, LSD off, explicit loopback peer, stop seeding immediately)
        var settings = new AppSettings
        {
            TorrentDhtEnabled = false,
            TorrentLsdEnabled = false,
            TorrentPexEnabled = false,
            TorrentPortForwarding = false,
            StopSeedingAtRatio = 0.0,
            StopSeedingAfterMinutes = 0
        };

        var item = new DownloadItem
        {
            FileName = fileName,
            Url = torrentPath,
            TorrentSource = torrentPath,
            TorrentFilePath = torrentPath,
            SaveDir = leechDir,
            Kind = DownloadKind.Torrent,
            TorrentStopRatio = 0.0,
            TorrentStopMinutes = 0
        };

        var explicitPeer = new IPEndPoint(IPAddress.Loopback, seederPort);
        await TorrentRunner.DownloadAsync(item, settings, _ => { }, cts.Token, loopbackOnly: true, explicitPeers: [explicitPeer]);

        await seederManager.StopAsync(TimeSpan.FromSeconds(2));
        await seederEngine.StopAllAsync(TimeSpan.FromSeconds(2));

        // 5. Verify downloaded file, SHA-256, DownloadItem status, and Mark of the Web
        string downloadedFilePath = Path.Combine(leechDir, fileName);
        bool exists = File.Exists(downloadedFilePath);
        long actualLen = exists ? new FileInfo(downloadedFilePath).Length : -1;
        string actualSha256 = exists ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(downloadedFilePath, cts.Token))) : "";

        Check("V3-7.01 Loopback swarm download finishes with Completed status within 60s",
            item.Status == DownloadStatus.Completed && exists,
            $"status={item.Status}, err={item.ErrorMessage}");

        Check("V3-7.02 Loopback swarm downloaded 3 MB file SHA-256 matches original",
            actualLen == fileSize && string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase),
            $"len={actualLen}, sha256={actualSha256[..Math.Min(16, actualSha256.Length)]}...");

        Check("V3-7.03 DownloadItem TorrentInfoHash and byte counters updated accurately",
            item.Downloaded == fileSize
            && item.TotalSize == fileSize
            && string.Equals(item.TorrentInfoHash, torrent.InfoHashes.V1?.ToHex(), StringComparison.OrdinalIgnoreCase),
            $"downloaded={item.Downloaded}, infoHash={item.TorrentInfoHash}");

        string zoneFile = downloadedFilePath + ":Zone.Identifier";
        bool motwExists = File.Exists(zoneFile);
        string motwContent = motwExists ? File.ReadAllText(zoneFile) : "";
        Check("V3-7.04 Mark of the Web (:Zone.Identifier) applied to completed torrent file",
            motwExists && motwContent.Contains("[ZoneTransfer]") && motwContent.Contains("ZoneId=3"),
            motwContent.Trim().Replace("\r\n", " "));

        // 6. Verify RemoveTorrentArtifactsAsync(item, deleteDownloadedFiles: true) deletes downloaded file while preserving SaveDir
        string bystanderFile = Path.Combine(leechDir, "keep_me.txt");
        await File.WriteAllTextAsync(bystanderFile, "bystander", cts.Token);
        await TorrentService.RemoveTorrentArtifactsAsync(item, deleteDownloadedFiles: true);
        Check("V3-7.05 RemoveTorrentArtifactsAsync deletes downloaded torrent file while preserving SaveDir and unrelated files",
            !File.Exists(downloadedFilePath) && Directory.Exists(leechDir) && File.Exists(bystanderFile));
    }

    // =========================================================================
    // V3-8: Settings.V3, DownloadItem serialization & i18n parity
    // =========================================================================
    private static void Test_V3_8_SettingsSerializationAndI18n()
    {
        Console.WriteLine("\n=== V3-8: Settings.V3, DownloadItem Serialization & i18n Parity ===");

        // V3-8.01: AppSettings V3 defaults and JSON round-trip
        {
            var s = new AppSettings();
            bool defaultsOk = s.TorrentListenPort == 0
                           && s.TorrentDhtEnabled
                           && s.TorrentPexEnabled
                           && s.TorrentLsdEnabled
                           && s.TorrentEncryptionPreferred
                           && !s.TorrentPortForwarding
                           && s.TorrentMaxConnections == 200
                           && Math.Abs(s.StopSeedingAtRatio - 1.0) < 1e-9
                           && s.StopSeedingAfterMinutes == 60
                           && s.MagnetMetadataTimeoutMinutes == 5
                           && !s.RegisterMagnetProtocol;

            s.TorrentListenPort = 51413;
            s.TorrentDhtEnabled = false;
            s.TorrentPexEnabled = false;
            s.TorrentLsdEnabled = false;
            s.TorrentEncryptionPreferred = false;
            s.TorrentPortForwarding = true;
            s.TorrentMaxConnections = 120;
            s.StopSeedingAtRatio = 2.5;
            s.StopSeedingAfterMinutes = 90;
            s.MagnetMetadataTimeoutMinutes = 8;
            s.RegisterMagnetProtocol = true;

            string json = JsonSerializer.Serialize(s, Json.Options);
            var s2 = JsonSerializer.Deserialize<AppSettings>(json, Json.Options)!;
            bool roundTripOk = s2.TorrentListenPort == 51413
                            && !s2.TorrentDhtEnabled
                            && !s2.TorrentPexEnabled
                            && !s2.TorrentLsdEnabled
                            && !s2.TorrentEncryptionPreferred
                            && s2.TorrentPortForwarding
                            && s2.TorrentMaxConnections == 120
                            && Math.Abs(s2.StopSeedingAtRatio - 2.5) < 1e-9
                            && s2.StopSeedingAfterMinutes == 90
                            && s2.MagnetMetadataTimeoutMinutes == 8
                            && s2.RegisterMagnetProtocol;

            Check("V3-8.01 AppSettings V3 defaults & JSON round-trip", defaultsOk && roundTripOk);
        }

        // V3-8.02: DownloadItem Torrent fields JSON round-trip
        {
            var item = new DownloadItem
            {
                FileName = "Linux.iso",
                Kind = DownloadKind.Torrent,
                TorrentInfoHash = "0123456789abcdef0123456789abcdef01234567",
                TorrentSource = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                TorrentFilePath = @"C:\temp\linux.torrent",
                TorrentSequential = true,
                TorrentSelectedFiles = [0, 2, 5],
                TorrentFilePriorities = new Dictionary<int, TorrentFilePriority>
                {
                    [0] = TorrentFilePriority.High,
                    [2] = TorrentFilePriority.Low,
                    [5] = TorrentFilePriority.Normal
                },
                TorrentUploadedBytes = 1_048_576,
                TorrentStopRatio = 1.5,
                TorrentStopMinutes = 120,
                TorrentSeeds = 14,
                TorrentPeers = 28,
                TorrentRatio = 0.75,
                TorrentUploadSpeed = 512_000
            };

            string json = JsonSerializer.Serialize(item, Json.Options);
            var copy = JsonSerializer.Deserialize<DownloadItem>(json, Json.Options)!;

            bool persistedOk = copy.Kind == DownloadKind.Torrent
                            && copy.TorrentInfoHash == item.TorrentInfoHash
                            && copy.TorrentSource == item.TorrentSource
                            && copy.TorrentFilePath == item.TorrentFilePath
                            && copy.TorrentSequential
                            && copy.TorrentSelectedFiles != null && copy.TorrentSelectedFiles.SequenceEqual([0, 2, 5])
                            && copy.TorrentFilePriorities != null
                            && copy.TorrentFilePriorities[0] == TorrentFilePriority.High
                            && copy.TorrentFilePriorities[2] == TorrentFilePriority.Low
                            && copy.TorrentUploadedBytes == 1_048_576
                            && copy.TorrentStopRatio == 1.5
                            && copy.TorrentStopMinutes == 120;

            bool liveIgnoredOk = copy.TorrentSeeds == 0
                              && copy.TorrentPeers == 0
                              && copy.TorrentRatio == 0.0
                              && copy.TorrentUploadSpeed == 0;

            Check("V3-8.02 DownloadItem Torrent fields JSON round-trip & [JsonIgnore] live stats",
                persistedOk && liveIgnoredOk);
        }

        // V3-8.03: i18n parity between v3.en.json and v3.ar.json
        {
            string i18nDir = FindI18nDirectory();
            string enFile = Path.Combine(i18nDir, "torrent.en.json");
            string arFile = Path.Combine(i18nDir, "torrent.ar.json");

            bool filesExist = File.Exists(enFile) && File.Exists(arFile);
            Dictionary<string, string>? enDict = filesExist
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enFile, Encoding.UTF8))
                : null;
            Dictionary<string, string>? arDict = filesExist
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(arFile, Encoding.UTF8))
                : null;

            bool keysMatch = enDict != null && arDict != null
                          && enDict.Count >= 20
                          && enDict.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(arDict.Keys)
                          && enDict.Keys.All(k => k.StartsWith("torrent.", StringComparison.Ordinal))
                          && enDict.Values.All(v => !string.IsNullOrWhiteSpace(v))
                          && arDict.Values.All(v => !string.IsNullOrWhiteSpace(v));

            Check("V3-8.03 i18n parity between v3.en.json and v3.ar.json (torrent.* keys)",
                keysMatch,
                $"en={enDict?.Count ?? 0}, ar={arDict?.Count ?? 0}");

            // V3-8.04: All TorrentText.T("...") and {v3:T ...} references in src/GDM exist in v3.en.json and v3.ar.json
            string srcRoot = Path.GetFullPath(Path.Combine(i18nDir, ".."));
            var v3Refs = new HashSet<string>(StringComparer.Ordinal);
            var csRx = new System.Text.RegularExpressions.Regex(@"TorrentText\.T\(\s*""([^""]+)""");
            var xamlRx = new System.Text.RegularExpressions.Regex(@"\{v3:T\s+([^\}\s,]+)");
            foreach (string file in Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (System.Text.RegularExpressions.Match m in csRx.Matches(File.ReadAllText(file)))
                        v3Refs.Add(m.Groups[1].Value);
                }
                else if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (System.Text.RegularExpressions.Match m in xamlRx.Matches(File.ReadAllText(file)))
                        v3Refs.Add(m.Groups[1].Value);
                }
            }

            bool allRefsExist = enDict != null && arDict != null
                             && v3Refs.Count >= 20
                             && v3Refs.All(k => enDict.ContainsKey(k) && arDict.ContainsKey(k));
            Check("V3-8.04 All TorrentText.T and {v3:T} keys in C#/XAML exist in v3.en.json and v3.ar.json",
                allRefsExist,
                $"referenced={v3Refs.Count}");
        }
    }

    private static string FindI18nDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "src", "GDM", "i18n");
            if (Directory.Exists(candidate)) return candidate;
            string candidate2 = Path.Combine(dir, "GDM", "src", "GDM", "i18n");
            if (Directory.Exists(candidate2)) return candidate2;
            string candidate3 = Path.Combine(dir, "i18n");
            if (Directory.Exists(candidate3) && File.Exists(Path.Combine(candidate3, "torrent.en.json"))) return candidate3;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppContext.BaseDirectory, "i18n");
    }
}
