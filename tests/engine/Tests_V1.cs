using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;
using Microsoft.Win32;

public static class Tests_V1
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

    public static async Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Console.WriteLine("=== Package V1 Verification Suite ===");
        var workRoot = Path.Combine(Path.GetTempPath(), "gdm-v1-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        try
        {
            Test_F1_PowerGuard();
            Test_F3_SpaceCheck(workRoot);
            Test_F14_PortableMode();
            Test_F4_InstallerAndUpdater(workRoot);
            await Test_F15_Cli(workRoot);
            Test_V1_SettingsAndI18n();
        }
        catch (Exception ex)
        {
            Check("Tests_V1 unhandled exception", false, ex.ToString());
        }
        finally
        {
            try { Directory.Delete(workRoot, true); } catch { }
            Loc.Init("ar");
        }

        Console.WriteLine($"\nTests_V1 Result: {_passed} passed, {_failed} failed");
        if (_failures.Count > 0)
        {
            Console.WriteLine("Tests_V1 Failures:");
            foreach (var f in _failures) Console.WriteLine($"  - {f}");
        }
        Console.WriteLine();
        return _failed;
    }

    // =========================================================================
    // F1: PowerGuard
    // =========================================================================
    static void Test_F1_PowerGuard()
    {
        Console.WriteLine("F1. PowerGuard (state machine, 60s debounce, deduplication, dedicated thread, dispose)");

        // F1-01 .. F1-07: State machine, debounce, anti-flapping, deduplication
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var calls = new List<uint>();
        var threadIds = new HashSet<int>();
        using (var pg = new PowerGuard(() => now, flags =>
        {
            lock (calls)
            {
                calls.Add(flags);
                threadIds.Add(Environment.CurrentManagedThreadId);
            }
        }))
        {
            // F1-01: Initial idle state & inactive tick
            pg.Update(false);
            Check("F1-01: initial idle state does not invoke SetThreadExecutionState",
                pg.State == PowerGuardState.Idle && calls.Count == 0,
                $"state={pg.State}, calls={calls.Count}");

            // F1-02: Idle -> Holding on first active
            pg.Update(true);
            Check("F1-02: first active transitions Idle -> Holding and sends ES_CONTINUOUS | ES_SYSTEM_REQUIRED",
                pg.State == PowerGuardState.Holding && calls.Count == 1 && calls[0] == (PowerGuard.ES_CONTINUOUS | PowerGuard.ES_SYSTEM_REQUIRED),
                $"state={pg.State}, calls=[{string.Join(", ", calls.Select(c => $"0x{c:X8}"))}]");

            // F1-04: Deduplication of consecutive active ticks
            for (int i = 0; i < 4; i++)
            {
                now = now.AddSeconds(1);
                pg.Update(true);
            }
            Check("F1-04: repeated active ticks deduplicate identical flags",
                pg.State == PowerGuardState.Holding && calls.Count == 1,
                $"calls={calls.Count}");

            // F1-05: Holding -> Releasing(t0) on inactive, holds during < 60s
            now = new DateTime(2026, 10, 18, 12, 0, 10, DateTimeKind.Utc);
            pg.Update(false);
            bool releasingAt10 = pg.State == PowerGuardState.Releasing && calls.Count == 1;
            now = now.AddSeconds(59.9);
            pg.Update(false);
            Check("F1-05: inactive < 60s stays in Releasing without releasing execution state",
                releasingAt10 && pg.State == PowerGuardState.Releasing && calls.Count == 1,
                $"state={pg.State}, calls={calls.Count}");

            // F1-07: Active again during Releasing -> Holding (no flapping)
            now = new DateTime(2026, 10, 18, 12, 0, 40, DateTimeKind.Utc);
            pg.Update(true);
            bool backToHolding = pg.State == PowerGuardState.Holding && calls.Count == 1;
            now = new DateTime(2026, 10, 18, 12, 1, 20, DateTimeKind.Utc);
            pg.Update(false); // new t0 = 12:01:20
            now = new DateTime(2026, 10, 18, 12, 2, 10, DateTimeKind.Utc); // 50s since new t0
            pg.Update(false);
            Check("F1-07: active during Releasing returns to Holding without flapping and resets 60s timer",
                backToHolding && pg.State == PowerGuardState.Releasing && calls.Count == 1,
                $"state={pg.State}, calls={calls.Count}");

            // F1-06: Debounce expiry at >= 60s -> Idle & sends ES_CONTINUOUS
            now = new DateTime(2026, 10, 18, 12, 2, 20, DateTimeKind.Utc); // 60s since 12:01:20
            pg.Update(false);
            Check("F1-06: inactive >= 60s transitions Releasing -> Idle and sends ES_CONTINUOUS",
                pg.State == PowerGuardState.Idle && calls.Count == 2 && calls[1] == PowerGuard.ES_CONTINUOUS,
                $"state={pg.State}, calls=[{string.Join(", ", calls.Select(c => $"0x{c:X8}"))}]");
        }

        // F1-03 & F1-08 & F1-09 & F1-10: Display flag, KeepSystemAwake=false, thread affinity, Dispose
        var calls2 = new List<uint>();
        var workerThreads = new ConcurrentBag<int>();
        var callerThreadId = Environment.CurrentManagedThreadId;
        var pg2 = new PowerGuard(() => now, flags =>
        {
            lock (calls2) calls2.Add(flags);
            workerThreads.Add(Environment.CurrentManagedThreadId);
        })
        {
            KeepSystemAwake = true,
            KeepDisplayOn = true,
        };

        Task.Run(() => pg2.Update(true)).GetAwaiter().GetResult();
        Check("F1-03: KeepDisplayOn=true includes ES_DISPLAY_REQUIRED (0x80000003)",
            calls2.Count == 1 && calls2[0] == (PowerGuard.ES_CONTINUOUS | PowerGuard.ES_SYSTEM_REQUIRED | PowerGuard.ES_DISPLAY_REQUIRED),
            $"calls=[{string.Join(", ", calls2.Select(c => $"0x{c:X8}"))}]");

        pg2.KeepSystemAwake = false;
        Task.Run(() => pg2.Update(true)).GetAwaiter().GetResult();
        Check("F1-08: disabling KeepSystemAwake while holding releases state immediately",
            pg2.State == PowerGuardState.Idle && calls2.Count == 2 && calls2[1] == PowerGuard.ES_CONTINUOUS,
            $"state={pg2.State}, calls={calls2.Count}");

        pg2.KeepSystemAwake = true;
        pg2.KeepDisplayOn = false;
        Task.Run(() => pg2.Update(true)).GetAwaiter().GetResult();
        int countBeforeDispose = calls2.Count;
        pg2.Dispose();
        pg2.Dispose(); // idempotent second dispose
        pg2.Update(true); // post-dispose no-op

        var distinctThreads = workerThreads.Distinct().ToList();
        Check("F1-09: all native SetThreadExecutionState calls run on ONE dedicated background thread",
            distinctThreads.Count == 1 && distinctThreads[0] != callerThreadId,
            $"distinctThreads={distinctThreads.Count}");

        Check("F1-10: Dispose sends ES_CONTINUOUS release and ignores subsequent calls",
            calls2.Count == countBeforeDispose + 1 && calls2[^1] == PowerGuard.ES_CONTINUOUS,
            $"calls=[{string.Join(", ", calls2.Select(c => $"0x{c:X8}"))}]");
    }

    // =========================================================================
    // F3: SpaceCheck
    // =========================================================================
    static void Test_F3_SpaceCheck(string workRoot)
    {
        Console.WriteLine("F3. SpaceCheck (Evaluate table & GetDiskFreeSpaceExW FreeBytes)");
        const long MB = 1024L * 1024L;
        const long GB = 1024L * MB;

        // F3-01: Standard download with sufficient space
        var v1 = SpaceCheck.Evaluate(free: 500 * MB, total: 100 * MB, downloaded: 20 * MB, isMedia: false, tempFree: -1);
        Check("F3-01: standard download with sufficient space -> Ok, NeedBytes = remaining + 64MB",
            v1.Kind == SpaceVerdictKind.Ok && v1.NeedBytes == 144 * MB,
            $"kind={v1.Kind}, need={v1.NeedBytes}");

        // F3-02: Standard download with insufficient space
        var v2 = SpaceCheck.Evaluate(free: 140 * MB, total: 100 * MB, downloaded: 20 * MB, isMedia: false, tempFree: -1);
        Check("F3-02: standard download with free < need -> Insufficient",
            v2.Kind == SpaceVerdictKind.Insufficient && v2.NeedBytes == 144 * MB,
            $"kind={v2.Kind}, need={v2.NeedBytes}");

        // F3-03: Boundary downloaded >= total -> remaining clamped to 0, need = 64MB
        var v3 = SpaceCheck.Evaluate(free: 64 * MB, total: 100 * MB, downloaded: 120 * MB, isMedia: false, tempFree: -1);
        Check("F3-03: downloaded >= total clamps remaining to 0 and requires 64MB safety buffer",
            v3.Kind == SpaceVerdictKind.Ok && v3.NeedBytes == 64 * MB,
            $"kind={v3.Kind}, need={v3.NeedBytes}");

        // F3-04: Media download (2.2x remaining + 64MB)
        var v4Ok = SpaceCheck.Evaluate(free: 300 * MB, total: 100 * MB, downloaded: 0, isMedia: true, tempFree: -1);
        var v4Ins = SpaceCheck.Evaluate(free: 280 * MB, total: 100 * MB, downloaded: 0, isMedia: true, tempFree: -1);
        long expectedMediaNeed = (long)Math.Ceiling(100 * MB * 2.2) + 64 * MB; // 284 MB
        Check("F3-04: media download requires 2.2x remaining + 64MB",
            v4Ok.Kind == SpaceVerdictKind.Ok && v4Ins.Kind == SpaceVerdictKind.Insufficient && v4Ok.NeedBytes == expectedMediaNeed,
            $"okKind={v4Ok.Kind}, insKind={v4Ins.Kind}, need={v4Ok.NeedBytes} (expected {expectedMediaNeed})");

        // F3-05: Unknown total size (-1): Warn when free < 512MB else Ok
        var v5Warn = SpaceCheck.Evaluate(free: 511 * MB, total: -1, downloaded: 0, isMedia: false, tempFree: -1);
        var v5Ok = SpaceCheck.Evaluate(free: 512 * MB, total: -1, downloaded: 0, isMedia: false, tempFree: -1);
        Check("F3-05: unknown total size (-1) returns Warn when free < 512MB and Ok when free >= 512MB",
            v5Warn.Kind == SpaceVerdictKind.Warn && v5Ok.Kind == SpaceVerdictKind.Ok,
            $"511MB={v5Warn.Kind}, 512MB={v5Ok.Kind}");

        // F3-06: Unknown free space (-1) fails open gracefully
        var v6 = SpaceCheck.Evaluate(free: -1, total: 1000 * MB, downloaded: 0, isMedia: false, tempFree: -1);
        var v6UnknownTotal = SpaceCheck.Evaluate(free: -1, total: -1, downloaded: 0, isMedia: false, tempFree: -1);
        Check("F3-06: unknown free space (-1) returns Ok without blocking",
            v6.Kind == SpaceVerdictKind.Ok && v6UnknownTotal.Kind == SpaceVerdictKind.Ok,
            $"v6={v6.Kind}, v6UnknownTotal={v6UnknownTotal.Kind}");

        // F3-07: Separate temp drive: temp drive insufficient for remaining bytes
        var v7 = SpaceCheck.Evaluate(free: 10 * GB, total: 1 * GB, downloaded: 200 * MB, isMedia: false, tempFree: 500 * MB);
        Check("F3-07: temp on another drive with insufficient tempFree returns Insufficient",
            v7.Kind == SpaceVerdictKind.Insufficient && v7.NeedBytes == (1 * GB - 200 * MB) + 64 * MB,
            $"kind={v7.Kind}, need={v7.NeedBytes}");

        // F3-08: Separate temp drive: target drive insufficient for final file size
        var v8 = SpaceCheck.Evaluate(free: 900 * MB, total: 1 * GB, downloaded: 200 * MB, isMedia: false, tempFree: 10 * GB);
        var v8Ok = SpaceCheck.Evaluate(free: 2 * GB, total: 1 * GB, downloaded: 200 * MB, isMedia: false, tempFree: 2 * GB);
        Check("F3-08: temp on another drive checks final size on target drive and remaining on temp drive",
            v8.Kind == SpaceVerdictKind.Insufficient && v8.NeedBytes >= 1 * GB && v8Ok.Kind == SpaceVerdictKind.Ok,
            $"v8={v8.Kind} (need={v8.NeedBytes}), v8Ok={v8Ok.Kind}");

        // F3-09: FreeBytes walks up to nearest existing parent directory
        var deepNonExistent = Path.Combine(workRoot, "nonexistent_a", "nonexistent_b", "nonexistent_c");
        long freeExisting = SpaceCheck.FreeBytes(workRoot);
        long freeDeep = SpaceCheck.FreeBytes(deepNonExistent);
        Check("F3-09: FreeBytes walks up to nearest existing parent and returns positive bytes",
            freeExisting > 0 && freeDeep > 0 && Math.Abs(freeExisting - freeDeep) < 100 * MB,
            $"existing={freeExisting}, deep={freeDeep}");

        // F3-10: FreeBytes returns -1 on invalid/non-existent roots without throwing
        long bad1 = SpaceCheck.FreeBytes("");
        long bad2 = SpaceCheck.FreeBytes("   ");
        long bad3 = SpaceCheck.FreeBytes(":::invalid:::path???***");
        long bad4 = SpaceCheck.FreeBytes(@"Q:\this_drive_does_not_exist_12345\sub");
        Check("F3-10: FreeBytes returns -1 on invalid paths without throwing",
            bad1 == -1 && bad2 == -1 && bad3 == -1 && bad4 == -1,
            $"bad1={bad1}, bad2={bad2}, bad3={bad3}, bad4={bad4}");
    }

    // =========================================================================
    // F14: Portable Mode (AppPaths.ResolveHome & AppPaths.IsPortable)
    // =========================================================================
    static void Test_F14_PortableMode()
    {
        Console.WriteLine("F14. Portable Mode (AppPaths.ResolveHome & IsPortableMode)");

        // F14-01: GOLAN_HOME / GDM_HOME priority over portable.txt / GDM.portable
        var h1 = AppPaths.ResolveHome(@"D:\TestGdmHome", @"C:\Apps\GDM", _ => true);
        bool p1 = AppPaths.IsPortableMode(@"D:\TestGdmHome", @"C:\Apps\GDM", _ => true);
        string? prevGolanHome = Environment.GetEnvironmentVariable("GOLAN_HOME");
        bool golanHomePipeOk;
        try
        {
            Environment.SetEnvironmentVariable("GOLAN_HOME", @"D:\TestGolanHome");
            golanHomePipeOk = Ipc.PipeName.StartsWith("Golan.GDM.", StringComparison.Ordinal) && Ipc.PipeName.Split('.').Length >= 4;
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOLAN_HOME", prevGolanHome);
        }
        Check("F14-01: GOLAN_HOME/GDM_HOME takes priority over portable markers and IsPortableMode is false",
            h1 == @"D:\TestGdmHome" && !p1 && !AppPaths.IsPortable && golanHomePipeOk,
            $"home={h1}, isPortable={p1}, golanHomePipeOk={golanHomePipeOk}");

        // F14-02: portable.txt next to exe
        var h2 = AppPaths.ResolveHome(null, @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\portable.txt", StringComparison.OrdinalIgnoreCase));
        bool p2 = AppPaths.IsPortableMode(null, @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\portable.txt", StringComparison.OrdinalIgnoreCase));
        Check("F14-02: portable.txt next to exe resolves Home to <exeDir>\\Data and sets IsPortableMode=true",
            h2 == @"C:\Apps\GDM\Data" && p2,
            $"home={h2}, isPortable={p2}");

        // F14-03: GDM.portable / Golan.portable next to exe; legacy marker ignored
        var h3 = AppPaths.ResolveHome("", @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\GDM.portable", StringComparison.OrdinalIgnoreCase));
        bool p3 = AppPaths.IsPortableMode("", @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\GDM.portable", StringComparison.OrdinalIgnoreCase));
        bool p3Golan = AppPaths.IsPortableMode("", @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\Golan.portable", StringComparison.OrdinalIgnoreCase));
        bool p3Legacy = AppPaths.IsPortableMode("", @"C:\Apps\GDM", p => string.Equals(p, @"C:\Apps\GDM\" + "\u0041dam.portable", StringComparison.OrdinalIgnoreCase));
        Check("F14-03: GDM.portable and Golan.portable next to exe resolve Home to <exeDir>\\Data and set IsPortableMode=true while legacy marker is ignored",
            h3 == @"C:\Apps\GDM\Data" && p3 && p3Golan && !p3Legacy,
            $"home={h3}, isPortable={p3}, golan={p3Golan}, legacyIgnored={!p3Legacy}");

        // F14-04: Normal non-portable mode
        var h4 = AppPaths.ResolveHome(null, @"C:\Apps\GDM", _ => false);
        bool p4 = AppPaths.IsPortableMode(null, @"C:\Apps\GDM", _ => false);
        Check("F14-04: without GOLAN_HOME/GDM_HOME or portable marker, ResolveHome returns null and IsPortableMode=false",
            h4 == null && !p4,
            $"home={h4 ?? "null"}, isPortable={p4}");
    }

    // =========================================================================
    // F4: Installer + Updater
    // =========================================================================
    sealed class FakeInstallHost : IInstallHost
    {
        public string CurrentExePath { get; set; } = @"D:\Downloads\GDM.exe";
        public string LocalAppDataDir { get; set; } = @"C:\FakeUser\AppData\Local";
        public string StartMenuProgramsDir { get; set; } = @"C:\FakeUser\StartMenu\Programs";
        public string DesktopDir { get; set; } = @"C:\FakeUser\Desktop";
        public string AppDataRoamingDir { get; set; } = @"C:\FakeUser\AppData\Roaming\Golan\GDM";
        public string AppDataLocalDir { get; set; } = @"C:\FakeUser\AppData\Local\Golan\GDM";
        public string Version { get; set; } = "2.2.0";
        public bool IsPortable { get; set; }
        public long ExeSize { get; set; } = 4 * 1024 * 1024;

        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CreatedDirs { get; } = [];
        public List<(string Src, string Dst, bool Overwrite)> CopiedFiles { get; } = [];
        public List<string> DeletedFiles { get; } = [];
        public List<string> DeletedDirs { get; } = [];
        public List<(string Path, string Target, string WorkDir, string Desc, string Icon)> Shortcuts { get; } = [];
        public Dictionary<string, (object Value, RegistryValueKind Kind)> RegistryValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DeletedRegistryValues { get; } = [];
        public List<string> DeletedRegistryTrees { get; } = [];
        public List<string> RegisteredBrowserExes { get; } = [];
        public int UnregisterBrowserCount { get; set; }
        public List<string> ScheduledDelayedDeletes { get; } = [];

        public bool FileExists(string path) => ExistingFiles.Contains(path);
        public long GetFileSize(string path) => ExeSize;
        public void CreateDirectory(string path) => CreatedDirs.Add(path);
        public void CopyFile(string source, string destination, bool overwrite)
        {
            CopiedFiles.Add((source, destination, overwrite));
            ExistingFiles.Add(destination);
        }
        public void DeleteFile(string path)
        {
            DeletedFiles.Add(path);
            ExistingFiles.Remove(path);
        }
        public void DeleteDirectory(string path) => DeletedDirs.Add(path);
        public void CreateShortcut(string shortcutPath, string targetExePath, string workingDir, string description, string iconPath)
        {
            Shortcuts.Add((shortcutPath, targetExePath, workingDir, description, iconPath));
            ExistingFiles.Add(shortcutPath);
        }
        public void SetRegistryValue(string subKey, string? valueName, object value, RegistryValueKind kind = RegistryValueKind.String)
            => RegistryValues[$"{subKey}\\{valueName ?? "(Default)"}"] = (value, kind);
        public void DeleteRegistryValue(string subKey, string valueName)
        {
            DeletedRegistryValues.Add($"{subKey}\\{valueName}");
            RegistryValues.Remove($"{subKey}\\{valueName}");
        }
        public void DeleteRegistryKeyTree(string subKey) => DeletedRegistryTrees.Add(subKey);
        public void RegisterBrowserHosts(string exePath) => RegisteredBrowserExes.Add(exePath);
        public void UnregisterBrowserHosts() => UnregisterBrowserCount++;
        public void ScheduleDelayedDeleteDir(string folderPath) => ScheduledDelayedDeletes.Add(folderPath);
    }

    sealed class FakeUpdateApplyHost : IUpdateApplyHost
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Content { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ProcessExitsInTime { get; set; } = true;
        public bool NewBinaryStartsOk { get; set; } = true;
        public List<string> Log { get; } = [];

        public bool FileExists(string path) => Files.Contains(path);

        public bool WaitForProcessExit(int pid, TimeSpan timeout)
        {
            Log.Add($"wait:{pid}:{timeout.TotalSeconds}s");
            return ProcessExitsInTime;
        }

        public void MoveFile(string source, string destination, bool overwrite)
        {
            Log.Add($"move:{Path.GetFileName(source)}->{Path.GetFileName(destination)}");
            if (Content.TryGetValue(source, out var c))
            {
                Content.Remove(source);
                Content[destination] = c;
            }
            Files.Remove(source);
            Files.Add(destination);
        }

        public void DeleteFile(string path)
        {
            Log.Add($"del:{Path.GetFileName(path)}");
            Files.Remove(path);
            Content.Remove(path);
        }

        public bool StartAndVerifyProcess(string exePath, TimeSpan timeout)
        {
            Log.Add($"start:{Path.GetFileName(exePath)}:{timeout.TotalSeconds}s");
            return NewBinaryStartsOk;
        }
    }

    static void Test_F4_InstallerAndUpdater(string workRoot)
    {
        Console.WriteLine("F4. Installer + Updater (IInstallHost, Uninstall key, safety guard, ParseFeed, IsNewer, SHA-256, apply & rollback)");

        // F4-01: Install from external location
        var host1 = new FakeInstallHost();
        host1.ExistingFiles.Add(host1.CurrentExePath);
        var res1 = Installer.Install(host1, createDesktopShortcut: true, startWithWindows: true);
        var expectedInstallDir = @"C:\FakeUser\AppData\Local\Programs\Golan\GDM";
        var expectedTargetExe = @"C:\FakeUser\AppData\Local\Programs\Golan\GDM\GDM.exe";
        Check("F4-01: Install copies exe to %LOCALAPPDATA%\\Programs\\Golan\\GDM\\GDM.exe, creates Start-menu group & shortcuts, writes HKCU Uninstall & Run keys, registers browser hosts",
            res1 == InstallResult.Installed
            && Installer.UninstallSubKey == @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Golan.GDM"
            && Installer.RunValueName == "Golan.GDM"
            && host1.CopiedFiles.Count == 1
            && string.Equals(host1.CopiedFiles[0].Dst, expectedTargetExe, StringComparison.OrdinalIgnoreCase)
            && host1.Shortcuts.Any(s =>
                (s.Path.EndsWith(@"StartMenu\Programs\جولان\جولان - مدير التنزيل.lnk", StringComparison.OrdinalIgnoreCase)
                 || s.Path.EndsWith(@"StartMenu\Programs\Golan\Golan Download Manager.lnk", StringComparison.OrdinalIgnoreCase))
                && s.Target == expectedTargetExe)
            && host1.Shortcuts.Any(s =>
                (s.Path.EndsWith(@"Desktop\جولان - مدير التنزيل.lnk", StringComparison.OrdinalIgnoreCase)
                 || s.Path.EndsWith(@"Desktop\Golan Download Manager.lnk", StringComparison.OrdinalIgnoreCase))
                && s.Target == expectedTargetExe)
            && host1.RegistryValues.TryGetValue(Installer.UninstallSubKey + @"\DisplayName", out var dispName) && dispName.Value.ToString() == "Golan Download Manager"
            && host1.RegistryValues.ContainsKey(Installer.UninstallSubKey + @"\DisplayIcon")
            && host1.RegistryValues.TryGetValue(Installer.UninstallSubKey + @"\UninstallString", out var uninstStr)
            && uninstStr.Value.ToString() == $"\"{expectedTargetExe}\" --uninstall"
            && host1.RegistryValues.ContainsKey(Installer.UninstallSubKey + @"\QuietUninstallString")
            && host1.RegistryValues.ContainsKey(Installer.UninstallSubKey + @"\InstallLocation")
            && host1.RegistryValues.ContainsKey(Installer.UninstallSubKey + @"\DisplayVersion")
            && host1.RegistryValues.TryGetValue(Installer.UninstallSubKey + @"\Publisher", out var pub) && pub.Value.ToString() == "Golan"
            && host1.RegistryValues.TryGetValue(Installer.UninstallSubKey + @"\EstimatedSize", out var est) && est.Kind == RegistryValueKind.DWord && (int)est.Value > 0
            && host1.RegistryValues.TryGetValue(Installer.UninstallSubKey + @"\NoModify", out var nm) && (int)nm.Value == 1
            && host1.RegistryValues.TryGetValue(Installer.UninstallSubKey + @"\NoRepair", out var nr) && (int)nr.Value == 1
            && host1.RegisteredBrowserExes.Contains(expectedTargetExe)
            && host1.RegistryValues.ContainsKey(Installer.RunSubKey + @"\Golan.GDM"),
            $"res={res1}, copied={host1.CopiedFiles.Count}, regCount={host1.RegistryValues.Count}");

        // F4-02: Install when already running from target path skips self-copy
        var host2 = new FakeInstallHost { CurrentExePath = expectedTargetExe };
        host2.ExistingFiles.Add(expectedTargetExe);
        var res2 = Installer.Install(host2, createDesktopShortcut: false, startWithWindows: false);
        Check("F4-02: Install when already at target path skips self-copy and omits optional desktop shortcut / Run key",
            res2 == InstallResult.Installed
            && host2.CopiedFiles.Count == 0
            && host2.Shortcuts.Count == 1
            && !host2.RegistryValues.ContainsKey(Installer.RunSubKey + @"\Golan.GDM"),
            $"copied={host2.CopiedFiles.Count}, shortcuts={host2.Shortcuts.Count}");

        // F4-03: Portable mode never installs or writes Run key
        var hostPortable = new FakeInstallHost { IsPortable = true };
        var resPortable = Installer.Install(hostPortable, createDesktopShortcut: true, startWithWindows: true);
        Check("F4-03: Portable mode blocks installation and never writes Uninstall or Run registry keys",
            resPortable == InstallResult.PortableSkipped
            && hostPortable.CopiedFiles.Count == 0
            && hostPortable.Shortcuts.Count == 0
            && hostPortable.RegistryValues.Count == 0,
            $"res={resPortable}, regCount={hostPortable.RegistryValues.Count}");

        // F4-04: Uninstall cleans shortcuts, Uninstall key, browser hosts, Run key, and schedules delayed delete
        bool uninstOk = Installer.Uninstall(host1, removeUserData: false);
        string delayedCmd = Installer.BuildDelayedDeleteCommand(expectedInstallDir);
        Check("F4-04: Uninstall removes shortcuts, Uninstall key, browser hosts, Run value, preserves user data, and schedules delayed rmdir",
            uninstOk
            && host1.DeletedFiles.Any(f =>
                f.EndsWith(@"StartMenu\Programs\جولان\جولان - مدير التنزيل.lnk", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(@"StartMenu\Programs\Golan\Golan Download Manager.lnk", StringComparison.OrdinalIgnoreCase))
            && host1.DeletedFiles.Any(f =>
                f.EndsWith(@"Desktop\جولان - مدير التنزيل.lnk", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(@"Desktop\Golan Download Manager.lnk", StringComparison.OrdinalIgnoreCase))
            && host1.DeletedRegistryTrees.Contains(Installer.UninstallSubKey)
            && host1.DeletedRegistryValues.Contains(Installer.RunSubKey + @"\Golan.GDM")
            && typeof(Installer).GetField("LegacyUninstallSubKey") == null
            && host1.UnregisterBrowserCount == 1
            && host1.DeletedDirs.Count == 0
            && host1.ScheduledDelayedDeletes.Count == 1
            && string.Equals(host1.ScheduledDelayedDeletes[0], expectedInstallDir, StringComparison.OrdinalIgnoreCase)
            && delayedCmd.Contains("ping 127.0.0.1 -n 3 >nul", StringComparison.OrdinalIgnoreCase)
            && delayedCmd.Contains($"rmdir /s /q \"{expectedInstallDir}\"", StringComparison.OrdinalIgnoreCase),
            $"uninstOk={uninstOk}, scheduled={host1.ScheduledDelayedDeletes.Count}, cmd={delayedCmd}");

        // F4-05: Security boundary: CanDeleteInstallFolder refuses unsafe folders or folders missing GDM.exe
        bool safeFolderWithExe = Installer.CanDeleteInstallFolder(
            @"C:\Users\Test\AppData\Local\Programs\Golan\GDM",
            p => string.Equals(p, @"C:\Users\Test\AppData\Local\Programs\Golan\GDM\GDM.exe", StringComparison.OrdinalIgnoreCase));
        bool safeFolderMissingExe = Installer.CanDeleteInstallFolder(
            @"C:\Users\Test\AppData\Local\Programs\Golan\GDM",
            _ => false);
        bool systemFolder = Installer.CanDeleteInstallFolder(@"C:\Windows\System32", _ => true);
        bool desktopFolder = Installer.CanDeleteInstallFolder(@"C:\Users\Test\Desktop", _ => true);
        bool injectionFolder = Installer.CanDeleteInstallFolder(@"C:\Programs\Golan&calc\Programs\Golan\GDM", _ => true);
        Check("F4-05: CanDeleteInstallFolder enforces \\Programs\\Golan\\GDM suffix, GDM.exe presence, and blocks command injection",
            safeFolderWithExe && !safeFolderMissingExe && !systemFolder && !desktopFolder && !injectionFolder,
            $"safe={safeFolderWithExe}, missingExe={safeFolderMissingExe}, sys={systemFolder}, desk={desktopFolder}, inj={injectionFolder}");

        // F4-06: Updater.ParseFeed valid JSON
        var validSha = new string('a', 64);
        var validJson = $$"""
            {
              "version": "2.1.10",
              "url": "https://updates.golan-gdm.test/releases/GDM-2.1.10.exe",
              "sha256": "{{validSha}}",
              "notes_ar": "تحسينات في السرعة",
              "notes_en": "Speed improvements"
            }
            """;
        UpdateFeed? parsedFeed = null;
        try { parsedFeed = Updater.ParseFeed(validJson); } catch { }
        Check("F4-06: Updater.ParseFeed parses valid HTTPS feed JSON with 64-hex SHA-256 and ar/en notes",
            parsedFeed != null
            && parsedFeed.Version == "2.1.10"
            && parsedFeed.Url == "https://updates.golan-gdm.test/releases/GDM-2.1.10.exe"
            && string.Equals(parsedFeed.Sha256, validSha, StringComparison.OrdinalIgnoreCase)
            && parsedFeed.NotesAr == "تحسينات في السرعة"
            && parsedFeed.NotesEn == "Speed improvements");

        // F4-07: Updater.ParseFeed rejects invalid feeds (including loopback & cloud metadata SSRF)
        string[] invalidFeeds =
        [
            """{"version":"2.0.0","url":"http://updates.golan-gdm.test/GDM.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """{"version":"2.0.0","url":"file:///C:/GDM.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """{"version":"2.0.0","url":"https://127.0.0.1/GDM.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """{"version":"2.0.0","url":"https://localhost/GDM.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """{"version":"2.0.0","url":"https://169.254.169.254/GDM.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """{"version":"2.0.0","url":"https://updates.golan-gdm.test/GDM.exe","sha256":"abcd"}""",
            """{"version":"2.0.0","url":"https://updates.golan-gdm.test/GDM.exe","sha256":"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg"}""",
            """{"version":"","url":"https://updates.golan-gdm.test/GDM.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """{"version":"not-a-version","url":"https://updates.golan-gdm.test/GDM.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """not valid json""",
        ];
        int rejectedCount = 0;
        foreach (var bad in invalidFeeds)
        {
            try { Updater.ParseFeed(bad); }
            catch { rejectedCount++; }
        }
        Check("F4-07: Updater.ParseFeed rejects HTTP/file/loopback/metadata URLs, non-64-hex hashes, invalid versions, and malformed JSON",
            rejectedCount == invalidFeeds.Length,
            $"rejected={rejectedCount}/{invalidFeeds.Length}");

        // F4-08: Updater.IsNewer numeric per part
        Check("F4-08: Updater.IsNewer compares numeric version parts accurately (1.2.10 > 1.2.9)",
            Updater.IsNewer("1.2.9", "1.2.10")
            && Updater.IsNewer("1.0.0", "2.0.0")
            && !Updater.IsNewer("1.2.10", "1.2.9")
            && !Updater.IsNewer("1.2.0", "1.2")
            && !Updater.IsNewer("2.0.0", "1.9.99")
            && !Updater.IsNewer("1.0.0", "1.0.0"));

        // F4-09: SHA-256 verification before anything else & deletes temp file on mismatch
        var tempUpdateFile = Path.Combine(workRoot, "GDM-update-tmp.exe");
        File.WriteAllText(tempUpdateFile, "tampered payload");
        var actualBytes = SHA256.HashData(File.ReadAllBytes(tempUpdateFile));
        bool signatureCheckedOnHashMismatch = false;
        bool deletedOnMismatch = false;
        var verifyMismatch = Updater.VerifyDownloadedFile(
            tempUpdateFile,
            new string('f', 64),
            _ => actualBytes,
            _ => { signatureCheckedOnHashMismatch = true; return SignatureCheckResult.Valid; },
            p => { deletedOnMismatch = true; File.Delete(p); });

        var tempValidFile = Path.Combine(workRoot, "GDM-update-ok.exe");
        File.WriteAllText(tempValidFile, "authentic payload");
        var validBytes = SHA256.HashData(File.ReadAllBytes(tempValidFile));
        var validHex = Convert.ToHexString(validBytes);
        var verifyOk = Updater.VerifyDownloadedFile(
            tempValidFile,
            validHex,
            _ => validBytes,
            _ => SignatureCheckResult.Unsigned,
            File.Delete);

        bool deletedOnUnsignedStrict = false;
        var verifyUnsignedStrict = Updater.VerifyDownloadedFile(
            tempValidFile,
            validHex,
            _ => validBytes,
            _ => SignatureCheckResult.Unsigned,
            _ => { deletedOnUnsignedStrict = true; },
            requireSignature: true);

        bool deletedOnBadSig = false;
        var verifyBadSig = Updater.VerifyDownloadedFile(
            tempValidFile,
            validHex,
            _ => validBytes,
            _ => SignatureCheckResult.Invalid,
            p => { deletedOnBadSig = true; File.Delete(p); });

        Check("F4-09: Updater verifies SHA-256 constant-time before signature check and deletes temp file on hash or signature mismatch",
            !verifyMismatch.Ok && !signatureCheckedOnHashMismatch && deletedOnMismatch && !File.Exists(tempUpdateFile)
            && verifyOk.Ok
            && !verifyUnsignedStrict.Ok && deletedOnUnsignedStrict
            && !verifyBadSig.Ok && deletedOnBadSig && !File.Exists(tempValidFile),
            $"mismatchOk={verifyMismatch.Ok}, sigCalledOnMismatch={signatureCheckedOnHashMismatch}, deletedMismatch={deletedOnMismatch}, verifyOk={verifyOk.Ok}, deletedBadSig={deletedOnBadSig}");

        // F4-10 & F4-11: Apply plan & execution (happy path + rollback on failed start)
        var newExe = Path.Combine(workRoot, "GDM.new.exe");
        var targetExe = Path.Combine(workRoot, "GDM.exe");
        var plan = Updater.BuildApplyPlan(newExe, targetExe, 4321);
        bool uncPlanRejected = false;
        bool nonExePlanRejected = false;
        try { Updater.BuildApplyPlan(@"\\evil\share\GDM.exe", targetExe, 4321); } catch { uncPlanRejected = true; }
        try { Updater.BuildApplyPlan(Path.Combine(workRoot, "payload.bat"), targetExe, 4321); } catch { nonExePlanRejected = true; }
        Check("F4-10a: Updater.BuildApplyPlan produces 30s wait timeout, .bak path, 10s start timeout, and rejects UNC/non-.exe paths",
            plan.WaitPid == 4321
            && plan.WaitExitTimeout == TimeSpan.FromSeconds(30)
            && plan.VerifyStartTimeout == TimeSpan.FromSeconds(10)
            && plan.BackupPath == targetExe + ".bak"
            && plan.Steps.Count >= 4
            && uncPlanRejected && nonExePlanRejected);

        var applyHostOk = new FakeUpdateApplyHost { ProcessExitsInTime = true, NewBinaryStartsOk = true };
        applyHostOk.Files.Add(newExe);
        applyHostOk.Files.Add(targetExe);
        applyHostOk.Content[newExe] = "v2-binary";
        applyHostOk.Content[targetExe] = "v1-binary";
        bool applySuccess = Updater.ExecuteApplyPlan(plan, applyHostOk);
        Check("F4-10b: Updater.ExecuteApplyPlan waits for pid, moves target->.bak, moves new->target, and starts target",
            applySuccess
            && applyHostOk.Content.TryGetValue(targetExe, out var finalBin) && finalBin == "v2-binary"
            && applyHostOk.Content.TryGetValue(plan.BackupPath, out var bakBin) && bakBin == "v1-binary",
            $"ok={applySuccess}, log=[{string.Join(", ", applyHostOk.Log)}]");

        var applyHostFail = new FakeUpdateApplyHost { ProcessExitsInTime = true, NewBinaryStartsOk = false };
        applyHostFail.Files.Add(newExe);
        applyHostFail.Files.Add(targetExe);
        applyHostFail.Content[newExe] = "broken-v2-binary";
        applyHostFail.Content[targetExe] = "working-v1-binary";
        bool applyFailed = !Updater.ExecuteApplyPlan(plan, applyHostFail);
        Check("F4-11: Updater.ExecuteApplyPlan rolls back .bak -> target when new binary fails to start within 10s",
            applyFailed
            && applyHostFail.Content.TryGetValue(targetExe, out var restoredBin) && restoredBin == "working-v1-binary",
            $"failedAsExpected={applyFailed}, restored={(applyHostFail.Content.TryGetValue(targetExe, out var rb) ? rb : "missing")}");
    }

    // =========================================================================
    // F15: Command Line (CliParser, CliActions, ICliTarget, exit codes)
    // =========================================================================
    sealed class FakeCliTarget : ICliTarget
    {
        public List<CliAddRequest> Added { get; } = [];
        public List<string> StartedQueues { get; } = [];
        public List<string> StoppedQueues { get; } = [];
        public int PauseAllCalls { get; set; }
        public bool RejectAll { get; set; }
        public List<CliListEntry> Items { get; } =
        [
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "ubuntu.iso", "https://releases.ubuntu.com/ubuntu.iso", "Downloading", 512, 1024, "Main"),
        ];

        public Task<(bool Ok, string Message)> AddDownloadAsync(CliAddRequest request)
        {
            if (RejectAll) return Task.FromResult((false, "Rejected by target"));
            Added.Add(request);
            return Task.FromResult((true, $"Added {request.Url}"));
        }

        public Task<(bool Ok, string Message)> StartQueueAsync(string queueName)
        {
            if (RejectAll) return Task.FromResult((false, "Queue not found"));
            StartedQueues.Add(queueName);
            return Task.FromResult((true, $"Started {queueName}"));
        }

        public Task<(bool Ok, string Message)> StopQueueAsync(string queueName)
        {
            if (RejectAll) return Task.FromResult((false, "Queue not found"));
            StoppedQueues.Add(queueName);
            return Task.FromResult((true, $"Stopped {queueName}"));
        }

        public Task<(bool Ok, string Message)> PauseAllAsync()
        {
            if (RejectAll) return Task.FromResult((false, "Cannot pause"));
            PauseAllCalls++;
            return Task.FromResult((true, "Paused all"));
        }

        public Task<IReadOnlyList<CliListEntry>> ListDownloadsAsync()
            => Task.FromResult<IReadOnlyList<CliListEntry>>(Items);
    }

    static async Task Test_F15_Cli(string workRoot)
    {
        Console.WriteLine("F15. Command Line (CliParser table, URL schemes, add-file comments, CliActions & exit codes)");

        // F15-01: Full `add` command with all flags
        var sha = new string('b', 64);
        var pAdd = CliParser.Parse([
            "add", "https://example.com/video.mp4",
            "--queue", "NightQueue",
            "--folder", @"D:\Downloads\Video",
            "--name", "lecture.mp4",
            "--referer", "https://example.com/course",
            "--later",
            "--quality", "720",
            "--hash", $"sha256:{sha}"
        ]);
        Check("F15-01: CliParser.Parse parses full 'add' command with all flags",
            pAdd.Ok && pAdd.Command != null
            && pAdd.Command.Verb == "add"
            && pAdd.Command.Url == "https://example.com/video.mp4"
            && pAdd.Command.QueueName == "NightQueue"
            && pAdd.Command.Folder == @"D:\Downloads\Video"
            && pAdd.Command.FileName == "lecture.mp4"
            && pAdd.Command.Referer == "https://example.com/course"
            && pAdd.Command.StartNow == false
            && pAdd.Command.Quality == "720"
            && pAdd.Command.ExpectedHash == $"sha256:{sha}",
            $"ok={pAdd.Ok}, err={pAdd.ErrorMessage}");

        // F15-02: Other verbs + allowed schemes (http, https, ftp, magnet) + localized --help
        var pFtp = CliParser.Parse(["add", "ftp://ftp.example.com/pub/file.zip", "--start"]);
        var pMagnet = CliParser.Parse(["add", "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=test"]);
        var pAddFile = CliParser.Parse(["add-file", "urls.txt"]);
        var pStartQ = CliParser.Parse(["start-queue", "Main"]);
        var pStopQ = CliParser.Parse(["stop-queue", "Main"]);
        var pPauseAll = CliParser.Parse(["pause-all"]);
        var pList = CliParser.Parse(["list"]);
        var pListJson = CliParser.Parse(["list", "--json"]);
        var pHelp1 = CliParser.Parse(["--help"]);
        var pHelp2 = CliParser.Parse(["-h"]);
        var pHelp3 = CliParser.Parse(["/?"]);

        Loc.Init("en");
        var helpEn = CliParser.GetHelpText();
        Loc.Init("ar");
        var helpAr = CliParser.GetHelpText();

        Check("F15-02: CliParser.Parse supports ftp/magnet schemes, add-file, start-queue, stop-queue, pause-all, list [--json], and localized --help/-h/?",
            pFtp.Ok && pFtp.Command?.StartNow == true
            && pMagnet.Ok
            && pAddFile.Ok && pAddFile.Command?.FilePath == "urls.txt"
            && pStartQ.Ok && pStartQ.Command?.QueueName == "Main"
            && pStopQ.Ok && pStopQ.Command?.QueueName == "Main"
            && pPauseAll.Ok && pPauseAll.Command?.Verb == "pause-all"
            && pList.Ok && pList.Command is { Verb: "list", JsonOutput: false }
            && pListJson.Ok && pListJson.Command is { Verb: "list", JsonOutput: true }
            && pHelp1.Ok && pHelp1.Command?.Verb == "help"
            && pHelp2.Ok && pHelp2.Command?.Verb == "help"
            && pHelp3.Ok && pHelp3.Command?.Verb == "help"
            && helpEn.Contains("Usage") && helpAr.Contains("الاستخدام"));

        // F15-03: Validation & security boundary table (exit code 2 on bad args / disallowed schemes)
        string[][] badArgCases =
        [
            [],
            ["unknown-verb"],
            ["add"],
            ["add", "file:///C:/Windows/System32/cmd.exe"],
            ["add", "javascript:alert(1)"],
            ["add", "data:text/plain;base64,SGVsbG8="],
            ["add", "https://example.com/a.zip", "--unknown-flag"],
            ["add", "https://example.com/a.zip", "--queue"],
            ["add", "https://example.com/a.zip", "--folder"],
            ["add", "https://example.com/a.zip", "--folder", @"\\evil\share"],
            ["add", "https://example.com/a.zip", "--folder", @"C:\Downloads\folder:ads"],
            ["add", "https://example.com/a.zip", "--folder", @"C:\Downloads\..\Windows"],
            ["add", "https://example.com/a.zip", "--name"],
            ["add", "https://example.com/a.zip", "--name", @"..\evil.exe"],
            ["add", "https://example.com/a.zip", "--name", "file:ads.bin"],
            ["add", "https://example.com/a.zip", "--referer", "javascript:evil()"],
            ["add", "https://example.com/a.zip", "--start", "--later"],
            ["add", "https://example.com/a.zip", "--hash", "md5:not_hex"],
            ["add-file"],
            ["start-queue"],
            ["stop-queue"],
            ["pause-all", "extra"],
            ["list", "--bad"],
        ];
        int badCaught = 0;
        foreach (var badArgs in badArgCases)
        {
            var r = CliParser.Parse(badArgs);
            if (!r.Ok && r.ExitCode == 2 && !string.IsNullOrEmpty(r.ErrorMessage))
                badCaught++;
        }
        Check("F15-03: CliParser.Parse rejects unknown flags, missing values, UNC/ADS/traversal paths, --start+--later conflict, and non-http/https/ftp/magnet URLs with exit code 2",
            badCaught == badArgCases.Length,
            $"caught={badCaught}/{badArgCases.Length}");

        // F15-04: add-file comment and blank-line parsing
        var listFileContent = """
            # Download list comment
               
            https://example.com/file1.zip
              # Indented comment
            ftp://ftp.example.com/file2.tar.gz
            magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567
            """;
        var parsedUrls = CliParser.ParseUrlList(listFileContent, out var invalidLine);
        var badListUrls = CliParser.ParseUrlList("https://example.com/ok.zip\nfile:///C:/Windows/win.ini", out var badLineDetected);
        Check("F15-04: CliParser.ParseUrlList ignores '#' comments and blank lines, and flags invalid schemes",
            invalidLine == null
            && parsedUrls.Count == 3
            && parsedUrls[0] == "https://example.com/file1.zip"
            && parsedUrls[1] == "ftp://ftp.example.com/file2.tar.gz"
            && parsedUrls[2].StartsWith("magnet:?xt=urn:btih:")
            && badLineDetected != null,
            $"count={parsedUrls.Count}, badLine={badLineDetected ?? "null"}");

        // F15-05: CliActions.HandleAsync against FakeCliTarget
        var target = new FakeCliTarget();
        var addMsg = pAdd.Command!.ToPipeMessage();
        var addReply = await CliActions.HandleAsync(addMsg, target);

        var addFileMsg = pAddFile.Command!.ToPipeMessage(parsedUrls);
        var addFileReply = await CliActions.HandleAsync(addFileMsg, target);

        var startQReply = await CliActions.HandleAsync(pStartQ.Command!.ToPipeMessage(), target);
        var stopQReply = await CliActions.HandleAsync(pStopQ.Command!.ToPipeMessage(), target);
        var pauseReply = await CliActions.HandleAsync(pPauseAll.Command!.ToPipeMessage(), target);
        var listReply = await CliActions.HandleAsync(pListJson.Command!.ToPipeMessage(), target);

        target.RejectAll = true;
        var rejectedReply = await CliActions.HandleAsync(addMsg, target);

        Check("F15-05: CliActions.HandleAsync dispatches add, add-file, start-queue, stop-queue, pause-all, and list to ICliTarget",
            addReply["ok"]?.GetValue<bool>() == true
            && addFileReply["ok"]?.GetValue<bool>() == true
            && target.Added.Count == 4
            && target.Added[0].Url == "https://example.com/video.mp4"
            && target.Added[0].QueueName == "NightQueue"
            && target.Added[0].Quality == "720"
            && startQReply["ok"]?.GetValue<bool>() == true && target.StartedQueues.SequenceEqual(["Main"])
            && stopQReply["ok"]?.GetValue<bool>() == true && target.StoppedQueues.SequenceEqual(["Main"])
            && pauseReply["ok"]?.GetValue<bool>() == true && target.PauseAllCalls == 1
            && listReply["ok"]?.GetValue<bool>() == true && (listReply["items"] as JsonArray)?.Count == 1
            && rejectedReply["ok"]?.GetValue<bool>() == false,
            $"added={target.Added.Count}");

        // F15-06: Program.cs compatibility & exit code mapping (0 ok, 2 bad args, 3 not reachable, 4 rejected)
        Check("F15-06: CliParser.IsCliInvocation preserves single-URL and --background while detecting CLI verbs, and exit codes map to 0/2/3/4",
            CliParser.IsCliInvocation(["add", "https://example.com/a.zip"])
            && CliParser.IsCliInvocation(["list", "--json"])
            && CliParser.IsCliInvocation(["--help"])
            && !CliParser.IsCliInvocation([])
            && !CliParser.IsCliInvocation(["--background"])
            && !CliParser.IsCliInvocation(["https://example.com/direct-url.zip"])
            && !CliParser.IsCliInvocation(["chrome-extension://fkjplbaglahdiphiodkgidkdfkbebgfh/"])
            && CliActions.MapPipeReplyToExitCode(addReply) == 0
            && CliActions.MapPipeReplyToExitCode(null) == 3
            && CliActions.MapPipeReplyToExitCode(rejectedReply) == 4);

        // F15-07: Server-side IPC handler rejects crafted invalid options and does not leak local file contents
        target.RejectAll = false;
        int addedBeforeSec = target.Added.Count;
        var badFolderIpc = await CliActions.HandleAsync(new JsonObject { ["action"] = "cli", ["verb"] = "add", ["url"] = "https://example.com/a.bin", ["folder"] = @"C:\Downloads\..\Windows" }, target);
        var badNameIpc = await CliActions.HandleAsync(new JsonObject { ["action"] = "cli", ["verb"] = "add", ["url"] = "https://example.com/a.bin", ["name"] = @"..\evil.bat" }, target);
        var badRefIpc = await CliActions.HandleAsync(new JsonObject { ["action"] = "cli", ["verb"] = "add", ["url"] = "https://example.com/a.bin", ["referer"] = "javascript:alert(1)" }, target);
        var secretFile = Path.Combine(workRoot, "secret.txt");
        File.WriteAllText(secretFile, "SUPER_SECRET_PRIVATE_KEY_LINE");
        var secretIpc = await CliActions.HandleAsync(new JsonObject { ["action"] = "cli", ["verb"] = "add-file", ["file"] = secretFile }, target);
        string secretErr = secretIpc["error"]?.GetValue<string>() ?? "";
        Check("F15-07: Server-side CliActions.HandleAsync rejects crafted traversal/option IPC messages and prevents arbitrary file-line disclosure",
            badFolderIpc["ok"]?.GetValue<bool>() == false
            && badNameIpc["ok"]?.GetValue<bool>() == false
            && badRefIpc["ok"]?.GetValue<bool>() == false
            && secretIpc["ok"]?.GetValue<bool>() == false
            && !secretErr.Contains("SUPER_SECRET_PRIVATE_KEY_LINE", StringComparison.Ordinal)
            && target.Added.Count == addedBeforeSec,
            $"addedDelta={target.Added.Count - addedBeforeSec}, secretErr={secretErr}");
    }

    // =========================================================================
    // V1 Settings & i18n parity
    // =========================================================================
    static void Test_V1_SettingsAndI18n()
    {
        Console.WriteLine("V1. Settings.V1 & v1.ar.json / v1.en.json parity");

        var s = new AppSettings();
        Check("V1-Settings: KeepSystemAwake defaults to true, KeepDisplayOn false, UpdateFeedUrl empty",
            s.KeepSystemAwake && !s.KeepDisplayOn && s.UpdateFeedUrl == "" && s.LastUpdateCheck == null);

        var repoRoot = FindRepoRoot();
        var i18nDir = Path.Combine(repoRoot, "GDM", "src", "GDM", "i18n");
        var arPath = Path.Combine(i18nDir, "system.ar.json");
        var enPath = Path.Combine(i18nDir, "system.en.json");
        var ar = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(arPath)) ?? [];
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath)) ?? [];

        bool sameKeys = ar.Count > 0 && ar.Count == en.Count && ar.Keys.All(en.ContainsKey);
        var phRegex = new Regex(@"\{\d+\}");
        bool samePlaceholders = ar.Keys.All(k =>
            string.Join(",", phRegex.Matches(ar[k]).Select(m => m.Value).Distinct().OrderBy(x => x)) ==
            string.Join(",", phRegex.Matches(en[k]).Select(m => m.Value).Distinct().OrderBy(x => x)));

        // Verify all V1Loc.Get / V1Loc.Format call sites across src/GDM resolve to existing keys in v1.*.json
        var v1LocRegex = new Regex(@"V1Loc\.(?:Get|Format)\(\s*""([^""]+)""");
        var missingCallSiteKeys = new List<string>();
        foreach (var csFile in Directory.EnumerateFiles(Path.Combine(repoRoot, "GDM", "src", "GDM"), "*.cs", SearchOption.AllDirectories))
        {
            var srcText = File.ReadAllText(csFile);
            foreach (Match m in v1LocRegex.Matches(srcText))
            {
                var key = m.Groups[1].Value;
                if (!ar.ContainsKey(key) || !en.ContainsKey(key))
                    missingCallSiteKeys.Add($"{Path.GetFileName(csFile)}:{key}");
            }
        }

        Loc.Init("ar");
        var spaceMsgAr = Loc.T("space.insufficient", "100 MB", "50 MB");
        Loc.Init("en");
        var spaceMsgEn = Loc.T("space.insufficient", "100 MB", "50 MB");

        Check("V1-i18n: v1.ar.json and v1.en.json have identical keys, matching placeholders, all V1Loc call-sites exist, and resolve via Loc.T",
            sameKeys && samePlaceholders && missingCallSiteKeys.Count == 0
            && spaceMsgAr.Contains("100 MB") && spaceMsgAr.Contains("50 MB")
            && spaceMsgEn.Contains("100 MB") && spaceMsgEn.Contains("50 MB"),
            $"keys={ar.Count}, sameKeys={sameKeys}, samePh={samePlaceholders}, missingCallSites=[{string.Join(", ", missingCallSiteKeys)}]");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "GDM", "src", "GDM")))
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
