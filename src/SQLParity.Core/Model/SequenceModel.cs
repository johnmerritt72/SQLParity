namespace SQLParity.Core.Model;

/// <summary>
/// Represents a sequence object.
/// </summary>
public sealed class SequenceModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required string Ddl { get; init; }
}
