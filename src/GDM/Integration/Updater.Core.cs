using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Golan.Gdm.Core;

namespace Golan.Gdm.Integration;

public sealed record UpdateFeed(
    string Version,
    string Url,
    string Sha256,
    string NotesAr,
    string NotesEn);

public enum SignatureCheckResult
{
    Unsigned,
    Valid,
    Invalid,
}

public enum ApplyStepKind
{
    WaitForProcessExit,
    MoveTargetToBackup,
    MoveNewToTarget,
    StartAndVerifyTarget,
    RestoreBackupOnFailure,
}

public sealed record UpdateApplyPlan(
    string NewPath,
    string TargetExe,
    string BackupPath,
    int WaitPid,
    TimeSpan WaitExitTimeout,
    TimeSpan VerifyStartTimeout,
    IReadOnlyList<ApplyStepKind> Steps);

public interface IUpdateApplyHost
{
    bool FileExists(string path);
    bool WaitForProcessExit(int pid, TimeSpan timeout);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    bool StartAndVerifyProcess(string exePath, TimeSpan timeout);
}

public static partial class Updater
{
    static readonly Regex Hex64Regex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    static readonly Regex VersionRegex = new(@"^\d+(\.\d+){1,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Signer Pinning Hook (HIGH-02):
    /// Once Golan is officially Authenticode code-signed, populate this list with the 64-character hex
    /// SHA-256 hash(es) of the publisher certificate's raw public key bytes (<c>X509Certificate.GetPublicKey()</c>)
    /// and set <see cref="RequirePinnedPublisher"/> to <c>true</c>.
    /// When populated, <see cref="VerifySignerIdentity"/> rejects any signed binary whose publisher public key
    /// SHA-256 does not match an entry in this list in constant time.
    /// </summary>
    public static readonly IReadOnlyList<string> PinnedPublisherPublicKeySha256Hex = Array.Empty<string>();

    /// <summary>
    /// Signer Pinning Hook toggle (HIGH-02):
    /// Set to <c>true</c> once all official Golan releases are signed. While <c>false</c>, unsigned update binaries
    /// remain accepted ONLY when (a) their SHA-256 matches the HTTPS feed in constant time and (b) the currently
    /// running Golan executable is also unsigned.
    /// </summary>
    public const bool RequirePinnedPublisher = false;

    /// <summary>
    /// Validates a redirect hop for the update feed or update binary, refusing any https-&gt;http downgrade,
    /// non-HTTPS scheme, loopback target, or link-local/private host.
    /// </summary>
    public static Uri ValidateHttpsRedirectHop(Uri currentUri, Uri locationUri)
    {
        ArgumentNullException.ThrowIfNull(currentUri);
        ArgumentNullException.ThrowIfNull(locationUri);

        if (!string.Equals(currentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("update.err_https"));

        var next = locationUri.IsAbsoluteUri ? locationUri : new Uri(currentUri, locationUri);
        if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(next.Host)
            || next.IsLoopback
            || !Core.Torrent.MagnetValidator.IsSafeRemoteHost(next.Host))
        {
            throw new InvalidOperationException(Loc.T("update.err_https"));
        }

        return next;
    }

    /// <summary>
    /// Verifies that the update candidate binary's Authenticode signer matches the currently running executable's
    /// signer (whenever the running executable is signed) and satisfies any configured <see cref="PinnedPublisherPublicKeySha256Hex"/>.
    /// </summary>
    public static bool VerifySignerIdentity(
        SignatureCheckResult currentExeSignatureStatus,
        byte[]? currentExeSignerPublicKey,
        SignatureCheckResult candidateSignatureStatus,
        byte[]? candidateSignerPublicKey,
        IReadOnlyList<string>? pinnedPublicKeySha256Hex = null,
        bool requirePinned = RequirePinnedPublisher)
    {
        if (candidateSignatureStatus == SignatureCheckResult.Invalid)
            return false;

        var pins = pinnedPublicKeySha256Hex ?? PinnedPublisherPublicKeySha256Hex;
        if ((requirePinned || pins.Count > 0) && candidateSignatureStatus != SignatureCheckResult.Valid && requirePinned)
            return false;

        if (currentExeSignatureStatus == SignatureCheckResult.Valid)
        {
            if (candidateSignatureStatus != SignatureCheckResult.Valid)
                return false;
            if (currentExeSignerPublicKey is not { Length: > 0 } || candidateSignerPublicKey is not { Length: > 0 })
                return false;
            if (!CryptographicOperations.FixedTimeEquals(currentExeSignerPublicKey, candidateSignerPublicKey))
                return false;
        }

        if (candidateSignatureStatus == SignatureCheckResult.Valid && pins.Count > 0)
        {
            if (candidateSignerPublicKey is not { Length: > 0 })
                return false;
            byte[] keyHash = SHA256.HashData(candidateSignerPublicKey);
            bool matchedPin = false;
            foreach (var pinHex in pins)
            {
                if (VerifySha256ConstantTime(keyHash, pinHex))
                {
                    matchedPin = true;
                    break;
                }
            }
            if (!matchedPin)
                return false;
        }

        return true;
    }

    public static UpdateFeed ParseFeed(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("Update feed JSON is empty.");

        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject
                ?? throw new FormatException("Update feed root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Malformed update feed JSON.", ex);
        }

        string version = ReadProp(obj, "version").Trim();
        string url = ReadProp(obj, "url").Trim();
        string sha256 = ReadProp(obj, "sha256").Trim();
        string notesAr = ReadProp(obj, "notes_ar", "notesAr");
        string notesEn = ReadProp(obj, "notes_en", "notesEn");

        if (!TryParseVersionParts(version, out _))
            throw new FormatException($"Invalid update feed version: '{version}'.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.IsLoopback
            || uri.Host.StartsWith("169.254.", StringComparison.Ordinal))
        {
            throw new FormatException("Update feed URL must be a valid non-loopback HTTPS URL.");
        }

        if (!Hex64Regex.IsMatch(sha256))
            throw new FormatException("Update feed sha256 must be exactly 64 hexadecimal characters.");

        return new UpdateFeed(version, url, sha256.ToLowerInvariant(), notesAr, notesEn);
    }

    static string ReadProp(JsonObject obj, params string[] names)
    {
        foreach (var kv in obj)
        {
            foreach (var name in names)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)
                    && kv.Value is JsonValue v
                    && v.TryGetValue<string>(out var s))
                {
                    return s ?? "";
                }
            }
        }
        return "";
    }

    static bool TryParseVersionParts(string? version, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(version)) return false;
        var trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        if (!VersionRegex.IsMatch(trimmed)) return false;

        var segments = trimmed.Split('.');
        var parsed = new int[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out int val) || val < 0)
                return false;
            parsed[i] = val;
        }
        parts = parsed;
        return true;
    }

    public static bool IsNewer(string current, string candidate)
    {
        if (!TryParseVersionParts(current, out var cur) || !TryParseVersionParts(candidate, out var cand))
            return false;

        int len = Math.Max(cur.Length, cand.Length);
        for (int i = 0; i < len; i++)
        {
            int a = i < cur.Length ? cur[i] : 0;
            int b = i < cand.Length ? cand[i] : 0;
            if (b > a) return true;
            if (b < a) return false;
        }
        return false;
    }

    /// <summary>Published with every release next to the installer (see installer/build-setup.ps1).</summary>
    public const string OfficialFeedUrl = "https://github.com/dark9guna/golan-download-manager-releases/releases/latest/download/update.json";

    /// <summary>The feed to use: none when checks are off, the user's own feed if set, otherwise the official one.</summary>
    public static string EffectiveFeedUrl(AppSettings? settings)
    {
        if (settings == null || !settings.CheckForUpdates) return "";
        return string.IsNullOrWhiteSpace(settings.UpdateFeedUrl) ? OfficialFeedUrl : settings.UpdateFeedUrl.Trim();
    }

    public static bool ShouldCheckNow(AppSettings settings, DateTime nowUtc, bool isPortable = false)
    {
        if (isPortable || settings == null) return false;
        var feedUrl = EffectiveFeedUrl(settings);
        if (string.IsNullOrWhiteSpace(feedUrl)) return false;
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        if (settings.LastUpdateCheck is { } last)
        {
            var lastUtc = last.Kind == DateTimeKind.Utc ? last : last.ToUniversalTime();
            if (nowUtc - lastUtc < TimeSpan.FromDays(1))
                return false;
        }
        return true;
    }

    public static bool VerifySha256ConstantTime(byte[] actualHashBytes, string expectedHex)
    {
        if (actualHashBytes == null || actualHashBytes.Length != 32 || string.IsNullOrWhiteSpace(expectedHex))
            return false;
        var trimmed = expectedHex.Trim();
        if (!Hex64Regex.IsMatch(trimmed))
            return false;

        byte[] expectedBytes;
        try { expectedBytes = Convert.FromHexString(trimmed); }
        catch (FormatException) { return false; }

        return CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedBytes);
    }

    public static (bool Ok, string Error) VerifyDownloadedFile(
        string tempFilePath,
        string expectedSha256Hex,
        Func<string, byte[]> computeSha256Bytes,
        Func<string, SignatureCheckResult> verifySignature,
        Action<string> deleteFile,
        bool requireSignature = false)
    {
        byte[] actualBytes;
        try
        {
            actualBytes = computeSha256Bytes(tempFilePath);
        }
        catch (Exception ex)
        {
            Log.Write($"updater: hashing downloaded file failed: {ex.Message}");
            try { deleteFile(tempFilePath); } catch (Exception delEx) { Log.Write($"updater: temp cleanup failed: {delEx.Message}"); }
            return (false, Loc.T("update.err_hash"));
        }

        if (!VerifySha256ConstantTime(actualBytes, expectedSha256Hex))
        {
            try { deleteFile(tempFilePath); } catch (Exception delEx) { Log.Write($"updater: temp cleanup failed: {delEx.Message}"); }
            return (false, Loc.T("update.err_hash"));
        }

        var sigResult = verifySignature(tempFilePath);
        if (sigResult == SignatureCheckResult.Invalid || (requireSignature && sigResult != SignatureCheckResult.Valid))
        {
            try { deleteFile(tempFilePath); } catch (Exception delEx) { Log.Write($"updater: temp cleanup failed: {delEx.Message}"); }
            return (false, Loc.T("update.err_signature"));
        }

        return (true, "");
    }

    public static UpdateApplyPlan BuildApplyPlan(string newPath, string targetExe, int pid)
    {
        if (string.IsNullOrWhiteSpace(newPath))
            throw new ArgumentException("New binary path is required.", nameof(newPath));
        if (string.IsNullOrWhiteSpace(targetExe))
            throw new ArgumentException("Target executable path is required.", nameof(targetExe));

        var trimmedNew = newPath.Trim();
        var trimmedTarget = targetExe.Trim();
        if (trimmedNew.StartsWith(@"\\", StringComparison.Ordinal) || trimmedTarget.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("UNC paths are not allowed for update binary swap.");

        var fullNew = Path.GetFullPath(trimmedNew);
        var fullTarget = Path.GetFullPath(trimmedTarget);
        if (!fullNew.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !fullTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Both source and target update paths must be .exe files.");
        }

        if (string.Equals(fullNew, fullTarget, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("New binary path and target executable path must differ.");

        var backupPath = fullTarget + ".bak";
        var steps = new List<ApplyStepKind>
        {
            ApplyStepKind.WaitForProcessExit,
            ApplyStepKind.MoveTargetToBackup,
            ApplyStepKind.MoveNewToTarget,
            ApplyStepKind.StartAndVerifyTarget,
            ApplyStepKind.RestoreBackupOnFailure,
        };

        return new UpdateApplyPlan(
            fullNew,
            fullTarget,
            backupPath,
            pid,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            steps);
    }

    public static bool ExecuteApplyPlan(UpdateApplyPlan plan, IUpdateApplyHost host)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(host);

        if (string.IsNullOrWhiteSpace(plan.NewPath) || string.IsNullOrWhiteSpace(plan.TargetExe))
            return false;
        if (!host.FileExists(plan.NewPath))
            return false;

        if (plan.WaitPid > 0 && !host.WaitForProcessExit(plan.WaitPid, plan.WaitExitTimeout))
            return false;

        bool hadBackup = false;
        if (host.FileExists(plan.TargetExe))
        {
            try
            {
                host.MoveFile(plan.TargetExe, plan.BackupPath, overwrite: true);
                hadBackup = true;
            }
            catch (Exception ex)
            {
                Log.Write($"updater: backup move failed: {ex.Message}");
                return false;
            }
        }

        try
        {
            host.MoveFile(plan.NewPath, plan.TargetExe, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"updater: new binary move failed: {ex.Message}");
            if (hadBackup && host.FileExists(plan.BackupPath))
            {
                try { host.MoveFile(plan.BackupPath, plan.TargetExe, overwrite: true); }
                catch (Exception restoreEx) { Log.Write($"updater: backup restore failed: {restoreEx.Message}"); }
            }
            return false;
        }

        bool startedOk = false;
        try
        {
            startedOk = host.StartAndVerifyProcess(plan.TargetExe, plan.VerifyStartTimeout);
        }
        catch (Exception ex)
        {
            Log.Write($"updater: start and verify failed: {ex.Message}");
            startedOk = false;
        }

        if (!startedOk)
        {
            if (hadBackup && host.FileExists(plan.BackupPath))
            {
                try { host.MoveFile(plan.BackupPath, plan.TargetExe, overwrite: true); }
                catch (Exception restoreEx) { Log.Write($"updater: backup restore failed: {restoreEx.Message}"); }
            }
            return false;
        }

        return true;
    }
}
