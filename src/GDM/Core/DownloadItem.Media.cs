using System.Text.Json.Serialization;
using Golan.Gdm.Core.Resolvers;

namespace Golan.Gdm.Core;

public sealed partial class DownloadItem
{
    /// <summary>Optional yt-dlp media extras for this download.</summary>
    public MediaOptions? MediaOptions { get; set; }

    /// <summary>True when this item was resolved from a MEGA link and requires AES-128-CTR streaming decryption.</summary>
    public bool NeedsMegaDecrypt { get; set; }

    /// <summary>DPAPI-protected base64 MEGA raw key (never written in cleartext to disk).</summary>
    public string ProtectedMegaKey { get; set; } = "";

    /// <summary>Decrypted 32-byte MEGA key (never serialized to disk).</summary>
    [JsonIgnore]
    public byte[]? MegaKey
    {
        get
        {
            var plain = Secret.Unprotect(ProtectedMegaKey);
            if (string.IsNullOrEmpty(plain)) return null;
            try { return Convert.FromBase64String(plain); }
            catch (FormatException) { return null; }
        }
        set
        {
            ProtectedMegaKey = value is { Length: > 0 } ? Secret.Protect(Convert.ToBase64String(value)) : "";
            if (value is { Length: > 0 } && !string.IsNullOrEmpty(Url))
                Url = MegaResolver.StripSecretFragment(Url);
        }
    }

    public void SanitizeMegaUrlIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        if (Uri.TryCreate(Url, UriKind.Absolute, out var u) && MegaResolver.TryParseLink(u, out _, out var rawKeyBytes))
        {
            NeedsMegaDecrypt = true;
            MegaKey = rawKeyBytes;
            Url = MegaResolver.StripSecretFragment(Url);
        }
    }
}
