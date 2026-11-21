using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MdReader;

/// <summary>One row of the library panel: a file, or a match inside a file.</summary>
public sealed class LibraryItem
{
    public LibraryItem(string header, string detail, string fullPath, string? query, int matchOrdinal)
    {
        Header = header;
        Detail = detail;
        FullPath = fullPath;
        Query = query;
        MatchOrdinal = matchOrdinal;
    }

    /// <summary>File name (or relative path) shown in bold.</summary>
    public string Header { get; }

    /// <summary>Folder, line number or a snippet of the matching line.</summary>
    public string Detail { get; }

    public string FullPath { get; }

    /// <summary>The text that was searched for, so opening the item can highlight it again.</summary>
    public string? Query { get; }

    /// <summary>Which occurrence inside the file the item points at.</summary>
    public int MatchOrdinal { get; }

    public bool IsMatch => Query != null;

    public override string ToString() => Header;
}

/// <summary>Lists and searches the Markdown files of one folder, for the library panel.</summary>
public static class LibraryScanner
{
    private static readonly string[] Extensions = { ".md", ".markdown", ".mdown", ".mkd", ".txt" };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "out", ".vs", ".idea",
        ".obsidian", "packages", "vendor", "__pycache__",
    };

    private const int MaxFiles = 3000;
    private const long MaxSearchBytes = 2 * 1024 * 1024;
    private const int MaxMatchesPerFile = 3;

    /// <summary>Every Markdown/text file below <paramref name="root"/>, sorted by relative path.</summary>
    public static List<string> Files(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            var directory = pending.Pop();

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(sub);
                    if (!string.IsNullOrEmpty(name) && !SkippedDirectories.Contains(name) && !name.StartsWith('.'))
                    {
                        pending.Push(sub);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                        if (files.Count >= MaxFiles)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A folder without permission is skipped instead of failing the whole scan.
            }
        }

        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        return files;
    }

    /// <summary>
    /// Turns the file list into panel rows. An empty query lists the files; a query matches file
    /// names and file contents (one row per matching line).
    /// </summary>
    public static List<LibraryItem> Search(IReadOnlyList<string> files, string root, string query, int maximum = 200)
    {
        var items = new List<LibraryItem>();
        query = query.Trim();

        foreach (var file in files)
        {
            if (items.Count >= maximum)
            {
                break;
            }

            var relative = MakeRelative(root, file);
            var name = Path.GetFileName(file);

            if (query.Length == 0)
            {
                items.Add(new LibraryItem(name, Path.GetDirectoryName(relative) is { Length: > 0 } folder ? folder : relative, file, null, 0));
                continue;
            }

            if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                items.Add(new LibraryItem(name, "نام فایل · " + relative, file, null, 0));
            }

            var ordinal = 0;
            foreach (var (lineNumber, line) in ReadMatches(file, query))
            {
                var snippet = line.Trim();
                if (snippet.Length > 120)
                {
                    snippet = snippet[..120] + "…";
                }

                items.Add(new LibraryItem(name, $"خط {lineNumber} · {snippet}", file, query, ordinal));
                ordinal++;

                if (items.Count >= maximum)
                {
                    break;
                }
            }
        }

        return items;
    }

    private static string MakeRelative(string root, string file)
    {
        try
        {
            return Path.GetRelativePath(root, file);
        }
        catch (Exception)
        {
            return file;
        }
    }

    private static IEnumerable<(int LineNumber, string Line)> ReadMatches(string file, string query)
    {
        string text;

        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxSearchBytes)
            {
                yield break;
            }

            text = TextLoader.Read(File.ReadAllBytes(file));
        }
        catch (Exception)
        {
            yield break;
        }

        var matches = 0;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length && matches < MaxMatchesPerFile; i++)
        {
            if (lines[i].Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                matches++;
                yield return (i + 1, lines[i]);
            }
        }
    }
}
