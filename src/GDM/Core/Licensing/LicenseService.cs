using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Golan.Gdm.Core.Licensing;

public enum LicenseState { Trial, Licensed, TrialExpired, LicenseExpired, ClockTampered }

/// <summary>The app's licence situation right now. Downloads are allowed during the trial and with a valid licence.</summary>
public sealed record LicenseStatus(LicenseState State, int DaysLeft, DateTime? EndsUtc, LicenseInfo? License)
{
    public bool CanDownload => State is LicenseState.Trial or LicenseState.Licensed;
}

/// <summary>This computer's code: a hash of Windows' MachineGuid and the system drive's serial, as XXXX-XXXX-XXXX-XXXX.</summary>
public static class MachineCode
{
    static string? _current;

    public static string Current => _current ??= Compute(ReadMachineGuid(), ReadSystemVolumeSerial());

    public static string Compute(string machineGuid, string volumeSerial)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("GDM-MACHINE|" + machineGuid.Trim().ToLowerInvariant() + "|" + volumeSerial.Trim()));
        return LicenseKey.FormatMachine(digest);
    }

    static string ReadMachineGuid()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string g && g.Length > 0) return g;
        }
        catch (Exception ex) { Log.Write("machine guid unavailable: " + ex.Message); }
        return Environment.MachineName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize, out uint serial,
        out uint maxComponent, out uint flags, StringBuilder? fsName, int fsNameSize);

    static string ReadSystemVolumeSerial()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            if (GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0))
                return serial.ToString("X8");
        }
        catch (Exception ex) { Log.Write("volume serial unavailable: " + ex.Message); }
        return "0";
    }
}

/// <summary>One place that remembers when the trial began (a file, the registry, …).</summary>
public interface ITrialStorage
{
    string? Read();
    void Write(string value);
}

public sealed class FileTrialStorage(string path) : ITrialStorage
{
    public string? Read()
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) { Log.Write("trial file unreadable: " + ex.Message); return null; }
    }

    public void Write(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }
        catch (Exception ex) { Log.Write("trial file not written: " + ex.Message); }
    }
}

public sealed class RegistryTrialStorage(string subKey, string valueName) : ITrialStorage
{
    public string? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) { Log.Write("trial registry unreadable: " + ex.Message); return null; }
    }

    public void Write(string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey);
            key.SetValue(valueName, value);
        }
        catch (Exception ex) { Log.Write("trial registry not written: " + ex.Message); }
    }
}

/// <summary>
/// Free trial for <see cref="TrialDays"/> days from the first start, then an activation key from the owner is needed.
/// The trial start is kept in several places, each sealed with an HMAC tied to this machine, so editing or copying it
/// does not extend the trial. Turning the clock back more than two days is detected through the "last seen" time.
/// </summary>
public sealed class LicenseService
{
    public const int TrialDays = 30;
    static readonly TimeSpan ClockSlack = TimeSpan.FromDays(2);

    readonly string _machine;
    readonly ECDsa _publicKey;
    readonly IReadOnlyList<ITrialStorage> _trial;
    readonly string _licenseFile;
    readonly Func<DateTime> _utcNow;

    public LicenseService(string machine, ECDsa publicKey, IReadOnlyList<ITrialStorage> trialStores, string licenseFile, Func<DateTime>? utcNow = null)
    {
        _machine = machine;
        _publicKey = publicKey;
        _trial = trialStores;
        _licenseFile = licenseFile;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Machine => _machine;

    public static LicenseService CreateDefault()
    {
        var stores = new List<ITrialStorage> { new FileTrialStorage(Path.Combine(AppPaths.LocalDir, "license", "trial.dat")) };
        if (!AppPaths.IsIsolated)
            stores.Add(new RegistryTrialStorage(@"Software\Golan\GDM", "Trial"));
        return new LicenseService(MachineCode.Current, LicensePublicKey.Load(), stores, Path.Combine(AppPaths.LocalDir, "license", "license.key"));
    }

    public LicenseStatus Evaluate()
    {
        var now = _utcNow();
        var (first, last, tampered) = ReadTrial(now);
        bool clockBack = now < last - ClockSlack;
        if (!clockBack && now > last + TimeSpan.FromHours(1)) WriteTrial(first, now);

        var license = ReadLicense();
        if (license != null)
        {
            if (license.IsLifetime) return new LicenseStatus(LicenseState.Licensed, int.MaxValue, null, license);
            if (clockBack) return new LicenseStatus(LicenseState.ClockTampered, 0, license.ExpiresUtc, license);
            if (now < license.ExpiresUtc) return new LicenseStatus(LicenseState.Licensed, DaysUntil(now, license.ExpiresUtc!.Value), license.ExpiresUtc, license);
            return new LicenseStatus(LicenseState.LicenseExpired, 0, license.ExpiresUtc, license);
        }

        var ends = first.AddDays(TrialDays);
        if (clockBack) return new LicenseStatus(LicenseState.ClockTampered, 0, ends, null);
        if (tampered || now >= ends) return new LicenseStatus(LicenseState.TrialExpired, 0, ends, null);
        return new LicenseStatus(LicenseState.Trial, DaysUntil(now, ends), ends, null);
    }

    /// <summary>Verifies a pasted key and, when valid for this machine, stores it. Expired keys are refused.</summary>
    public LicenseKeyError Activate(string? keyText, out LicenseInfo? info)
    {
        if (!LicenseKey.TryVerify(keyText, _publicKey, _machine, out info, out var error)) return error;
        if (info!.ExpiresUtc is { } e && e <= _utcNow()) return LicenseKeyError.Expired;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_licenseFile)!);
            File.WriteAllText(_licenseFile, new string((keyText ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()));
        }
        catch (Exception ex)
        {
            Log.Write("license not saved: " + ex.Message);
            return LicenseKeyError.SaveFailed;
        }
        return LicenseKeyError.None;
    }

    LicenseInfo? ReadLicense()
    {
        try
        {
            if (!File.Exists(_licenseFile)) return null;
            return LicenseKey.TryVerify(File.ReadAllText(_licenseFile), _publicKey, _machine, out var info, out _) ? info : null;
        }
        catch (Exception ex) { Log.Write("license unreadable: " + ex.Message); return null; }
    }

    static int DaysUntil(DateTime now, DateTime end) => Math.Max(0, (int)Math.Ceiling((end - now).TotalDays));

    (DateTime First, DateTime Last, bool Tampered) ReadTrial(DateTime now)
    {
        DateTime? first = null, last = null;
        bool tampered = false;
        foreach (var store in _trial)
        {
            var raw = store.Read();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (TryOpen(raw, out var f, out var l))
            {
                if (first == null || f < first) first = f;
                if (last == null || l > last) last = l;
            }
            else tampered = true;
        }
        if (first == null)
        {
            if (tampered) return (now.AddDays(-TrialDays - 1), now, true);
            WriteTrial(now, now);
            return (now, now, false);
        }
        // Heal stores that were deleted or damaged so all of them agree again.
        WriteTrial(first.Value, last!.Value);
        return (first.Value, last.Value, tampered);
    }

    void WriteTrial(DateTime first, DateTime last)
    {
        var sealedValue = Seal(first, last);
        foreach (var store in _trial)
            if (store.Read() != sealedValue) store.Write(sealedValue);
    }

    string Seal(DateTime first, DateTime last)
    {
        var body = $"{new DateTimeOffset(first).ToUnixTimeSeconds()}|{new DateTimeOffset(last).ToUnixTimeSeconds()}";
        return body + "|" + Mac(body);
    }

    bool TryOpen(string raw, out DateTime first, out DateTime last)
    {
        first = last = default;
        var parts = raw.Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[0], out var f) || !long.TryParse(parts[1], out var l)) return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(parts[0] + "|" + parts[1])), Encoding.ASCII.GetBytes(parts[2])))
            return false;
        try
        {
            first = DateTimeOffset.FromUnixTimeSeconds(f).UtcDateTime;
            last = DateTimeOffset.FromUnixTimeSeconds(l).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException) { return false; }
        return first <= last.AddMinutes(1);
    }

    string Mac(string body)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("GDM-TRIAL|" + LicenseKey.NormalizeMachine(_machine)));
        return LicenseKey.Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)))[..22];
    }
}

/// <summary>App-wide access to the licence status (cached briefly). Tests may set <see cref="Override"/>.</summary>
public static class LicenseGate
{
    static readonly object Sync = new();
    static LicenseService? _service;
    static LicenseStatus? _status;
    static DateTime _checkedAt;

    public static Func<bool>? Override { get; set; }

    public static LicenseService Service
    {
        get { lock (Sync) return _service ??= LicenseService.CreateDefault(); }
    }

    public static LicenseStatus Status
    {
        get
        {
            lock (Sync)
            {
                if (_status == null || DateTime.UtcNow - _checkedAt > TimeSpan.FromMinutes(5))
                {
                    try { _status = Service.Evaluate(); }
                    catch (Exception ex)
                    {
                        Log.Write("license check failed: " + ex.Message);
                        _status ??= new LicenseStatus(LicenseState.Trial, 1, null, null);
                    }
                    _checkedAt = DateTime.UtcNow;
                }
                return _status;
            }
        }
    }

    public static bool CanDownload => Override?.Invoke() ?? Status.CanDownload;

    public static void Refresh()
    {
        lock (Sync) _status = null;
    }
}
