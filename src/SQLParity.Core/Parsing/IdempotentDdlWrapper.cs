using System;
using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Wraps a bare CREATE statement in the appropriate idempotent guard so the
/// resulting script can be re-run safely. Pure function — output depends only
/// on inputs. Does NOT add USE/GO/header comments; those are emitted by
/// <c>FolderSyncWriter</c>.
/// </summary>
/// <remarks>
/// Two strategies, picked by object type per the v1.2 spec:
/// <list type="bullet">
/// <item><b>CREATE OR ALTER</b> — for procedures, functions, views, triggers.
/// Idempotency is built into the statement itself; the wrapper just rewrites
/// a leading <c>CREATE</c> to <c>CREATE OR ALTER</c> if not already present.</item>
/// <item><b>IF NOT EXISTS skip-guard</b> — for tables, UDDTs, UDTTs, sequences,
/// synonyms, schemas. Per explicit user direction, we never DROP+CREATE for
/// these — the script silently no-ops if the object already exists, leaving
/// the existing definition (and any data it holds) untouched.</item>
/// </list>
/// </remarks>
public static class IdempotentDdlWrapper
{
    /// <summary>
    /// Returns a guarded DROP statement for an object that has been removed
    /// from the source database. Uses the T-SQL 2016+ <c>DROP &lt;type&gt; IF
    /// EXISTS</c> shorthand so the script no-ops when the object is already
    /// gone. Used by <c>FolderSyncWriter</c> when a Dropped change needs the
    /// .sql file to encode "this object should not exist".
    /// </summary>
    public static string WrapDrop(ObjectType objectType, string schema, string name)
    {
        schema ??= "dbo";
        name ??= string.Empty;
        string fq = objectType == ObjectType.Schema
            ? $"[{name}]"
            : $"[{schema}].[{name}]";

        string keyword = objectType switch
        {
            ObjectType.StoredProcedure => "PROCEDURE",
            ObjectType.UserDefinedFunction => "FUNCTION",
            ObjectType.View => "VIEW",
            ObjectType.Table => "TABLE",
            ObjectType.Trigger => "TRIGGER",
            ObjectType.UserDefinedDataType or ObjectType.UserDefinedTableType => "TYPE",
            ObjectType.Sequence => "SEQUENCE",
            ObjectType.Synonym => "SYNONYM",
            ObjectType.Schema => "SCHEMA",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(keyword)) return string.Empty;

        return $"DROP {keyword} IF EXISTS {fq};";
    }

    public static string Wrap(ObjectType objectType, string schema, string name, string bareDdl)
    {
        if (string.IsNullOrEmpty(bareDdl)) bareDdl = string.Empty;
        bareDdl = bareDdl.TrimEnd();
        schema ??= "dbo";
        name ??= string.Empty;

        return objectType switch
        {
            ObjectType.StoredProcedure or
            ObjectType.UserDefinedFunction or
            ObjectType.View or
            ObjectType.Trigger
                => EnsureCreateOrAlter(bareDdl),

            ObjectType.Table
                => WrapWithObjectIdGuard(schema, name, "U", ExtractCreateBatch(bareDdl, ObjectType.Table)),

            ObjectType.UserDefinedDataType
                => WrapWithTypesGuard(schema, name, isTableType: false, bareDdl),

            ObjectType.UserDefinedTableType
                => WrapWithTypesGuard(schema, name, isTableType: true, bareDdl),

            ObjectType.Sequence
                => WrapWithCatalogGuard("sys.sequences", schema, name, bareDdl),

            ObjectType.Synonym
                => WrapWithCatalogGuard("sys.synonyms", schema, name, bareDdl),

            ObjectType.Schema
                => WrapSchema(name),

            _ => bareDdl,
        };
    }

    /// <summary>
    /// Inserts <c>OR ALTER</c> after the leading <c>CREATE</c> keyword, unless
    /// it's already there. Comments and whitespace before <c>CREATE</c> are
    /// preserved verbatim.
    /// </summary>
    private static string EnsureCreateOrAlter(string bareDdl)
    {
        int createIdx = FindCreateKeyword(bareDdl);
        if (createIdx < 0) return bareDdl;

        int afterCreate = createIdx + "CREATE".Length;
        if (IsAlreadyOrAlter(bareDdl, afterCreate))
            return bareDdl;

        return bareDdl.Substring(0, afterCreate) + " OR ALTER" + bareDdl.Substring(afterCreate);
    }

    private static bool IsAlreadyOrAlter(string text, int startAfterCreate)
    {
        int n = text.Length;
        int i = startAfterCreate;
        SkipInsignificant(text, ref i);
        if (i + 2 > n) return false;
        if (!Match(text, i, "OR")) return false;
        i += 2;
        if (i < n && IsIdentChar(text[i])) return false;
        SkipInsignificant(text, ref i);
        if (i + 5 > n) return false;
        if (!Match(text, i, "ALTER")) return false;
        if (i + 5 < n && IsIdentChar(text[i + 5])) return false;
        return true;
    }

    private static string WrapWithObjectIdGuard(string schema, string name, string objectIdType, string bareDdl)
    {
        // OBJECT_ID accepts the full bracketed name. 'U' = user table.
        return
            $"IF OBJECT_ID(N'[{Esc(schema)}].[{Esc(name)}]', N'{objectIdType}') IS NULL\n" +
            "BEGIN\n" +
            Indent(bareDdl) + "\n" +
            "END";
    }

    private static string WrapWithTypesGuard(string schema, string name, bool isTableType, string bareDdl)
    {
        string extra = isTableType ? " AND is_table_type = 1" : string.Empty;
        return
            "IF NOT EXISTS (\n" +
            $"    SELECT 1 FROM sys.types\n" +
            $"    WHERE schema_id = SCHEMA_ID(N'{Esc(schema)}') AND name = N'{Esc(name)}'{extra}\n" +
            ")\n" +
            "BEGIN\n" +
            Indent(bareDdl) + "\n" +
            "END";
    }

    private static string WrapWithCatalogGuard(string catalog, string schema, string name, string bareDdl)
    {
        return
            "IF NOT EXISTS (\n" +
            $"    SELECT 1 FROM {catalog}\n" +
            $"    WHERE schema_id = SCHEMA_ID(N'{Esc(schema)}') AND name = N'{Esc(name)}'\n" +
            ")\n" +
            "BEGIN\n" +
            Indent(bareDdl) + "\n" +
            "END";
    }

    /// <summary>
    /// CREATE SCHEMA must be the first statement in its batch, so an IF
    /// wrapping requires EXEC(). The bare CREATE SCHEMA text is re-emitted
    /// rather than embedded — schema DDL is trivial and varies only by name
    /// and (optional) AUTHORIZATION clause, neither of which we attempt to
    /// preserve in v1.2.
    /// </summary>
    private static string WrapSchema(string name)
    {
        return
            $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{Esc(name)}')\n" +
            $"    EXEC(N'CREATE SCHEMA [{Esc(name)}]');";
    }

    private static string Esc(string sqlIdent) => sqlIdent.Replace("'", "''");

    private static string Indent(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);
            lines[i] = line.Length == 0 ? string.Empty : "    " + line;
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Finds the first <c>CREATE</c> keyword in the text outside string
    /// literals, bracketed identifiers, and line/block comments. Returns -1
    /// if not found. Word-boundary aware so <c>RECREATE</c> isn't a match.
    /// </summary>
    private static int FindCreateKeyword(string text)
    {
        int n = text.Length;
        int i = 0;
        while (i < n)
        {
            char c = text[i];
            char next = i + 1 < n ? text[i + 1] : '\0';

            if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (text[i] == '\'' && i + 1 < n && text[i + 1] == '\'') i += 2;
                    else if (text[i] == '\'') { i++; break; }
                    else i++;
                }
                continue;
            }

            if (c == '[')
            {
                i++;
                while (i < n)
                {
                    if (text[i] == ']' && i + 1 < n && text[i + 1] == ']') i += 2;
                    else if (text[i] == ']') { i++; break; }
                    else i++;
                }
                continue;
            }

            if (c == '-' && next == '-')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;
                while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/')) i++;
                if (i < n) i += 2;
                continue;
            }

            // Word-boundary CREATE check.
            if ((c == 'C' || c == 'c')
                && i + 6 <= n
                && Match(text, i, "CREATE")
                && (i == 0 || !IsIdentChar(text[i - 1]))
                && (i + 6 == n || !IsIdentChar(text[i + 6])))
            {
                return i;
            }

            i++;
        }
        return -1;
    }

    private static void SkipInsignificant(string text, ref int i)
    {
        int n = text.Length;
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
                while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/')) i++;
                if (i < n) i += 2;
                continue;
            }
            break;
        }
    }

    private static bool Match(string text, int at, string keyword)
    {
        if (at + keyword.Length > text.Length) return false;
        for (int k = 0; k < keyword.Length; k++)
        {
            char a = text[at + k];
            char b = keyword[k];
            if (char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return false;
        }
        return true;
    }

    private static bool IsIdentChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9') || c == '_' || c == '@' || c == '#' || c == '$';

    /// <summary>
    /// SMO scripts tables as a multi-batch stream (SET ANSI_NULLS / SET
    /// QUOTED_IDENTIFIER / CREATE TABLE / ALTER TABLE constraint blocks).
    /// Wrapping that whole stream inside <c>IF OBJECT_ID(…) IS NULL BEGIN …
    /// END</c> would put GOs inside a BEGIN/END block — invalid T-SQL because
    /// the client splits on GO. We extract just the CREATE batch and wrap
    /// only that. v1.2 limitation: constraints scripted as separate ALTER
    /// statements are not preserved in the folder file.
    /// </summary>
    private static string ExtractCreateBatch(string bareDdl, ObjectType expectedType)
    {
        if (string.IsNullOrEmpty(bareDdl)) return bareDdl ?? string.Empty;
        var parsed = new SqlFileParser().Parse(bareDdl);
        foreach (var p in parsed)
            if (p.ObjectType == expectedType)
                return p.Ddl;
        return bareDdl;
    }
}
