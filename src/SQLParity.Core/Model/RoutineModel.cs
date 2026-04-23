namespace SQLParity.Core.Model;

/// <summary>
/// Represents a view.
/// </summary>
public sealed class ViewModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required bool IsSchemaBound { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// Represents a stored procedure.
/// </summary>
public sealed class StoredProcedureModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// The kind of user-defined function.
/// </summary>
public enum FunctionKind
{
    Scalar,
    InlineTableValued,
    MultiStatementTableValued
}

/// <summary>
/// Represents a user-defined function (scalar, inline TVF, or multi-statement TVF).
/// </summary>
public sealed class UserDefinedFunctionModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required FunctionKind Kind { get; init; }
    public required string Ddl { get; init; }
}
