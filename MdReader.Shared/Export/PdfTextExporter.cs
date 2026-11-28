using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdReader.Rendering;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace MdReader.Export;

/// <summary>
/// Saves a document as a text PDF: every heading, paragraph, list, table and footnote is laid out
/// as real text, so the file can be searched, copied and read aloud, and Persian is shaped by the
/// same font the reader uses. Avalonia has no text PDF backend, so this exporter lays the Markdown
/// out a second time with QuestPDF.
/// </summary>
public static class PdfTextExporter
{
    private const string BodyFont = "Vazirmatn";

    private const string TextColor = "#1F2328";
    private const string MutedColor = "#5B6470";
    private const string RuleColor = "#D9DEE5";
    private const string CodeBackground = "#F4F6F8";
    private const string LinkColor = "#1F6FEB";
    private const string QuoteColor = "#C8D0DA";
    private const string TableHeaderBackground = "#F3F5F7";
    private const string InlineCodeBackground = "#EDF1F5";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static readonly float[] HeadingFactors = { 0, 1.75f, 1.45f, 1.25f, 1.12f, 1.05f, 1f };

    private static readonly object FontGate = new();
    private static bool _fontsRegistered;

    private sealed class ExportContext
    {
        public required string Source { get; init; }
        public required bool Rtl { get; init; }
        public required float BaseFontSize { get; init; }
        public string? DocumentDirectory { get; init; }
    }

    private readonly struct InlineStyle
    {
        public InlineStyle(bool bold = false, bool italic = false, bool strike = false)
        {
            Bold = bold;
            Italic = italic;
            Strike = strike;
        }

        public bool Bold { get; }
        public bool Italic { get; }
        public bool Strike { get; }

        public InlineStyle Derive(bool? bold = null, bool? italic = null, bool? strike = null) =>
            new(bold ?? Bold, italic ?? Italic, strike ?? Strike);
    }

    /// <summary>Whether the bundled Persian font could be registered; without it the caller uses
    /// the image exporter, so text never comes out in a font that cannot shape it.</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureFonts();
            return _fontsRegistered;
        }
    }

    public static int Export(string markdown, MarkdownRenderOptions options, Stream output)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // A document may contain a character the bundled font has no glyph for (an emoji, an
        // arrow); a page with one gap is better than an export that fails completely.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        EnsureFonts();

        var source = markdown ?? string.Empty;
        var parsed = Markdown.Parse(source, Pipeline);

        var context = new ExportContext
        {
            Source = source,
            Rtl = options.Rtl,
            BaseFontSize = (float)Math.Clamp(options.BaseFontSize * 0.72, 8, 20),
            DocumentDirectory = options.DocumentDirectory,
        };

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(48);
                page.MarginBottom(62);

                page.DefaultTextStyle(style =>
                {
                    var text = style.FontFamily(BodyFont)
                        .FontSize(context.BaseFontSize)
                        .FontColor(TextColor)
                        .LineHeight(1.45f);

                    // Persian reads right to left; the direction belongs to the text style, so
                    // every paragraph, list and table cell follows it.
                    return context.Rtl ? text.DirectionFromRightToLeft() : text.DirectionFromLeftToRight();
                });

                page.Content()
                    .Column(column =>
                    {
                        foreach (var block in parsed)
                        {
                            AddBlock(block, column, context, 0);
                        }
                    });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(9).FontColor(MutedColor));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        var bytes = document.GeneratePdf();
        output.Write(bytes, 0, bytes.Length);
        output.Flush();

        return CountPages(bytes);
    }

    private static void AddBlock(MdBlock block, ColumnDescriptor column, ExportContext context, int depth,
        bool forceBold = false, bool compact = false)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var factor = HeadingFactors[Math.Clamp(heading.Level, 1, 6)];
                var size = context.BaseFontSize * factor;

                column.Item()
                    .PaddingTop(heading.Level <= 2 ? 16 : 10)
                    .PaddingBottom(4)
                    .Text(text =>
                    {
                        text.DefaultTextStyle(style => style.FontSize(size).Bold());
                        AddInlines(text, heading.Inline, context, new InlineStyle(bold: true), null);
                    });

                if (heading.Level <= 2)
                {
                    column.Item().PaddingBottom(10).LineHorizontal(0.8f).LineColor(RuleColor);
                }

                break;
            }

            case ParagraphBlock paragraph:
            {
                var images = new List<string>();

                var body = column.Item().PaddingBottom(compact ? 3 : 8);

                (context.Rtl ? body.AlignRight() : body.AlignLeft())
                    .Text(text => AddInlines(text, paragraph.Inline, context, new InlineStyle(bold: forceBold), images));

                foreach (var url in images)
                {
                    AddImage(column, url, context);
                }

                break;
            }

            case MathBlock math:
                AddCode(column, ExtractSource(context.Source, math), context);
                break;

            case FencedCodeBlock fenced:
                AddCode(column, ExtractSource(context.Source, fenced), context);
                break;

            case CodeBlock code:
                AddCode(column, ExtractSource(context.Source, code), context);
                break;

            case ListBlock list:
            {
                var index = ParseStartIndex(list);

                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var marker = list.IsOrdered
                        ? (index++) + "."
                        : (depth % 3) switch
                        {
                            1 => "–",
                            2 => "·",
                            _ => "•",
                        };

                    column.Item().PaddingBottom(2).Row(row =>
                    {
                        row.ConstantItem(18).Text(marker).FontSize(context.BaseFontSize);
                        row.RelativeItem().Column(inner =>
                        {
                            foreach (var child in item)
                            {
                                AddBlock(child, inner, context, depth + 1, compact: true);
                            }
                        });
                    });
                }

                break;
            }

            case QuoteBlock quote:
                column.Item()
                    .PaddingBottom(8)
                    .BorderLeft(3)
                    .BorderColor(QuoteColor)
                    .PaddingLeft(12)
                    .Column(inner =>
                    {
                        foreach (var child in quote)
                        {
                            AddBlock(child, inner, context, depth);
                        }
                    });

                break;

            case ThematicBreakBlock:
                column.Item().PaddingVertical(6).LineHorizontal(0.8f).LineColor(RuleColor);
                break;

            case MdTable table:
                AddTable(table, column, context, depth);
                break;

            case FootnoteGroup footnotes:
                AddFootnotes(footnotes, column, context);
                break;

            case HtmlBlock html:
                AddPlain(column, StripTags(ExtractSource(context.Source, html)), context);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    AddBlock(child, column, context, depth, forceBold, compact);
                }

                break;
        }
    }

    private static void AddTable(MdTable table, ColumnDescriptor column, ExportContext context, int depth)
    {
        var columns = table.ColumnDefinitions?.Count ?? 0;

        if (columns == 0)
        {
            columns = table.OfType<MdTableRow>()
                .Select(row => row.OfType<MdTableCell>().Sum(cell => Math.Max(1, cell.ColumnSpan)))
                .DefaultIfEmpty(0)
                .Max();
        }

        columns = Math.Clamp(columns, 1, 12);

        column.Item().PaddingBottom(10).Table(grid =>
        {
            grid.ColumnsDefinition(definitions =>
            {
                for (var i = 0; i < columns; i++)
                {
                    definitions.RelativeColumn();
                }
            });

            foreach (var row in table.OfType<MdTableRow>())
            {
                foreach (var cell in row.OfType<MdTableCell>())
                {
                    var container = grid.Cell()
                        .Border(0.7f)
                        .BorderColor(RuleColor)
                        .Padding(4);

                    if (row.IsHeader)
                    {
                        container = container.Background(TableHeaderBackground);
                    }

                    container.Column(inner =>
                    {
                        foreach (var child in cell)
                        {
                            AddBlock(child, inner, context, depth, forceBold: row.IsHeader, compact: true);
                        }
                    });
                }
            }
        });
    }

    private static void AddFootnotes(FootnoteGroup group, ColumnDescriptor column, ExportContext context)
    {
        var notes = group.OfType<Footnote>().Where(note => note.Order > 0).ToList();
        if (notes.Count == 0)
        {
            return;
        }

        column.Item().PaddingTop(14).PaddingBottom(6).LineHorizontal(0.8f).LineColor(RuleColor);

        foreach (var note in notes)
        {
            column.Item().PaddingBottom(4).Row(row =>
            {
                row.ConstantItem(20)
                    .Text(note.Order.ToString(CultureInfo.CurrentCulture) + ".")
                    .FontSize(context.BaseFontSize * 0.85f)
                    .FontColor(MutedColor);

                row.RelativeItem().Column(inner =>
                {
                    foreach (var child in note)
                    {
                        AddBlock(child, inner, context, 0, compact: true);
                    }
                });
            });
        }
    }

    private static void AddCode(ColumnDescriptor column, string text, ExportContext context)
    {
        var code = TrimFences(text);
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        // Code is always read left to right, even inside a right-to-left document.
        column.Item()
            .PaddingBottom(10)
            .Background(CodeBackground)
            .Padding(8)
            .AlignLeft()
            .Text(descriptor => descriptor.Span(code)
                .FontFamily(BodyFont)
                .FontSize(context.BaseFontSize * 0.92f)
                .LineHeight(1.3f)
                .DirectionFromLeftToRight());
    }

    private static void AddPlain(ColumnDescriptor column, string text, ExportContext context)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        column.Item().PaddingBottom(8).Text(descriptor => descriptor.Span(text).FontSize(context.BaseFontSize));
    }

    private static void AddImage(ColumnDescriptor column, string url, ExportContext context)
    {
        var bytes = ImageLoader.TryLoadBytes(url, context.DocumentDirectory);
        if (bytes == null)
        {
            return;
        }

        try
        {
            column.Item().PaddingBottom(10).MaxWidth(380).Image(bytes);
        }
        catch (Exception)
        {
            // An image format the PDF engine cannot read is skipped instead of failing the export.
        }
    }

    private static void AddInlines(TextDescriptor text, ContainerInline? container, ExportContext context,
        InlineStyle style, List<string>? images)
    {
        if (container == null)
        {
            return;
        }

        foreach (var node in container)
        {
            switch (node)
            {
                case LiteralInline literal:
                    AddSpan(text, literal.Content.ToString(), style);
                    break;

                case EmphasisInline emphasis:
                {
                    var derived = emphasis.DelimiterChar switch
                    {
                        '~' => style.Derive(strike: true),
                        '^' => style.Derive(italic: true),
                        _ => emphasis.DelimiterCount >= 2 ? style.Derive(bold: true) : style.Derive(italic: true),
                    };

                    AddInlines(text, emphasis, context, derived, images);
                    break;
                }

                case CodeInline code:
                    text.Span(code.Content ?? string.Empty)
                        .BackgroundColor(InlineCodeBackground)
                        .FontFamily(BodyFont)
                        .FontSize(context.BaseFontSize * 0.9f);
                    break;

                case MathInline math:
                    text.Span(math.Content.ToString())
                        .FontFamily(BodyFont)
                        .FontColor("#334155")
                        .Italic();
                    break;

                case FootnoteLink footnote:
                    // The back link is a reading aid on screen; the PDF numbers its footnotes and
                    // leaves the return trip to the reader, so no extra glyph is needed here.
                    if (!footnote.IsBackLink)
                    {
                        text.Span("[" + footnote.Index.ToString(CultureInfo.CurrentCulture) + "]")
                            .FontSize(context.BaseFontSize * 0.75f)
                            .FontColor(LinkColor);
                    }

                    break;

                case LinkInline link when link.IsImage:
                    images?.Add(link.Url ?? string.Empty);
                    break;

                case LinkInline link:
                    text.Span(string.IsNullOrWhiteSpace(link.Label) ? link.Url ?? string.Empty : link.Label)
                        .FontColor(LinkColor)
                        .Underline();
                    break;

                case AutolinkInline autolink:
                    text.Span(autolink.Url ?? string.Empty).FontColor(LinkColor).Underline();
                    break;

                case TaskList task:
                    text.Span(task.Checked ? "[x] " : "[ ] ");
                    break;

                case LineBreakInline:
                    text.Span(" ");
                    break;

                case HtmlEntityInline entity:
                    AddSpan(text, entity.Transcoded.ToString(), style);
                    break;

                case ContainerInline nested:
                    AddInlines(text, nested, context, style, images);
                    break;
            }
        }
    }

    private static void AddSpan(TextDescriptor text, string value, InlineStyle style)
    {
        if (value.Length == 0)
        {
            return;
        }

        var span = text.Span(value);

        if (style.Bold)
        {
            span = span.Bold();
        }

        if (style.Italic)
        {
            span = span.Italic();
        }

        if (style.Strike)
        {
            span = span.Strikethrough();
        }
    }

    private static int ParseStartIndex(ListBlock block)
    {
        var raw = block.OrderedStart?.TrimEnd('.', ')', ' ');
        return int.TryParse(raw, out var start) && start >= 0 ? start : 1;
    }

    private static string ExtractSource(string source, LeafBlock block)
    {
        var start = block.Span.Start;
        var end = block.Span.End;

        if (start < 0 || end < start || end >= source.Length)
        {
            return string.Empty;
        }

        return source.Substring(start, end - start + 1);
    }

    private static string TrimFences(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        bool IsFence(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("```", StringComparison.Ordinal) ||
                   trimmed.StartsWith("~~~", StringComparison.Ordinal);
        }

        if (lines.Count > 0 && IsFence(lines[0]))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && IsFence(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines).Trim('\n');
    }

    private static string StripTags(string html)
    {
        var withoutTags = Regex.Replace(html, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(withoutTags).Trim();
    }

    /// <summary>The text PDF writes its pages in the clear, so counting the page objects is enough
    /// for the status message that reports how many pages were saved.</summary>
    private static int CountPages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var pages = Count(text, "/Type /Page") - Count(text, "/Type /Pages");
        return Math.Max(1, pages);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void EnsureFonts()
    {
        if (_fontsRegistered)
        {
            return;
        }

        lock (FontGate)
        {
            if (_fontsRegistered)
            {
                return;
            }

            // The bold face is registered as well, so <b> keeps its weight in the PDF.
            var regular = Register("Vazirmatn-Regular.ttf");
            Register("Vazirmatn-Bold.ttf");

            _fontsRegistered = regular;
        }
    }

    /// <summary>Registers one bundled face: first the Avalonia resource, then the copy that is
    /// shipped next to the executable for the PDF engine's own font discovery.</summary>
    private static bool Register(string fileName)
    {
        try
        {
            using var asset = AssetLoader.Open(new Uri("avares://MdReader.Shared/assets/fonts/" + fileName));
            FontManager.RegisterFontFromStream(asset);
            return true;
        }
        catch (Exception)
        {
            // Without an Avalonia application the asset loader may be unavailable.
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", fileName);
            if (!File.Exists(path))
            {
                return false;
            }

            using var file = File.OpenRead(path);
            FontManager.RegisterFontFromStream(file);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
