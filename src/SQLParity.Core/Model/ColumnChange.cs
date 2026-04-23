using System;
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// A column-level sub-change within a modified table. Tracks what specifically
/// changed about the column (type, nullability, default, etc.).
/// </summary>
public sealed class ColumnChange
{
    public required SchemaQualifiedName Id { get; init; }
    public required string ColumnName { get; init; }
    public required ChangeStatus Status { get; init; }

    /// <summary>Column definition on SideA (null if Dropped).</summary>
    public required ColumnModel? SideA { get; init; }

    /// <summary>Column definition on SideB (null if New).</summary>
    public required ColumnModel? SideB { get; init; }

    /// <summary>
    /// For <see cref="ChangeStatus.Renamed"/>: the old column name (from SideB,
    /// the side being modified). The new name is in <see cref="ColumnName"/>.
    /// </summary>
    public string? OldColumnName { get; set; }

    /// <summary>Risk tier for this column change.</summary>
    public RiskTier Risk { get; set; }

    /// <summary>Reasons explaining the risk classification.</summary>
    public IReadOnlyList<RiskReason> Reasons { get; set; } = Array.Empty<RiskReason>();

    /// <summary>Pre-flight query SQL (null if not applicable).</summary>
    public string? PreFlightSql { get; set; }

    /// <summary>Human-readable description of what the pre-flight query measures.</summary>
    public string? PreFlightDescription { get; set; }
}
