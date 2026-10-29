using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.Integration;

public interface IInstallHost
{
    string CurrentExePath { get; }
    string LocalAppDataDir { get; }
    string StartMenuProgramsDir { get; }
    string DesktopDir { get; }
    string AppDataRoamingDir { get; }
    string AppDataLocalDir { get; }
    string Version { get; }
    bool IsPortable { get; }

    bool FileExists(string path);
    long GetFileSize(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void CreateShortcut(string shortcutPath, string targetExePath, string workingDir, string description, string iconPath);
    void SetRegistryValue(string subKey, string? valueName, object value, RegistryValueKind kind = RegistryValueKind.String);
    void DeleteRegistryValue(string subKey, string valueName);
    void DeleteRegistryKeyTree(string subKey);
    void RegisterBrowserHosts(string exePath);
    void UnregisterBrowserHosts();
    void ScheduleDelayedDeleteDir(string folderPath);
}

public enum InstallResult
{
    Installed,
    PortableSkipped,
    Failed,
}

public static partial class Installer
{
    public const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Golan.GDM";
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "Golan.GDM";

    static readonly char[] ForbiddenCmdChars = ['&', '|', ';', '"', '<', '>', '\r', '\n', '%', '!', '^', '`', '\0'];

    public static string GetTargetInstallDir(string localAppDataDir)
        => Path.Combine(localAppDataDir, "Programs", "Golan", "GDM");

    public static string GetTargetExePath(string localAppDataDir)
        => Path.Combine(GetTargetInstallDir(localAppDataDir), "GDM.exe");

    public static Dictionary<string, (object Value, RegistryValueKind Kind)> BuildUninstallValues(
        string targetExe,
        string installDir,
        string version,
        long exeSizeBytes)
    {
        int estimatedKb = (int)Math.Clamp((Math.Max(1L, exeSizeBytes) + 1023L) / 1024L, 1L, int.MaxValue);
        string displayVer = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim();
        return new Dictionary<string, (object Value, RegistryValueKind Kind)>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = ("Golan Download Manager", RegistryValueKind.String),
            ["DisplayIcon"] = (targetExe, RegistryValueKind.String),
            ["UninstallString"] = ($"\"{targetExe}\" --uninstall", RegistryValueKind.String),
            ["QuietUninstallString"] = ($"\"{targetExe}\" --uninstall --quiet", RegistryValueKind.String),
            ["InstallLocation"] = (installDir, RegistryValueKind.String),
            ["DisplayVersion"] = (displayVer, RegistryValueKind.String),
            ["Publisher"] = ("Golan", RegistryValueKind.String),
            ["EstimatedSize"] = (estimatedKb, RegistryValueKind.DWord),
            ["NoModify"] = (1, RegistryValueKind.DWord),
            ["NoRepair"] = (1, RegistryValueKind.DWord),
        };
    }

    public static bool CanDeleteInstallFolder(string? folderPath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return false;
        if (folderPath.IndexOfAny(ForbiddenCmdChars) >= 0) return false;

        try
        {
            var full = Path.GetFullPath(folderPath.Trim()).TrimEnd('\\', '/');
            if (full.IndexOfAny(ForbiddenCmdChars) >= 0) return false;

            var suffix = Path.DirectorySeparatorChar + Path.Combine("Programs", "Golan", "GDM");
            if (!full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;

            var exePath = Path.Combine(full, "GDM.exe");
            return fileExists(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string BuildDelayedDeleteCommand(string installDir)
    {
        var sanitized = Path.GetFullPath(installDir.Trim()).TrimEnd('\\', '/');
        if (sanitized.IndexOfAny(ForbiddenCmdChars) >= 0)
            throw new ArgumentException("Install directory contains invalid characters.", nameof(installDir));
        return $"cmd /c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{sanitized}\"";
    }

    public static InstallResult Install(IInstallHost host, bool createDesktopShortcut = true, bool startWithWindows = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.IsPortable)
            return InstallResult.PortableSkipped;

        var installDir = GetTargetInstallDir(host.LocalAppDataDir);
        var targetExe = GetTargetExePath(host.LocalAppDataDir);
        host.CreateDirectory(installDir);

        bool alreadyInPlace = false;
        try
        {
            alreadyInPlace = string.Equals(
                Path.GetFullPath(host.CurrentExePath.Trim()),
                Path.GetFullPath(targetExe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Treat invalid current exe path as not in place so CopyFile handles/reports it.
            Log.Write($"installer: path comparison fallback: {ex.Message}");
        }

        if (!alreadyInPlace)
            host.CopyFile(host.CurrentExePath, targetExe, overwrite: true);

        var desc = Loc.T("install.shortcut_desc");
        bool isAr = string.Equals(Loc.Language, "ar", StringComparison.OrdinalIgnoreCase);
        var groupName = isAr ? "جولان" : "Golan";
        var shortcutFileName = (isAr ? "جولان - مدير التنزيل" : "Golan Download Manager") + ".lnk";
        var startMenuGroupDir = Path.Combine(host.StartMenuProgramsDir, groupName);
        host.CreateDirectory(startMenuGroupDir);
        var startMenuLnk = Path.Combine(startMenuGroupDir, shortcutFileName);
        host.CreateShortcut(startMenuLnk, targetExe, installDir, desc, targetExe);

        if (createDesktopShortcut)
        {
            var desktopLnk = Path.Combine(host.DesktopDir, shortcutFileName);
            host.CreateShortcut(desktopLnk, targetExe, installDir, desc, targetExe);
        }

        long sizeBytes = host.GetFileSize(targetExe);
        foreach (var (name, entry) in BuildUninstallValues(targetExe, installDir, host.Version, sizeBytes))
            host.SetRegistryValue(UninstallSubKey, name, entry.Value, entry.Kind);

        host.RegisterBrowserHosts(targetExe);

        if (startWithWindows)
            host.SetRegistryValue(RunSubKey, RunValueName, $"\"{targetExe}\" --background", RegistryValueKind.String);

        return InstallResult.Installed;
    }

    public static bool Uninstall(IInstallHost host, bool removeUserData = false)
    {
        ArgumentNullException.ThrowIfNull(host);

        host.DeleteFile(Path.Combine(host.StartMenuProgramsDir, "جولان", "جولان - مدير التنزيل.lnk"));
        host.DeleteFile(Path.Combine(host.StartMenuProgramsDir, "Golan", "Golan Download Manager.lnk"));
        host.DeleteFile(Path.Combine(host.DesktopDir, "جولان - مدير التنزيل.lnk"));
        host.DeleteFile(Path.Combine(host.DesktopDir, "Golan Download Manager.lnk"));

        host.DeleteRegistryKeyTree(UninstallSubKey);
        host.UnregisterBrowserHosts();
        host.DeleteRegistryValue(RunSubKey, RunValueName);

        if (removeUserData)
        {
            if (!string.IsNullOrWhiteSpace(host.AppDataRoamingDir))
                host.DeleteDirectory(host.AppDataRoamingDir);
            if (!string.IsNullOrWhiteSpace(host.AppDataLocalDir))
                host.DeleteDirectory(host.AppDataLocalDir);
        }

        var installDir = GetTargetInstallDir(host.LocalAppDataDir);
        if (CanDeleteInstallFolder(installDir, host.FileExists))
            host.ScheduleDelayedDeleteDir(installDir);

        return true;
    }
}
