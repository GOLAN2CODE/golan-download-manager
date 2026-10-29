using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

public sealed class MegaResolver : IHostResolver
{
    static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex ModernFilePathRx = new(@"^/file/([a-zA-Z0-9_-]{6,32})$", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex LegacyFragmentRx = new(@"^#!([a-zA-Z0-9_-]{6,32})!([a-zA-Z0-9_-]{40,48})$", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex ModernFragmentRx = new(@"^#([a-zA-Z0-9_-]{40,48})$", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);

    public bool CanHandle(Uri u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return TryParseLink(u, out _, out _);
    }

    public static bool TryParseLink(Uri u, out string fileId, out byte[] rawKeyBytes)
    {
        fileId = "";
        rawKeyBytes = [];
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("mega.nz" or "www.mega.nz" or "mega.co.nz" or "www.mega.co.nz"))
            return false;

        var path = u.AbsolutePath.TrimEnd('/');
        if (path.StartsWith("/folder/", StringComparison.OrdinalIgnoreCase)
            || u.Fragment.StartsWith("#F!", StringComparison.OrdinalIgnoreCase))
            return false;

        var modPath = ModernFilePathRx.Match(path);
        if (modPath.Success)
        {
            var modFrag = ModernFragmentRx.Match(u.Fragment);
            if (!modFrag.Success) return false;
            if (!TryDecodeBase64Url(modFrag.Groups[1].Value, out var key) || key.Length != 32)
                return false;

            fileId = modPath.Groups[1].Value;
            rawKeyBytes = key;
            return true;
        }

        if (string.IsNullOrEmpty(path) || path == "/")
        {
            var legFrag = LegacyFragmentRx.Match(u.Fragment);
            if (!legFrag.Success) return false;
            if (!TryDecodeBase64Url(legFrag.Groups[2].Value, out var key) || key.Length != 32)
                return false;

            fileId = legFrag.Groups[1].Value;
            rawKeyBytes = key;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Strips the secret <c>#key</c> fragment from a MEGA URL once the key has been stored in DPAPI-protected storage.
    /// </summary>
    public static string StripSecretFragment(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        if (!TryParseLink(u, out var fileId, out _)) return url;
        return $"https://{u.Host}/file/{fileId}";
    }

    public static (byte[] AesKey16, byte[] Nonce8) UnfoldKey(ReadOnlySpan<byte> rawKey32)
    {
        if (rawKey32.Length != 32)
            throw new ArgumentException("MEGA file key must be 32 bytes.", nameof(rawKey32));

        byte[] aesKey16 = new byte[16];
        for (int i = 0; i < 16; i++)
            aesKey16[i] = (byte)(rawKey32[i] ^ rawKey32[i + 16]);

        byte[] nonce8 = rawKey32.Slice(16, 8).ToArray();
        return (aesKey16, nonce8);
    }

    public static string? DecryptAttributesFileName(ReadOnlySpan<byte> encryptedAt, ReadOnlySpan<byte> aesKey16)
    {
        const int MaxAttributesBytes = 64 * 1024;
        if (encryptedAt.IsEmpty || encryptedAt.Length > MaxAttributesBytes || encryptedAt.Length % 16 != 0 || aesKey16.Length != 16)
            return null;

        try
        {
            using var aes = Aes.Create();
            aes.Key = aesKey16.ToArray();
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            byte[] decrypted = new byte[encryptedAt.Length];
            using (var dec = aes.CreateDecryptor())
            {
                byte[] cipherArr = encryptedAt.ToArray();
                dec.TransformBlock(cipherArr, 0, cipherArr.Length, decrypted, 0);
            }

            int len = decrypted.Length;
            while (len > 0 && decrypted[len - 1] == 0) len--;
            if (len <= 4) return null;

            var text = Encoding.UTF8.GetString(decrypted, 0, len);
            if (!text.StartsWith("MEGA{", StringComparison.Ordinal))
                return null;

            var jsonPart = text[4..];
            using var doc = JsonDocument.Parse(jsonPart);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("n", out var nEl) || nEl.ValueKind != JsonValueKind.String)
                return null;

            return HostResolvers.SanitizeDecodedFileName(nEl.GetString());
        }
        catch
        {
            return null;
        }
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!TryParseLink(u, out var fileId, out var rawKeyBytes))
            return null;

        var payload = $"[{{\"a\":\"g\",\"g\":1,\"p\":\"{fileId}\"}}]";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://g.api.mega.co.nz/cs")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var resp = await HostResolvers.SendFollowHttpsAsync(http, req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await HostResolvers.ReadBoundedStringAsync(resp.Content, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var first = doc.RootElement[0];
        if (first.ValueKind != JsonValueKind.Object)
            return null;

        if (!first.TryGetProperty("g", out var gEl) || gEl.ValueKind != JsonValueKind.String)
            return null;
        var gStr = gEl.GetString();
        if (string.IsNullOrWhiteSpace(gStr) || !Uri.TryCreate(gStr, UriKind.Absolute, out var directUri))
            return null;
        if (!HostResolvers.IsSafePublicHttpsUri(directUri))
            return null;
        var dlHost = directUri.Host.ToLowerInvariant();
        if (dlHost != "mega.co.nz" && dlHost != "mega.nz"
            && !dlHost.EndsWith(".mega.co.nz", StringComparison.Ordinal)
            && !dlHost.EndsWith(".mega.nz", StringComparison.Ordinal))
        {
            return null;
        }

        long size = -1;
        if (first.TryGetProperty("s", out var sEl) && sEl.TryGetInt64(out var parsedSize))
            size = parsedSize;

        string? fileName = null;
        if (first.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.String)
        {
            var atStr = atEl.GetString();
            if (!string.IsNullOrWhiteSpace(atStr) && TryDecodeBase64Url(atStr, out var atBytes))
            {
                var (aesKey16, _) = UnfoldKey(rawKeyBytes);
                fileName = DecryptAttributesFileName(atBytes, aesKey16);
            }
        }

        return new ResolvedLink(
            directUri,
            fileName ?? "mega_download.bin",
            size,
            HostResolvers.EmptyHeaders,
            NeedsMegaDecrypt: true,
            MegaKey: rawKeyBytes);
    }

    internal static bool TryDecodeBase64Url(string input, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(input)) return false;
        try
        {
            var s = input.Trim().Replace('-', '+').Replace('_', '/');
            int mod = s.Length % 4;
            if (mod == 2) s += "==";
            else if (mod == 3) s += "=";
            else if (mod == 1) return false;
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
