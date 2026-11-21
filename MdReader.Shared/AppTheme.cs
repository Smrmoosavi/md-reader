using System;
using Avalonia;
using Avalonia.Styling;

namespace MdReader;

/// <summary>Maps the saved theme preference onto Avalonia's theme variant.</summary>
public static class AppTheme
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    public static string Normalize(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };

    public static ThemeVariant VariantOf(string? theme) => Normalize(theme) switch
    {
        Light => ThemeVariant.Light,
        Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Applies the preference to the running application.</summary>
    public static void Apply(string? theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = VariantOf(theme);
        }
    }

    /// <summary>
    /// True when documents should be rendered with the dark palette. With the "system" preference
    /// this follows whatever the platform reported.
    /// </summary>
    public static bool IsDark
    {
        get
        {
            var current = Application.Current;
            if (current == null)
            {
                return false;
            }

            var variant = current.ActualThemeVariant;
            return variant == ThemeVariant.Dark;
        }
    }
}
