using System;
using System.Collections.Generic;

namespace MdReader.Rendering;

public enum CodeTokenKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
    Type,
}

public readonly record struct CodeToken(string Text, CodeTokenKind Kind);

/// <summary>
/// A deliberately small, dependency-free highlighter: it colors keywords, strings, comments,
/// numbers and type-like identifiers for the handful of languages that show up in Markdown
/// files. Unknown languages stay plain, so a wrong guess can only cost color, never text.
/// </summary>
public static class CodeHighlighter
{
    private enum Flavor
    {
        None,
        CLike,
        Python,
        Shell,
        Sql,
        Markup,
        Json,
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C# / Java / JS / TS / Go / Rust / Kotlin / Swift and friends.
        "abstract", "as", "async", "await", "base", "bool", "break", "by", "case", "catch", "chan",
        "char", "checked", "class", "const", "continue", "debugger", "decimal", "default", "defer",
        "del", "delete", "do", "double", "dynamic", "elif", "else", "enum", "event", "except",
        "explicit", "export", "extends", "extern", "false", "final", "finally", "fixed", "float",
        "fn", "for", "foreach", "from", "func", "function", "fun", "get", "global", "go", "goto",
        "if", "implements", "implicit", "import", "in", "init", "instanceof", "int", "interface",
        "internal", "is", "let", "lock", "long", "loop", "match", "mod", "mutable", "namespace",
        "new", "nil", "none", "not", "null", "object", "operator", "or", "out", "override",
        "package", "params", "pass", "private", "protected", "protocol", "pub", "public", "raise",
        "readonly", "ref", "return", "sealed", "self", "set", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "super", "switch", "this", "throw", "throws", "trait", "true",
        "try", "type", "typeof", "uint", "ulong", "unchecked", "undef", "union", "unsafe",
        "unsigned", "use", "ushort", "using", "val", "var", "virtual", "void", "volatile", "when",
        "where", "while", "with", "yield",

        // SQL
        "add", "alter", "and", "any", "begin", "between", "column", "commit", "constraint",
        "create", "cross", "database", "declare", "desc", "distinct", "drop", "exec", "exists",
        "foreign", "group", "having", "index", "inner", "insert", "into", "join", "key", "left",
        "like", "limit", "not", "offset", "on", "order", "outer", "primary", "procedure", "right",
        "rollback", "select", "table", "top", "truncate", "union", "unique", "update", "values",
        "view", "asc", "delete", "set", "primary",

        // Shell
        "alias", "cd", "echo", "elif", "esac", "fi", "then", "done", "exit", "export", "local",
        "sudo", "source",

        // Markup (also matched as tag names)
        "html", "head", "body", "div", "span", "table", "tr", "td", "th", "img", "a", "p", "ul",
        "ol", "li", "br", "hr", "pre", "code", "script", "style", "link", "meta", "section",
        "article", "header", "footer", "nav", "details", "summary", "input", "button", "form",
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "string", "int", "long", "double", "float", "bool", "char", "byte", "object", "decimal",
        "Task", "List", "Dictionary", "IEnumerable", "Action", "Func", "String", "Int32", "Boolean",
        "Decimal", "Object", "Array", "Map", "Set", "Promise", "Date", "Number",
    };

    public static bool Supports(string? language) => Map(language) != Flavor.None;

    /// <summary>
    /// Splits one line into colored tokens. <paramref name="inBlockComment"/> carries a
    /// <c>/*</c> or triple-quoted string across lines, so multi-line comments stay comments.
    /// </summary>
    public static List<CodeToken> Highlight(string line, string? language, ref bool inBlockComment)
    {
        var tokens = new List<CodeToken>();
        var flavor = Map(language);

        if (flavor == Flavor.None || line.Length == 0)
        {
            if (line.Length > 0)
            {
                tokens.Add(new CodeToken(line, CodeTokenKind.Plain));
            }

            return tokens;
        }

        var index = 0;

        if (inBlockComment)
        {
            var close = FindBlockCommentEnd(line, flavor);
            if (close < 0)
            {
                tokens.Add(new CodeToken(line, CodeTokenKind.Comment));
                return tokens;
            }

            tokens.Add(new CodeToken(line[..close], CodeTokenKind.Comment));
            index = close;
            inBlockComment = false;
        }

        while (index < line.Length)
        {
            var ch = line[index];

            // Comments.
            if (TryStartLineComment(line, index, flavor, out var commentStart))
            {
                Add(tokens, line[commentStart..], CodeTokenKind.Comment);
                break;
            }

            if (TryStartBlockComment(line, index, flavor))
            {
                var end = FindBlockCommentEnd(line, flavor, index + 2);
                if (end < 0)
                {
                    Add(tokens, line[index..], CodeTokenKind.Comment);
                    inBlockComment = true;
                    break;
                }

                Add(tokens, line[index..end], CodeTokenKind.Comment);
                index = end;
                continue;
            }

            // Strings (and triple-quoted Python strings, which carry over lines).
            if (ch is '"' or '\'' or '`')
            {
                // An unterminated quote (a triple-quoted Python string, say) only colors the
                // current line; carrying it over would turn the next lines into comments.
                var (text, _) = ReadString(line, index, ch, flavor);
                Add(tokens, text, CodeTokenKind.String);
                index += text.Length;
                continue;
            }

            if (flavor == Flavor.Markup && ch == '<')
            {
                var end = line.IndexOf('>', index);
                end = end < 0 ? line.Length : end + 1;
                Add(tokens, line[index..end], CodeTokenKind.Keyword);
                index = end;
                continue;
            }

            if (char.IsDigit(ch))
            {
                var start = index;
                index++;
                while (index < line.Length && (char.IsDigit(line[index]) || line[index] is '.' or 'x' or 'X' or '_' ||
                                               (line[index] is >= 'a' and <= 'f') || (line[index] is >= 'A' and <= 'F')))
                {
                    index++;
                }

                Add(tokens, line[start..index], CodeTokenKind.Number);
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = index;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
                {
                    index++;
                }

                var word = line[start..index];
                var kind = Keywords.Contains(word) || Keywords.Contains(word.ToLowerInvariant())
                    ? CodeTokenKind.Keyword
                    : Types.Contains(word)
                        ? CodeTokenKind.Type
                        : CodeTokenKind.Plain;

                Add(tokens, word, kind);
                continue;
            }

            // Whitespace and punctuation are grouped so the inline collection stays small.
            var plainStart = index;
            while (index < line.Length &&
                   !char.IsLetterOrDigit(line[index]) &&
                   line[index] is not ('"' or '\'' or '`' or '<') &&
                   !char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            if (index == plainStart)
            {
                index++;
            }

            Add(tokens, line[plainStart..index], CodeTokenKind.Plain);
        }

        return tokens;
    }

    private static void Add(List<CodeToken> tokens, string text, CodeTokenKind kind)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (tokens.Count > 0 && tokens[^1].Kind == kind)
        {
            tokens[^1] = tokens[^1] with { Text = tokens[^1].Text + text };
            return;
        }

        tokens.Add(new CodeToken(text, kind));
    }

    private static bool TryStartLineComment(string line, int index, Flavor flavor, out int start)
    {
        start = index;
        var two = index + 1 < line.Length ? line.Substring(index, 2) : string.Empty;

        var marker = flavor switch
        {
            Flavor.Shell or Flavor.Python => index < line.Length && line[index] == '#' ? "#" : null,
            Flavor.Sql => two == "--" ? "--" : null,
            Flavor.Markup => line.AsSpan(index).StartsWith("<!--") ? "<!--" : null,
            _ => two == "//" ? "//" : null,
        };

        return marker != null;
    }

    private static bool TryStartBlockComment(string line, int index, Flavor flavor) =>
        flavor is not (Flavor.Python or Flavor.Shell or Flavor.Json) &&
        index + 1 < line.Length && line[index] == '/' && line[index + 1] == '*';

    private static int FindBlockCommentEnd(string line, Flavor flavor, int from = 0)
    {
        if (line.AsSpan(from).StartsWith("<!--"))
        {
            var closeHtml = line.IndexOf("-->", from, StringComparison.Ordinal);
            return closeHtml < 0 ? -1 : closeHtml + 3;
        }

        var close = line.IndexOf("*/", from, StringComparison.Ordinal);
        return close < 0 ? -1 : close + 2;
    }

    private static (string Text, bool Closed) ReadString(string line, int start, char quote, Flavor flavor)
    {
        // Python and shell also use quotes; only Python's triple quotes span lines.
        if (flavor == Flavor.Python && start + 2 < line.Length && line[start + 1] == quote && line[start + 2] == quote)
        {
            var triple = new string(quote, 3);
            var end = line.IndexOf(triple, start + 3, StringComparison.Ordinal);
            return end < 0
                ? (line[start..], false)
                : (line[start..(end + 3)], true);
        }

        var index = start + 1;
        while (index < line.Length)
        {
            var ch = line[index];
            if (ch == '\\' && index + 1 < line.Length)
            {
                index += 2;
                continue;
            }

            if (ch == quote)
            {
                index++;
                return (line[start..index], true);
            }

            index++;
        }

        return (line[start..], false);
    }

    private static Flavor Map(string? language) => (language ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "cs" or "csharp" or "c#" or "java" or "c" or "h" or "cpp" or "c++" or "hpp" or "js" or
            "javascript" or "jsx" or "ts" or "typescript" or "tsx" or "go" or "golang" or "rust" or
            "rs" or "kotlin" or "kt" or "swift" or "php" or "dart" or "css" or "scss" or "less" or
            "csharp-linq" => Flavor.CLike,
        "py" or "python" => Flavor.Python,
        "sh" or "bash" or "shell" or "zsh" or "console" or "ps1" or "powershell" or "bat" or "cmd" => Flavor.Shell,
        "sql" or "mysql" or "postgres" or "postgresql" or "sqlite" => Flavor.Sql,
        "html" or "xml" or "svg" or "xaml" or "razor" or "cshtml" => Flavor.Markup,
        "json" or "jsonc" or "geojson" => Flavor.Json,
        _ => Flavor.None,
    };
}
