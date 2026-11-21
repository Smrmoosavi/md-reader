using System.Text.RegularExpressions;

namespace MdReader;

/// <summary>
/// Guesses the writing direction of a document from its first strong character, the same way
/// editors do: Persian, Arabic and Hebrew text is right to left, Latin text is left to right and a
/// document without any strong character keeps the previous choice.
/// </summary>
public static class TextDirection
{
    private const string RtlRanges =
        @"\u0590-\u05FF\u0600-\u06FF\u0700-\u074F\u0750-\u077F\u08A0-\u08FF\uFB50-\uFDFF\uFE70-\uFEFF";

    // A strong character is skipped by Markdown syntax, digits, punctuation and whitespace, so
    // "# ۳. عنوان" and "1. Title" both report their real script.
    private static readonly Regex StrongCharacter = new($@"[{RtlRanges}A-Za-z\u00C0-\u024F]", RegexOptions.Compiled);

    /// <summary>True when the first strong character of <paramref name="text"/> is right to left.</summary>
    public static bool IsRtl(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = StrongCharacter.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var ch = match.Value[0];
        return ch is >= '\u0590' and <= '\u08FF' or >= '\uFB50' and <= '\uFEFF';
    }
}
