using System;
using System.Collections.Generic;
using SQLParity.Core.Model;

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

    public static ComparisonResult Compare(DatabaseSchema sideA, DatabaseSchema sideB, SchemaReadOptions options)
    {
        options = options ?? SchemaReadOptions.All;
        var changes = new List<Change>();

        // Tables
        if (options.IncludeTables)
            changes.AddRange(CompareTables(sideA.Tables, sideB.Tables));

        // Views
        if (options.IncludeViews)
            changes.AddRange(CompareById(
                sideA.Views, sideB.Views,
                ObjectType.View,
                v => v.Id, v => v.Ddl));

        // Stored Procedures
        if (options.IncludeStoredProcedures)
            changes.AddRange(CompareById(
                sideA.StoredProcedures, sideB.StoredProcedures,
                ObjectType.StoredProcedure,
                p => p.Id, p => p.Ddl));

        // Functions
        if (options.IncludeFunctions)
            changes.AddRange(CompareById(
                sideA.Functions, sideB.Functions,
                ObjectType.UserDefinedFunction,
                f => f.Id, f => f.Ddl));

        // Sequences
        if (options.IncludeSequences)
            changes.AddRange(CompareById(
                sideA.Sequences, sideB.Sequences,
                ObjectType.Sequence,
                s => s.Id, s => s.Ddl));

        // Synonyms
        if (options.IncludeSynonyms)
            changes.AddRange(CompareById(
                sideA.Synonyms, sideB.Synonyms,
                ObjectType.Synonym,
                s => s.Id, s => s.Ddl));

        // UserDefinedDataTypes
        if (options.IncludeUserDefinedDataTypes)
            changes.AddRange(CompareById(
                sideA.UserDefinedDataTypes, sideB.UserDefinedDataTypes,
                ObjectType.UserDefinedDataType,
                t => t.Id, t => t.Ddl));

        // UserDefinedTableTypes
        if (options.IncludeUserDefinedTableTypes)
            changes.AddRange(CompareById(
                sideA.UserDefinedTableTypes, sideB.UserDefinedTableTypes,
                ObjectType.UserDefinedTableType,
                t => t.Id, t => t.Ddl));

        // Schemas (match by Name, case-insensitive)
        if (options.IncludeSchemas)
            changes.AddRange(CompareSchemas(sideA.Schemas, sideB.Schemas));

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
        IReadOnlyList<TableModel> tablesB)
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

                bool ddlDiffers = !string.Equals(NormalizeDdl(tableA.Ddl), NormalizeDdl(tableB.Ddl), StringComparison.Ordinal);

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
        Func<T, string> ddlSelector)
    {
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
                if (!string.Equals(NormalizeDdl(ddlSelector(itemA)), NormalizeDdl(ddlSelector(itemB)), StringComparison.Ordinal))
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
        IReadOnlyList<SchemaModel> schemasB)
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
                if (!string.Equals(NormalizeDdl(schemaA.Ddl), NormalizeDdl(schemaB.Ddl), StringComparison.Ordinal))
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
}
