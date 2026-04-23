using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a column's default constraint.
/// </summary>
public sealed class DefaultConstraintModel
{
    public required string Name { get; init; }
    public required string Definition { get; init; }
}

/// <summary>
/// Represents a single column within a table or table type.
/// </summary>
public sealed class ColumnModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required int MaxLength { get; init; }
    public required int Precision { get; init; }
    public required int Scale { get; init; }
    public required bool IsNullable { get; init; }
    public required bool IsIdentity { get; init; }
    public required long IdentitySeed { get; init; }
    public required long IdentityIncrement { get; init; }
    public required bool IsComputed { get; init; }
    public required string? ComputedText { get; init; }
    public required bool IsPersisted { get; init; }
    public required string? Collation { get; init; }
    public required DefaultConstraintModel? DefaultConstraint { get; init; }
    public required int OrdinalPosition { get; init; }
}

/// <summary>
/// Represents a user table in the database.
/// </summary>
public sealed class TableModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Ddl { get; init; }
    public required IReadOnlyList<ColumnModel> Columns { get; init; }
    public required IReadOnlyList<IndexModel> Indexes { get; init; }
    public required IReadOnlyList<ForeignKeyModel> ForeignKeys { get; init; }
    public required IReadOnlyList<CheckConstraintModel> CheckConstraints { get; init; }
    public required IReadOnlyList<TriggerModel> Triggers { get; init; }
}
