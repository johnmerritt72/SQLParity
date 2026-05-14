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

    /// <summary>
    /// In folder-mode comparisons, the absolute path of the .sql file that
    /// contains this object's CREATE statement on Side B. Null when Side B
    /// is a live database, or when the object is New on Side A (no file
    /// backs it yet). Set by the host VM during the per-DB merge.
    /// </summary>
    public string? SourceFilePath { get; set; }

    /// <summary>
    /// File-derived names of orphan-counterpart candidates for this orphan Change
    /// in a folder-vs-DB comparison. Populated by SchemaComparator's post-pass
    /// when a folder-side DROP's file name matches a DB-side orphan NEW's name
    /// (or vice versa). Empty when no candidates. Used by the UI to offer a
    /// "looks like a typo — pair them?" hint.
    /// </summary>
    public List<string> RenameCandidateNames { get; set; } = new();

    /// <summary>
    /// When the user has paired this Change with another via the typo-rename
    /// hint, the original Id.Name of the partner that was collapsed in.
    /// Null when this Change isn't a pair result. ScriptGenerator uses this
    /// to rewrite the CREATE name token in DdlSideB to use Id.Name (the DB's
    /// correct name) at apply time.
    /// </summary>
    public string? PairedFromName { get; set; }
}
