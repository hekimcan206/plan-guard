using System.Security.Cryptography;
using System.Text;

namespace PlanGuard.Sql;

/// <summary>
/// Minimal lexical handling of SQL text: enough to know where quotes, comments and statements end. plan-guard
/// splices catalog SQL into <c>EXPLAIN ...</c>, so a file that smuggles a second statement after a semicolon would
/// run outside the EXPLAIN (and outside its rolled-back transaction). Such files are refused.
/// </summary>
public static class SqlText
{
    /// <summary>Returns the statement without trailing semicolons and whitespace; throws if it holds more than one statement.</summary>
    public static string EnsureSingleStatement(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var segments = Split(sql).Where(s => !IsBlank(s)).ToList();
        return segments.Count switch
        {
            0 => throw new FormatException("The query file contains no SQL statement."),
            1 => segments[0].Trim(),
            _ => throw new FormatException($"Expected exactly one SQL statement, found {segments.Count}."),
        };
    }

    /// <summary>Collapses whitespace outside literals and comments, and removes comments, giving a stable identity text.</summary>
    public static string Normalize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var sb = new StringBuilder(sql.Length);
        var pendingSpace = false;
        foreach (var (token, kind) in Tokens(sql))
        {
            switch (kind)
            {
                case TokenKind.Whitespace:
                case TokenKind.Comment:
                    pendingSpace = sb.Length > 0;
                    break;
                default:
                    if (pendingSpace)
                    {
                        sb.Append(' ');
                        pendingSpace = false;
                    }

                    sb.Append(token);
                    break;
            }
        }

        return sb.ToString().TrimEnd(';', ' ');
    }

    /// <summary>Identity for a discovered statement: <c>q</c> + 12 hex characters of the SHA-256 of the normalised text.</summary>
    public static string StatementId(string sql) =>
        "q" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sql))))[..12];

    /// <summary>True for statements EXPLAIN accepts and plan-guard plans: SELECT, WITH, VALUES, TABLE, INSERT, UPDATE, DELETE, MERGE.</summary>
    public static bool IsExplainable(string sql)
    {
        var first = Tokens(sql).FirstOrDefault(t => t.Kind is TokenKind.Word or TokenKind.Symbol or TokenKind.Literal);
        if (first.Kind != TokenKind.Word)
        {
            return false;
        }

        return first.Text.ToUpperInvariant() is "SELECT" or "WITH" or "VALUES" or "TABLE" or "INSERT" or "UPDATE" or "DELETE" or "MERGE";
    }

    private static bool IsBlank(string segment) => Tokens(segment).All(t => t.Kind is TokenKind.Whitespace or TokenKind.Comment);

    private static IEnumerable<string> Split(string sql)
    {
        var current = new StringBuilder();
        foreach (var (token, kind) in Tokens(sql))
        {
            if (kind == TokenKind.Symbol && token == ";")
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(token);
            }
        }

        yield return current.ToString();
    }

    internal enum TokenKind
    {
        Whitespace,
        Comment,
        Literal,
        Word,
        Symbol,
    }

    /// <summary>
    /// Splits into tokens, honouring '...' (with '' escapes and E'' backslash escapes), "...", $tag$...$tag$,
    /// -- line comments and nested /* */ block comments, which is what PostgreSQL's own scanner does.
    /// An unterminated literal or comment runs to the end of the text, like the server would report.
    /// </summary>
    internal static IEnumerable<(string Text, TokenKind Kind)> Tokens(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var start = i;
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i]))
                {
                    i++;
                }

                yield return (sql[start..i], TokenKind.Whitespace);
            }
            else if (c == '-' && Peek(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                yield return (sql[start..i], TokenKind.Comment);
            }
            else if (c == '/' && Peek(sql, i + 1) == '*')
            {
                var depth = 0;
                while (i < sql.Length)
                {
                    if (sql[i] == '/' && Peek(sql, i + 1) == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && Peek(sql, i + 1) == '/')
                    {
                        depth--;
                        i += 2;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                yield return (sql[start..i], TokenKind.Comment);
            }
            else if (c == '\'' || ((c is 'E' or 'e') && Peek(sql, i + 1) == '\'' && !IsWordChar(Peek(sql, i - 1))))
            {
                var backslashEscapes = c != '\'';
                i += backslashEscapes ? 2 : 1;
                while (i < sql.Length)
                {
                    if (backslashEscapes && sql[i] == '\\')
                    {
                        i += 2;
                    }
                    else if (sql[i] == '\'')
                    {
                        if (Peek(sql, i + 1) == '\'')
                        {
                            i += 2;
                        }
                        else
                        {
                            i++;
                            break;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                i = Math.Min(i, sql.Length);
                yield return (sql[start..i], TokenKind.Literal);
            }
            else if (c == '"')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '"')
                    {
                        if (Peek(sql, i + 1) == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                yield return (sql[start..i], TokenKind.Word);
            }
            else if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + tag.Length;
                yield return (sql[start..i], TokenKind.Literal);
            }
            else if (IsWordChar(c))
            {
                while (i < sql.Length && (IsWordChar(sql[i]) || sql[i] == '$'))
                {
                    i++;
                }

                yield return (sql[start..i], TokenKind.Word);
            }
            else
            {
                i++;
                yield return (sql[start..i], TokenKind.Symbol);
            }
        }
    }

    /// <summary>Returns <c>$tag$</c> when a dollar quote starts at <paramref name="i"/>; <c>$1</c> parameters are not quotes.</summary>
    private static string? DollarTag(string sql, int i)
    {
        var j = i + 1;
        if (j < sql.Length && char.IsDigit(sql[j]))
        {
            return null;
        }

        while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
        {
            j++;
        }

        return j < sql.Length && sql[j] == '$' ? sql[i..(j + 1)] : null;
    }

    private static char Peek(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
