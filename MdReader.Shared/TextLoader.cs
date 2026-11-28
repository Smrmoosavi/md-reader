using System.Text;

namespace MdReader;

/// <summary>Decodes a Markdown file the same way on every platform and entry point.</summary>
public static class TextLoader
{
    static TextLoader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Read(byte[] bytes) => ReadWithEncoding(bytes).Text;

    /// <summary>Encodes an edited document for saving, putting back the byte-order mark the file
    /// came with. Without it a UTF-16 file would be written without its mark and would be read as
    /// Windows-1256 the next time.</summary>
    public static byte[] Encode(string text, Encoding encoding)
    {
        var bytes = encoding.GetBytes(text);
        var preamble = encoding.GetPreamble();

        if (preamble.Length == 0)
        {
            return bytes;
        }

        var result = new byte[preamble.Length + bytes.Length];
        preamble.CopyTo(result, 0);
        bytes.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>Decodes the file and reports the encoding it was written with, so an edited file
    /// is saved back in the same encoding instead of being rewritten as UTF-8.</summary>
    public static (string Text, Encoding Encoding) ReadWithEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);
        }

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            // Legacy Windows-1256 files (common for older Persian text).
            return (Encoding.GetEncoding(1256).GetString(bytes), Encoding.GetEncoding(1256));
        }
    }
}
