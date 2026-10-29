using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Golan.Gdm.Core.Torrent;

public interface IMagnetRegistry
{
    void SetValue(string subKey, string? valueName, string value);
    string? GetValue(string subKey, string? valueName);
    void DeleteTree(string subKey);
    bool KeyExists(string subKey);
}

public sealed class HkcuMagnetRegistry : IMagnetRegistry
{
    public void SetValue(string subKey, string? valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key?.SetValue(valueName ?? "", value, RegistryValueKind.String);
    }

    public string? GetValue(string subKey, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName ?? "") as string;
    }

    public void DeleteTree(string subKey)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Write("magnet registry delete failed: " + ex.Message);
        }
    }

    public bool KeyExists(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key is not null;
    }
}

public sealed class FakeMagnetRegistry : IMagnetRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public void SetValue(string subKey, string? valueName, string value)
    {
        string normKey = NormalizeKey(subKey);
        if (!_store.TryGetValue(normKey, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _store[normKey] = values;
        }
        values[valueName ?? ""] = value;
    }

    public string? GetValue(string subKey, string? valueName)
    {
        string normKey = NormalizeKey(subKey);
        if (_store.TryGetValue(normKey, out var values) &&
            values.TryGetValue(valueName ?? "", out string? val))
        {
            return val;
        }
        return null;
    }

    public void DeleteTree(string subKey)
    {
        string normKey = NormalizeKey(subKey);
        string prefix = normKey + @"\";
        var keysToRemove = _store.Keys
            .Where(k => k.Equals(normKey, StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string k in keysToRemove)
        {
            _store.Remove(k);
        }
    }

    public bool KeyExists(string subKey)
    {
        string normKey = NormalizeKey(subKey);
        string prefix = normKey + @"\";
        return _store.Keys.Any(k =>
            k.Equals(normKey, StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string subKey) =>
        (subKey ?? "").Trim().Trim('\\').Replace('/', '\\');
}

public sealed class MagnetProtocolRegistrar
{
    public const string MagnetBaseKey = @"Software\Classes\magnet";
    public const string DefaultIconKey = @"Software\Classes\magnet\DefaultIcon";
    public const string CommandKey = @"Software\Classes\magnet\shell\open\command";

    private readonly IMagnetRegistry _registry;

    public MagnetProtocolRegistrar(IMagnetRegistry? registry = null)
    {
        _registry = registry ?? new HkcuMagnetRegistry();
    }

    public void Register(string exePath)
    {
        ValidateExePath(exePath);

        _registry.SetValue(MagnetBaseKey, null, "URL:Magnet Protocol");
        _registry.SetValue(MagnetBaseKey, "URL Protocol", "");
        _registry.SetValue(DefaultIconKey, null, $"\"{exePath}\",0");
        _registry.SetValue(CommandKey, null, FormatCommand(exePath));
    }

    public void Unregister(string? onlyIfExePath = null)
    {
        if (!string.IsNullOrWhiteSpace(onlyIfExePath))
        {
            ValidateExePath(onlyIfExePath);
            if (!IsRegistered(onlyIfExePath))
                return;
        }

        _registry.DeleteTree(MagnetBaseKey);
    }

    public bool IsRegistered(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            ValidateExePath(exePath);
        }
        catch
        {
            return false;
        }

        if (!_registry.KeyExists(MagnetBaseKey)) return false;
        string? urlProtocol = _registry.GetValue(MagnetBaseKey, "URL Protocol");
        if (urlProtocol is null) return false;

        string? cmd = _registry.GetValue(CommandKey, null);
        return string.Equals(cmd, FormatCommand(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public void ApplySetting(bool registerMagnetProtocol, string? exePath = null)
    {
        string resolvedExe = exePath ?? Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(resolvedExe)) return;

        if (registerMagnetProtocol)
            Register(resolvedExe);
        else
            Unregister(resolvedExe);
    }

    public static string FormatCommand(string exePath)
    {
        ValidateExePath(exePath);
        return $"\"{exePath}\" \"%1\"";
    }

    private static void ValidateExePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path cannot be empty.", nameof(exePath));

        foreach (char c in exePath)
        {
            if (char.IsControl(c) || c is '"' or '<' or '>' or '|' or '?' or '*')
                throw new ArgumentException("Executable path contains invalid characters.", nameof(exePath));
        }

        if (!Path.IsPathRooted(exePath) || exePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Executable path must be a local rooted path.", nameof(exePath));

        string root = Path.GetPathRoot(exePath) ?? "";
        if (root.Length < 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] is not ('\\' or '/'))
            throw new ArgumentException("Executable path must be a full drive-rooted path.", nameof(exePath));

        // Reject NTFS Alternate Data Streams (any colon after drive separator at index 1)
        if (exePath.IndexOf(':', 2) >= 0)
            throw new ArgumentException("Executable path cannot contain alternate data streams.", nameof(exePath));

        string[] segments = exePath[3..].Split(['\\', '/'], StringSplitOptions.None);
        foreach (string seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg) || seg is "." or "..")
                throw new ArgumentException("Executable path cannot contain relative traversal segments.", nameof(exePath));
        }

        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Executable path must end with .exe.", nameof(exePath));
    }
}
