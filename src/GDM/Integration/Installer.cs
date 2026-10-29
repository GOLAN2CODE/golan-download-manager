using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.Integration;

/// <summary>F4: Self-contained per-user installer and uninstaller (no MSI/Setup.exe, no UAC elevation).</summary>
public static partial class Installer
{
    static readonly string[] BrowserRegistryRoots =
    [
        @"Software\Google\Chrome",
        @"Software\Microsoft\Edge",
        @"Software\Mozilla",
        @"Software\BraveSoftware\Brave-Browser",
        @"Software\Chromium",
        @"Software\Opera Software",
        @"Software\Vivaldi",
    ];

    public static IReadOnlyList<string> AllBrowserNativeHostSubKeys { get; } =
        BrowserRegistryRoots.Select(r => $@"{r}\NativeMessagingHosts\{BrowserIntegration.HostName}").ToArray();

    public static int RunCliInstall(string[] args)
    {
        try
        {
            bool createDesktop = !args.Any(a => string.Equals(a, "--no-desktop", StringComparison.OrdinalIgnoreCase));
            bool startWithWin = args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));
            var host = new WindowsInstallHost();
            var res = Install(host, createDesktop, startWithWin);
            return res == InstallResult.Failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log.Write("install failed: " + ex);
            return 1;
        }
    }

    public static int RunCliUninstall(string[] args)
    {
        try
        {
            bool quiet = args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(a, "-q", StringComparison.OrdinalIgnoreCase));
            bool removeData = args.Any(a => string.Equals(a, "--remove-data", StringComparison.OrdinalIgnoreCase));

            if (!quiet)
            {
                var confirm = System.Windows.MessageBox.Show(
                    Loc.T("uninstall.confirm_body"),
                    Loc.T("uninstall.confirm_title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return 0;

                var dataChoice = System.Windows.MessageBox.Show(
                    Loc.T("uninstall.keep_data_prompt"),
                    Loc.T("uninstall.confirm_title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                removeData = dataChoice == MessageBoxResult.No;
            }

            var host = new WindowsInstallHost();
            if (host.IsPortable)
                return 0;
            bool ok = Uninstall(host, removeData);

            if (!quiet && ok)
            {
                System.Windows.MessageBox.Show(
                    Loc.T("uninstall.done"),
                    Loc.T("uninstall.confirm_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Write("uninstall failed: " + ex);
            return 1;
        }
    }
}

public sealed class WindowsInstallHost : IInstallHost
{
    public string CurrentExePath => AppPaths.ExePath;
    public string LocalAppDataDir => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string StartMenuProgramsDir => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string AppDataRoamingDir => AppPaths.DataDir;
    public string AppDataLocalDir => AppPaths.LocalDir;
    public string Version => typeof(WindowsInstallHost).Assembly.GetName().Version?.ToString(3) ?? App.Version;
    public bool IsPortable => AppPaths.IsPortable || AppPaths.IsIsolated;

    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public long GetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0L; }
        catch { return 0L; }
    }

    public void CreateDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            Directory.CreateDirectory(path);
    }

    public void CopyFile(string source, string destination, bool overwrite)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.Copy(source, destination, overwrite);
    }

    public void DeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Write("delete file failed (" + path + "): " + ex.Message);
        }
    }

    public void DeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Write("delete dir failed (" + path + "): " + ex.Message);
        }
    }

    public void CreateShortcut(string shortcutPath, string targetExePath, string workingDir, string description, string iconPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            ShellLinkCom.CreateShortcut(shortcutPath, targetExePath, workingDir, description, iconPath);
        }
        catch (Exception ex)
        {
            Log.Write("create shortcut failed (" + shortcutPath + "): " + ex.Message);
        }
    }

    public void SetRegistryValue(string subKey, string? valueName, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey);
            key.SetValue(valueName, value, kind);
        }
        catch (Exception ex)
        {
            Log.Write("registry set failed (" + subKey + "): " + ex.Message);
        }
    }

    public void DeleteRegistryValue(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("registry value delete failed (" + subKey + "): " + ex.Message);
        }
    }

    public void DeleteRegistryKeyTree(string subKey)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Write("registry key delete failed (" + subKey + "): " + ex.Message);
        }
    }

    public void RegisterBrowserHosts(string exePath)
    {
        try
        {
            var chromiumManifest = new JsonObject
            {
                ["name"] = BrowserIntegration.HostName,
                ["description"] = "Golan Download Manager",
                ["path"] = exePath,
                ["type"] = "stdio",
                ["allowed_origins"] = BrowserIntegration.BuildAllowedOriginsJson(),
            };
            Json.WriteAtomic(AppPaths.NativeManifest, chromiumManifest.ToJsonString(Json.Options));

            var firefoxManifest = new JsonObject
            {
                ["name"] = BrowserIntegration.HostName,
                ["description"] = "Golan Download Manager",
                ["path"] = exePath,
                ["type"] = "stdio",
                ["allowed_extensions"] = new JsonArray(BrowserIntegration.FirefoxExtensionId),
            };
            Json.WriteAtomic(BrowserIntegration.FirefoxNativeManifest, firefoxManifest.ToJsonString(Json.Options));

            foreach (var subKey in Installer.AllBrowserNativeHostSubKeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                bool isFirefox = subKey.Contains(@"Software\Mozilla\", StringComparison.OrdinalIgnoreCase);
                key.SetValue(null, isFirefox ? BrowserIntegration.FirefoxNativeManifest : AppPaths.NativeManifest);
            }

            BrowserIntegration.ExtractExtension();
        }
        catch (Exception ex)
        {
            Log.Write("register browser hosts failed: " + ex.Message);
        }
    }

    public void UnregisterBrowserHosts()
    {
        foreach (var subKey in Installer.AllBrowserNativeHostSubKeys)
        {
            DeleteRegistryKeyTree(subKey);
        }

        DeleteFile(AppPaths.NativeManifest);
        DeleteFile(BrowserIntegration.FirefoxNativeManifest);
    }

    public void ScheduleDelayedDeleteDir(string folderPath)
    {
        if (!Installer.CanDeleteInstallFolder(folderPath, FileExists))
            return;

        try
        {
            var cmdLine = Installer.BuildDelayedDeleteCommand(folderPath);
            // Strip leading "cmd " since FileName is cmd.exe
            var args = cmdLine.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase)
                ? cmdLine[4..]
                : "/c " + cmdLine;

            var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process.Start(new ProcessStartInfo(cmdExe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Exception ex)
        {
            Log.Write("delayed delete schedule failed: " + ex.Message);
        }
    }
}

internal static class ShellLinkCom
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description, string iconPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            if (!string.IsNullOrWhiteSpace(workingDir))
                link.SetWorkingDirectory(workingDir);
            if (!string.IsNullOrWhiteSpace(description))
                link.SetDescription(description);
            if (!string.IsNullOrWhiteSpace(iconPath))
                link.SetIconLocation(iconPath, 0);

            var file = (IPersistFile)link;
            file.Save(shortcutPath, fRemember: false);
        }
        finally
        {
            if (Marshal.IsComObject(link))
                Marshal.ReleaseComObject(link);
        }
    }
}
