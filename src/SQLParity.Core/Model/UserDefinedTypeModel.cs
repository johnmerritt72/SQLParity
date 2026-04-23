using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a user-defined data type (alias type, e.g., CREATE TYPE PhoneNumber FROM nvarchar(20)).
/// </summary>
public sealed class UserDefinedDataTypeModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string BaseType { get; init; }
    public required int MaxLength { get; init; }
    public required bool IsNullable { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// Represents a user-defined table type (UDTT).
/// </summary>
public sealed class UserDefinedTableTypeModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ColumnModel> Columns { get; init; }
    public required string Ddl { get; init; }
}
