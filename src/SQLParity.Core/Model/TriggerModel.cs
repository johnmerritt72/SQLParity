namespace SQLParity.Core.Model;

/// <summary>
/// Represents a DML trigger on a table.
/// </summary>
public sealed class TriggerModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool FiresOnInsert { get; init; }
    public required bool FiresOnUpdate { get; init; }
    public required bool FiresOnDelete { get; init; }
    public required string Ddl { get; init; }
}
