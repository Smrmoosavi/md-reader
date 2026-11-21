using System;
using System.Collections.Generic;
using System.IO;

namespace MdReader;

/// <summary>Walks a folder and its subfolders to find the Markdown files of the library.</summary>
public static class LibraryScanner
{
    public static readonly string[] Extensions = { ".md", ".markdown", ".mdown", ".mkd", ".txt" };

    public const int MaxFiles = 3000;
    public const long MaxSearchBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "out", ".vs", ".idea",
        ".obsidian", "packages", "vendor", "__pycache__",
    };

    public static bool IsDocument(string name) =>
        Array.IndexOf(Extensions, Path.GetExtension(name).ToLowerInvariant()) >= 0;

    public static bool IsSkippedDirectory(string name) =>
        string.IsNullOrEmpty(name) || name.StartsWith('.') || SkippedDirectories.Contains(name);

    /// <summary>Every Markdown/text file below <paramref name="root"/>, sorted by path.</summary>
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
                    if (!IsSkippedDirectory(name))
                    {
                        pending.Push(sub);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (IsDocument(file))
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

    /// <summary>Relative path for display; falls back to the absolute path across volumes.</summary>
    public static string MakeRelative(string root, string file)
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
}
