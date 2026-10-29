using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;

namespace Golan.Gdm.Core;

public sealed partial class AppSettings
{
    [JsonIgnore]
    public string RemoteCertificateFingerprint => RemoteTls.CurrentFingerprintGroups;
}

/// <summary>
/// Manages the per-installation self-signed ECDSA P-256 TLS certificate for <see cref="Integration.Remote.RemoteServer"/>,
/// stored as a DPAPI-protected PFX under <c>AppPaths.LocalDir\remote\</c>.
/// </summary>
public static class RemoteTls
{
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const int ValidityDays = 730; // 2 years

    static readonly byte[] DpapiEntropy = "Golan.GDM.v2.RemoteTls"u8.ToArray();
    static readonly Lock Gate = new();
    static string _currentFingerprintHex = "";

    public static string RemoteDir => Path.Combine(AppPaths.LocalDir, "remote");
    public static string CertFilePath => Path.Combine(RemoteDir, "remote-cert.dpapi");

    public static string CurrentFingerprint
    {
        get { lock (Gate) return _currentFingerprintHex; }
    }

    public static string CurrentFingerprintGroups
    {
        get
        {
            string hex = CurrentFingerprint;
            return string.IsNullOrEmpty(hex) ? "" : FormatHexShortGroups(hex, ' ');
        }
    }

    /// <summary>
    /// Loads the existing DPAPI-protected self-signed certificate if it is valid and its SAN covers
    /// <paramref name="boundAddress"/> and <see cref="Environment.MachineName"/>; otherwise generates and persists a new one.
    /// </summary>
    public static X509Certificate2 EnsureCertificate(IPAddress boundAddress, IEnumerable<IPAddress>? additionalLanIps = null)
    {
        ArgumentNullException.ThrowIfNull(boundAddress);

        lock (Gate)
        {
            var existing = TryLoadExistingCertificate();
            if (existing != null)
            {
                if (IsCertificateValidFor(existing, boundAddress))
                {
                    _currentFingerprintHex = GetSha256FingerprintHex(existing);
                    return existing;
                }
                existing.Dispose();
            }

            return GenerateAndSaveCertificateCore(boundAddress, additionalLanIps);
        }
    }

    /// <summary>Forces regeneration of the self-signed certificate for <paramref name="boundAddress"/>.</summary>
    public static X509Certificate2 RegenerateCertificate(IPAddress boundAddress, IEnumerable<IPAddress>? additionalLanIps = null)
    {
        ArgumentNullException.ThrowIfNull(boundAddress);

        lock (Gate)
        {
            return GenerateAndSaveCertificateCore(boundAddress, additionalLanIps);
        }
    }

    public static bool IsCertificateValidFor(X509Certificate2? cert, IPAddress boundAddress)
    {
        if (cert is null || !cert.HasPrivateKey)
            return false;

        try
        {
            if (cert.Subject == null || !cert.Subject.Contains("Golan", StringComparison.OrdinalIgnoreCase))
                return false;

            using var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa is null || ecdsa.KeySize != 256)
                return false;

            var now = DateTime.UtcNow;
            if (cert.NotBefore.ToUniversalTime() > now.AddMinutes(10) ||
                cert.NotAfter.ToUniversalTime() < now.AddDays(14))
            {
                return false;
            }

            var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            if (san is null)
                return false;

            var sanIps = san.EnumerateIPAddresses().ToList();
            if (!sanIps.Any(ip => ip.Equals(IPAddress.Loopback)))
                return false;

            if (!boundAddress.Equals(IPAddress.Any) && !boundAddress.Equals(IPAddress.IPv6Any))
            {
                if (!sanIps.Any(ip => ip.Equals(boundAddress)))
                    return false;
            }

            var machineName = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(machineName))
            {
                var sanDns = san.EnumerateDnsNames().ToList();
                if (!sanDns.Any(d => string.Equals(d, machineName, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    static X509Certificate2? TryLoadExistingCertificate()
    {
        string path = CertFilePath;
        if (!File.Exists(path))
            return null;

        byte[]? pfxBytes = null;
        try
        {
            byte[] encryptedBlob = File.ReadAllBytes(path);
            if (encryptedBlob.Length == 0)
                return null;

            pfxBytes = UnprotectPfxBytes(encryptedBlob);
            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password: null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pfxBytes != null)
                CryptographicOperations.ZeroMemory(pfxBytes);
        }
    }

    static X509Certificate2 GenerateAndSaveCertificateCore(IPAddress boundAddress, IEnumerable<IPAddress>? additionalLanIps)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            "CN=Golan Remote Control",
            ecdsa,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));

        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                critical: true));

        var ekuOids = new OidCollection { new Oid(ServerAuthOid) };
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(ekuOids, critical: true));

        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        var addedIps = new HashSet<IPAddress>();

        void AddIp(IPAddress? ip)
        {
            if (ip is null || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
                return;
            if (addedIps.Add(ip))
                sanBuilder.AddIpAddress(ip);
        }

        AddIp(IPAddress.Loopback);
        AddIp(boundAddress);
        if (additionalLanIps != null)
        {
            foreach (var ip in additionalLanIps)
                AddIp(ip);
        }

        var addedDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDns(string? dns)
        {
            if (string.IsNullOrWhiteSpace(dns)) return;
            var clean = dns.Trim();
            if (addedDns.Add(clean))
                sanBuilder.AddDnsName(clean);
        }

        AddDns("localhost");
        AddDns(Environment.MachineName);

        req.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(ValidityDays);

        using var ephemeral = req.CreateSelfSigned(notBefore, notAfter);
        byte[]? pfxBytes = null;
        try
        {
            pfxBytes = ephemeral.Export(X509ContentType.Pfx);
            byte[] protectedBlob = ProtectPfxBytes(pfxBytes);

            string dir = RemoteDir;
            Directory.CreateDirectory(dir);
            string targetPath = CertFilePath;
            string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(tempPath, protectedBlob);
            File.Move(tempPath, targetPath, overwrite: true);

            var loaded = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password: null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

            _currentFingerprintHex = GetSha256FingerprintHex(loaded);
            return loaded;
        }
        finally
        {
            if (pfxBytes != null)
                CryptographicOperations.ZeroMemory(pfxBytes);
        }
    }

    /// <summary>Protects raw PKCS#12 bytes in memory using Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>).</summary>
    public static byte[] ProtectPfxBytes(byte[] pfxBytes)
    {
        ArgumentNullException.ThrowIfNull(pfxBytes);
        return ProtectedData.Protect(pfxBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>Unprotects a DPAPI-encrypted PKCS#12 blob (<see cref="DataProtectionScope.CurrentUser"/>).</summary>
    public static byte[] UnprotectPfxBytes(byte[] protectedBlob)
    {
        ArgumentNullException.ThrowIfNull(protectedBlob);
        return ProtectedData.Unprotect(protectedBlob, DpapiEntropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>Computes the 64-character uppercase hex SHA-256 fingerprint of <paramref name="cert"/>.</summary>
    public static string GetSha256FingerprintHex(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        byte[] hash = SHA256.HashData(cert.RawData);
        return Convert.ToHexString(hash);
    }

    /// <summary>Formats the certificate SHA-256 fingerprint as 16 short groups of 4 hex chars separated by spaces.</summary>
    public static string FormatFingerprintShortGroups(X509Certificate2 cert)
        => FormatHexShortGroups(GetSha256FingerprintHex(cert), ' ');

    /// <summary>Formats the certificate SHA-256 fingerprint as 16 short groups of 4 hex chars separated by hyphens for URL fragments.</summary>
    public static string FormatFingerprintUrlFragment(X509Certificate2 cert)
        => FormatHexShortGroups(GetSha256FingerprintHex(cert), '-');

    /// <summary>Formats the certificate SHA-256 fingerprint as colon-separated byte pairs (AA:BB:CC:...).</summary>
    public static string FormatFingerprintColonBytes(X509Certificate2 cert)
    {
        string hex = GetSha256FingerprintHex(cert);
        var sb = new StringBuilder(hex.Length + 31);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hex, i, 2);
        }
        return sb.ToString();
    }

    public static string FormatHexShortGroups(string hexOrGrouped, char separator = ' ')
    {
        string clean = NormalizeFingerprint(hexOrGrouped);
        if (clean.Length == 0) return "";
        var sb = new StringBuilder(clean.Length + (clean.Length / 4));
        for (int i = 0; i < clean.Length; i += 4)
        {
            if (i > 0) sb.Append(separator);
            int len = Math.Min(4, clean.Length - i);
            sb.Append(clean, i, len);
        }
        return sb.ToString();
    }

    /// <summary>Strips group separators (spaces, hyphens, colons) and returns uppercase hex.</summary>
    public static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return "";
        var sb = new StringBuilder(64);
        foreach (char c in fingerprint)
        {
            if (Uri.IsHexDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Builds the full HTTPS pairing URL including the <c>#fp=...</c> certificate fingerprint fragment.</summary>
    public static string BuildPairingUrl(Uri baseUri, X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(cert);
        return BuildPairingUrl(baseUri, FormatFingerprintUrlFragment(cert));
    }

    /// <summary>Builds the full HTTPS pairing URL including the <c>#fp=...</c> certificate fingerprint fragment.</summary>
    public static string BuildPairingUrl(Uri baseUri, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        string fragmentFp = FormatHexShortGroups(fingerprint, '-');
        string root = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/";
        return string.IsNullOrEmpty(fragmentFp) ? root : $"{root}#fp={fragmentFp}";
    }

    /// <summary>Constant-time verification of a server certificate against a pinned SHA-256 fingerprint.</summary>
    public static bool VerifyPinnedFingerprint(X509Certificate2? cert, string? expectedFingerprint)
    {
        if (cert is null || string.IsNullOrWhiteSpace(expectedFingerprint))
            return false;

        string normalizedExpected = NormalizeFingerprint(expectedFingerprint);
        if (normalizedExpected.Length != 64)
            return false;

        byte[] actualHash = SHA256.HashData(cert.RawData);
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(normalizedExpected);
        }
        catch
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>Creates a <see cref="SocketsHttpHandler"/> that validates the TLS server certificate by pinned SHA-256 fingerprint.</summary>
    public static SocketsHttpHandler CreatePinnedHttpHandler(string pinnedFingerprint)
    {
        string expected = NormalizeFingerprint(pinnedFingerprint);
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                    return VerifyPinnedFingerprint(cert2, expected);
                },
            },
        };
        return handler;
    }

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> that pins loopback (<c>127.0.0.1</c> / <c>localhost</c>) TLS connections
    /// to <see cref="CurrentFingerprint"/> while enforcing standard OS trust validation for all other hosts.
    /// </summary>
    public static SocketsHttpHandler CreateLoopbackPinnedHttpHandler()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (sender, cert, _, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors == SslPolicyErrors.None)
                        return true;
                    if (cert is null)
                        return false;

                    string? targetHost = (sender as SslStream)?.TargetHostName
                                         ?? (sender as HttpRequestMessage)?.RequestUri?.Host;
                    if (targetHost is not null &&
                        !string.Equals(targetHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(targetHost, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    string pinned = CurrentFingerprint;
                    if (string.IsNullOrEmpty(pinned))
                        return false;

                    using var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                    return VerifyPinnedFingerprint(cert2, pinned);
                },
            },
        };
        return handler;
    }

    /// <summary>Helper for looking up remote-tls i18n keys at runtime via <see cref="Loc.T"/>.</summary>
    public static string T(string key, params object?[] args)
        => args.Length == 0 ? Loc.T(key) : Loc.T(key, args);
}
