using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MdReader;

/// <summary>One bookmark or note inside a document, anchored to a vertical scroll offset.</summary>
public sealed class DocumentMark
{
    public DocumentMark(string kind, double offset, string text)
    {
        Kind = kind;
        Offset = offset;
        Text = text;
    }

    /// <summary><c>bookmark</c> or <c>note</c>.</summary>
    public string Kind { get; }

    public double Offset { get; }

    public string Text { get; }

    public bool IsNote => Kind == "note";

    /// <summary>Leading glyph of the row in the marks panel.</summary>
    public string Glyph => IsNote ? "✎" : "🔖";

    public override string ToString() => Text;
}

/// <summary>Everything the reader remembers about one file between sessions.</summary>
public sealed class DocumentState
{
    /// <summary>Last scroll offset, so the next open continues where the reader stopped.</summary>
    public double Position { get; set; }

    public List<DocumentMark> Marks { get; } = new();
}

/// <summary>
/// Stores <see cref="DocumentState"/> in a small sidecar file next to the document
/// (<c>notes.md</c> gets <c>notes.md.mdreader</c>) as <c>key=value</c> lines. The document itself is
/// never modified, so the file stays compatible with every other Markdown tool.
/// </summary>
public static class ReadingStateStore
{
    private const string Suffix = ".mdreader";
    private const double MinPosition = 4;

    private static readonly Dictionary<string, DocumentState> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>The state of <paramref name="path"/>, loaded once and kept in memory.</summary>
    public static DocumentState Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new DocumentState();
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var state = Load(path);
            Cache[path] = state;
            return state;
        }
    }

    /// <summary>True when there is a position worth restoring.</summary>
    public static bool HasPosition(DocumentState state) => state.Position > MinPosition;

    public static void Save(string? path, DocumentState state)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("position=" + state.Position.ToString("0.##", CultureInfo.InvariantCulture));

            foreach (var mark in state.Marks)
            {
                // The note text is the rest of the line after the second separator, so it may
                // contain any character except a line break.
                var text = mark.Text.Replace('\r', ' ').Replace('\n', ' ');
                builder.AppendLine($"mark={mark.Kind}|{mark.Offset.ToString("0.##", CultureInfo.InvariantCulture)}|{text}");
            }

            File.WriteAllText(path + Suffix, builder.ToString());
        }
        catch (Exception)
        {
            // Bookmarks are best-effort: a read-only folder must not break reading.
        }
    }

    private static DocumentState Load(string path)
    {
        var state = new DocumentState();

        try
        {
            var sidecar = path + Suffix;
            if (!File.Exists(sidecar))
            {
                return state;
            }

            foreach (var line in File.ReadAllLines(sidecar))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..];

                if (key == "position" &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var position))
                {
                    state.Position = position;
                }
                else if (key == "mark")
                {
                    var first = value.IndexOf('|');
                    var second = first < 0 ? -1 : value.IndexOf('|', first + 1);

                    if (first <= 0 || second < 0 ||
                        !double.TryParse(value[(first + 1)..second], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var offset))
                    {
                        continue;
                    }

                    var kind = value[..first].Trim();
                    state.Marks.Add(new DocumentMark(kind.Length == 0 ? "bookmark" : kind, offset, value[(second + 1)..]));
                }
            }
        }
        catch (Exception)
        {
            // A damaged sidecar behaves like a missing one.
        }

        return state;
    }
}
