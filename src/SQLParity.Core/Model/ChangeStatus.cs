namespace SQLParity.Core.Model;

/// <summary>
/// The kind of difference detected for an object.
/// </summary>
public enum ChangeStatus
{
    /// <summary>Object exists only on SideA (will be created on SideB if syncing A→B).</summary>
    New,

    /// <summary>Object exists on both sides but differs.</summary>
    Modified,

    /// <summary>Object exists only on SideB (will be dropped from SideB if syncing A→B).</summary>
    Dropped,

    /// <summary>
    /// Column rename mapping explicitly set by the user. The "add" and "drop"
    /// pair is collapsed into a single rename operation (sp_rename).
    /// Only used for columns on modified tables.
    /// </summary>
    Renamed
}
