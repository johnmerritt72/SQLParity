using System;
using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Compares two lists of columns and produces column-level change records.
/// </summary>
public static class ColumnComparator
{
    public static IList<ColumnChange> Compare(
        string tableSchema,
        string tableName,
        IReadOnlyList<ColumnModel> columnsA,
        IReadOnlyList<ColumnModel> columnsB)
    {
        var dictA = BuildDictionary(columnsA);
        var dictB = BuildDictionary(columnsB);

        var changes = new List<ColumnChange>();

        // New: in A not B
        foreach (var kvpA in dictA)
        {
            if (!dictB.ContainsKey(kvpA.Key))
            {
                changes.Add(new ColumnChange
                {
                    Id = SchemaQualifiedName.Child(tableSchema, tableName, kvpA.Value.Name),
                    ColumnName = kvpA.Value.Name,
                    Status = ChangeStatus.New,
                    SideA = kvpA.Value,
                    SideB = null,
                });
            }
        }

        // Dropped: in B not A
        foreach (var kvpB in dictB)
        {
            if (!dictA.ContainsKey(kvpB.Key))
            {
                changes.Add(new ColumnChange
                {
                    Id = SchemaQualifiedName.Child(tableSchema, tableName, kvpB.Value.Name),
                    ColumnName = kvpB.Value.Name,
                    Status = ChangeStatus.Dropped,
                    SideA = null,
                    SideB = kvpB.Value,
                });
            }
        }

        // Modified: in both but properties differ
        foreach (var kvpA2 in dictA)
        {
            var colA = kvpA2.Value;
            if (dictB.TryGetValue(kvpA2.Key, out var colB) && IsModified(colA, colB))
            {
                changes.Add(new ColumnChange
                {
                    Id = SchemaQualifiedName.Child(tableSchema, tableName, colA.Name),
                    ColumnName = colA.Name,
                    Status = ChangeStatus.Modified,
                    SideA = colA,
                    SideB = colB,
                });
            }
        }

        return changes;
    }

    private static Dictionary<string, ColumnModel> BuildDictionary(IReadOnlyList<ColumnModel> columns)
    {
        var dict = new Dictionary<string, ColumnModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
            dict[col.Name] = col;
        return dict;
    }

    private static bool IsModified(ColumnModel a, ColumnModel b)
    {
        if (!string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase)) return true;
        // sys.columns reports byte/digit widths for every numeric/date type but
        // T-SQL only lets you write the corresponding parameter on a subset of
        // them — varchar(N), decimal(p,s), datetime2(s), etc. For the rest the
        // folder-side parser leaves MaxLength / Precision / Scale at 0, so an
        // unguarded comparison flags every int / bigint / datetime / bit / ...
        // column as modified on every run.
        if (IsVariableWidthType(a.DataType) && a.MaxLength != b.MaxLength) return true;
        if (IsPrecisionMeaningful(a.DataType) && a.Precision != b.Precision) return true;
        if (IsScaleMeaningful(a.DataType) && a.Scale != b.Scale) return true;
        if (a.IsNullable != b.IsNullable) return true;
        if (a.IsIdentity != b.IsIdentity) return true;
        if (a.IsComputed != b.IsComputed) return true;
        if (!string.Equals(a.ComputedText, b.ComputedText, StringComparison.Ordinal)) return true;
        if (a.IsPersisted != b.IsPersisted) return true;
        if (!string.Equals(a.Collation, b.Collation, StringComparison.OrdinalIgnoreCase)) return true;
        if (!DefaultConstraintsEqual(a.DefaultConstraint, b.DefaultConstraint)) return true;
        return false;
    }

    private static bool IsVariableWidthType(string dataType) =>
        dataType.Equals("varchar", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("char", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("nchar", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("binary", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("varbinary", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrecisionMeaningful(string dataType) =>
        dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("numeric", StringComparison.OrdinalIgnoreCase);

    private static bool IsScaleMeaningful(string dataType) =>
        dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("numeric", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("datetime2", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("time", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase);

    private static bool DefaultConstraintsEqual(DefaultConstraintModel? a, DefaultConstraintModel? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        var canonA = SQLParity.Core.Parsing.ExpressionCanonicalizer.Canonicalize(a.Definition);
        var canonB = SQLParity.Core.Parsing.ExpressionCanonicalizer.Canonicalize(b.Definition);
        return string.Equals(canonA, canonB, StringComparison.OrdinalIgnoreCase);
    }
}
