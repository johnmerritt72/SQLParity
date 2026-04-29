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
}
