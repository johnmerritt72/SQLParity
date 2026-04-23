using System;
using System.Collections.Generic;

namespace SQLParity.Core.Project;

/// <summary>
/// The data model for a .sqlparity project file. Contains everything
/// needed to re-open a comparison: connection identities, labels, tags,
/// filters, and ignored differences. Never contains credentials.
/// </summary>
public sealed class ProjectFile
{
    /// <summary>File format version for forward compatibility.</summary>
    public int Version { get; init; } = 1;

    /// <summary>When the project was last saved (UTC).</summary>
    public DateTime LastSavedUtc { get; init; }

    /// <summary>SideA connection identity.</summary>
    public required ConnectionSide SideA { get; init; }

    /// <summary>SideB connection identity.</summary>
    public required ConnectionSide SideB { get; init; }

    /// <summary>Filter settings for the comparison.</summary>
    public FilterSettings Filters { get; init; } = new();

    /// <summary>Differences the user has chosen to ignore.</summary>
    public IReadOnlyList<IgnoredDifference> IgnoredDifferences { get; init; } = Array.Empty<IgnoredDifference>();
}
