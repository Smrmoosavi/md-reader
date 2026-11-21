using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MdReader.Export;

/// <summary>
/// Minimal PNG reader for the screenshots that <c>RenderTargetBitmap</c> produces: 8-bit,
/// non-interlaced images in gray, gray+alpha, RGB or RGBA. Pixels are composited over white so a
/// PDF page never needs an alpha channel.
/// </summary>
internal static class PngImage
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static (int Width, int Height, byte[] Rgb) DecodeToRgb(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();

        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG image.");
        }

        var position = 8;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        using var idat = new MemoryStream();

        while (position + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position, 4));
            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            position += 8;

            if (length < 0 || position + length > data.Length)
            {
                break;
            }

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position + 4, 4));
                    bitDepth = data[position + 8];
                    colorType = data[position + 9];

                    if (data[position + 12] != 0)
                    {
                        throw new NotSupportedException("Interlaced PNG images are not supported.");
                    }

                    break;
                case "IDAT":
                    idat.Write(data, position, length);
                    break;
                case "IEND":
                    position = data.Length;
                    break;
            }

            position += length + 4;
        }

        if (bitDepth != 8 || width <= 0 || height <= 0)
        {
            throw new NotSupportedException("Only 8-bit PNG images are supported.");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException($"Unsupported PNG color type {colorType}."),
        };

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        var stride = width * channels;
        var rgb = new byte[width * height * 3];
        var previous = new byte[stride];
        var current = new byte[stride];
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            if (offset >= raw.Length)
            {
                break;
            }

            var filter = raw[offset++];
            var available = Math.Min(stride, raw.Length - offset);
            Array.Clear(current);
            Array.Copy(raw, offset, current, 0, available);
            offset += available;

            Unfilter(filter, current, previous, channels);

            var target = y * width * 3;
            for (var x = 0; x < width; x++)
            {
                var source = x * channels;
                byte r;
                byte g;
                byte b;
                byte a = 255;

                switch (channels)
                {
                    case 1:
                        r = g = b = current[source];
                        break;
                    case 2:
                        r = g = b = current[source];
                        a = current[source + 1];
                        break;
                    case 3:
                        r = current[source];
                        g = current[source + 1];
                        b = current[source + 2];
                        break;
                    default:
                        r = current[source];
                        g = current[source + 1];
                        b = current[source + 2];
                        a = current[source + 3];
                        break;
                }

                if (a == 255)
                {
                    rgb[target] = r;
                    rgb[target + 1] = g;
                    rgb[target + 2] = b;
                }
                else
                {
                    rgb[target] = Blend(r, a);
                    rgb[target + 1] = Blend(g, a);
                    rgb[target + 2] = Blend(b, a);
                }

                target += 3;
            }

            (previous, current) = (current, previous);
        }

        return (width, height, rgb);
    }

    private static byte Blend(byte channel, byte alpha) =>
        (byte)((channel * alpha + 255 * (255 - alpha) + 127) / 255);

    private static void Unfilter(byte filter, byte[] current, byte[] previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 1:
                for (var i = bytesPerPixel; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + current[i - bytesPerPixel]);
                }

                break;

            case 2:
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + previous[i]);
                }

                break;

            case 3:
                for (var i = 0; i < current.Length; i++)
                {
                    var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    current[i] = (byte)(current[i] + ((left + previous[i]) >> 1));
                }

                break;

            case 4:
                for (var i = 0; i < current.Length; i++)
                {
                    var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    current[i] = (byte)(current[i] + Paeth(left, previous[i], upLeft));
                }

                break;
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);

        if (toLeft <= toUp && toLeft <= toUpLeft)
        {
            return left;
        }

        return toUp <= toUpLeft ? up : upLeft;
    }
}
