using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MdReader.Rendering;

namespace MdReader.Export;

/// <summary>
/// The image PDF export, kept for platforms where the text engine is not available (Android).
/// Each page is rendered from the same view the reader draws and embedded as an image: the result
/// is faithful to the screen (fonts, right-to-left, tables, images) but its text is not selectable.
/// </summary>
public static class PdfImageExporter
{
    // A4 at 96 dpi: one unit below is one pixel of the rendered page.
    private const double PageWidth = 794;
    private const double PageHeight = 1123;

    private const double A4WidthPoints = 595.28;
    private const double A4HeightPoints = 841.89;

    private const int MaxPages = 300;

    public static int Export(string markdown, MarkdownRenderOptions options, Stream output)
    {
        var pageOptions = new MarkdownRenderOptions
        {
            FontFamilyName = options.FontFamilyName,
            BaseFontSize = options.BaseFontSize,
            Rtl = options.Rtl,
            DocumentDirectory = options.DocumentDirectory,
            MaxImageWidth = PageWidth - 100,

            // Search highlights belong to the reader, not to the exported file.
            SearchQuery = null,
        };

        var root = MarkdownRenderer.Render(markdown, pageOptions).Root;
        root.Width = PageWidth;
        root.Measure(new Size(PageWidth, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, PageWidth, root.DesiredSize.Height));

        var contentHeight = Math.Max(1, root.DesiredSize.Height);
        var pageCount = Math.Clamp((int)Math.Ceiling(contentHeight / PageHeight), 1, MaxPages);

        var pages = new List<(int Width, int Height, byte[] Rgb)>(pageCount);
        var pixelSize = new PixelSize((int)PageWidth, (int)PageHeight);

        for (var page = 0; page < pageCount; page++)
        {
            var canvas = new Canvas
            {
                Width = PageWidth,
                Height = PageHeight,
                ClipToBounds = true,
                Background = Brushes.White,
            };

            canvas.Children.Add(root);
            Canvas.SetTop(root, -page * PageHeight);

            canvas.Measure(new Size(PageWidth, PageHeight));
            canvas.Arrange(new Rect(0, 0, PageWidth, PageHeight));

            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            bitmap.Render(canvas);

            using var png = new MemoryStream();
            bitmap.Save(png, new PngBitmapEncoderOptions());
            png.Position = 0;
            pages.Add(PngImage.DecodeToRgb(png));

            // Detach the document again so the next page can re-host it.
            canvas.Children.Clear();
        }

        WritePdf(output, pages);
        return pageCount;
    }

    private static void WritePdf(Stream output, IReadOnlyList<(int Width, int Height, byte[] Rgb)> pages)
    {
        var document = new MemoryStream();
        var offsets = new List<long> { 0 };

        void WriteText(string text) => document.Write(Encoding.ASCII.GetBytes(text));

        void WriteBytes(byte[] bytes) => document.Write(bytes);

        // The binary comment after the header marks the file as containing binary data.
        WriteText("%PDF-1.4\n");
        WriteBytes(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

        void StartObject() => offsets.Add(document.Position);

        // Object 1 is the catalog and 2 the page tree, then three objects per page.
        StartObject();
        WriteText("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        StartObject();
        var kids = new StringBuilder();
        var pageObject = new int[pages.Count];

        for (var i = 0; i < pages.Count; i++)
        {
            pageObject[i] = 3 + (i * 3);
            kids.Append(CultureInfo.InvariantCulture, $"{pageObject[i]} 0 R ");
        }

        WriteText("2 0 obj\n<< /Type /Pages /Kids [" + kids.ToString().TrimEnd() + "] /Count " +
                  pages.Count.ToString(CultureInfo.InvariantCulture) + " >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var (width, height, rgb) = pages[i];
            var contentObject = pageObject[i] + 1;
            var imageObject = pageObject[i] + 2;

            StartObject();
            WriteText(string.Format(
                CultureInfo.InvariantCulture,
                "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {1:0.##} {2:0.##}] " +
                "/Resources << /XObject << /Im0 {3} 0 R >> >> /Contents {4} 0 R >>\nendobj\n",
                pageObject[i], A4WidthPoints, A4HeightPoints, imageObject, contentObject));

            var content = string.Format(
                CultureInfo.InvariantCulture,
                "q {0:0.##} 0 0 {1:0.##} 0 0 cm /Im0 Do Q\n",
                A4WidthPoints, A4HeightPoints);
            var contentBytes = Encoding.ASCII.GetBytes(content);

            StartObject();
            WriteText($"{contentObject} 0 obj\n<< /Length {contentBytes.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n");
            WriteBytes(contentBytes);
            WriteText("\nendstream\nendobj\n");

            var compressed = Deflate(rgb);

            StartObject();
            WriteText(string.Format(
                CultureInfo.InvariantCulture,
                "{0} 0 obj\n<< /Type /XObject /Subtype /Image /Width {1} /Height {2} " +
                "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {3} >>\nstream\n",
                imageObject, width, height, compressed.Length));
            WriteBytes(compressed);
            WriteText("\nendstream\nendobj\n");
        }

        var xrefOffset = document.Position;
        var xref = new StringBuilder();
        xref.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");

        for (var i = 1; i < offsets.Count; i++)
        {
            xref.Append(offsets[i].ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        xref.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        WriteText(xref.ToString());

        document.Position = 0;
        document.CopyTo(output);
        output.Flush();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();

        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }
}
