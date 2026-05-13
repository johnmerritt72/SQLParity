using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// What <see cref="FolderSchemaReader"/> returns: the assembled virtual
/// schema and the side-table that says where each object came from.
/// </summary>
public sealed class FolderReadResult
{
    public required DatabaseSchema Schema { get; init; }
    public required FolderSchemaContext Context { get; init; }
}
