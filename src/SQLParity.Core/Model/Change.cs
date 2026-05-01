using System;
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// The type of database object that changed.
/// </summary>
public enum ObjectType
{
    Table,
    View,
    StoredProcedure,
    UserDefinedFunction,
    Schema,
    Sequence,
    Synonym,
    UserDefinedDataType,
    UserDefinedTableType,
    Index,
    ForeignKey,
    CheckConstraint,
    Trigger
}

/// <summary>
/// A single detected difference between two database schemas.
/// </summary>
public sealed class Change
{
    public required SchemaQualifiedName Id { get; init; }
    public required ObjectType ObjectType { get; init; }
    public required ChangeStatus Status { get; init; }

    /// <summary>DDL on SideA (null if Dropped).</summary>
    public string? DdlSideA { get; set; }

    /// <summary>DDL on SideB (null if New).</summary>
    public string? DdlSideB { get; set; }

    /// <summary>Risk tier for this change.</summary>
    public RiskTier Risk { get; set; }

    /// <summary>Reasons explaining the risk classification.</summary>
    public IReadOnlyList<RiskReason> Reasons { get; set; } = Array.Empty<RiskReason>();

    /// <summary>
    /// For modified tables: the column-level sub-changes. Empty for non-table
    /// objects and for New/Dropped tables. Mutable so the user can apply
    /// rename mappings (collapsing Drop+Add pairs into a single Renamed entry).
    /// </summary>
    public required IList<ColumnChange> ColumnChanges { get; set; }

    /// <summary>Pre-flight query SQL (null if not applicable).</summary>
    public string? PreFlightSql { get; set; }

    /// <summary>Human-readable description of what the pre-flight query measures.</summary>
    public string? PreFlightDescription { get; set; }

    /// <summary>
    /// External database or linked-server references detected in the source-side
    /// object (for views, procs, functions, triggers). Empty if none.
    /// Each entry is a human-readable descriptor like "MTD_01" or "[DbArchives] (linked server)".
    /// </summary>
    public IReadOnlyList<string> ExternalReferences { get; set; } = Array.Empty<string>();

    /// <summary>
    /// In folder-mode comparisons that span multiple databases on Side A,
    /// the database this change belongs to. Set by the host VM after
    /// running the comparator per-DB and merging the result. Null for
    /// single-DB comparisons (the existing default).
    /// </summary>
    public string? SourceDatabase { get; set; }
}
