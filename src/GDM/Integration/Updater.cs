using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Golan.Gdm.Core;

namespace Golan.Gdm.Integration;

/// <summary>F4: Auto-updater feed checker, downloader, SHA-256 + Authenticode verifier, and binary swapper.</summary>
public static partial class Updater
{
    static readonly HttpClient FeedHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 256 * 1024,
    };

    static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    const uint WTD_UI_NONE = 2;
    const uint WTD_REVOKE_WHOLECHAIN = 1;
    const uint WTD_CHOICE_FILE = 1;
    const uint WTD_STATEACTION_VERIFY = 1;
    const uint WTD_STATEACTION_CLOSE = 2;
    const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid pgActionID, [In] ref WINTRUST_DATA pWVTData);

    public static SignatureCheckResult VerifyAuthenticodeSignature(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return SignatureCheckResult.Invalid;

        IntPtr filePathPtr = Marshal.StringToCoTaskMemUni(filePath);
        IntPtr fileInfoPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };

            var action = WinTrustActionGenericVerifyV2;
            int hr = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            try
            {
                if (data.hWVTStateData != IntPtr.Zero)
                {
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"WinVerifyTrust state close warning: {ex.Message}");
            }

            if (hr == 0)
                return SignatureCheckResult.Valid;
            if (hr is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
                return SignatureCheckResult.Unsigned;

            return SignatureCheckResult.Invalid;
        }
        catch (Exception ex)
        {
            Log.Write("WinVerifyTrust error: " + ex.Message);
            return SignatureCheckResult.Invalid;
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(fileInfoPtr);
            if (filePathPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    public static byte[]? TryGetAuthenticodeSignerPublicKey(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            var rawKey = cert.GetPublicKey();
            return rawKey is { Length: > 0 } ? rawKey : null;
        }
        catch
        {
            return null;
        }
    }

    public static byte[] ComputeFileSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(fs);
    }

    public static async Task<UpdateFeed?> CheckForUpdateAsync(
        AppSettings settings,
        bool force = false,
        CancellationToken ct = default)
    {
        if (settings == null) return null;
        if (!force && !ShouldCheckNow(settings, DateTime.UtcNow, AppPaths.IsPortable))
            return null;

        var feedUrl = force && !settings.CheckForUpdates
            ? (string.IsNullOrWhiteSpace(settings.UpdateFeedUrl) ? OfficialFeedUrl : settings.UpdateFeedUrl.Trim())
            : EffectiveFeedUrl(settings);
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var currentUri = uri;
            string? json = null;
            for (int hop = 0; hop <= 5; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
                req.Headers.UserAgent.ParseAdd($"Golan/{App.Version}");
                using var resp = await FeedHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc)
                {
                    if (hop == 5)
                        throw new InvalidOperationException("Too many update feed redirects.");
                    currentUri = ValidateHttpsRedirectHop(currentUri, loc);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync(ct);
                break;
            }

            if (json == null)
                return null;

            settings.LastUpdateCheck = DateTime.UtcNow;
            settings.Save();

            var feed = ParseFeed(json);
            return IsNewer(App.Version, feed.Version) ? feed : null;
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            return null;
        }
    }

    public static async Task<(bool Ok, string TempFilePath, string Error)> DownloadAndVerifyAsync(
        UpdateFeed feed,
        SpeedLimiter? limiter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feed);

        if (!Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "", Loc.T("update.err_https"));
        }

        var updatesDir = Path.Combine(AppPaths.LocalDir, "updates");
        Directory.CreateDirectory(updatesDir);
        var tempFile = Path.Combine(updatesDir, $"GDM-{FileNames.Sanitize(feed.Version)}-{Guid.NewGuid():N}.exe");

        var item = new DownloadItem
        {
            Url = feed.Url,
            SaveDir = updatesDir,
            FileName = Path.GetFileName(tempFile),
            NameLocked = true,
            MaxConnections = 4,
            Kind = DownloadKind.Http,
        };

        try
        {
            await Task.Run(() => new HttpDownloader(item, limiter ?? new SpeedLimiter()).RunAsync(ct), ct);
        }
        catch (Exception ex)
        {
            MediaDownloader.TryDelete(item.TempPath);
            MediaDownloader.TryDelete(item.FullPath);
            return (false, "", ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(item.ResolvedUrl))
        {
            try
            {
                if (!Uri.TryCreate(item.ResolvedUrl, UriKind.Absolute, out var resolvedUri))
                    throw new InvalidOperationException(Loc.T("update.err_https"));
                _ = ValidateHttpsRedirectHop(uri, resolvedUri);
            }
            catch (Exception ex)
            {
                MediaDownloader.TryDelete(item.TempPath);
                MediaDownloader.TryDelete(item.FullPath);
                return (false, "", ex.Message);
            }
        }

        var downloadedPath = item.FullPath;
        var (ok, error) = VerifyDownloadedFile(
            downloadedPath,
            feed.Sha256,
            ComputeFileSha256,
            VerifyAuthenticodeSignature,
            MediaDownloader.TryDelete,
            requireSignature: RequirePinnedPublisher);

        if (ok)
        {
            string currentExe = AppPaths.ExePath;
            var currentSig = VerifyAuthenticodeSignature(currentExe);
            var currentKey = currentSig == SignatureCheckResult.Valid ? TryGetAuthenticodeSignerPublicKey(currentExe) : null;
            var candSig = VerifyAuthenticodeSignature(downloadedPath);
            var candKey = candSig == SignatureCheckResult.Valid ? TryGetAuthenticodeSignerPublicKey(downloadedPath) : null;
            if (!VerifySignerIdentity(currentSig, currentKey, candSig, candKey))
            {
                MediaDownloader.TryDelete(downloadedPath);
                return (false, "", Loc.T("update.err_signature"));
            }
        }

        return ok ? (true, downloadedPath, "") : (false, "", error);
    }

    public static bool LaunchApplyUpdate(string downloadedNewExe, string? targetExe = null)
    {
        try
        {
            var actualTarget = string.IsNullOrWhiteSpace(targetExe) ? AppPaths.ExePath : targetExe;
            int pid = Environment.ProcessId;
            var psi = new ProcessStartInfo(downloadedNewExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(Path.GetFullPath(downloadedNewExe));
            psi.ArgumentList.Add(Path.GetFullPath(actualTarget));
            psi.ArgumentList.Add(pid.ToString());

            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            Log.Write("launch apply-update failed: " + ex.Message);
            return false;
        }
    }

    public static int RunCliApplyUpdate(string[] args)
    {
        // Expected: --apply-update <newPath> <targetExe> <pid>
        if (args == null || args.Length < 4)
            return 2;

        string newPath = args[1];
        string targetExe = args[2];
        if (!int.TryParse(args[3], out int pid) || pid <= 0)
            return 2;

        try
        {
            var plan = BuildApplyPlan(newPath, targetExe, pid);
            var candSig = VerifyAuthenticodeSignature(plan.NewPath);
            if (candSig == SignatureCheckResult.Invalid)
                return 1;

            var targetSig = File.Exists(plan.TargetExe) ? VerifyAuthenticodeSignature(plan.TargetExe) : SignatureCheckResult.Unsigned;
            var targetKey = targetSig == SignatureCheckResult.Valid ? TryGetAuthenticodeSignerPublicKey(plan.TargetExe) : null;
            var candKey = candSig == SignatureCheckResult.Valid ? TryGetAuthenticodeSignerPublicKey(plan.NewPath) : null;
            if (!VerifySignerIdentity(targetSig, targetKey, candSig, candKey))
                return 1;

            bool ok = ExecuteApplyPlan(plan, new WindowsUpdateApplyHost());
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Write("apply-update failed: " + ex.Message);
            return 1;
        }
    }
}

public sealed class WindowsUpdateApplyHost : IUpdateApplyHost
{
    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public bool WaitForProcessExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return true;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 100, 120_000));
        }
        catch (ArgumentException)
        {
            // Process already exited.
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("wait for pid exit failed: " + ex.Message);
            return false;
        }
    }

    public void MoveFile(string source, string destination, bool overwrite)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        if (destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            StopBrowserHelpers(destination);

        try
        {
            File.Move(source, destination, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cross-volume move when source is the currently executing update binary: copy instead.
            File.Copy(source, destination, overwrite);
        }
    }

    /// <summary>
    /// The browser keeps a small Golan helper running (its open connection to the extension), which holds the exe.
    /// Golan itself has already exited here, so any other process from that exe is such a helper: stop it so the
    /// file can be replaced. The extension reconnects to the new version by itself.
    /// </summary>
    static void StopBrowserHelpers(string exePath)
    {
        string target;
        try { target = Path.GetFullPath(exePath); }
        catch (Exception) { return; }
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;
                    var path = p.MainModule?.FileName;
                    if (path == null || !string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                    p.WaitForExit(3000);
                    Log.Write("updater: stopped browser helper " + p.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // already gone or not ours to touch
                }
            }
        }
    }

    public void DeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"updater: best-effort delete '{Path.GetFileName(path)}' failed: {ex.Message}");
        }
    }

    public bool StartAndVerifyProcess(string exePath, TimeSpan timeout)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
            });
            if (proc == null)
                return false;

            using (proc)
            {
                int windowMs = (int)Math.Clamp(timeout.TotalMilliseconds, 100, 30_000);
                try
                {
                    if (proc.WaitForInputIdle(Math.Min(windowMs, 2000)))
                        return !proc.HasExited || proc.ExitCode == 0;
                }
                catch
                {
                    // Non-GUI or console process may not have a message loop; fall back to polling.
                }

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < windowMs)
                {
                    if (proc.HasExited)
                        return proc.ExitCode == 0;
                    Thread.Sleep(100);
                }
                return !proc.HasExited || proc.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            Log.Write("start updated exe failed: " + ex.Message);
            return false;
        }
    }
}
