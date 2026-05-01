using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;
using SQLParity.Core.Parsing;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Compares two DatabaseSchema snapshots and produces a ComparisonResult.
/// </summary>
public static class SchemaComparator
{
    public static ComparisonResult Compare(DatabaseSchema sideA, DatabaseSchema sideB)
    {
        return Compare(sideA, sideB, SchemaReadOptions.All);
    }

    public static ComparisonResult Compare(
        DatabaseSchema sideA,
        DatabaseSchema sideB,
        SchemaReadOptions options,
        bool ignoreCommentsInStoredProcedures = false,
        bool ignoreWhitespaceInStoredProcedures = false,
        bool ignoreOptionalBrackets = false,
        bool limitToFolderObjects = false,
        bool sideBIsFolder = false)
    {
        options = options ?? SchemaReadOptions.All;
        var changes = new List<Change>();

        // The "general" normalizer applies to every object type. When the
        // user opts in, we strip optional square brackets first so that
        // [dbo].[Foo] and dbo.Foo collapse to the same canonical form.
        Func<string, string> generalNormalizer = ignoreOptionalBrackets
            ? (Func<string, string>)(s => NormalizeDdl(StripOptionalSqlBrackets(s)))
            : NormalizeDdl;

        // In folder mode, table DDL on Side A is SMO's full multi-batch
        // output (SET / CREATE TABLE / ALTER TABLE constraint blocks) while
        // Side B holds only the parsed CREATE TABLE batch. Normalize both
        // sides to the CREATE TABLE batch alone so a freshly synced folder
        // doesn't re-appear as Modified on the next compare.
        Func<string, string> tableNormalizer = sideBIsFolder
            ? (Func<string, string>)(s => generalNormalizer(ExtractCreateBatch(s, ObjectType.Table)))
            : generalNormalizer;

        // Tables
        if (options.IncludeTables)
            changes.AddRange(CompareTables(sideA.Tables, sideB.Tables, tableNormalizer));

        // Views
        if (options.IncludeViews)
            changes.AddRange(CompareById(
                sideA.Views, sideB.Views,
                ObjectType.View,
                v => v.Id, v => v.Ddl,
                generalNormalizer));

        // Stored procedures and user-defined functions are both "routines" —
        // textual T-SQL bodies. They share the same routine-level normalizer,
        // which composes (in order) optional comment removal, optional bracket
        // stripping, and either literal-aware whitespace collapsing or the
        // standard whitespace-aggressive normalize. Tables, views, schemas,
        // etc. continue to use the general normalizer (no comment/whitespace
        // skipping). The Change still carries the original DDL so the diff
        // panel always shows the source.
        Func<string, string> routineNormalizer = BuildRoutineNormalizer(
            ignoreCommentsInStoredProcedures,
            ignoreWhitespaceInStoredProcedures,
            ignoreOptionalBrackets);

        if (options.IncludeStoredProcedures)
            changes.AddRange(CompareById(
                sideA.StoredProcedures, sideB.StoredProcedures,
                ObjectType.StoredProcedure,
                p => p.Id, p => p.Ddl,
                routineNormalizer));

        if (options.IncludeFunctions)
            changes.AddRange(CompareById(
                sideA.Functions, sideB.Functions,
                ObjectType.UserDefinedFunction,
                f => f.Id, f => f.Ddl,
                routineNormalizer));

        // Sequences
        if (options.IncludeSequences)
            changes.AddRange(CompareById(
                sideA.Sequences, sideB.Sequences,
                ObjectType.Sequence,
                s => s.Id, s => s.Ddl,
                generalNormalizer));

        // Synonyms
        if (options.IncludeSynonyms)
            changes.AddRange(CompareById(
                sideA.Synonyms, sideB.Synonyms,
                ObjectType.Synonym,
                s => s.Id, s => s.Ddl,
                generalNormalizer));

        // UserDefinedDataTypes
        if (options.IncludeUserDefinedDataTypes)
            changes.AddRange(CompareById(
                sideA.UserDefinedDataTypes, sideB.UserDefinedDataTypes,
                ObjectType.UserDefinedDataType,
                t => t.Id, t => t.Ddl,
                generalNormalizer));

        // UserDefinedTableTypes
        if (options.IncludeUserDefinedTableTypes)
            changes.AddRange(CompareById(
                sideA.UserDefinedTableTypes, sideB.UserDefinedTableTypes,
                ObjectType.UserDefinedTableType,
                t => t.Id, t => t.Ddl,
                generalNormalizer));

        // Schemas (match by Name, case-insensitive)
        if (options.IncludeSchemas)
            changes.AddRange(CompareSchemas(sideA.Schemas, sideB.Schemas, generalNormalizer));

        // Folder-mode filter: when Side B is a folder of .sql files and the
        // user wants to focus only on objects represented in source control,
        // drop changes whose object is missing from B (Status == New, i.e.
        // exists on A only). Modified and Dropped (B-only) changes survive
        // — Modified is the actual drift, Dropped means the file has an
        // object the live DB doesn't, which the user still wants to see.
        if (limitToFolderObjects)
            changes.RemoveAll(c => c.Status == ChangeStatus.New);

        // Classify column-level risk first so RiskClassifier can aggregate them.
        foreach (var change in changes)
        {
            foreach (var col in change.ColumnChanges)
            {
                var (colTier, colReasons) = ColumnRiskClassifier.Classify(col);
                col.Risk = colTier;
                col.Reasons = colReasons;
            }
        }

        // Classify top-level change risk.
        foreach (var change in changes)
        {
            var (tier, reasons) = RiskClassifier.Classify(change);
            change.Risk = tier;
            change.Reasons = reasons;
        }

        // Attach external references from the source side (the side whose DDL
        // will be created on the destination). We check both sides' dictionaries
        // since an object might exist only on one. For modified objects, SideA's
        // DDL is what gets applied, so SideA's refs are the relevant ones.
        foreach (var change in changes)
        {
            if (change.ObjectType != ObjectType.View
                && change.ObjectType != ObjectType.StoredProcedure
                && change.ObjectType != ObjectType.UserDefinedFunction
                && change.ObjectType != ObjectType.Trigger)
                continue;

            var key = (change.Id.Schema, change.Id.Name);
            IReadOnlyList<string>? refs = null;
            if (sideA.ExternalReferences != null && sideA.ExternalReferences.TryGetValue(key, out var a))
                refs = a;
            else if (sideB.ExternalReferences != null && sideB.ExternalReferences.TryGetValue(key, out var b))
                refs = b;

            if (refs != null && refs.Count > 0)
                change.ExternalReferences = refs;
        }

        // Attach pre-flight queries
        foreach (var change in changes)
        {
            var pf = PreFlightQueryBuilder.Build(change);
            if (pf is not null)
            {
                change.PreFlightSql = pf.Value.Sql;
                change.PreFlightDescription = pf.Value.Description;
            }

            if (change.ObjectType == ObjectType.Table)
            {
                foreach (var colChange in change.ColumnChanges)
                {
                    var cpf = PreFlightQueryBuilder.BuildForColumn(change.Id.Schema, change.Id.Name, colChange);
                    if (cpf is not null)
                    {
                        colChange.PreFlightSql = cpf.Value.Sql;
                        colChange.PreFlightDescription = cpf.Value.Description;
                    }
                }
            }
        }

        return new ComparisonResult
        {
            SideA = sideA,
            SideB = sideB,
            Changes = changes,
        };
    }

    private static IEnumerable<Change> CompareTables(
        IReadOnlyList<TableModel> tablesA,
        IReadOnlyList<TableModel> tablesB,
        Func<string, string> ddlNormalizer)
    {
        var dictA = BuildDictionary(tablesA, t => t.Id);
        var dictB = BuildDictionary(tablesB, t => t.Id);

        // New: in A not B
        foreach (var kvpA in dictA)
        {
            if (!dictB.ContainsKey(kvpA.Key))
            {
                yield return new Change
                {
                    Id = kvpA.Value.Id,
                    ObjectType = ObjectType.Table,
                    Status = ChangeStatus.New,
                    DdlSideA = kvpA.Value.Ddl,
                    DdlSideB = null,
                    ColumnChanges = Array.Empty<ColumnChange>(),
                };
            }
        }

        // Dropped: in B not A
        foreach (var kvpB in dictB)
        {
            if (!dictA.ContainsKey(kvpB.Key))
            {
                yield return new Change
                {
                    Id = kvpB.Value.Id,
                    ObjectType = ObjectType.Table,
                    Status = ChangeStatus.Dropped,
                    DdlSideA = null,
                    DdlSideB = kvpB.Value.Ddl,
                    ColumnChanges = Array.Empty<ColumnChange>(),
                };
            }
        }

        // Modified: in both, DDL or columns differ
        foreach (var kvpA2 in dictA)
        {
            var tableA = kvpA2.Value;
            if (dictB.TryGetValue(kvpA2.Key, out var tableB))
            {
                var columnChanges = ColumnComparator.Compare(
                    tableA.Id.Schema, tableA.Name,
                    tableA.Columns, tableB.Columns);

                bool ddlDiffers = !string.Equals(ddlNormalizer(tableA.Ddl), ddlNormalizer(tableB.Ddl), StringComparison.Ordinal);

                if (ddlDiffers || columnChanges.Count > 0)
                {
                    yield return new Change
                    {
                        Id = tableA.Id,
                        ObjectType = ObjectType.Table,
                        Status = ChangeStatus.Modified,
                        DdlSideA = tableA.Ddl,
                        DdlSideB = tableB.Ddl,
                        ColumnChanges = columnChanges,
                    };
                }
            }
        }
    }

    private static IEnumerable<Change> CompareById<T>(
        IReadOnlyList<T> listA,
        IReadOnlyList<T> listB,
        ObjectType objectType,
        Func<T, SchemaQualifiedName> idSelector,
        Func<T, string> ddlSelector,
        Func<string, string>? ddlNormalizer = null)
    {
        var normalize = ddlNormalizer ?? NormalizeDdl;
        var dictA = BuildDictionary(listA, idSelector);
        var dictB = BuildDictionary(listB, idSelector);

        // New: in A not B
        foreach (var kvpA in dictA)
        {
            if (!dictB.ContainsKey(kvpA.Key))
            {
                yield return new Change
                {
                    Id = idSelector(kvpA.Value),
                    ObjectType = objectType,
                    Status = ChangeStatus.New,
                    DdlSideA = ddlSelector(kvpA.Value),
                    DdlSideB = null,
                    ColumnChanges = Array.Empty<ColumnChange>(),
                };
            }
        }

        // Dropped: in B not A
        foreach (var kvpB in dictB)
        {
            if (!dictA.ContainsKey(kvpB.Key))
            {
                yield return new Change
                {
                    Id = idSelector(kvpB.Value),
                    ObjectType = objectType,
                    Status = ChangeStatus.Dropped,
                    DdlSideA = null,
                    DdlSideB = ddlSelector(kvpB.Value),
                    ColumnChanges = Array.Empty<ColumnChange>(),
                };
            }
        }

        // Modified: in both, DDL differs (whitespace-normalized comparison)
        foreach (var kvpA2 in dictA)
        {
            var itemA = kvpA2.Value;
            if (dictB.TryGetValue(kvpA2.Key, out var itemB))
            {
                if (!string.Equals(normalize(ddlSelector(itemA)), normalize(ddlSelector(itemB)), StringComparison.Ordinal))
                {
                    yield return new Change
                    {
                        Id = idSelector(itemA),
                        ObjectType = objectType,
                        Status = ChangeStatus.Modified,
                        DdlSideA = ddlSelector(itemA),
                        DdlSideB = ddlSelector(itemB),
                        ColumnChanges = Array.Empty<ColumnChange>(),
                    };
                }
            }
        }
    }

    private static IEnumerable<Change> CompareSchemas(
        IReadOnlyList<SchemaModel> schemasA,
        IReadOnlyList<SchemaModel> schemasB,
        Func<string, string> ddlNormalizer)
    {
        var dictA = BuildDictionary(schemasA, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var dictB = BuildDictionary(schemasB, s => s.Name, StringComparer.OrdinalIgnoreCase);

        // New: in A not B
        foreach (var kvpA in dictA)
        {
            if (!dictB.ContainsKey(kvpA.Key))
            {
                yield return new Change
                {
                    Id = SchemaQualifiedName.TopLevel(kvpA.Value.Name, kvpA.Value.Name),
                    ObjectType = ObjectType.Schema,
                    Status = ChangeStatus.New,
                    DdlSideA = kvpA.Value.Ddl,
                    DdlSideB = null,
                    ColumnChanges = Array.Empty<ColumnChange>(),
                };
            }
        }

        // Dropped: in B not A
        foreach (var kvpB in dictB)
        {
            if (!dictA.ContainsKey(kvpB.Key))
            {
                yield return new Change
                {
                    Id = SchemaQualifiedName.TopLevel(kvpB.Value.Name, kvpB.Value.Name),
                    ObjectType = ObjectType.Schema,
                    Status = ChangeStatus.Dropped,
                    DdlSideA = null,
                    DdlSideB = kvpB.Value.Ddl,
                    ColumnChanges = Array.Empty<ColumnChange>(),
                };
            }
        }

        // Modified: in both, DDL differs
        foreach (var kvpA2 in dictA)
        {
            var schemaA = kvpA2.Value;
            if (dictB.TryGetValue(kvpA2.Key, out var schemaB))
            {
                if (!string.Equals(ddlNormalizer(schemaA.Ddl), ddlNormalizer(schemaB.Ddl), StringComparison.Ordinal))
                {
                    yield return new Change
                    {
                        Id = SchemaQualifiedName.TopLevel(schemaA.Name, schemaA.Name),
                        ObjectType = ObjectType.Schema,
                        Status = ChangeStatus.Modified,
                        DdlSideA = schemaA.Ddl,
                        DdlSideB = schemaB.Ddl,
                        ColumnChanges = Array.Empty<ColumnChange>(),
                    };
                }
            }
        }
    }

    private static Dictionary<SchemaQualifiedName, T> BuildDictionary<T>(
        IReadOnlyList<T> items,
        Func<T, SchemaQualifiedName> keySelector)
    {
        var dict = new Dictionary<SchemaQualifiedName, T>();
        foreach (var item in items)
            dict[keySelector(item)] = item;
        return dict;
    }

    private static Dictionary<string, T> BuildDictionary<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector,
        StringComparer comparer)
    {
        var dict = new Dictionary<string, T>(comparer);
        foreach (var item in items)
            dict[keySelector(item)] = item;
        return dict;
    }

    /// <summary>
    /// Normalizes DDL for comparison purposes: trims each line, collapses
    /// multiple whitespace to single space, removes trailing whitespace,
    /// and normalizes line endings. This prevents false "Modified" results
    /// from insignificant whitespace differences.
    /// </summary>
    /// <summary>
    /// Normalizes DDL for comparison: trims each line, collapses whitespace,
    /// removes blank lines, and lowercases. This prevents false "Modified"
    /// results from insignificant whitespace or case differences in SQL keywords.
    /// </summary>
    private static string NormalizeDdl(string ddl)
    {
        if (string.IsNullOrEmpty(ddl)) return string.Empty;

        var lines = ddl.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var normalized = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            // Trim both ends
            var trimmed = line.Trim();

            // Skip blank lines
            if (trimmed.Length == 0) continue;

            // Collapse multiple internal whitespace to single space
            while (trimmed.Contains("  "))
                trimmed = trimmed.Replace("  ", " ");

            // Lowercase for case-insensitive comparison
            normalized.AppendLine(trimmed.ToLowerInvariant());
        }
        return normalized.ToString().TrimEnd();
    }

    /// <summary>
    /// Removes square brackets from any T-SQL identifier <c>[name]</c> where the
    /// brackets are optional — i.e. <c>name</c> is a regular identifier (letter
    /// or underscore start, then alphanumerics / _ / @ / # / $) AND is not a
    /// T-SQL reserved keyword. Identifiers that need their brackets (reserved
    /// words like <c>[Order]</c>, names with spaces, escapes, or special chars)
    /// are preserved verbatim. Single-quoted string literals are skipped so
    /// that bracket characters inside data ('foo[bar]') are never altered.
    /// </summary>
    internal static string StripOptionalSqlBrackets(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;

        var sb = new System.Text.StringBuilder(sql.Length);
        int n = sql.Length;
        int i = 0;

        while (i < n)
        {
            char c = sql[i];

            // String literal — copy verbatim, including any [ or ] inside.
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < n)
                {
                    if (sql[i] == '\'' && i + 1 < n && sql[i + 1] == '\'')
                    {
                        sb.Append("''");
                        i += 2;
                    }
                    else if (sql[i] == '\'')
                    {
                        sb.Append('\'');
                        i++;
                        break;
                    }
                    else
                    {
                        sb.Append(sql[i]);
                        i++;
                    }
                }
                continue;
            }

            // Bracketed identifier — find the matching ']' (with ']]' escape)
            // and decide whether the name inside still needs the brackets.
            if (c == '[')
            {
                int contentStart = i + 1;
                int j = contentStart;
                bool foundClose = false;
                while (j < n)
                {
                    if (sql[j] == ']' && j + 1 < n && sql[j + 1] == ']')
                    {
                        j += 2;
                        continue;
                    }
                    if (sql[j] == ']')
                    {
                        foundClose = true;
                        break;
                    }
                    j++;
                }

                if (foundClose)
                {
                    string inner = sql.Substring(contentStart, j - contentStart);
                    if (CanUnbracketIdentifier(inner))
                        sb.Append(inner);
                    else
                        sb.Append(sql, i, j - i + 1); // keep [name] verbatim
                    i = j + 1;
                    continue;
                }

                // Unmatched '[' — leave it as-is and move on.
                sb.Append(c);
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool CanUnbracketIdentifier(string ident)
    {
        if (string.IsNullOrEmpty(ident)) return false;
        char first = ident[0];
        if (!(IsAsciiLetter(first) || first == '_')) return false;
        for (int k = 1; k < ident.Length; k++)
        {
            char ch = ident[k];
            if (!(IsAsciiLetter(ch) || (ch >= '0' && ch <= '9')
                  || ch == '_' || ch == '@' || ch == '#' || ch == '$'))
                return false;
        }
        return !ReservedKeywords.Contains(ident);
    }

    private static bool IsAsciiLetter(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>
    /// T-SQL reserved keywords (Books Online). Used by
    /// <see cref="StripOptionalSqlBrackets"/> to keep brackets around names
    /// that would be parse errors without them. Case-insensitive lookup.
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ADD","ALL","ALTER","AND","ANY","AS","ASC","AUTHORIZATION","BACKUP","BEGIN",
        "BETWEEN","BREAK","BROWSE","BULK","BY","CASCADE","CASE","CHECK","CHECKPOINT",
        "CLOSE","CLUSTERED","COALESCE","COLLATE","COLUMN","COMMIT","COMPUTE",
        "CONSTRAINT","CONTAINS","CONTAINSTABLE","CONTINUE","CONVERT","CREATE","CROSS",
        "CURRENT","CURRENT_DATE","CURRENT_TIME","CURRENT_TIMESTAMP","CURRENT_USER",
        "CURSOR","DATABASE","DBCC","DEALLOCATE","DECLARE","DEFAULT","DELETE","DENY",
        "DESC","DISK","DISTINCT","DISTRIBUTED","DOUBLE","DROP","DUMP","ELSE","END",
        "ERRLVL","ESCAPE","EXCEPT","EXEC","EXECUTE","EXISTS","EXIT","EXTERNAL",
        "FETCH","FILE","FILLFACTOR","FOR","FOREIGN","FREETEXT","FREETEXTTABLE","FROM",
        "FULL","FUNCTION","GOTO","GRANT","GROUP","HAVING","HOLDLOCK","IDENTITY",
        "IDENTITY_INSERT","IDENTITYCOL","IF","IN","INDEX","INNER","INSERT","INTERSECT",
        "INTO","IS","JOIN","KEY","KILL","LEFT","LIKE","LINENO","LOAD","MERGE",
        "NATIONAL","NOCHECK","NONCLUSTERED","NOT","NULL","NULLIF","OF","OFF","OFFSETS",
        "ON","OPEN","OPENDATASOURCE","OPENQUERY","OPENROWSET","OPENXML","OPTION","OR",
        "ORDER","OUTER","OVER","PERCENT","PIVOT","PLAN","PRECISION","PRIMARY","PRINT",
        "PROC","PROCEDURE","PUBLIC","RAISERROR","READ","READTEXT","RECONFIGURE",
        "REFERENCES","REPLICATION","RESTORE","RESTRICT","RETURN","REVERT","REVOKE",
        "RIGHT","ROLLBACK","ROWCOUNT","ROWGUIDCOL","RULE","SAVE","SCHEMA",
        "SECURITYAUDIT","SELECT","SEMANTICKEYPHRASETABLE",
        "SEMANTICSIMILARITYDETAILSTABLE","SEMANTICSIMILARITYTABLE","SESSION_USER",
        "SET","SETUSER","SHUTDOWN","SOME","STATISTICS","SYSTEM_USER","TABLE",
        "TABLESAMPLE","TEXTSIZE","THEN","TO","TOP","TRAN","TRANSACTION","TRIGGER",
        "TRUNCATE","TRY_CONVERT","TSEQUAL","UNION","UNIQUE","UNPIVOT","UPDATE",
        "UPDATETEXT","USE","USER","VALUES","VARYING","VIEW","WAITFOR","WHEN","WHERE",
        "WHILE","WITH","WRITETEXT",
    };

    /// <summary>
    /// Builds the normalize-then-compare function for routine bodies (stored
    /// procedures and user-defined functions). The pipeline composes (in
    /// order): optional comment removal, optional bracket stripping for
    /// unambiguous identifiers, then either literal-aware whitespace
    /// normalization (when "ignore whitespace" is on) or the standard
    /// whitespace-aggressive normalize. Both whitespace paths drop blank
    /// lines as a side effect. String literals are preserved verbatim by
    /// every stage that touches them.
    /// </summary>
    private static Func<string, string> BuildRoutineNormalizer(
        bool ignoreComments,
        bool ignoreWhitespace,
        bool ignoreOptionalBrackets)
    {
        Func<string, string> normalizeFinal = ignoreWhitespace
            ? (Func<string, string>)NormalizeDdlPreservingLiterals
            : NormalizeDdl;

        return s =>
        {
            // Always rewrite CREATE OR ALTER to bare CREATE for comparison.
            // The two forms are semantically equivalent, but folder mode
            // emits the OR ALTER variant for re-runnable files while SMO
            // scripts the database side as plain CREATE — without this step
            // every freshly-synced folder file would re-appear as Modified
            // on the next compare.
            s = StripCreateOrAlter(s);
            if (ignoreComments) s = StripSqlComments(s);
            if (ignoreOptionalBrackets) s = StripOptionalSqlBrackets(s);
            return normalizeFinal(s);
        };
    }

    /// <summary>
    /// Removes the optional <c>OR ALTER</c> tokens between <c>CREATE</c> and
    /// the object-type keyword (PROCEDURE / FUNCTION / VIEW / TRIGGER), so
    /// <c>CREATE PROCEDURE Foo</c> and <c>CREATE OR ALTER PROCEDURE Foo</c>
    /// canonicalize to the same string. No-op when CREATE is not found or
    /// OR ALTER is not present. Comments and string literals are skipped.
    /// </summary>
    internal static string StripCreateOrAlter(string ddl)
    {
        if (string.IsNullOrEmpty(ddl)) return string.Empty;

        int n = ddl.Length;
        int i = 0;

        // Walk to the first significant CREATE keyword.
        while (i < n)
        {
            char c = ddl[i];
            char next = i + 1 < n ? ddl[i + 1] : '\0';

            if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (ddl[i] == '\'' && i + 1 < n && ddl[i + 1] == '\'') i += 2;
                    else if (ddl[i] == '\'') { i++; break; }
                    else i++;
                }
                continue;
            }
            if (c == '-' && next == '-')
            {
                while (i < n && ddl[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                i += 2;
                while (i < n && !(ddl[i] == '*' && i + 1 < n && ddl[i + 1] == '/')) i++;
                if (i < n) i += 2;
                continue;
            }
            if ((c == 'C' || c == 'c')
                && i + 6 <= n
                && string.Equals(ddl.Substring(i, 6), "CREATE", StringComparison.OrdinalIgnoreCase)
                && (i == 0 || !IsIdentChar(ddl[i - 1]))
                && (i + 6 == n || !IsIdentChar(ddl[i + 6])))
            {
                int afterCreate = i + 6;
                int j = afterCreate;
                while (j < n && char.IsWhiteSpace(ddl[j])) j++;

                // OR
                if (j + 2 <= n
                    && string.Equals(ddl.Substring(j, 2), "OR", StringComparison.OrdinalIgnoreCase)
                    && (j + 2 == n || !IsIdentChar(ddl[j + 2])))
                {
                    int afterOr = j + 2;
                    int k = afterOr;
                    while (k < n && char.IsWhiteSpace(ddl[k])) k++;

                    // ALTER
                    if (k + 5 <= n
                        && string.Equals(ddl.Substring(k, 5), "ALTER", StringComparison.OrdinalIgnoreCase)
                        && (k + 5 == n || !IsIdentChar(ddl[k + 5])))
                    {
                        // Drop the "OR ALTER" tokens (inclusive of leading whitespace
                        // since afterCreate's space is already in the prefix).
                        return ddl.Substring(0, afterCreate) + ddl.Substring(k + 5);
                    }
                }
                return ddl;
            }
            i++;
        }
        return ddl;
    }

    private static bool IsIdentChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9') || c == '_' || c == '@' || c == '#' || c == '$';

    /// <summary>
    /// Returns the first batch in a multi-batch DDL string that defines an
    /// object of the requested type, stripped of any surrounding
    /// <c>IF OBJECT_ID(…) IS NULL BEGIN … END</c> idempotency wrapper.
    /// SMO scripts tables as SET / CREATE TABLE / ALTER TABLE constraint
    /// stanzas (no wrapper); folder-mode files contain the CREATE inside
    /// an IF wrapper. Comparison must collapse both forms to the bare CREATE
    /// statement so a freshly-synced file doesn't flag as Modified.
    /// </summary>
    internal static string ExtractCreateBatch(string ddl, ObjectType expectedType)
    {
        if (string.IsNullOrEmpty(ddl)) return string.Empty;
        var parsed = new SqlFileParser().Parse(ddl);
        var match = parsed.FirstOrDefault(p => p.ObjectType == expectedType);
        if (match == null) return ddl;
        return StripIfNotExistsWrapper(match.Ddl);
    }

    /// <summary>
    /// If the batch text starts with an <c>IF</c>-condition guard around a
    /// CREATE statement, strips the guard and returns just the CREATE body.
    /// Recognizes both single-statement IFs (<c>IF … CREATE TABLE …</c>) and
    /// BEGIN/END block IFs. Returns the input unchanged when no IF wrapper
    /// is present (e.g. SMO's bare CREATE TABLE batch).
    /// </summary>
    private static string StripIfNotExistsWrapper(string batch)
    {
        if (string.IsNullOrEmpty(batch)) return string.Empty;

        int createIdx = FindCreateKeyword(batch);
        if (createIdx < 0) return batch;
        if (createIdx == 0) return batch; // no preamble to strip

        // Verify the preamble is an IF guard (vs. say leading comments only —
        // those should be preserved). Find the first significant keyword.
        int probe = 0;
        SkipInsignificantTokens(batch, ref probe);
        if (probe >= batch.Length) return batch;
        if (!IsKeywordAt(batch, probe, "IF")) return batch;

        // Take from CREATE onwards, then trim a trailing END if it's the
        // closer of a BEGIN/END block.
        string fromCreate = batch.Substring(createIdx);
        return TrimTrailingEnd(fromCreate);
    }

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
            if (IsKeywordAt(text, i, "CREATE")) return i;
            i++;
        }
        return -1;
    }

    private static void SkipInsignificantTokens(string text, ref int i)
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

    private static bool IsKeywordAt(string text, int pos, string keyword)
    {
        if (pos + keyword.Length > text.Length) return false;
        if (pos > 0 && IsIdentChar(text[pos - 1])) return false;
        if (pos + keyword.Length < text.Length && IsIdentChar(text[pos + keyword.Length])) return false;
        for (int k = 0; k < keyword.Length; k++)
        {
            if (char.ToUpperInvariant(text[pos + k]) != char.ToUpperInvariant(keyword[k]))
                return false;
        }
        return true;
    }

    private static string TrimTrailingEnd(string text)
    {
        string trimmed = text.TrimEnd();
        if (trimmed.Length < 3) return trimmed;
        if (!trimmed.EndsWith("END", StringComparison.OrdinalIgnoreCase)) return trimmed;
        // Word boundary: char before END must not be an identifier char.
        if (trimmed.Length > 3 && IsIdentChar(trimmed[trimmed.Length - 4])) return trimmed;
        return trimmed.Substring(0, trimmed.Length - 3).TrimEnd();
    }

    /// <summary>
    /// Normalizes DDL by collapsing all whitespace runs OUTSIDE of single-quoted
    /// string literals to a single space, and lowercasing non-literal text.
    /// String literals ('...') are copied verbatim — every space, tab, newline,
    /// and case-sensitive character inside them is preserved, since whitespace
    /// or case differences inside a literal can change runtime behavior.
    /// Comments and bracketed identifiers are not given a special state but
    /// their delimiters are recognized so that a stray quote inside (e.g.
    /// <c>-- it's broken</c>) is not mistaken for a string literal opener.
    /// </summary>
    internal static string NormalizeDdlPreservingLiterals(string ddl)
    {
        if (string.IsNullOrEmpty(ddl)) return string.Empty;

        var sb = new System.Text.StringBuilder(ddl.Length);
        int n = ddl.Length;
        int i = 0;
        bool prevWasSpace = false;

        void AppendCodeChar(char c)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                prevWasSpace = false;
            }
        }

        while (i < n)
        {
            char c = ddl[i];
            char next = i + 1 < n ? ddl[i + 1] : '\0';

            // String literal — preserve verbatim (case AND whitespace).
            if (c == '\'')
            {
                sb.Append(c);
                prevWasSpace = false;
                i++;
                while (i < n)
                {
                    if (ddl[i] == '\'' && i + 1 < n && ddl[i + 1] == '\'')
                    {
                        sb.Append("''");
                        i += 2;
                    }
                    else if (ddl[i] == '\'')
                    {
                        sb.Append('\'');
                        i++;
                        break;
                    }
                    else
                    {
                        sb.Append(ddl[i]);
                        i++;
                    }
                }
                continue;
            }

            // Line comment — collapse whitespace and lowercase, but don't
            // interpret quotes inside as string-literal openers.
            if (c == '-' && next == '-')
            {
                AppendCodeChar('-');
                AppendCodeChar('-');
                i += 2;
                while (i < n && ddl[i] != '\n' && ddl[i] != '\r')
                {
                    AppendCodeChar(ddl[i]);
                    i++;
                }
                continue;
            }

            // Block comment — same treatment.
            if (c == '/' && next == '*')
            {
                AppendCodeChar('/');
                AppendCodeChar('*');
                i += 2;
                while (i < n)
                {
                    if (ddl[i] == '*' && i + 1 < n && ddl[i + 1] == '/')
                    {
                        AppendCodeChar('*');
                        AppendCodeChar('/');
                        i += 2;
                        break;
                    }
                    AppendCodeChar(ddl[i]);
                    i++;
                }
                continue;
            }

            // Bracketed identifier — collapse/lowercase, but don't parse
            // strings inside (e.g. [Some'Name] is a single identifier).
            if (c == '[')
            {
                AppendCodeChar('[');
                i++;
                while (i < n)
                {
                    if (ddl[i] == ']' && i + 1 < n && ddl[i + 1] == ']')
                    {
                        AppendCodeChar(']');
                        AppendCodeChar(']');
                        i += 2;
                    }
                    else if (ddl[i] == ']')
                    {
                        AppendCodeChar(']');
                        i++;
                        break;
                    }
                    else
                    {
                        AppendCodeChar(ddl[i]);
                        i++;
                    }
                }
                continue;
            }

            AppendCodeChar(c);
            i++;
        }

        if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Removes T-SQL comments (-- line and /* */ block) from a DDL string.
    /// Respects single-quoted string literals and bracketed identifiers so
    /// comment-like sequences inside them are preserved. Newlines outside
    /// removed comments are preserved; block comments are replaced with a
    /// single space to keep adjacent tokens from merging.
    /// </summary>
    internal static string StripSqlComments(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;

        var sb = new System.Text.StringBuilder(sql.Length);
        int n = sql.Length;
        int i = 0;
        while (i < n)
        {
            char c = sql[i];
            char next = i + 1 < n ? sql[i + 1] : '\0';

            // Line comment: skip until newline (newline itself is preserved)
            if (c == '-' && next == '-')
            {
                i += 2;
                while (i < n && sql[i] != '\n' && sql[i] != '\r') i++;
                continue;
            }

            // Block comment: skip through closing */
            if (c == '/' && next == '*')
            {
                i += 2;
                while (i < n && !(sql[i] == '*' && i + 1 < n && sql[i + 1] == '/'))
                    i++;
                if (i < n) i += 2; // skip */
                sb.Append(' ');
                continue;
            }

            // Single-quoted string literal — copy verbatim, handling '' escape
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < n)
                {
                    if (sql[i] == '\'' && i + 1 < n && sql[i + 1] == '\'')
                    {
                        sb.Append("''");
                        i += 2;
                    }
                    else if (sql[i] == '\'')
                    {
                        sb.Append('\'');
                        i++;
                        break;
                    }
                    else
                    {
                        sb.Append(sql[i]);
                        i++;
                    }
                }
                continue;
            }

            // Bracketed identifier — copy verbatim, handling ]] escape
            if (c == '[')
            {
                sb.Append(c);
                i++;
                while (i < n)
                {
                    if (sql[i] == ']' && i + 1 < n && sql[i + 1] == ']')
                    {
                        sb.Append("]]");
                        i += 2;
                    }
                    else if (sql[i] == ']')
                    {
                        sb.Append(']');
                        i++;
                        break;
                    }
                    else
                    {
                        sb.Append(sql[i]);
                        i++;
                    }
                }
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
