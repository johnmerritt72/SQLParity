using System;
using System.Collections.Generic;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

/// <summary>
/// Generates ALTER TABLE statements for modified tables, based on column-level changes.
/// </summary>
public static class AlterTableGenerator
{
    /// <summary>
    /// Generates a combined ALTER TABLE script for all column changes on a modified table.
    /// Individual statements are separated by GO.
    /// </summary>
    public static string GenerateForModifiedTable(string schema, string table, IList<ColumnChange> columnChanges)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < columnChanges.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine("GO");
            }
            sb.AppendLine(GenerateColumnAlter(schema, table, columnChanges[i]));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generates ALTER TABLE statement(s) for a single column change.
    /// Multiple statements (e.g. drop constraint then drop column) are separated by newlines.
    /// </summary>
    public static string GenerateColumnAlter(string schema, string table, ColumnChange change)
    {
        var sb = new StringBuilder();
        string tableRef = $"[{schema}].[{table}]";

        switch (change.Status)
        {
            case ChangeStatus.New:
            {
                var col = change.SideA!;
                string colDef = BuildColumnDefinition(col);
                sb.Append($"ALTER TABLE {tableRef} ADD {colDef}");
                if (col.DefaultConstraint != null)
                {
                    sb.Append($" CONSTRAINT [{col.DefaultConstraint.Name}] DEFAULT {col.DefaultConstraint.Definition}");
                }
                break;
            }

            case ChangeStatus.Dropped:
            {
                var col = change.SideB!;
                // Drop the default constraint first if one exists
                if (col.DefaultConstraint != null)
                {
                    sb.AppendLine($"ALTER TABLE {tableRef} DROP CONSTRAINT [{col.DefaultConstraint.Name}]");
                    sb.AppendLine("GO");
                }
                sb.Append($"ALTER TABLE {tableRef} DROP COLUMN [{col.Name}]");
                break;
            }

            case ChangeStatus.Renamed:
            {
                // User explicitly mapped OldColumnName → ColumnName as a rename.
                // Emit sp_rename instead of DROP + ADD. Doubled single quotes
                // handle names with apostrophes.
                string oldName = (change.OldColumnName ?? string.Empty).Replace("'", "''");
                string newName = change.ColumnName.Replace("'", "''");
                string tableArg = $"{schema}.{table}.{oldName}".Replace("'", "''");
                sb.Append($"EXEC sp_rename N'{tableArg}', N'{newName}', N'COLUMN'");
                break;
            }

            case ChangeStatus.Modified:
            {
                var sideA = change.SideA!;
                var sideB = change.SideB!;
                bool typeChanged = sideA.DataType != sideB.DataType
                    || sideA.MaxLength != sideB.MaxLength
                    || sideA.Precision != sideB.Precision
                    || sideA.Scale != sideB.Scale
                    || sideA.IsNullable != sideB.IsNullable
                    || !string.Equals(sideA.Collation, sideB.Collation, StringComparison.OrdinalIgnoreCase);

                if (typeChanged)
                {
                    string colDef = BuildColumnDefinition(sideA);
                    sb.AppendLine($"ALTER TABLE {tableRef} ALTER COLUMN {colDef}");
                }

                // Handle default constraint changes
                bool hadDefault = sideB.DefaultConstraint != null;
                bool hasDefault = sideA.DefaultConstraint != null;

                if (hadDefault && !hasDefault)
                {
                    // Default removed
                    if (sb.Length > 0) sb.AppendLine("GO");
                    sb.Append($"ALTER TABLE {tableRef} DROP CONSTRAINT [{sideB.DefaultConstraint!.Name}]");
                }
                else if (!hadDefault && hasDefault)
                {
                    // Default added
                    if (sb.Length > 0) sb.AppendLine("GO");
                    sb.Append($"ALTER TABLE {tableRef} ADD CONSTRAINT [{sideA.DefaultConstraint!.Name}] DEFAULT {sideA.DefaultConstraint.Definition} FOR [{sideA.Name}]");
                }
                else if (hadDefault && hasDefault
                    && (sideA.DefaultConstraint!.Name != sideB.DefaultConstraint!.Name
                        || sideA.DefaultConstraint.Definition != sideB.DefaultConstraint.Definition))
                {
                    // Default changed: drop old then add new
                    if (sb.Length > 0) sb.AppendLine("GO");
                    sb.AppendLine($"ALTER TABLE {tableRef} DROP CONSTRAINT [{sideB.DefaultConstraint.Name}]");
                    sb.AppendLine("GO");
                    sb.Append($"ALTER TABLE {tableRef} ADD CONSTRAINT [{sideA.DefaultConstraint.Name}] DEFAULT {sideA.DefaultConstraint.Definition} FOR [{sideA.Name}]");
                }

                // If nothing was emitted (edge case), emit a comment
                if (sb.Length == 0)
                {
                    sb.Append($"-- No schema change detected for column [{change.ColumnName}]");
                }

                break;
            }

            default:
                sb.Append($"-- Unhandled column change status {change.Status} for [{change.ColumnName}]");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats a column definition as: [Name] DataType NULL/NOT NULL
    /// </summary>
    private static string BuildColumnDefinition(ColumnModel col)
    {
        string typePart = FormatDataType(col);
        // Preserve column collation so ALTER TABLE ADD/ALTER COLUMN produces a
        // column with the source's collation, not the destination database's
        // default (which would cause collation conflicts in joins/comparisons).
        string collatePart = !string.IsNullOrEmpty(col.Collation)
            ? $" COLLATE {col.Collation}"
            : string.Empty;
        string nullPart = col.IsNullable ? "NULL" : "NOT NULL";
        return $"[{col.Name}] {typePart}{collatePart} {nullPart}";
    }

    /// <summary>
    /// Formats the SQL Server data type string for a column, including length/precision/scale where applicable.
    /// </summary>
    public static string FormatDataType(ColumnModel col)
    {
        string dt = col.DataType.ToUpperInvariant();

        switch (dt)
        {
            case "NVARCHAR":
            case "VARCHAR":
            case "NCHAR":
            case "CHAR":
            case "VARBINARY":
            case "BINARY":
            {
                string len = col.MaxLength <= 0 ? "MAX" : col.MaxLength.ToString();
                return $"{col.DataType}({len})";
            }

            case "DECIMAL":
            case "NUMERIC":
                return $"{col.DataType}({col.Precision},{col.Scale})";

            default:
                return col.DataType;
        }
    }
}
