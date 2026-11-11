using System;
using System.Collections.Generic;
using System.IO;

namespace MdReaderSetup;

public sealed class SetupOptions
{
    public bool Uninstall { get; private set; }
    public bool Silent { get; private set; }
    public bool Associate { get; private set; } = true;
    public bool StartMenu { get; private set; } = true;
    public bool Desktop { get; private set; }
    public string InstallDir { get; private set; } = SetupService.DefaultInstallDir;
    public List<string> Extensions { get; } = new();

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();
        var explicitExtensions = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].Trim().ToLowerInvariant())
            {
                case "--uninstall":
                    options.Uninstall = true;
                    break;
                case "--silent":
                    options.Silent = true;
                    break;
                case "--no-associate":
                    options.Associate = false;
                    break;
                case "--no-shortcuts":
                    options.StartMenu = false;
                    options.Desktop = false;
                    break;
                case "--desktop":
                    options.Desktop = true;
                    break;
                case "--dir" when i + 1 < args.Length:
                    options.InstallDir = Path.GetFullPath(args[++i]);
                    break;
                case "--ext" when i + 1 < args.Length:
                    var ext = args[++i].Trim();
                    if (!ext.StartsWith('.'))
                    {
                        ext = "." + ext;
                    }

                    explicitExtensions.Add(ext.ToLowerInvariant());
                    break;
            }
        }

        options.Extensions.AddRange(explicitExtensions.Count > 0 ? explicitExtensions : SetupService.DefaultExtensions);

        if (options.Uninstall && explicitExtensions.Count == 0)
        {
            options.Extensions.Clear();
            options.Extensions.AddRange(SetupService.ReadRegisteredExtensions());
        }

        // When the uninstaller runs from the install folder, that folder is the install location.
        if (options.Uninstall && !HasDirectoryArgument(args))
        {
            var executableDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(executableDir))
            {
                options.InstallDir = executableDir;
            }
        }

        return options;
    }

    private static bool HasDirectoryArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
