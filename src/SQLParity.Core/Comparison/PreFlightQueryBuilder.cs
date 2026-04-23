using System;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Builds read-only SQL queries to quantify the impact of Risky and Destructive
/// changes. Does NOT execute them.
/// </summary>
public static class PreFlightQueryBuilder
{
    /// <summary>
    /// A pre-flight query paired with a human-readable description of what it measures.
    /// </summary>
    public readonly struct PreFlightQuery
    {
        public string Sql { get; }
        public string Description { get; }

        public PreFlightQuery(string sql, string description)
        {
            Sql = sql ?? throw new ArgumentNullException(nameof(sql));
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }
    }

    /// <summary>
    /// Builds a pre-flight query for a top-level change, or returns null if none is needed.
    /// </summary>
    public static PreFlightQuery? Build(Change change)
    {
        if (change is null) throw new ArgumentNullException(nameof(change));

        if (change.Risk == RiskTier.Safe || change.Risk == RiskTier.Caution)
            return null;

        if (change.Status == ChangeStatus.Dropped)
        {
            switch (change.ObjectType)
            {
                case ObjectType.Table:
                    return new PreFlightQuery(
                        $"SELECT COUNT(*) FROM [{change.Id.Schema}].[{change.Id.Name}]",
                        "Count rows that will be lost");

                case ObjectType.ForeignKey:
                    // Id.Parent is the table name, Id.Name is the constraint name
                    return new PreFlightQuery(
                        $"-- Dropping foreign key [{change.Id.Name}] on [{change.Id.Schema}].[{change.Id.Parent}] removes referential integrity enforcement.",
                        $"Removing foreign key [{change.Id.Name}] removes referential integrity enforcement between [{change.Id.Schema}].[{change.Id.Parent}] and its referenced table.");

                case ObjectType.CheckConstraint:
                    return new PreFlightQuery(
                        $"-- Dropping check constraint [{change.Id.Name}] on [{change.Id.Schema}].[{change.Id.Parent}] removes validation enforcement.",
                        $"Removing check constraint [{change.Id.Name}] removes validation enforcement on [{change.Id.Schema}].[{change.Id.Parent}].");

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a pre-flight query for a column-level change, or returns null if none is needed.
    /// </summary>
    public static PreFlightQuery? BuildForColumn(string tableSchema, string tableName, ColumnChange colChange)
    {
        if (tableSchema is null) throw new ArgumentNullException(nameof(tableSchema));
        if (tableName is null) throw new ArgumentNullException(nameof(tableName));
        if (colChange is null) throw new ArgumentNullException(nameof(colChange));

        if (colChange.Risk == RiskTier.Safe || colChange.Risk == RiskTier.Caution)
            return null;

        string colName = colChange.ColumnName;

        if (colChange.Status == ChangeStatus.Dropped)
        {
            return new PreFlightQuery(
                $"SELECT COUNT(*) FROM [{tableSchema}].[{tableName}] WHERE [{colName}] IS NOT NULL",
                "Count rows with non-null values");
        }

        if (colChange.Status == ChangeStatus.Modified)
        {
            var sideA = colChange.SideA;
            var sideB = colChange.SideB;

            if (sideA is null || sideB is null)
                return null;

            bool sameType = string.Equals(sideA.DataType, sideB.DataType, StringComparison.OrdinalIgnoreCase);

            if (sameType)
            {
                // Narrowed: SideA.MaxLength < SideB.MaxLength && SideA.MaxLength > 0
                // Wait — "narrowed" means new (SideA/target) is shorter than old (SideB/source).
                // Per spec: SideA.MaxLength < SideB.MaxLength, SideA.MaxLength > 0
                if (sideA.MaxLength > 0 && sideA.MaxLength < sideB.MaxLength)
                {
                    int newMaxLen = sideA.MaxLength;
                    return new PreFlightQuery(
                        $"SELECT COUNT(*) FROM [{tableSchema}].[{tableName}] WHERE LEN([{colName}]) > {newMaxLen}",
                        $"Count rows that exceed new max length ({newMaxLen})");
                }
            }
            else
            {
                // Different type
                return new PreFlightQuery(
                    $"SELECT COUNT(*) FROM [{tableSchema}].[{tableName}] WHERE [{colName}] IS NOT NULL",
                    "Count rows that will be converted");
            }
        }

        return null;
    }
}
