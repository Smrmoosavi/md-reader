using System.Text;

namespace MdReader.Export;

/// <summary>
/// Wraps an HTML fragment in the CF_HTML clipboard header that Windows applications (Word,
/// Outlook, LibreOffice) expect when pasting formatted text. Linux and macOS paste the plain
/// <c>text/html</c> flavour instead, which is set next to this one.
/// </summary>
public static class RichText
{
    private const string Header =
        "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

    public static string ToCfHtml(string fragment)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";

        var document = "<html><head><meta charset=\"utf-8\"></head><body>" +
                       startMarker + fragment + endMarker + "</body></html>";

        // The offsets are byte offsets in the UTF-8 payload, not character counts.
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(Header, 0, 0, 0, 0));
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(document[..(document.IndexOf(startMarker, System.StringComparison.Ordinal) + startMarker.Length)]);
        var endFragment = headerLength + Encoding.UTF8.GetByteCount(document[..document.IndexOf(endMarker, System.StringComparison.Ordinal)]);
        var endHtml = headerLength + Encoding.UTF8.GetByteCount(document);

        return string.Format(Header, headerLength, endHtml, startFragment, endFragment) + document;
    }
}
