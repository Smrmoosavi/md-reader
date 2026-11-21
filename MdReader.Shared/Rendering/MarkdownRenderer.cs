using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdBlock = Markdig.Syntax.Block;

namespace MdReader.Rendering;

public sealed class MarkdownRenderOptions
{
    public string? FontFamilyName { get; set; }
    public double BaseFontSize { get; set; } = 16;
    public bool Rtl { get; set; }

    /// <summary>In-document search text; every match is highlighted while it is set.</summary>
    public string? SearchQuery { get; set; }

    /// <summary>Called for a <c>#anchor</c> link inside the document.</summary>
    public Action<string>? NavigateToAnchor { get; set; }

    /// <summary>Folder of the document; relative image paths are resolved against it.</summary>
    public string? DocumentDirectory { get; set; }

    /// <summary>Largest width an image may take, in pixels.</summary>
    public double MaxImageWidth { get; set; } = 560;

    /// <summary>Colours of the rendered document (light by default, so PDF pages stay white).</summary>
    public MarkdownPalette Palette { get; set; } = MarkdownPalette.Light;

    /// <summary>Maximum width of the text column; 0 lets the document use the whole viewport.</summary>
    public double MaxContentWidth { get; set; }
}

/// <summary>One entry of the document outline (a heading and the control it rendered into).</summary>
public sealed class OutlineEntry : INotifyPropertyChanged
{
    private bool _isActive;

    public OutlineEntry(int level, string title, Control target)
    {
        Level = level;
        Title = title;
        Target = target;
    }

    public int Level { get; }

    public string Title { get; }

    public Control Target { get; }

    /// <summary>True for the heading of the section currently at the top of the viewport.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Left/right indentation for the nested outline levels.</summary>
    public Thickness Indent => new((Math.Clamp(Level, 1, 6) - 1) * 12, 0, 0, 0);

    public override string ToString() => Title;
}

/// <summary>Rendered document plus the callbacks needed to re-scale it when the font size changes.</summary>
public sealed class RenderedMarkdown
{
    internal RenderedMarkdown(Control root) => Root = root;

    public Control Root { get; }

    /// <summary>Headings in document order, used to build the table of contents.</summary>
    public List<OutlineEntry> Outline { get; } = new();

    /// <summary>Auto identifier of each heading, so <c>#heading</c> links can jump to it.</summary>
    public Dictionary<string, Control> Anchors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The text block owning each search match, in reading order.</summary>
    public List<TextBlock> MatchHosts { get; } = new();

    /// <summary>How many images could actually be decoded and drawn.</summary>
    public int ImageCount { get; internal set; }

    internal List<Action<double>> SizeAppliers { get; } = new();
}

public static class MarkdownRenderer
{
    private const string MonoFontFamily = "Consolas, Menlo, DejaVu Sans Mono, monospace";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static IBrush Transparent { get; } = new SolidColorBrush(Colors.Transparent);

    private static readonly double[] HeadingFactors = { 0, 1.9, 1.55, 1.32, 1.16, 1.06, 1.0 };

    private sealed class RenderContext
    {
        public required string Source { get; init; }
        public required MarkdownRenderOptions Options { get; init; }
        public required RenderedMarkdown Result { get; init; }

        public bool Rtl => Options.Rtl;
        public double BaseSize => Options.BaseFontSize;
        public FlowDirection Direction => Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        public TextAlignment Align => Rtl ? TextAlignment.Right : TextAlignment.Left;

        public MarkdownPalette Palette => Options.Palette;

        public FontFamily Font => string.IsNullOrEmpty(Options.FontFamilyName)
            ? FontFamily.Default
            : new FontFamily(Options.FontFamilyName);
    }

    public static RenderedMarkdown Render(string markdown, MarkdownRenderOptions options)
    {
        markdown ??= string.Empty;

        var panel = new StackPanel
        {
            Margin = new Thickness(30, 22, 30, 48),
            FlowDirection = options.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };

        if (options.MaxContentWidth > 0)
        {
            panel.MaxWidth = options.MaxContentWidth;
            panel.HorizontalAlignment = HorizontalAlignment.Center;
        }

        var result = new RenderedMarkdown(panel);
        var context = new RenderContext { Source = markdown, Options = options, Result = result };

        var parsed = Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
        {
            AddBlock(block, panel, context, 0);
        }

        return result;
    }

    private static void AddBlock(MdBlock block, Panel target, RenderContext context, int depth, bool forceBold = false, bool compact = false)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AddHeading(heading, target, context);
                break;
            case ParagraphBlock paragraph:
                AddParagraph(paragraph, target, context, forceBold, compact);
                break;
            case FencedCodeBlock code:
                AddCodeBlock(code, target, context);
                break;
            case CodeBlock code:
                AddCodeBlock(code, target, context);
                break;
            case ListBlock list:
                AddList(list, target, context, depth);
                break;
            case QuoteBlock quote:
                AddQuote(quote, target, context, depth);
                break;
            case ThematicBreakBlock:
                target.Children.Add(new Border
                {
                    Height = 1,
                    Background = context.Palette.Rule,
                    Margin = new Thickness(0, 14, 0, 18),
                });
                break;
            case MdTable table:
                AddTable(table, target, context, depth);
                break;
            case HtmlBlock html:
                AddPlainBlock(ExtractSource(context.Source, html), target, context);
                break;
            case ContainerBlock container:
                // Custom containers, definition lists, footnote groups... render their children.
                foreach (var child in container)
                {
                    AddBlock(child, target, context, depth);
                }
                break;
        }
    }

    private static SelectableTextBlock CreateText(
        RenderContext context,
        double sizeFactor,
        bool bold = false,
        bool mono = false,
        double lineHeightFactor = 1.6,
        TextAlignment? align = null,
        bool leftToRight = false)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = mono ? new FontFamily(MonoFontFamily) : context.Font,
            FontSize = context.BaseSize * sizeFactor,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = context.Palette.Text,
            FlowDirection = leftToRight ? FlowDirection.LeftToRight : context.Direction,
            TextAlignment = align ?? (leftToRight ? TextAlignment.Left : context.Align),
            LineHeight = context.BaseSize * sizeFactor * lineHeightFactor,
        };

        context.Result.SizeAppliers.Add(size =>
        {
            text.FontSize = size * sizeFactor;
            text.LineHeight = size * sizeFactor * lineHeightFactor;
        });

        return text;
    }

    private static InlineCollection NewInlines(TextBlock owner)
    {
        var inlines = new InlineCollection();
        owner.Inlines = inlines;
        return inlines;
    }

    /// <summary>The document as HTML, used for pasting it into other applications as rich text.</summary>
    public static string ToHtml(string markdown) => Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    /// <summary>The document as plain text (no Markdown syntax), for the plain clipboard flavour.</summary>
    public static string ToPlainText(string markdown) => Markdown.ToPlainText(markdown ?? string.Empty, Pipeline);

    private static void AddHeading(HeadingBlock block, Panel target, RenderContext context)
    {
        var factor = HeadingFactors[Math.Clamp(block.Level, 1, 6)];
        var text = CreateText(context, factor, bold: true, lineHeightFactor: 1.35);
        text.Margin = new Thickness(0, block.Level <= 2 ? 20 : 16, 0, 6);

        AddInlines(block.Inline, text, NewInlines(text), new InlineStyle(), context);

        Control rendered;

        if (block.Level <= 2)
        {
            var border = new Border
            {
                Child = text,
                BorderBrush = context.Palette.Rule,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 6),
                Margin = new Thickness(0, 20, 0, 6),
            };

            text.Margin = new Thickness(0);
            target.Children.Add(border);
            rendered = border;
        }
        else
        {
            target.Children.Add(text);
            rendered = text;
        }

        var title = PlainText(block.Inline);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Lang.F("Section", context.Result.Outline.Count + 1);
        }

        context.Result.Outline.Add(new OutlineEntry(block.Level, title, rendered));

        // UseAdvancedExtensions includes the auto identifier extension, so each heading can be
        // reached through "#slug" links written in other documents. Documents written by hand
        // sometimes skip that extension's output, so a GitHub-style slug is derived as fallback.
        // AutoIdentifiers only produces ASCII ids (Persian headings all become "section"), so the
        // GitHub-style slug of the heading text is registered as well; both kinds of link work.
        var id = block.TryGetAttributes()?.Id;
        RegisterAnchor(context.Result, id, rendered);
        RegisterAnchor(context.Result, Slugify(title), rendered);
    }

    private static void AddParagraph(ParagraphBlock block, Panel target, RenderContext context, bool bold, bool compact)
    {
        var text = CreateText(context, 1.0, bold: bold);
        text.Margin = new Thickness(0, 0, 0, compact ? 0 : 12);
        AddInlines(block.Inline, text, NewInlines(text), new InlineStyle(bold: bold), context);
        target.Children.Add(text);
    }

    private static void AddCodeBlock(CodeBlock block, Panel target, RenderContext context)
    {
        var text = ExtractSource(context.Source, block);
        var lines = SplitLines(text);

        if (block is FencedCodeBlock)
        {
            if (lines.Count > 0 && IsFence(lines[0])) lines.RemoveAt(0);
            if (lines.Count > 0 && IsFence(lines[^1])) lines.RemoveAt(lines.Count - 1);
        }
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i] = RemoveIndent(lines[i], 4);
            }
        }

        AddPlainBlock(
            string.Join("\n", lines),
            target,
            context,
            (block as FencedCodeBlock)?.Info);
    }

    private static void AddPlainBlock(string text, Panel target, RenderContext context, string? language = null)
    {
        const double factor = 0.9;
        const double lineFactor = 1.5;

        var content = new SelectableTextBlock
        {
            FontFamily = new FontFamily(MonoFontFamily),
            FontSize = context.BaseSize * factor,
            Foreground = context.Palette.Text,
            TextWrapping = TextWrapping.NoWrap,
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Left,
            LineHeight = context.BaseSize * factor * lineFactor,
        };

        context.Result.SizeAppliers.Add(size =>
        {
            content.FontSize = size * factor;
            content.LineHeight = size * factor * lineFactor;
        });

        if (CodeHighlighter.Supports(language))
        {
            var inlines = new InlineCollection();
            content.Inlines = inlines;

            var inBlockComment = false;
            var lines = SplitLines(text);

            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    inlines.Add(new LineBreak());
                }

                foreach (var token in CodeHighlighter.Highlight(lines[i], language, ref inBlockComment))
                {
                    AddCodeRun(inlines, content, token, context);
                }
            }
        }
        else
        {
            content.Text = text;
        }

        var scroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        // Line numbers sit in their own column so they stay put while the code scrolls sideways.
        var code = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            FlowDirection = FlowDirection.LeftToRight,
        };

        var lineCount = SplitLines(text).Count;
        if (lineCount > 2)
        {
            var gutter = new TextBlock
            {
                Text = string.Join("\n", Enumerable.Range(1, lineCount)),
                FontFamily = new FontFamily(MonoFontFamily),
                FontSize = context.BaseSize * factor,
                LineHeight = context.BaseSize * factor * lineFactor,
                Foreground = context.Palette.Muted,
                TextAlignment = TextAlignment.Right,
                MinWidth = 18,
                Margin = new Thickness(0, 0, 10, 0),
            };

            context.Result.SizeAppliers.Add(size =>
            {
                gutter.FontSize = size * factor;
                gutter.LineHeight = size * factor * lineFactor;
            });

            code.Children.Add(gutter);
        }

        Grid.SetColumn(scroller, 1);
        code.Children.Add(scroller);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = language?.Trim() ?? string.Empty,
            FontSize = 11,
            FontFamily = new FontFamily(MonoFontFamily),
            Foreground = context.Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = !string.IsNullOrWhiteSpace(language),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var copy = new Button
        {
            Content = Lang.T("CodeCopy"),
            FontSize = 11,
            Padding = new Thickness(7, 1),
            MinHeight = 0,
            MinWidth = 0,
            Margin = new Thickness(0, 0, 0, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = context.Palette.Link,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        ToolTip.SetTip(copy, Lang.T("CodeCopy"));
        var source = text;
        copy.Click += async (_, _) => await CopyTextAsync(copy, source);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(code);

        var border = new Border
        {
            Background = context.Palette.CodeBackground,
            BorderBrush = context.Palette.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 9),
            Margin = new Thickness(0, 4, 0, 14),
            Child = body,
        };

        target.Children.Add(border);
    }

    /// <summary>Copies one code block as plain text; the status bar reports nothing, so a failed
    /// clipboard write stays silent instead of interrupting the reader.</summary>
    private static async System.Threading.Tasks.Task CopyTextAsync(Control source, string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(source)?.Clipboard;
            if (clipboard == null)
            {
                return;
            }

            var item = new DataTransferItem();
            item.Set(DataFormat.Text, text);

            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        catch (Exception)
        {
            // The clipboard may be unavailable; copying is best-effort.
        }
    }

    private static void AddList(ListBlock block, Panel target, RenderContext context, int depth)
    {
        // The document panel is mirrored as a whole in RTL mode, so margins, borders and column
        // order are all expressed in left-to-right terms and mirrored by the parent.
        var container = new StackPanel
        {
            Margin = new Thickness(22, 2, 0, 12),
        };

        var index = ParseStartIndex(block);

        foreach (var item in block.OfType<ListItemBlock>())
        {
            var marker = block.IsOrdered
                ? $"{index++}."
                : (depth % 3) switch
                {
                    1 => "◦",
                    2 => "▪",
                    _ => "•",
                };

            var markerText = new TextBlock
            {
                Text = marker,
                FontSize = context.BaseSize,
                Foreground = context.Palette.Text,
                FlowDirection = context.Direction,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 8, 0),
            };

            context.Result.SizeAppliers.Add(size => markerText.FontSize = size);

            var content = new StackPanel();
            foreach (var child in item)
            {
                AddBlock(child, content, context, depth + 1);
            }

            if (content.Children.Count == 0)
            {
                content.Children.Add(new TextBlock { FontSize = context.BaseSize });
            }

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            Grid.SetColumn(markerText, 0);
            Grid.SetColumn(content, 1);

            row.Children.Add(content);
            row.Children.Add(markerText);

            container.Children.Add(row);
        }

        if (container.Children.Count > 0)
        {
            target.Children.Add(container);
        }
    }

    private static void AddQuote(QuoteBlock block, Panel target, RenderContext context, int depth)
    {
        var content = new StackPanel();

        foreach (var child in block)
        {
            AddBlock(child, content, context, depth);
        }

        if (content.Children.Count == 0)
        {
            return;
        }

        target.Children.Add(new Border
        {
            Child = content,
            BorderBrush = context.Palette.QuoteBorder,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(14, 0, 2, 0),
            Margin = new Thickness(0, 6, 0, 12),
        });
    }

    private static void AddTable(MdTable table, Panel target, RenderContext context, int depth)
    {
        var columns = table.ColumnDefinitions?.Count ?? 0;
        if (columns == 0)
        {
            columns = table.OfType<MdTableRow>()
                .Select(row => row.OfType<MdTableCell>().Sum(cell => Math.Max(1, cell.ColumnSpan)))
                .DefaultIfEmpty(0)
                .Max();
        }

        columns = Math.Clamp(columns, 1, 32);

        var grid = new Grid();

        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }

        var rowIndex = 0;
        foreach (var row in table.OfType<MdTableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var columnIndex = 0;
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var content = new StackPanel();

                foreach (var child in cell)
                {
                    AddBlock(child, content, context, depth, forceBold: row.IsHeader, compact: true);
                }

                if (content.Children.Count == 0)
                {
                    content.Children.Add(new TextBlock { FontSize = context.BaseSize });
                }

                var border = new Border
                {
                    Child = content,
                    Background = row.IsHeader ? context.Palette.TableHeaderBackground : Transparent,
                    BorderBrush = context.Palette.TableBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(10, 6, 10, 6),
                };

                Grid.SetColumn(border, columnIndex);
                Grid.SetRow(border, rowIndex);
                Grid.SetColumnSpan(border, Math.Max(1, cell.ColumnSpan));
                Grid.SetRowSpan(border, Math.Max(1, cell.RowSpan));

                grid.Children.Add(border);
                columnIndex += Math.Max(1, cell.ColumnSpan);
            }

            rowIndex++;
        }

        target.Children.Add(new Border
        {
            Child = grid,
            BorderBrush = context.Palette.TableBorder,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 16),
        });
    }

    private static void AddInlines(ContainerInline? container, TextBlock host, InlineCollection target, InlineStyle style, RenderContext context)
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
                    AddRun(target, host, literal.Content.ToString(), style, context);
                    break;

                case EmphasisInline emphasis:
                    var emphasisStyle = emphasis.DelimiterChar switch
                    {
                        '~' => style.Derive(strike: true),
                        '^' => style.Derive(muted: true),
                        _ => emphasis.DelimiterCount >= 2
                            ? style.Derive(bold: true)
                            : style.Derive(italic: true),
                    };
                    AddInlines(emphasis, host, target, emphasisStyle, context);
                    break;

                case CodeInline code:
                    AddRun(target, host, code.Content ?? string.Empty, style.Derive(code: true), context);
                    break;

                case LinkInline link when link.IsImage:
                    if (!AddImage(link, target, context))
                    {
                        var alt = PlainText(link);
                        AddRun(target, host, string.IsNullOrWhiteSpace(alt) ? "[تصویر]" : $"[{alt}]",
                            style.Derive(italic: true, muted: true), context);
                    }

                    break;

                case LinkInline link:
                    AddLink(target, link.Label ?? PlainText(link), link.Url, style, context);
                    break;

                case AutolinkInline autolink:
                    AddLink(target, autolink.Url ?? string.Empty, autolink.Url, style, context);
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        target.Add(new LineBreak());
                    }
                    else
                    {
                        AddRun(target, host, " ", style, context);
                    }
                    break;

                case HtmlEntityInline entity:
                    AddRun(target, host, entity.Transcoded.ToString(), style, context);
                    break;

                case TaskList task:
                    AddRun(target, host, task.Checked ? "☑ " : "☐ ", style, context);
                    break;

                case HtmlInline:
                    break;

                case ContainerInline nested:
                    AddInlines(nested, host, target, style, context);
                    break;
            }
        }
    }

    /// <summary>
    /// Draws an inline image. Relative paths are resolved against the document folder; when the
    /// image cannot be read (remote URL, Android content document without a folder) the caller
    /// falls back to the alt text.
    /// </summary>
    private static bool AddImage(LinkInline link, InlineCollection target, RenderContext context)
    {
        var image = ImageLoader.TryLoad(link.Url, context.Options.DocumentDirectory);
        if (image == null)
        {
            return false;
        }

        var control = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            MaxWidth = context.Options.MaxImageWidth,
            MaxHeight = 900,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var alt = PlainText(link);
        if (alt.Length > 0)
        {
            ToolTip.SetTip(control, alt);
        }

        target.Add(new InlineUIContainer
        {
            Child = new Border
            {
                Child = control,
                Margin = new Thickness(0, 4, 0, 8),
            },
        });

        context.Result.ImageCount++;
        return true;
    }

    private static void AddCodeRun(InlineCollection target, TextBlock host, CodeToken token, RenderContext context)
    {
        var before = target.Count;
        AddRun(target, host, token.Text, new InlineStyle(), context);

        var palette = context.Palette;
        var brush = token.Kind switch
        {
            CodeTokenKind.Keyword => palette.Keyword,
            CodeTokenKind.String => palette.String,
            CodeTokenKind.Comment => palette.Comment,
            CodeTokenKind.Number => palette.Number,
            CodeTokenKind.Type => palette.Type,
            _ => null,
        };

        if (brush == null)
        {
            return;
        }

        // A search match keeps its highlight background; everything else gets the token colour.
        for (var i = before; i < target.Count; i++)
        {
            if (target[i] is Run run && run.Background == null)
            {
                run.Foreground = brush;
            }
        }
    }

    private static void AddLink(InlineCollection target, string? label, string? url, InlineStyle style, RenderContext context)
    {
        var text = string.IsNullOrWhiteSpace(label) ? url : label;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var link = new TextBlock
        {
            Text = text,
            Foreground = context.Palette.Link,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            FontWeight = style.Bold ? FontWeight.SemiBold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
            FontFamily = context.Font,
            FlowDirection = context.Direction,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        if (!string.IsNullOrEmpty(url) && url.TrimStart().StartsWith('#'))
        {
            // "#heading" is a jump inside the same document, not something to open externally.
            var anchor = url.TrimStart().TrimStart('#');
            link.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                context.Options.NavigateToAnchor?.Invoke(anchor);
            };
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsExternalScheme(uri))
        {
            link.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(link, uri);
            };
        }

        target.Add(new InlineUIContainer { Child = link });
    }

    private static void AddRun(InlineCollection target, TextBlock host, string? text, InlineStyle style, RenderContext context)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var query = context.Options.SearchQuery;
        if (string.IsNullOrEmpty(query))
        {
            target.Add(CreateRun(text, style, context));
            return;
        }

        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                target.Add(CreateRun(text[index..], style, context));
                break;
            }

            if (found > index)
            {
                target.Add(CreateRun(text[index..found], style, context));
            }

            var match = CreateRun(text.Substring(found, query.Length), style, context);
            match.Background = context.Palette.Search;
            target.Add(match);
            context.Result.MatchHosts.Add(host);

            index = found + query.Length;
        }
    }

    private static Run CreateRun(string text, InlineStyle style, RenderContext context)
    {
        var run = new Run(text);

        if (style.Bold) run.FontWeight = FontWeight.SemiBold;
        if (style.Italic) run.FontStyle = FontStyle.Italic;
        if (style.Strike) run.TextDecorations = TextDecorations.Strikethrough;
        if (style.Muted) run.Foreground = context.Palette.Muted;

        if (style.Code)
        {
            const double factor = 0.9;
            run.FontFamily = new FontFamily(MonoFontFamily);
            run.Background = context.Palette.InlineCodeBackground;
            run.Foreground = context.Palette.Text;
            run.FontSize = context.BaseSize * factor;
            context.Result.SizeAppliers.Add(size => run.FontSize = size * factor);
        }

        return run;
    }

    private static void RegisterAnchor(RenderedMarkdown result, string? id, Control target)
    {
        if (!string.IsNullOrWhiteSpace(id) && !result.Anchors.ContainsKey(id))
        {
            result.Anchors[id] = target;
        }
    }

    private static string Slugify(string title)
    {
        var builder = new StringBuilder(title.Length);
        var previousDash = false;

        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousDash = false;
            }
            else if (ch is ' ' or '-' or '_' && !previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string PlainText(ContainerInline? container)
    {
        if (container == null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var node in container.FindDescendants<Markdig.Syntax.Inlines.Inline>())
        {
            switch (node)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
            }
        }

        return builder.ToString().Trim();
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

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static bool IsFence(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static string RemoveIndent(string line, int max)
    {
        var index = 0;
        while (index < line.Length && index < max && line[index] == ' ')
        {
            index++;
        }

        return line[index..];
    }

    private static bool IsExternalScheme(Uri uri) =>
        uri.Scheme is "http" or "https" or "mailto";

    private static async void OpenExternal(Control source, Uri uri)
    {
        if (!IsExternalScheme(uri))
        {
            return;
        }

        // Launcher works on every platform; Process.Start cannot open a URL on Android.
        try
        {
            var launcher = TopLevel.GetTopLevel(source)?.Launcher;
            if (launcher != null)
            {
                await launcher.LaunchUriAsync(uri);
                return;
            }
        }
        catch
        {
            // Fall through to the desktop shell.
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is best-effort; ignore failures.
        }
    }

    private readonly struct InlineStyle
    {
        public InlineStyle(bool bold = false, bool italic = false, bool strike = false, bool code = false, bool muted = false)
        {
            Bold = bold;
            Italic = italic;
            Strike = strike;
            Code = code;
            Muted = muted;
        }

        public bool Bold { get; }
        public bool Italic { get; }
        public bool Strike { get; }
        public bool Code { get; }
        public bool Muted { get; }

        public InlineStyle Derive(bool? bold = null, bool? italic = null, bool? strike = null, bool? code = null, bool? muted = null) =>
            new(bold ?? Bold, italic ?? Italic, strike ?? Strike, code ?? Code, muted ?? Muted);
    }
}
