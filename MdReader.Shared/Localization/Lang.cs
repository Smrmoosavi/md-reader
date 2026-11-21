using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace MdReader;

/// <summary>
/// The interface language. Every user-facing string lives in one of the
/// <c>Localization/Strings.*.axaml</c> dictionaries, so XAML reads them through
/// <c>{DynamicResource Key}</c> and code reads them through <see cref="T"/>; switching the language
/// swaps the merged dictionary and the markup follows without a restart.
/// </summary>
public static class Lang
{
    public const string Persian = "fa";
    public const string English = "en";

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [Persian] = "فارسی",
        [English] = "English",
    };

    private static string _code = Persian;

    /// <summary>Raised after the interface language changed.</summary>
    public static event EventHandler? Changed;

    public static string Code => _code;

    /// <summary>True when the interface itself is laid out right to left.</summary>
    public static bool IsRtl => _code != English;

    public static string DisplayName(string code) => Names.TryGetValue(code, out var name) ? name : code;

    public static IEnumerable<string> Supported => Names.Keys;

    /// <summary>The localized text of <paramref name="key"/>, or the key itself when it is missing.</summary>
    public static string T(string key)
    {
        var host = Application.Current;
        return host != null && host.TryFindResource(key, out var value) && value is string text ? text : key;
    }

    /// <summary>The localized text of <paramref name="key"/> with <see cref="string.Format(string,object?)"/> applied.</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Switches the interface language and swaps its resource dictionary.</summary>
    public static void Apply(string? code)
    {
        var wanted = code is not null && Names.ContainsKey(code) ? code.ToLowerInvariant() : Persian;
        var host = Application.Current;
        var changed = !string.Equals(wanted, _code, StringComparison.Ordinal);
        _code = wanted;

        if (host == null)
        {
            return;
        }

        var dictionaries = host.Resources.MergedDictionaries;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i] is ResourceInclude include && IsLanguageDictionary(include))
            {
                dictionaries.RemoveAt(i);
            }
        }

        var uri = new Uri($"avares://MdReader.Shared/Localization/Strings.{_code}.axaml");
        dictionaries.Add(new ResourceInclude(uri) { Source = uri });

        if (changed)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static bool IsLanguageDictionary(ResourceInclude include) =>
        include.Source?.OriginalString.Contains("/Localization/Strings.", StringComparison.OrdinalIgnoreCase) == true;
}
