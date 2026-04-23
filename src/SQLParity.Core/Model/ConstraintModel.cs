using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a column mapping in a foreign key.
/// </summary>
public sealed class ForeignKeyColumnModel
{
    public required string LocalColumn { get; init; }
    public required string ReferencedColumn { get; init; }
}

/// <summary>
/// Represents a foreign key constraint.
/// </summary>
public sealed class ForeignKeyModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string ReferencedTableSchema { get; init; }
    public required string ReferencedTableName { get; init; }
    public required string DeleteAction { get; init; }
    public required string UpdateAction { get; init; }
    public required bool IsEnabled { get; init; }
    public required IReadOnlyList<ForeignKeyColumnModel> Columns { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// Represents a CHECK constraint.
/// </summary>
public sealed class CheckConstraintModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string Definition { get; init; }
    public required bool IsEnabled { get; init; }
    public required string Ddl { get; init; }
}
