using System;
using System.Collections.Generic;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Parses a T-SQL file into the CREATE statements it contains. Only top-level
/// CREATE / CREATE OR ALTER batches are returned; ALTER, DROP, USE, and other
/// statements are ignored. Batch boundaries are <c>GO</c> on its own line
/// (case-insensitive, optional leading/trailing whitespace, optional batch
/// count). Strings, bracketed identifiers, and line/block comments are
/// recognized so that <c>GO</c> or <c>CREATE</c> tokens appearing inside them
/// are not mistaken for real keywords.
/// </summary>
public sealed class SqlFileParser
{
    public IReadOnlyList<ParsedSqlObject> Parse(string sqlText)
        => Parse(sqlText, out _);

    /// <summary>
    /// Same as <see cref="Parse(string)"/> but also returns parse warnings —
    /// currently used to flag <c>USE</c> statements that get overruled by a
    /// later <c>USE</c> without an intervening CREATE.
    /// </summary>
    public IReadOnlyList<ParsedSqlObject> Parse(string sqlText, out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        warnings = warningList;

        if (string.IsNullOrEmpty(sqlText))
            return Array.Empty<ParsedSqlObject>();

        var batches = SplitIntoBatches(sqlText);
        var results = new List<ParsedSqlObject>();

        // Walk batches in order, tracking the most recent USE [Db] declaration.
        // Each CREATE binds to the USE that immediately precedes it (or null
        // if no USE has been seen yet). When two USEs appear with no CREATE
        // between them, the earlier one is "overruled" and we emit a warning
        // — the user almost certainly didn't mean to declare two databases
        // back-to-back for a single object.
        string? currentUseDb = null;
        bool sawCreateSinceLastUse = true;
        var overruled = new List<string>();

        for (int i = 0; i < batches.Count; i++)
        {
            string? useDb = DetectUseStatement(batches[i]);
            if (useDb != null)
            {
                if (!sawCreateSinceLastUse && currentUseDb != null
                    && !string.Equals(currentUseDb, useDb, StringComparison.OrdinalIgnoreCase))
                {
                    overruled.Add(currentUseDb);
                }
                currentUseDb = useDb;
                sawCreateSinceLastUse = false;
                continue;
            }

            var obj = ExtractObject(batches[i], i, currentUseDb);
            if (obj is not null)
            {
                results.Add(obj);
                sawCreateSinceLastUse = true;
            }
        }

        if (overruled.Count > 0)
        {
            string list = string.Join(", ", overruled);
            warningList.Add(
                $"USE statement(s) for {list} were overruled by a later USE before any CREATE — "
                + $"effective database is {currentUseDb}.");
        }

        return results;
    }

    /// <summary>
    /// Returns the database name from a batch that consists of a single
    /// <c>USE [DbName]</c> statement (with optional surrounding whitespace
    /// and a trailing semicolon). Returns null when the batch is anything
    /// else — even a USE statement followed by other code.
    /// </summary>
    private static string? DetectUseStatement(string batch)
    {
        if (string.IsNullOrWhiteSpace(batch)) return null;

        var first = NextSignificantWord(batch, 0);
        if (first is null) return null;
        if (!string.Equals(first.Value.Word, "USE", StringComparison.OrdinalIgnoreCase))
            return null;

        var name = NextSignificantWord(batch, first.Value.NextPos);
        if (name is null) return null;

        // Anything after the name (other than an optional semicolon) means
        // this batch isn't a pure USE statement.
        var trailing = NextSignificantWord(batch, name.Value.NextPos);
        if (trailing != null && trailing.Value.Word != ";")
            return null;

        return name.Value.Word;
    }

    /// <summary>
    /// Splits the input into batches at GO separators, respecting string
    /// literals, bracketed identifiers, and comments.
    /// </summary>
    private static List<string> SplitIntoBatches(string text)
    {
        var batches = new List<string>();
        var sb = new StringBuilder();
        int n = text.Length;
        int i = 0;
        bool atLineStart = true;

        while (i < n)
        {
            if (atLineStart && TryConsumeGoSeparator(text, i, out int after))
            {
                batches.Add(sb.ToString());
                sb.Clear();
                i = after;
                atLineStart = true;
                continue;
            }

            char c = text[i];

            if (c == '\'')
            {
                CopyStringLiteral(text, ref i, sb);
                atLineStart = false;
                continue;
            }

            if (c == '[')
            {
                CopyBracketedIdent(text, ref i, sb);
                atLineStart = false;
                continue;
            }

            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                CopyLineComment(text, ref i, sb);
                // Line comment ends at \n — leave the \n for the outer loop.
                continue;
            }

            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                CopyBlockComment(text, ref i, sb);
                atLineStart = false;
                continue;
            }

            sb.Append(c);
            if (c == '\n')
                atLineStart = true;
            else if (c != ' ' && c != '\t' && c != '\r')
                atLineStart = false;
            i++;
        }

        if (sb.Length > 0)
            batches.Add(sb.ToString());

        return batches;
    }

    /// <summary>
    /// Returns true if position <paramref name="i"/> begins a GO batch separator
    /// — possibly preceded by tabs/spaces, followed by an optional batch-count
    /// integer, and terminated by end-of-line or end-of-input.
    /// </summary>
    private static bool TryConsumeGoSeparator(string text, int i, out int after)
    {
        after = i;
        int n = text.Length;
        int p = i;

        while (p < n && (text[p] == ' ' || text[p] == '\t'))
            p++;

        if (p + 1 >= n)
            return false;
        if ((text[p] != 'G' && text[p] != 'g') || (text[p + 1] != 'O' && text[p + 1] != 'o'))
            return false;

        int afterGo = p + 2;

        // Optional batch count: any whitespace then digits.
        int q = afterGo;
        while (q < n && (text[q] == ' ' || text[q] == '\t'))
            q++;
        if (q < n && char.IsDigit(text[q]))
        {
            while (q < n && char.IsDigit(text[q])) q++;
            afterGo = q;
        }

        // Allow trailing whitespace then EOL or EOF.
        while (afterGo < n && (text[afterGo] == ' ' || text[afterGo] == '\t'))
            afterGo++;

        if (afterGo >= n)
        {
            after = afterGo;
            return true;
        }
        if (text[afterGo] == '\r' || text[afterGo] == '\n')
        {
            if (text[afterGo] == '\r') afterGo++;
            if (afterGo < n && text[afterGo] == '\n') afterGo++;
            after = afterGo;
            return true;
        }

        return false;
    }

    private static void CopyStringLiteral(string text, ref int i, StringBuilder sb)
    {
        int n = text.Length;
        sb.Append(text[i]);
        i++;
        while (i < n)
        {
            if (text[i] == '\'' && i + 1 < n && text[i + 1] == '\'')
            {
                sb.Append("''");
                i += 2;
            }
            else if (text[i] == '\'')
            {
                sb.Append('\'');
                i++;
                return;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
    }

    private static void CopyBracketedIdent(string text, ref int i, StringBuilder sb)
    {
        int n = text.Length;
        sb.Append(text[i]);
        i++;
        while (i < n)
        {
            if (text[i] == ']' && i + 1 < n && text[i + 1] == ']')
            {
                sb.Append("]]");
                i += 2;
            }
            else if (text[i] == ']')
            {
                sb.Append(']');
                i++;
                return;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
    }

    private static void CopyLineComment(string text, ref int i, StringBuilder sb)
    {
        int n = text.Length;
        while (i < n && text[i] != '\n')
        {
            sb.Append(text[i]);
            i++;
        }
    }

    private static void CopyBlockComment(string text, ref int i, StringBuilder sb)
    {
        int n = text.Length;
        sb.Append(text[i]);
        sb.Append(text[i + 1]);
        i += 2;
        while (i < n)
        {
            if (text[i] == '*' && i + 1 < n && text[i + 1] == '/')
            {
                sb.Append('*');
                sb.Append('/');
                i += 2;
                return;
            }
            sb.Append(text[i]);
            i++;
        }
    }

    /// <summary>
    /// Looks for <c>CREATE [OR ALTER] &lt;type&gt; &lt;name&gt;</c> in a single
    /// batch and extracts a <see cref="ParsedSqlObject"/>. Returns null when
    /// the batch contains no recognizable CREATE.
    /// </summary>
    private static ParsedSqlObject? ExtractObject(string batch, int batchIndex, string? targetDatabase = null)
    {
        int pos = 0;

        // Walk word-by-word until we find the leading CREATE.
        while (true)
        {
            var word = NextSignificantWord(batch, pos);
            if (word is null) return null;
            if (Equals(word.Value.Word, "CREATE"))
            {
                pos = word.Value.NextPos;
                break;
            }
            pos = word.Value.NextPos;
        }

        bool isCreateOrAlter = false;
        var w1 = NextSignificantWord(batch, pos);
        if (w1 is null) return null;

        if (Equals(w1.Value.Word, "OR"))
        {
            var w2 = NextSignificantWord(batch, w1.Value.NextPos);
            if (w2 is null) return null;
            if (!Equals(w2.Value.Word, "ALTER")) return null;
            isCreateOrAlter = true;
            w1 = NextSignificantWord(batch, w2.Value.NextPos);
            if (w1 is null) return null;
        }

        ObjectType? objectType = MapToObjectType(w1.Value.Word);
        if (objectType is null) return null;
        pos = w1.Value.NextPos;

        // Schema is special: CREATE SCHEMA <name>  (no schema qualifier).
        if (objectType == ObjectType.Schema)
        {
            var nameWord = NextSignificantWord(batch, pos);
            if (nameWord is null) return null;
            return new ParsedSqlObject
            {
                ObjectType = ObjectType.Schema,
                Id = SchemaQualifiedName.TopLevel(nameWord.Value.Word, nameWord.Value.Word),
                Ddl = batch,
                BatchIndex = batchIndex,
                IsCreateOrAlter = isCreateOrAlter,
                TargetDatabase = targetDatabase,
            };
        }

        // Schema-qualified or unqualified name.
        var firstWord = NextSignificantWord(batch, pos);
        if (firstWord is null) return null;
        string firstName = firstWord.Value.Word;
        pos = firstWord.Value.NextPos;

        string schema = "dbo";
        string objectName = firstName;

        var dotMaybe = NextSignificantWord(batch, pos);
        if (dotMaybe is not null && dotMaybe.Value.Word == ".")
        {
            var second = NextSignificantWord(batch, dotMaybe.Value.NextPos);
            if (second is not null)
            {
                schema = firstName;
                objectName = second.Value.Word;
                pos = second.Value.NextPos;
            }
        }

        // Disambiguate TYPE: CREATE TYPE <name> AS TABLE → table type;
        // anything else (FROM <basetype>, etc.) → user-defined data type.
        if (objectType == ObjectType.UserDefinedDataType)
        {
            var maybeAs = NextSignificantWord(batch, pos);
            if (maybeAs is not null && Equals(maybeAs.Value.Word, "AS"))
            {
                var maybeTable = NextSignificantWord(batch, maybeAs.Value.NextPos);
                if (maybeTable is not null && Equals(maybeTable.Value.Word, "TABLE"))
                    objectType = ObjectType.UserDefinedTableType;
            }
        }

        return new ParsedSqlObject
        {
            ObjectType = objectType.Value,
            Id = SchemaQualifiedName.TopLevel(schema, objectName),
            Ddl = batch,
            BatchIndex = batchIndex,
            IsCreateOrAlter = isCreateOrAlter,
            TargetDatabase = targetDatabase,
        };
    }

    /// <summary>
    /// Yields the next "word" — an identifier (regular, bracketed, or quoted),
    /// a single-character symbol like <c>.</c>, or null at end of input.
    /// Whitespace, line comments, block comments, and string literals are
    /// skipped (string literals are stepped over, not returned).
    /// </summary>
    private static (string Word, int NextPos)? NextSignificantWord(string text, int start)
    {
        int n = text.Length;
        int i = start;

        while (i < n)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                i += 2;
                while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                    i++;
                if (i < n) i += 2;
                continue;
            }

            if (c == '\'')
            {
                // Skip string literal.
                i++;
                while (i < n)
                {
                    if (text[i] == '\'' && i + 1 < n && text[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }
                    if (text[i] == '\'') { i++; break; }
                    i++;
                }
                continue;
            }

            if (c == '[')
            {
                int contentStart = i + 1;
                int j = contentStart;
                while (j < n)
                {
                    if (text[j] == ']' && j + 1 < n && text[j + 1] == ']')
                    {
                        j += 2;
                        continue;
                    }
                    if (text[j] == ']') break;
                    j++;
                }
                if (j >= n) return null;
                string raw = text.Substring(contentStart, j - contentStart);
                string unescaped = raw.Replace("]]", "]");
                return (unescaped, j + 1);
            }

            if (IsIdentStart(c))
            {
                int wordEnd = i + 1;
                while (wordEnd < n && IsIdentChar(text[wordEnd]))
                    wordEnd++;
                return (text.Substring(i, wordEnd - i), wordEnd);
            }

            // Single-char symbol (., ,, (, ), ;, etc.)
            return (c.ToString(), i + 1);
        }

        return null;
    }

    private static bool IsIdentStart(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '@' || c == '#';

    private static bool IsIdentChar(char c)
        => IsIdentStart(c) || (c >= '0' && c <= '9') || c == '$';

    private static bool Equals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ObjectType? MapToObjectType(string keyword)
    {
        if (Equals(keyword, "PROC") || Equals(keyword, "PROCEDURE")) return ObjectType.StoredProcedure;
        if (Equals(keyword, "FUNCTION")) return ObjectType.UserDefinedFunction;
        if (Equals(keyword, "VIEW")) return ObjectType.View;
        if (Equals(keyword, "TABLE")) return ObjectType.Table;
        if (Equals(keyword, "TRIGGER")) return ObjectType.Trigger;
        if (Equals(keyword, "TYPE")) return ObjectType.UserDefinedDataType;
        if (Equals(keyword, "SEQUENCE")) return ObjectType.Sequence;
        if (Equals(keyword, "SYNONYM")) return ObjectType.Synonym;
        if (Equals(keyword, "SCHEMA")) return ObjectType.Schema;
        return null;
    }
}
