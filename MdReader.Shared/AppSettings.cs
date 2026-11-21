using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MdReader;

/// <summary>
/// User preferences (font, size, direction, theme, language, text width, outline visibility,
/// auto-reload, library folder, recently opened files and the tabs of the last session) stored as a
/// tiny <c>key=value</c> file in the per-user application data folder, so they survive a restart on
/// Windows, Linux, macOS and Android without pulling in a serialization stack.
/// </summary>
public sealed class AppSettings
{
    private const string FileName = "settings.json";

    /// <summary>How many recently opened files are remembered.</summary>
    public const int MaxRecent = 12;

    /// <summary>How many tabs of the last session are reopened on start-up.</summary>
    public const int MaxSession = 8;

    public string? FontFamilyName { get; set; }

    public double BaseFontSize { get; set; } = 16;

    /// <summary><c>auto</c>, <c>rtl</c> or <c>ltr</c>.</summary>
    public string Direction { get; set; } = "auto";

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = AppTheme.System;

    /// <summary><c>fa</c> or <c>en</c>.</summary>
    public string Language { get; set; } = Lang.Persian;

    /// <summary><c>narrow</c>, <c>normal</c> or <c>wide</c>.</summary>
    public string Width { get; set; } = "normal";

    public bool OutlineVisible { get; set; }

    /// <summary>Re-render a document when its file changes on disk.</summary>
    public bool AutoReload { get; set; }

    /// <summary>The folder shown in the library panel.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>Most recently opened files, newest first.</summary>
    public List<string> Recent { get; } = new();

    /// <summary>Files that were open in tabs when the app last closed.</summary>
    public List<string> Session { get; } = new();

    private static string FilePath
    {
        get
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.GetTempPath();
            }

            return Path.Combine(folder, "MD Reader", FileName);
        }
    }

    public static AppSettings Load()
    {
        var settings = new AppSettings();

        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return settings;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                switch (key)
                {
                    case "font":
                        settings.FontFamilyName = value.Length == 0 ? null : value;
                        break;
                    case "size" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size):
                        settings.BaseFontSize = size;
                        break;

                    // Older versions stored a single RTL flag; keep honouring it.
                    case "rtl":
                        settings.Direction = value == "1" ? "rtl" : "auto";
                        break;
                    case "direction" when value.Length > 0:
                        settings.Direction = value.ToLowerInvariant() switch
                        {
                            "rtl" => "rtl",
                            "ltr" => "ltr",
                            _ => "auto",
                        };
                        break;
                    case "theme" when value.Length > 0:
                        settings.Theme = AppTheme.Normalize(value);
                        break;
                    case "language" when value.Length > 0:
                        settings.Language = value.Equals(Lang.English, StringComparison.OrdinalIgnoreCase)
                            ? Lang.English
                            : Lang.Persian;
                        break;
                    case "width" when value.Length > 0:
                        settings.Width = value.ToLowerInvariant() switch
                        {
                            "narrow" => "narrow",
                            "wide" => "wide",
                            _ => "normal",
                        };
                        break;
                    case "outline":
                        settings.OutlineVisible = value == "1";
                        break;
                    case "autoreload":
                        settings.AutoReload = value == "1";
                        break;
                    case "library":
                        settings.LibraryPath = value.Length == 0 ? null : value;
                        break;

                    // Both lists use one repeated key per entry, so no escaping is needed and a
                    // path may contain any character a filesystem allows.
                    case "recent" when value.Length > 0:
                        settings.AddRecent(value);
                        break;
                    case "session" when value.Length > 0:
                        if (!settings.Session.Contains(value, StringComparer.OrdinalIgnoreCase))
                        {
                            settings.Session.Add(value);
                        }

                        break;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable preferences fall back to the defaults.
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var builder = new StringBuilder();
            builder.AppendLine($"font={FontFamilyName}");
            builder.AppendLine("size=" + BaseFontSize.ToString("0.##", CultureInfo.InvariantCulture));
            builder.AppendLine($"direction={Direction}");
            builder.AppendLine($"theme={Theme}");
            builder.AppendLine($"language={Language}");
            builder.AppendLine($"width={Width}");
            builder.AppendLine($"outline={(OutlineVisible ? 1 : 0)}");
            builder.AppendLine($"autoreload={(AutoReload ? 1 : 0)}");
            builder.AppendLine($"library={LibraryPath}");

            foreach (var recent in Recent)
            {
                builder.AppendLine($"recent={recent}");
            }

            foreach (var tab in Session)
            {
                builder.AppendLine($"session={tab}");
            }

            File.WriteAllText(path, builder.ToString());
        }
        catch (Exception)
        {
            // Preferences are best-effort; failing to save them must never break the reader.
        }
    }

    /// <summary>Moves <paramref name="path"/> to the front of the recent list.</summary>
    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);

        while (Recent.Count > MaxRecent)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }
    }

    /// <summary>Forgets a file that no longer exists.</summary>
    public void RemoveRecent(string path) =>
        Recent.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
}
