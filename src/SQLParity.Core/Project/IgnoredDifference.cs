using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

/// <summary>
/// A specific difference that the user has chosen to ignore. Persisted
/// in the project file so it stays ignored across runs.
/// </summary>
public sealed class IgnoredDifference
{
    /// <summary>The identity of the object (e.g., "[dbo].[Orders]").</summary>
    public required string ObjectId { get; init; }

    /// <summary>The object type.</summary>
    public required ObjectType ObjectType { get; init; }

    /// <summary>Optional: user's reason for ignoring this difference.</summary>
    public string? Reason { get; init; }
}
