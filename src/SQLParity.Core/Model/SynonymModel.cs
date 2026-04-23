namespace SQLParity.Core.Model;

/// <summary>
/// Represents a synonym.
/// </summary>
public sealed class SynonymModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string BaseObject { get; init; }
    public required string Ddl { get; init; }
}
