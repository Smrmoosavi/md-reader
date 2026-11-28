using System;
using System.IO;
using MdReader.Rendering;

namespace MdReader.Export;

/// <summary>
/// Saves a document as a PDF. Desktop uses the text engine, so the result keeps real text: it can
/// be selected, searched and read by assistive tools, and Persian is shaped properly. Android keeps
/// the image exporter, where the native text engine is not available.
/// </summary>
public static class PdfExporter
{
    public static int Export(string markdown, MarkdownRenderOptions options, Stream output) =>
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || !PdfTextExporter.IsAvailable
            ? PdfImageExporter.Export(markdown, options, output)
            : PdfTextExporter.Export(markdown, options, output);
}
