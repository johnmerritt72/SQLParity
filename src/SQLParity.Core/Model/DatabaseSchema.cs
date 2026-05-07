using System;
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Immutable snapshot of an entire database's schema. This is the top-level
/// type that the SchemaReader produces and the Comparator consumes.
/// </summary>
public sealed class DatabaseSchema
{
    public required string ServerName { get; init; }
    public required string DatabaseName { get; init; }
    public required DateTime ReadAtUtc { get; init; }

    public required IReadOnlyList<SchemaModel> Schemas { get; init; }
    public required IReadOnlyList<TableModel> Tables { get; init; }
    public required IReadOnlyList<ViewModel> Views { get; init; }
    public required IReadOnlyList<StoredProcedureModel> StoredProcedures { get; init; }
    public required IReadOnlyList<UserDefinedFunctionModel> Functions { get; init; }
    public required IReadOnlyList<SequenceModel> Sequences { get; init; }
    public required IReadOnlyList<SynonymModel> Synonyms { get; init; }
    public required IReadOnlyList<UserDefinedDataTypeModel> UserDefinedDataTypes { get; init; }
    public required IReadOnlyList<UserDefinedTableTypeModel> UserDefinedTableTypes { get; init; }

    /// <summary>
    /// Map of (schema, object name) → list of external database / linked-server
    /// references for views, procs, functions, triggers. Objects with no external
    /// refs are absent. Sourced from sys.sql_expression_dependencies.
    /// </summary>
    public IReadOnlyDictionary<(string Schema, string Name), IReadOnlyList<string>> ExternalReferences { get; init; }
        = new Dictionary<(string, string), IReadOnlyList<string>>();

    /// <summary>
    /// True when this schema contains at least one user object (table, view,
    /// proc, function, sequence, synonym, or user-defined type). Excludes the
    /// Schemas list because the dbo schema is always present on a live DB
    /// even when the database is otherwise empty — so a non-empty Schemas
    /// list is not evidence of any user-authored content.
    ///
    /// Used by the comparison-results UI to disable the B→A direction
    /// button when Side B is effectively empty (folder with no .sql files,
    /// or live DB with no user objects), to prevent accidental drop-everything
    /// applies on Side A.
    /// </summary>
    public bool HasObjects =>
        Tables.Count > 0
        || Views.Count > 0
        || StoredProcedures.Count > 0
        || Functions.Count > 0
        || Sequences.Count > 0
        || Synonyms.Count > 0
        || UserDefinedDataTypes.Count > 0
        || UserDefinedTableTypes.Count > 0;
}
