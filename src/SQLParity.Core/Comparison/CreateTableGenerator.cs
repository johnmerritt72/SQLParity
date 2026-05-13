using System;
using System.Linq;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Synthesizes CREATE TABLE DDL from catalog-derived TableModel metadata.
/// No DB I/O, no SMO. Matches the scope of CreateTableScriptingOptions
/// (table-level inline constraints only — indexes, FKs, triggers compare as separate child nodes).
/// </summary>
public static class CreateTableGenerator
{
    /// <summary>
    /// Generates CREATE TABLE DDL for the given table model.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the table has zero columns.</exception>
    public static string Generate(TableModel table)
    {
        if (table.Columns.Count == 0)
            throw new InvalidOperationException(
                $"Cannot generate CREATE TABLE for [{table.Schema}].[{table.Name}] — no columns.");

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE [").Append(table.Schema).Append("].[").Append(table.Name).Append("](");
        sb.AppendLine();

        var orderedColumns = table.Columns.OrderBy(c => c.OrdinalPosition).ToList();
        for (int i = 0; i < orderedColumns.Count; i++)
        {
            sb.Append('\t').Append(FormatColumn(orderedColumns[i]));
            bool isLastColumn = i == orderedColumns.Count - 1;
            if (!isLastColumn)
                sb.Append(',');
            sb.AppendLine();
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string FormatColumn(ColumnModel col)
    {
        // Simplest case: [name] [type] NULL/NOT NULL
        return $"[{col.Name}] [{col.DataType}] {(col.IsNullable ? "NULL" : "NOT NULL")}";
    }
}
