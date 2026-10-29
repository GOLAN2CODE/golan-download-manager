using System.Security.Cryptography;

namespace Golan.Gdm.Core.Licensing;

/// <summary>
/// The owner's licence-signing PUBLIC key (ECDSA P-256, SubjectPublicKeyInfo). The matching private key never ships:
/// it lives only on the owner's PC (DPAPI-protected under %USERPROFILE%\.golan-keys) and is used by the License Generator.
/// </summary>
public static class LicensePublicKey
{
    public const string SubjectPublicKeyInfo =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEtvogtHx9rnJdRkm9W1dya5sBQgSqasJzO4G4KeY+ldG0EJntQDiolEka0zN7wsYTd9EvTco4oYngtiuQw3odcw==";

    public static ECDsa Load()
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(SubjectPublicKeyInfo), out _);
        return key;
    }
}
