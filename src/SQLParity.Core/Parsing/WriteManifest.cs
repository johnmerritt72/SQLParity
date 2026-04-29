using System.Collections.Generic;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Result of a folder-sync write pass. Lists let the host VM tell the user
/// what happened and feed Solution Explorer's add-file API.
/// </summary>
public sealed class WriteManifest
{
    /// <summary>Existing files whose contents were replaced.</summary>
    public required IReadOnlyList<string> FilesUpdated { get; init; }

    /// <summary>New files written. Solution Explorer needs to register these.</summary>
    public required IReadOnlyList<string> FilesCreated { get; init; }

    /// <summary>Files that previously held multiple objects, now commented out.</summary>
    public required IReadOnlyList<string> FilesCommentedOut { get; init; }

    /// <summary>Changes that couldn't be processed (e.g. no source-file mapping).</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>Errors encountered during writing (file-system permission, etc.).</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}
