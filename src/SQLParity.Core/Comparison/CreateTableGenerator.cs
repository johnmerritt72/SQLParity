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
        var sb = new StringBuilder();
        sb.Append('[').Append(col.Name).Append("] ");
        sb.Append(FormatDataType(col));

        if (col.IsIdentity)
            sb.Append(" IDENTITY(").Append(col.IdentitySeed).Append(',').Append(col.IdentityIncrement).Append(')');

        if (!string.IsNullOrEmpty(col.Collation) && IsCharacterType(col.DataType))
            sb.Append(" COLLATE ").Append(col.Collation);

        sb.Append(col.IsNullable ? " NULL" : " NOT NULL");

        if (col.DefaultConstraint != null)
            sb.Append(" CONSTRAINT [").Append(col.DefaultConstraint.Name).Append("] DEFAULT ").Append(col.DefaultConstraint.Definition);

        return sb.ToString();
    }

    private static string FormatDataType(ColumnModel col)
    {
        string dt = col.DataType.ToLowerInvariant();
        return dt switch
        {
            "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary"
                => $"[{dt}]({(col.MaxLength == -1 ? "max" : col.MaxLength.ToString())})",
            "decimal" or "numeric"
                => $"[{dt}]({col.Precision}, {col.Scale})",
            "datetime2" or "time" or "datetimeoffset"
                => $"[{dt}]({col.Scale})",
            _ => $"[{dt}]",
        };
    }

    private static bool IsCharacterType(string dataType) =>
        dataType.Equals("varchar", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("char", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("nchar", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("text", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("ntext", StringComparison.OrdinalIgnoreCase);
}
