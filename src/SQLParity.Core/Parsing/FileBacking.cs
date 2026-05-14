namespace SQLParity.Core.Parsing;

/// <summary>
/// Records which on-disk .sql file holds a parsed object, and whether that
/// file holds only that object or shares it with others. Used by the
/// folder-sync writer to decide between in-place overwrite and split-and-
/// comment-out.
/// </summary>
public sealed class FileBacking
{
    public required string FilePath { get; init; }
    public required bool IsSingleObjectFile { get; init; }

    /// <summary>
    /// Object name derived from the file basename, e.g. "EndOfCallEvent_Insert"
    /// from "PROC_Protech.centurion.EndOfCallEvent_Insert.sql". Null when the
    /// file contained multiple CREATE batches — the file name can't represent
    /// any single object in that case. Used by the comparator to detect
    /// filename-vs-CREATE-name typo mismatches.
    /// </summary>
    public string? FileName { get; init; }
}
