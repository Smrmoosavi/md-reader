using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MdReaderSetup;

internal static class SetupService
{
    public const string AppName = "MD Reader";
    public const string Version = "1.0.0";
    public const string ProgId = "MdReader.md";
    public const string ExeName = "MdReader.exe";
    public const string UninstallerName = "Uninstall.exe";

    private const string PayloadResource = "MdReaderSetup.payload.zip";
    private const string RegisteredAppName = "MdReader";
    private const string CapabilitiesKeyPath = @"Software\MdReader\Capabilities";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MdReader";
    private const string ApplicationKeyPath = @"Software\Classes\Applications\MdReader.exe";

    public static readonly string[] DefaultExtensions = { ".md", ".markdown", ".mdown" };

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MdReader");

    public static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", $"{AppName}.lnk");

    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

    public static string ExePath(string installDir) => Path.Combine(installDir, ExeName);

    public static string UninstallerPath(string installDir) => Path.Combine(installDir, UninstallerName);

    public static void Install(string installDir, IReadOnlyList<string> extensions, bool associate, bool startMenu, bool desktop)
    {
        installDir = Path.GetFullPath(installDir);
        Directory.CreateDirectory(installDir);

        ExtractPayload(installDir);
        CopyUninstaller(installDir);

        var executable = ExePath(installDir);
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException("فایل‌های برنامه در بسته‌ی نصب پیدا نشد.");
        }

        if (associate)
        {
            RegisterAssociations(executable, extensions);
        }

        if (startMenu)
        {
            CreateShortcut(StartMenuShortcutPath, executable);
        }

        if (desktop)
        {
            CreateShortcut(DesktopShortcutPath, executable);
        }

        WriteUninstallEntry(installDir, executable);
        NotifyShell();
    }

    public static void Uninstall(string installDir)
    {
        installDir = Path.GetFullPath(installDir);
        var extensions = ReadRegisteredExtensions();

        foreach (var extension in extensions)
        {
            RemoveAssociation(extension);
        }

        DeleteKeyTree($@"Software\Classes\{ProgId}");
        DeleteKeyTree(ApplicationKeyPath);
        DeleteKeyTree(@"Software\MdReader");
        DeleteKeyTree(UninstallKeyPath);

        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registered?.DeleteValue(RegisteredAppName, throwOnMissingValue: false);
        }

        DeleteFile(StartMenuShortcutPath);
        DeleteFile(DesktopShortcutPath);

        NotifyShell();
        ScheduleDirectoryRemoval(installDir);
    }

    public static IReadOnlyList<string> ReadRegisteredExtensions()
    {
        var extensions = new List<string>();

        using (var key = Registry.CurrentUser.OpenSubKey($@"{CapabilitiesKeyPath}\FileAssociations"))
        {
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        extensions.Add(name.ToLowerInvariant());
                    }
                }
            }
        }

        if (extensions.Count == 0)
        {
            extensions.AddRange(DefaultExtensions);
        }

        return extensions;
    }

    private static void ExtractPayload(string installDir)
    {
        using var payload = typeof(SetupService).Assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("بسته‌ی برنامه (payload.zip) در فایل نصب‌کننده موجود نیست.");

        ZipFile.ExtractToDirectory(payload, installDir, overwriteFiles: true);
    }

    private static void CopyUninstaller(string installDir)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
        {
            return;
        }

        var target = UninstallerPath(installDir);
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(self, target, overwrite: true);
    }

    private static void RegisterAssociations(string executable, IReadOnlyList<string> extensions)
    {
        var quotedExe = $"\"{executable}\"";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            if (progId == null)
            {
                throw new InvalidOperationException("ثبت نوع فایل در رجیستری ممکن نشد.");
            }

            progId.SetValue(null, "Markdown Document");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
            {
                icon?.SetValue(null, $"{quotedExe},0");
            }

            using (var command = progId.CreateSubKey(@"shell\open\command"))
            {
                command?.SetValue(null, $"{quotedExe} \"%1\"");
            }
        }

        foreach (var extension in extensions)
        {
            using (var extensionKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}"))
            {
                extensionKey?.SetValue(null, ProgId);
                using var openWith = extensionKey?.CreateSubKey("OpenWithProgids");
                openWith?.SetValue(ProgId, string.Empty);
            }

            using (var openWithProgids = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithProgids"))
            {
                openWithProgids?.SetValue(ProgId, string.Empty);
            }

            // A stale per-user choice would keep the old app as the handler.
            DeleteKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
        }

        using (var application = Registry.CurrentUser.CreateSubKey(ApplicationKeyPath))
        {
            application?.SetValue("FriendlyAppName", AppName);
            using (var icon = application?.CreateSubKey("DefaultIcon"))
            {
                icon?.SetValue(null, $"{quotedExe},0");
            }

            using (var command = application?.CreateSubKey(@"shell\open\command"))
            {
                command?.SetValue(null, $"{quotedExe} \"%1\"");
            }

            using (var supported = application?.CreateSubKey("SupportedTypes"))
            {
                foreach (var extension in extensions)
                {
                    supported?.SetValue(extension, string.Empty);
                }
            }
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath))
        {
            capabilities?.SetValue("ApplicationName", AppName);
            capabilities?.SetValue("ApplicationDescription", "خواننده‌ی سبک فایل‌های Markdown با پشتیبانی RTL");
            using var fileAssociations = capabilities?.CreateSubKey("FileAssociations");
            foreach (var extension in extensions)
            {
                fileAssociations?.SetValue(extension, ProgId);
            }
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registered?.SetValue(RegisteredAppName, CapabilitiesKeyPath);
        }
    }

    private static void RemoveAssociation(string extension)
    {
        using (var progIds = Registry.CurrentUser.OpenSubKey(
                   $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithProgids",
                   writable: true))
        {
            progIds?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        using (var extensionKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: true))
        {
            if (extensionKey != null)
            {
                if (extensionKey.GetValue(null) is string current && current == ProgId)
                {
                    extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                }

                using var openWith = extensionKey.OpenSubKey("OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }
    }

    private static void WriteUninstallEntry(string installDir, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        if (key == null)
        {
            return;
        }

        var uninstaller = UninstallerPath(installDir);

        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", $"\"{executable}\",0");
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(installDir), RegistryValueKind.DWord);
    }

    private static int EstimateSizeKb(string directory)
    {
        try
        {
            var total = 0L;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }

            return (int)Math.Max(1, total / 1024);
        }
        catch (IOException)
        {
            return 1;
        }
    }

    private static void CreateShortcut(string shortcutPath, string executable)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
            shortcut.IconLocation = $"{executable},0";
            shortcut.Description = $"{AppName} — {Version}";
            shortcut.Save();

            Marshal.ReleaseComObject(shell);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            // Shortcuts are a convenience; a failure here must not abort the install.
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignore files locked by the shell.
        }
    }

    private static void DeleteKeyTree(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore keys protected by policy.
        }
    }

    private static void ScheduleDirectoryRemoval(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir))
            {
                return;
            }

            var info = new ProcessStartInfo(
                "cmd.exe",
                $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{installDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process.Start(info);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The folder stays behind; the app itself is already unregistered.
        }
    }

    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            // Not fatal.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
