using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a column within an index (key or included).
/// </summary>
public sealed class IndexedColumnModel
{
    public required string Name { get; init; }
    public required bool IsDescending { get; init; }
    public required bool IsIncluded { get; init; }
}

/// <summary>
/// Represents an index on a table.
/// </summary>
public sealed class IndexModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string IndexType { get; init; }
    public required bool IsClustered { get; init; }
    public required bool IsUnique { get; init; }
    public required bool IsPrimaryKey { get; init; }
    public required bool IsUniqueConstraint { get; init; }
    public required bool HasFilter { get; init; }
    public required string? FilterDefinition { get; init; }
    public required IReadOnlyList<IndexedColumnModel> Columns { get; init; }
    public required string Ddl { get; init; }
}
