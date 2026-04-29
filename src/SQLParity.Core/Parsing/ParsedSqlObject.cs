using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// One CREATE statement extracted from a .sql file batch.
/// </summary>
public sealed class ParsedSqlObject
{
    /// <summary>What kind of object this is.</summary>
    public required ObjectType ObjectType { get; init; }

    /// <summary>Schema-qualified name. Schema defaults to <c>dbo</c> when absent in source.</summary>
    public required SchemaQualifiedName Id { get; init; }

    /// <summary>
    /// The full batch text containing this CREATE statement, verbatim from the
    /// source file (including any leading header comments). Trailing <c>GO</c>
    /// separator is not included.
    /// </summary>
    public required string Ddl { get; init; }

    /// <summary>0-based index of the batch within the source file.</summary>
    public required int BatchIndex { get; init; }

    /// <summary>True if the source used <c>CREATE OR ALTER</c>.</summary>
    public required bool IsCreateOrAlter { get; init; }
}
