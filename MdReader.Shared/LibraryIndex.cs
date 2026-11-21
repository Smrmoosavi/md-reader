using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace MdReader;

/// <summary>One Markdown file of the library. Files picked through Android's storage access
/// framework have no filesystem path, so the storage file is kept for reading.</summary>
public sealed class LibraryFile
{
    public LibraryFile(string name, string relative, string? path, IStorageFile? file)
    {
        Name = name;
        Relative = relative;
        Path = path;
        File = file;
    }

    public string Name { get; }

    public string Relative { get; }

    public string? Path { get; }

    public IStorageFile? File { get; }

    public string Display => Path ?? Relative;
}

/// <summary>One row of the library panel: a file, or a match inside a file.</summary>
public sealed class LibraryItem
{
    public LibraryItem(string header, string detail, LibraryFile file, string? query = null, int matchOrdinal = 0)
    {
        Header = header;
        Detail = detail;
        Source = file;
        Query = query;
        MatchOrdinal = matchOrdinal;
    }

    /// <summary>File name (or relative path) shown in the first line.</summary>
    public string Header { get; }

    /// <summary>Folder, line number or the file path, used as the tooltip and second line.</summary>
    public string Detail { get; }

    public LibraryFile Source { get; }

    public string? Path => Source.Path;

    public IStorageFile? File => Source.File;

    public string FullPath => Source.Display;

    /// <summary>The text that was searched for, so opening the item can highlight it again.</summary>
    public string? Query { get; }

    /// <summary>Which occurrence inside the file the item points at.</summary>
    public int MatchOrdinal { get; }

    public bool IsMatch => Query != null;

    // ---- snippet of a content match, split so the panel can highlight the match itself ----

    public string? LineNumber { get; init; }

    public string? SnippetBefore { get; init; }

    public string? SnippetMatch { get; init; }

    public string? SnippetAfter { get; init; }

    public bool HasSnippet => SnippetMatch != null;

    public override string ToString() => Header;
}

/// <summary>
/// Keeps the text of every library file in memory, so typing in the library search box does not
/// read the whole folder again. A search first re-checks the modification time and size of each
/// file and re-reads only the ones that changed.
/// </summary>
public sealed class LibraryIndex
{
    private const int MaxMatchesPerFile = 3;
    private const int SnippetBefore = 44;
    private const int SnippetAfter = 70;

    private sealed class Entry
    {
        public Entry(LibraryFile file, string text, DateTime modified, long size)
        {
            File = file;
            Text = text;
            Modified = modified;
            Size = size;
        }

        public LibraryFile File { get; }

        public string Text { get; set; }

        public DateTime Modified { get; set; }

        public long Size { get; set; }
    }

    private readonly List<Entry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>The local folder that was indexed, if the library is path based.</summary>
    public string? RootPath { get; private set; }

    /// <summary>Loads every document of a local folder.</summary>
    public void LoadPath(string root)
    {
        _entries.Clear();
        RootPath = root;

        foreach (var path in LibraryScanner.Files(root))
        {
            _entries.Add(ReadPathEntry(root, path));
        }
    }

    /// <summary>
    /// Loads every document of a storage folder (Android's SAF, or any other non-local provider).
    /// The enumeration is bounded, so a huge tree cannot hang the app.
    /// </summary>
    public async Task LoadFolderAsync(IStorageFolder folder, Action<int>? progress = null)
    {
        _entries.Clear();
        RootPath = null;

        var pending = new Stack<(IStorageFolder Folder, string Relative, int Depth)>();
        pending.Push((folder, string.Empty, 0));

        while (pending.Count > 0 && _entries.Count < LibraryScanner.MaxFiles)
        {
            var (current, relative, depth) = pending.Pop();

            var items = new List<IStorageItem>();
            try
            {
                await foreach (var item in current.GetItemsAsync())
                {
                    items.Add(item);
                }
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var item in items)
            {
                if (item is IStorageFolder sub)
                {
                    if (depth < 8 && !LibraryScanner.IsSkippedDirectory(sub.Name))
                    {
                        pending.Push((sub, Combine(relative, sub.Name), depth + 1));
                    }

                    continue;
                }

                if (item is not IStorageFile file || !LibraryScanner.IsDocument(file.Name))
                {
                    continue;
                }

                var text = await ReadTextAsync(file);
                _entries.Add(new Entry(new LibraryFile(file.Name, Combine(relative, file.Name), null, file),
                    text, DateTime.MinValue, 0));

                if (_entries.Count >= LibraryScanner.MaxFiles)
                {
                    break;
                }
            }

            progress?.Invoke(_entries.Count);
        }
    }

    /// <summary>Re-reads the files that changed since they were indexed.</summary>
    public int RefreshChanged()
    {
        var changed = 0;

        foreach (var entry in _entries)
        {
            if (entry.File.Path is not { Length: > 0 } path)
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.LastWriteTimeUtc == entry.Modified && info.Length == entry.Size)
                {
                    continue;
                }

                entry.Text = ReadText(path);
                entry.Modified = info.LastWriteTimeUtc;
                entry.Size = info.Length;
                changed++;
            }
            catch (Exception)
            {
                // A file that is being written keeps its previous text.
            }
        }

        return changed;
    }

    /// <summary>
    /// Turns the index into panel rows. An empty query lists the files; a query matches file names
    /// and file contents, with a snippet around each matching line.
    /// </summary>
    public List<LibraryItem> Search(string query, int maximum = 200)
    {
        var items = new List<LibraryItem>();
        query = query.Trim();

        foreach (var entry in _entries)
        {
            if (items.Count >= maximum)
            {
                break;
            }

            var file = entry.File;
            var folder = Folder(file.Relative);

            if (query.Length == 0)
            {
                items.Add(new LibraryItem(file.Name, folder, file));
                continue;
            }

            if (file.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                items.Add(new LibraryItem(file.Name, folder.Length > 0 ? folder : file.Relative, file));
            }

            if (entry.Text.Length == 0)
            {
                continue;
            }

            var ordinal = 0;
            foreach (var (lineNumber, line) in MatchLines(entry.Text, query))
            {
                items.Add(CreateSnippet(file, lineNumber, line, query, ordinal));
                ordinal++;

                if (items.Count >= maximum)
                {
                    break;
                }
            }
        }

        return items;
    }

    private static LibraryItem CreateSnippet(LibraryFile file, int lineNumber, string line, string query, int ordinal)
    {
        var index = line.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        var match = index >= 0 ? line.Substring(index, query.Length) : query;
        var before = index > 0 ? line[..index] : string.Empty;
        var after = index >= 0 ? line[(index + query.Length)..] : string.Empty;

        if (before.Length > SnippetBefore)
        {
            before = "…" + before[^SnippetBefore..];
        }

        if (after.Length > SnippetAfter)
        {
            after = after[..SnippetAfter] + "…";
        }

        return new LibraryItem(file.Name, $"خط {lineNumber} · {line.Trim()}", file, query, ordinal)
        {
            LineNumber = lineNumber.ToString(CultureInfo.CurrentCulture),
            SnippetBefore = before.TrimStart(),
            SnippetMatch = match,
            SnippetAfter = after.TrimEnd(),
        };
    }

    private static IEnumerable<(int LineNumber, string Line)> MatchLines(string text, string query)
    {
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

    private static Entry ReadPathEntry(string root, string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new Entry(
                new LibraryFile(info.Name, LibraryScanner.MakeRelative(root, path), path, null),
                ReadText(path),
                info.LastWriteTimeUtc,
                info.Length);
        }
        catch (Exception)
        {
            return new Entry(new LibraryFile(Path.GetFileName(path), LibraryScanner.MakeRelative(root, path), path, null),
                string.Empty, DateTime.MinValue, 0);
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > LibraryScanner.MaxSearchBytes)
            {
                return string.Empty;
            }

            return TextLoader.Read(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadTextAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            if (stream.Length > LibraryScanner.MaxSearchBytes)
            {
                return string.Empty;
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return TextLoader.Read(buffer.ToArray());
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Combine(string relative, string name) =>
        relative.Length == 0 ? name : relative + Path.DirectorySeparatorChar + name;

    private static string Folder(string folder)
    {
        var separator = folder.LastIndexOfAny(new[] { '/', '\\' });
        return separator <= 0 ? string.Empty : folder[..separator];
    }
}
