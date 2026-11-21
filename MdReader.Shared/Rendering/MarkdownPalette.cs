using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MdReader.Rendering;

/// <summary>
/// The colours a document is drawn with. The renderer builds plain controls, so the palette is
/// handed to it instead of resolved from the theme, which keeps the PDF export on white paper.
/// </summary>
public sealed class MarkdownPalette
{
    public required IBrush Text { get; init; }
    public required IBrush Muted { get; init; }
    public required IBrush Link { get; init; }
    public required IBrush Rule { get; init; }
    public required IBrush CodeBackground { get; init; }
    public required IBrush CodeBorder { get; init; }
    public required IBrush InlineCodeBackground { get; init; }
    public required IBrush QuoteBorder { get; init; }
    public required IBrush TableBorder { get; init; }
    public required IBrush TableHeaderBackground { get; init; }
    public required IBrush Search { get; init; }

    // Code highlighting.
    public required IBrush Keyword { get; init; }
    public required IBrush String { get; init; }
    public required IBrush Comment { get; init; }
    public required IBrush Number { get; init; }
    public required IBrush Type { get; init; }

    public static MarkdownPalette Light { get; } = new()
    {
        Text = Frozen(0x1F, 0x23, 0x28),
        Muted = Frozen(0x6B, 0x72, 0x80),
        Link = Frozen(0x0B, 0x62, 0xC4),
        Rule = Frozen(0xE3, 0xE8, 0xEE),
        CodeBackground = Frozen(0xF4, 0xF6, 0xF8),
        CodeBorder = Frozen(0xE1, 0xE6, 0xEB),
        InlineCodeBackground = Frozen(0xEF, 0xF2, 0xF5),
        QuoteBorder = Frozen(0xD5, 0xDD, 0xE5),
        TableBorder = Frozen(0xDD, 0xE3, 0xE9),
        TableHeaderBackground = Frozen(0xF5, 0xF7, 0xF9),
        Search = Frozen(0xFF, 0xE2, 0x8A),
        Keyword = Frozen(0xCF, 0x22, 0x2E),
        String = Frozen(0x0A, 0x30, 0x69),
        Comment = Frozen(0x6E, 0x77, 0x81),
        Number = Frozen(0x05, 0x50, 0xAE),
        Type = Frozen(0x82, 0x50, 0xDF),
    };

    public static MarkdownPalette Dark { get; } = new()
    {
        Text = Frozen(0xE6, 0xE9, 0xEC),
        Muted = Frozen(0xA0, 0xAA, 0xB5),
        Link = Frozen(0x6F, 0xB3, 0xF7),
        Rule = Frozen(0x2C, 0x32, 0x3A),
        CodeBackground = Frozen(0x1B, 0x20, 0x26),
        CodeBorder = Frozen(0x2E, 0x35, 0x3D),
        InlineCodeBackground = Frozen(0x26, 0x2C, 0x33),
        QuoteBorder = Frozen(0x3A, 0x43, 0x4D),
        TableBorder = Frozen(0x2E, 0x35, 0x3D),
        TableHeaderBackground = Frozen(0x22, 0x27, 0x2E),
        Search = Frozen(0x6B, 0x53, 0x20),
        Keyword = Frozen(0xF4, 0x7E, 0x84),
        String = Frozen(0xA5, 0xD6, 0xFF),
        Comment = Frozen(0x8B, 0x94, 0x9E),
        Number = Frozen(0x79, 0xC0, 0xFF),
        Type = Frozen(0xD2, 0xA8, 0xFF),
    };

    public static MarkdownPalette For(bool dark) => dark ? Dark : Light;

    private static IBrush Frozen(byte r, byte g, byte b) =>
        new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
}
