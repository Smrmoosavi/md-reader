using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MdReader.Rendering;

/// <summary>
/// Resolves the <c>src</c> of a Markdown image without any network access: <c>data:</c> URIs and
/// image files. Relative paths are resolved against the folder of the document, which is why the
/// renderer takes <see cref="MarkdownRenderOptions.DocumentDirectory"/>.
/// </summary>
public static class ImageLoader
{
    private static readonly ConcurrentDictionary<string, IImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IImage? TryLoad(string? url, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var source = url.Trim();
        if (source.Length == 0)
        {
            return null;
        }

        try
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return Decode(source);
            }

            // The reader works offline: remote images are left as their alt text.
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = ResolvePath(source, baseDirectory);
            return path == null ? null : LoadFile(path);
        }
        catch (Exception)
        {
            // A broken image must never take the whole document down.
            return null;
        }
    }

    public static void ClearCache() => Cache.Clear();

    private static string? ResolvePath(string source, string? baseDirectory)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        var unescaped = Uri.UnescapeDataString(source.Split('?', '#')[0]);

        if (Path.IsPathRooted(unescaped))
        {
            return unescaped;
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory!, unescaped));
    }

    private static IImage? LoadFile(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        IImage? image = null;

        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);

                // Fully decode before the stream is disposed; Bitmap keeps a decoded copy.
                image = new Bitmap(stream);
            }
            catch (Exception)
            {
                image = null;
            }
        }

        Cache[path] = image;
        return image;
    }

    private static IImage? Decode(string dataUri)
    {
        if (Cache.TryGetValue(dataUri, out var cached))
        {
            return cached;
        }

        IImage? image = null;
        var comma = dataUri.IndexOf(',');
        var meta = comma > 0 ? dataUri[5..comma] : string.Empty;
        var payload = comma > 0 ? dataUri[(comma + 1)..] : string.Empty;

        if (payload.Length > 0)
        {
            try
            {
                var bytes = meta.Contains("base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : Convert.FromBase64String(Uri.UnescapeDataString(payload));

                using var stream = new MemoryStream(bytes);
                image = new Bitmap(stream);
            }
            catch (Exception)
            {
                image = null;
            }
        }

        Cache[dataUri] = image;
        return image;
    }
}
