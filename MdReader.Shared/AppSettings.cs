using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MdReader;

/// <summary>
/// User preferences (font, size, direction and outline visibility) stored as a tiny
/// <c>key=value</c> file in the per-user application data folder, so they survive a restart
/// on Windows, Linux, macOS and Android without pulling in a serialization stack.
/// </summary>
public sealed class AppSettings
{
    private const string FileName = "settings.json";

    public string? FontFamilyName { get; set; }

    public double BaseFontSize { get; set; } = 16;

    public bool Rtl { get; set; }

    public bool OutlineVisible { get; set; }

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
                    case "rtl":
                        settings.Rtl = value == "1";
                        break;
                    case "outline":
                        settings.OutlineVisible = value == "1";
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
            builder.AppendLine($"rtl={(Rtl ? 1 : 0)}");
            builder.AppendLine($"outline={(OutlineVisible ? 1 : 0)}");

            File.WriteAllText(path, builder.ToString());
        }
        catch (Exception)
        {
            // Preferences are best-effort; failing to save them must never break the reader.
        }
    }
}
