using System;
using System.Collections.Generic;
using System.Net;
using MonoTorrent;

namespace Golan.Gdm.Core.Torrent;

public sealed record MagnetInfo(
    string InfoHashHex,
    string? DisplayName,
    IReadOnlyList<string> Trackers,
    IReadOnlyList<string> HttpsSources,
    MagnetLink MagnetLink
);

public static class MagnetValidator
{
    public const int MaxMagnetUriLength = 8192;

    public static bool IsMagnetUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        ReadOnlySpan<char> trimmed = raw.AsSpan().Trim();
        if (trimmed.Length > MaxMagnetUriLength) return false;
        return trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParse(string? raw, out MagnetInfo? info, out string? error)
    {
        info = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = TorrentText.T("torrent.error.invalidMagnet");
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.Length > MaxMagnetUriLength)
        {
            error = TorrentText.T("torrent.error.invalidMagnet");
            return false;
        }

        foreach (char c in trimmed)
        {
            if (char.IsControl(c))
            {
                error = TorrentText.T("torrent.error.invalidMagnet");
                return false;
            }
        }

        if (!trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            error = TorrentText.T("torrent.error.invalidMagnet");
            return false;
        }

        string query = trimmed["magnet:?".Length..];
        if (string.IsNullOrWhiteSpace(query))
        {
            error = TorrentText.T("torrent.error.invalidMagnet");
            return false;
        }

        InfoHash? v1Hash = null;
        InfoHash? v2Hash = null;
        string? primaryHex = null;
        string? displayName = null;
        long? size = null;
        var trackers = new List<string>();
        var trackerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var httpsSources = new List<string>();
        var sourceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            string rawKey = pair[..eq].Trim();
            string rawVal = pair[(eq + 1)..].Trim();

            // Support numbered keys like xt.1, tr.1, xs.1, as.1 per BEP-9
            int dot = rawKey.IndexOf('.');
            string key = (dot > 0 ? rawKey[..dot] : rawKey).ToLowerInvariant();

            switch (key)
            {
                case "xt":
                {
                    string xtVal;
                    try
                    {
                        xtVal = Uri.UnescapeDataString(rawVal);
                    }
                    catch
                    {
                        error = TorrentText.T("torrent.error.invalidMagnet");
                        return false;
                    }

                    if (xtVal.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                    {
                        string hashPart = xtVal["urn:btih:".Length..];
                        if (!TryParseBtih(hashPart, out byte[]? hashBytes) || hashBytes is null)
                        {
                            error = TorrentText.T("torrent.error.invalidMagnet");
                            return false;
                        }

                        var parsedV1 = new InfoHash(hashBytes);
                        if (v1Hash != null && !v1Hash.Equals(parsedV1))
                        {
                            error = TorrentText.T("torrent.error.invalidMagnet");
                            return false;
                        }
                        v1Hash = parsedV1;
                        primaryHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                    else if (xtVal.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase))
                    {
                        string mhPart = xtVal["urn:btmh:".Length..];
                        if (!TryParseBtmh(mhPart, out byte[]? sha256Bytes) || sha256Bytes is null)
                        {
                            error = TorrentText.T("torrent.error.invalidMagnet");
                            return false;
                        }

                        var parsedV2 = new InfoHash(sha256Bytes);
                        if (v2Hash != null && !v2Hash.Equals(parsedV2))
                        {
                            error = TorrentText.T("torrent.error.invalidMagnet");
                            return false;
                        }
                        v2Hash = parsedV2;
                        primaryHex ??= Convert.ToHexString(sha256Bytes).ToLowerInvariant();
                    }
                    else
                    {
                        // Reject unsupported URN schemes in xt
                        error = TorrentText.T("torrent.error.invalidMagnet");
                        return false;
                    }
                    break;
                }

                case "dn":
                {
                    if (displayName is null && !string.IsNullOrWhiteSpace(rawVal))
                    {
                        try
                        {
                            string unescaped = Uri.UnescapeDataString(rawVal.Replace('+', ' ')).Trim();
                            if (!string.IsNullOrWhiteSpace(unescaped))
                                displayName = FileNames.Sanitize(unescaped);
                        }
                        catch (UriFormatException)
                        {
                            // Ignore malformed percent-encoding in optional magnet display name.
                        }
                    }
                    break;
                }

                case "xl":
                {
                    if (long.TryParse(rawVal, out long parsedSize) && parsedSize > 0)
                        size = parsedSize;
                    break;
                }

                case "tr":
                {
                    if (TryValidateTrackerUri(rawVal, out string? cleanTracker) && cleanTracker != null)
                    {
                        if (trackerSet.Add(cleanTracker))
                            trackers.Add(cleanTracker);
                    }
                    break;
                }

                case "xs" or "as" or "ws":
                {
                    // Ignore xs / as / ws unless strictly https://
                    if (TryValidateHttpsSourceUri(rawVal, out string? cleanHttps) && cleanHttps != null)
                    {
                        if (sourceSet.Add(cleanHttps))
                            httpsSources.Add(cleanHttps);
                    }
                    break;
                }
            }
        }

        if (v1Hash is null && v2Hash is null || string.IsNullOrEmpty(primaryHex))
        {
            error = TorrentText.T("torrent.error.invalidMagnet");
            return false;
        }

        var infoHashes = new InfoHashes(v1Hash, v2Hash);
        var magnetLink = new MagnetLink(infoHashes, displayName, trackers, httpsSources, size);
        info = new MagnetInfo(primaryHex, displayName, trackers, httpsSources, magnetLink);
        return true;
    }

    private static bool TryParseBtih(string hashPart, out byte[]? bytes)
    {
        bytes = null;
        if (hashPart.Length == 40)
        {
            if (!IsHex(hashPart)) return false;
            bytes = Convert.FromHexString(hashPart);
            return bytes.Length == 20;
        }

        if (hashPart.Length == 32)
        {
            return TryDecodeBase32(hashPart, out bytes) && bytes is { Length: 20 };
        }

        return false;
    }

    private static bool TryParseBtmh(string mhPart, out byte[]? sha256Bytes)
    {
        sha256Bytes = null;
        // BEP-52 multihash for SHA-256: 0x12 (SHA-256) 0x20 (32 bytes) followed by 64 hex chars = 68 chars total
        if (mhPart.Length != 68) return false;
        if (!mhPart.StartsWith("1220", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsHex(mhPart)) return false;

        sha256Bytes = Convert.FromHexString(mhPart[4..]);
        return sha256Bytes.Length == 32;
    }

    public static bool IsHex(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return false;
        foreach (char c in span)
        {
            bool isDigit = c >= '0' && c <= '9';
            bool isLower = c >= 'a' && c <= 'f';
            bool isUpper = c >= 'A' && c <= 'F';
            if (!isDigit && !isLower && !isUpper) return false;
        }
        return true;
    }

    private static bool TryDecodeBase32(string input, out byte[]? output)
    {
        output = null;
        if (input.Length != 32) return false;

        var result = new byte[20];
        int buffer = 0;
        int bitsLeft = 0;
        int outIndex = 0;

        foreach (char c in input)
        {
            int val;
            if (c >= 'A' && c <= 'Z') val = c - 'A';
            else if (c >= 'a' && c <= 'z') val = c - 'a';
            else if (c >= '2' && c <= '7') val = 26 + (c - '2');
            else return false;

            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                if (outIndex >= result.Length) return false;
                result[outIndex++] = (byte)((buffer >> bitsLeft) & 0xFF);
            }
        }

        if (outIndex != 20) return false;
        output = result;
        return true;
    }

    private static bool TryValidateTrackerUri(string rawEscaped, out string? cleanUri)
    {
        cleanUri = null;
        if (string.IsNullOrWhiteSpace(rawEscaped)) return false;

        string unescaped;
        try
        {
            unescaped = Uri.UnescapeDataString(rawEscaped).Trim();
        }
        catch
        {
            return false;
        }

        foreach (char c in unescaped)
        {
            if (c <= 0x20 || c == 0x7F) return false;
        }

        if (!Uri.TryCreate(unescaped, UriKind.Absolute, out var uri))
            return false;

        if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("udp" or "http" or "https"))
            return false;

        if (!IsSafeRemoteHost(uri.DnsSafeHost))
            return false;

        cleanUri = uri.AbsoluteUri;
        return true;
    }

    private static bool TryValidateHttpsSourceUri(string rawEscaped, out string? cleanUri)
    {
        cleanUri = null;
        if (string.IsNullOrWhiteSpace(rawEscaped)) return false;

        string unescaped;
        try
        {
            unescaped = Uri.UnescapeDataString(rawEscaped).Trim();
        }
        catch
        {
            return false;
        }

        foreach (char c in unescaped)
        {
            if (c <= 0x20 || c == 0x7F) return false;
        }

        if (!Uri.TryCreate(unescaped, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (!IsSafeRemoteHost(uri.DnsSafeHost))
            return false;

        cleanUri = uri.AbsoluteUri;
        return true;
    }

    public static bool IsSafeRemoteHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "ip6-localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "ip6-loopback", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)
                || ip.Equals(IPAddress.Any)
                || ip.Equals(IPAddress.IPv6Any)
                || ip.Equals(IPAddress.Broadcast)
                || ip.IsIPv6LinkLocal
                || ip.IsIPv6SiteLocal
                || ip.IsIPv6Multicast)
                return false;

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                byte[] b6 = ip.GetAddressBytes();
                // fc00::/7 (IPv6 Unique Local Address)
                if ((b6[0] & 0xFE) == 0xFC)
                    return false;
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                // 0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/4
                if (b[0] == 0
                    || b[0] == 10
                    || (b[0] == 100 && (b[1] & 0xC0) == 64)
                    || b[0] == 127
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || b[0] >= 224)
                    return false;
            }
        }
        return true;
    }
}
