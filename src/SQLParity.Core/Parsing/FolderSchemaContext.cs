using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Side-channel state produced by <see cref="FolderSchemaReader"/> alongside
/// a <c>DatabaseSchema</c>. Tracks where each parsed object came from on
/// disk so the folder-sync writer can update the right file (or split a
/// multi-object file) on A→B sync.
/// </summary>
public sealed class FolderSchemaContext
{
    /// <summary>Maps each parsed object to its source file and single/multi flag.</summary>
    public required IReadOnlyDictionary<SchemaQualifiedName, FileBacking> ObjectToFile { get; init; }

    /// <summary>
    /// Human-readable warnings — duplicate-name collisions, unrecognized
    /// batches, file-read failures. Surfaced by the host so the user can
    /// resolve before relying on the comparison.
    /// </summary>
    public required IReadOnlyList<string> ParseWarnings { get; init; }

    /// <summary>The absolute folder that was read.</summary>
    public required string FolderPath { get; init; }
}
