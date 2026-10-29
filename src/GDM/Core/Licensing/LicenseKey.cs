using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Golan.Gdm.Core.Licensing;

/// <summary>What a license grants: one named person, one machine, until a date (null = lifetime).</summary>
public sealed record LicenseInfo(string Name, string Machine, DateTime IssuedUtc, DateTime? ExpiresUtc)
{
    public bool IsLifetime => ExpiresUtc is null;
}

public enum LicenseKeyError { None, Empty, Malformed, BadSignature, WrongMachine, Expired, SaveFailed }

/// <summary>
/// Activation keys: "GDM1.&lt;payload&gt;.&lt;signature&gt;" where the payload is compact JSON signed with the owner's
/// private ECDSA P-256 key. Only the owner's License Generator holds the private key; the app embeds the public key,
/// so a key cannot be forged or edited (name, machine or date) without breaking the signature.
/// This file is shared by the app and the generator and must stay free of app dependencies.
/// </summary>
public static class LicenseKey
{
    public const string Prefix = "GDM1";

    sealed record Payload(
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("m")] string Machine,
        [property: JsonPropertyName("i")] long Issued,
        [property: JsonPropertyName("e")] long Expires);

    public static string Create(LicenseInfo info, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (string.IsNullOrWhiteSpace(info.Name)) throw new ArgumentException("name required", nameof(info));
        var machine = NormalizeMachine(info.Machine);
        if (machine.Length != 16) throw new ArgumentException("machine code must have 16 characters", nameof(info));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(
            info.Name.Trim(), machine, ToUnix(info.IssuedUtc), info.ExpiresUtc is { } e ? ToUnix(e) : 0));
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Prefix + "." + Base64Url(payload) + "." + Base64Url(signature);
    }

    /// <summary>Checks the signature and that the key belongs to <paramref name="currentMachine"/>. Expiry is the caller's business.</summary>
    public static bool TryVerify(string? text, ECDsa publicKey, string currentMachine, out LicenseInfo? info, out LicenseKeyError error)
    {
        info = null;
        var key = Compact(text);
        if (key.Length == 0) { error = LicenseKeyError.Empty; return false; }
        var parts = key.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase)
            || !TryFromBase64Url(parts[1], out var payloadBytes) || !TryFromBase64Url(parts[2], out var signature)
            || payloadBytes.Length is 0 or > 4096)
        {
            error = LicenseKeyError.Malformed;
            return false;
        }
        bool valid;
        try { valid = publicKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        catch (CryptographicException) { valid = false; }
        if (!valid) { error = LicenseKeyError.BadSignature; return false; }

        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(payloadBytes); }
        catch (JsonException) { p = null; }
        if (p is null || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrEmpty(p.Machine)) { error = LicenseKeyError.Malformed; return false; }
        if (!string.Equals(p.Machine, NormalizeMachine(currentMachine), StringComparison.Ordinal)) { error = LicenseKeyError.WrongMachine; return false; }

        info = new LicenseInfo(p.Name, p.Machine, FromUnix(p.Issued), p.Expires > 0 ? FromUnix(p.Expires) : null);
        error = LicenseKeyError.None;
        return true;
    }

    /// <summary>Machine codes are compared without dashes, spaces or case ("abcd-efgh..." == "ABCDEFGH...").</summary>
    public static string NormalizeMachine(string? code)
        => new string((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Keys pasted from chats often gain spaces or line breaks; drop all whitespace.</summary>
    static string Compact(string? text) => new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryFromBase64Url(string s, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(s) || s.Length > 8192) return false;
        var b = s.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        try { data = Convert.FromBase64String(b); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>16 characters from an alphabet without look-alikes (no 0/O, 1/I), shown as XXXX-XXXX-XXXX-XXXX.</summary>
    public static string FormatMachine(byte[] digest)
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new StringBuilder(19);
        int bitBuffer = 0, bits = 0, written = 0;
        foreach (var b in digest)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bits += 8;
            while (bits >= 5 && written < 16)
            {
                bits -= 5;
                if (written > 0 && written % 4 == 0) sb.Append('-');
                sb.Append(Alphabet[(bitBuffer >> bits) & 31]);
                written++;
            }
            if (written == 16) break;
        }
        return sb.ToString();
    }
}
