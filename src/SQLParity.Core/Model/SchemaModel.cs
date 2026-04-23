namespace SQLParity.Core.Model;

/// <summary>
/// Represents a schema (the CREATE SCHEMA container).
/// </summary>
public sealed class SchemaModel
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public required string Ddl { get; init; }
}
