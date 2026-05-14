using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Parses a single CREATE TABLE batch (with optional IF OBJECT_ID idempotency
/// guard and sibling CREATE INDEX / ALTER TABLE ADD CONSTRAINT statements)
/// into a fully-populated <see cref="TableModel"/>. Returns null on parse
/// failure, with a warning describing the cause.
/// </summary>
public sealed class TableDdlParser
{
    public TableModel? Parse(
        string tableBatchDdl,
        string schema,
        string name,
        string? sourceDatabase,
        out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        warnings = warningList;

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(tableBatchDdl ?? string.Empty);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors != null && errors.Count > 0)
        {
            var first = errors[0];
            warningList.Add(
                $"Could not parse table DDL: {first.Message} at line {first.Line}. " +
                "Falling back to text-only diff.");
            return null;
        }

        var createTable = FindFirstCreateTable(fragment);
        if (createTable == null)
        {
            warningList.Add("No CREATE TABLE statement found in batch. Falling back to text-only diff.");
            return null;
        }

        // Structural mapping arrives in a later task.
        return null;
    }

    /// <summary>
    /// Depth-first walk that recurses into IfStatement.ThenStatement and
    /// BeginEndBlockStatement.StatementList so a CREATE TABLE inside an
    /// IF OBJECT_ID(...) IS NULL BEGIN ... END idempotency wrapper is found.
    /// </summary>
    private static CreateTableStatement? FindFirstCreateTable(TSqlFragment root)
    {
        var visitor = new CreateTableFinder();
        root.Accept(visitor);
        return visitor.Found;
    }

    private sealed class CreateTableFinder : TSqlFragmentVisitor
    {
        public CreateTableStatement? Found { get; private set; }

        public override void Visit(CreateTableStatement node)
        {
            if (Found == null) Found = node;
        }
    }
}
