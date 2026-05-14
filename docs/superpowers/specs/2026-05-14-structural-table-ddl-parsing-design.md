# Structural Table DDL Parsing for Folder Mode

**Date:** 2026-05-14
**Status:** Draft — pending implementation plan

## Problem

In folder-mode comparisons, every table reports as fully-different in the side-by-side DDL panel even when the only structural change is, say, a single added column. `FolderSchemaReader.AddToBucket` stubs `TableModel.Columns = Array.Empty<ColumnModel>()` for tables, so:

1. The "is this modified?" decision in `SchemaComparator.CompareTables` falls back to comparing two raw DDL *strings*. SMO scripts the DB side as `[ID] [int] IDENTITY(1,1) NOT NULL` with separate `CONSTRAINT [PK_…] PRIMARY KEY CLUSTERED`, while source `.sql` files typically write `ID INT IDENTITY(1,1) PRIMARY KEY` inline. The strings always differ.
2. `ColumnComparator` sees N DB columns vs zero folder columns and marks every column as "New", drowning real changes in false positives.
3. `SimpleDiffHighlighter`'s line-by-line LCS can't find matching lines (different bracketing, casing, inline-vs-separate constraints, auto-generated `DF__Foo_AABBCCDD` names, double-paren `((1))` vs `1`), so the entire pane lights up.

`FolderSchemaReader`'s own comment acknowledges this: *"folder mode does DDL-text-only diff for v1.2; structural decomposition of CREATE TABLE is deferred."* This spec lifts that limitation.

## Goals

- Folder-side `TableModel` is populated with the same shape as the live-DB side: `Columns`, `Indexes`, `ForeignKeys`, `CheckConstraints` (triggers stay out of scope — they live in separate `CREATE TRIGGER` files).
- "Modified" detection and the side-by-side panel both run on canonical, regenerated DDL emitted by a single `CreateTableGenerator`, so only true structural differences light up.
- Files that ScriptDom cannot parse degrade gracefully to today's text-only diff and surface a non-blocking warning on the results panel.

## Non-Goals

- Sequences, synonyms, UDDTs, UDTTs: out of scope for this round. They remain text-only diff. Will be a follow-up if their diff is ever a problem.
- Triggers: out of scope here. `SqlFileParser` already recognizes top-level `CREATE TRIGGER` batches as their own `ParsedSqlObject`s with `ObjectType.Trigger`; triggers embedded inside a table batch are uncommon in practice and not handled.
- Folder structures that put indexes / FKs in their own files outside the table file (e.g. `Indexes/IX_Foo.sql`). This spec handles indexes/constraints *embedded inside the same table batch* (which is the convention in EM CenturionSync). Separate-file indexes can be a follow-up.

## Architecture

### New: `SQLParity.Core/Parsing/TableDdlParser.cs`

Public surface:

```csharp
public sealed class TableDdlParser
{
    public TableModel? Parse(
        string tableBatchDdl,
        string schema,
        string name,
        string? sourceDatabase,
        out IReadOnlyList<string> warnings);
}
```

Returns `null` on parse failure with a single warning explaining the cause. Otherwise returns a fully-populated `TableModel`.

Internally uses `Microsoft.SqlServer.TransactSql.ScriptDom`:

- `new TSql160Parser(initialQuotedIdentifiers: true).Parse(reader, out errors)` — if `errors` is non-empty, return null with `"Could not parse table file: <first error> at line N. Falling back to text-only diff."`.
- Walk `TSqlScript.Batches[*].Statements[*]` depth-first, recursing into `IfStatement.ThenStatement` and `BeginEndBlockStatement.StatementList` so an `IF OBJECT_ID(...) IS NULL BEGIN CREATE TABLE ... END` wrapper is transparent.
- Find the first `CreateTableStatement`. If none, return null with `"No CREATE TABLE statement found in batch."`.
- Map the `CreateTableStatement` to a `TableModel` (mapping table below).
- Continue the walk for sibling `CreateIndexStatement` and `AlterTableAddTableElementStatement` nodes that target the same `(schema, name)`; append their results to `Indexes` / `ForeignKeys` / `CheckConstraints`.

### Edited: `SQLParity.Core/Parsing/FolderSchemaReader.cs`

In `AddToBucket` for `ObjectType.Table`, call `TableDdlParser.Parse`. If non-null, use the populated `TableModel`. If null, keep today's stub-with-Ddl-text fallback and propagate the warning into `FolderReadResult.Warnings` (existing channel — same one used by the "USE overruled" warning).

### Edited: `SQLParity.Core/Comparison/CreateTableGenerator.cs`

Detect SQL Server auto-generated default-constraint names via regex `^DF__[A-Za-z0-9_]+__[A-Za-z0-9_]+_[A-F0-9]{8}$` (case-insensitive). When the column's `DefaultConstraint.Name` matches, emit `DEFAULT <def>` only (no `CONSTRAINT [name]` clause). User-named constraints like `DF_MyTable_IsActive` keep their explicit name. `ColumnComparator.DefaultConstraintsEqual` already compares only `Definition`, so this purely affects canonical text — no behavior change in the modified-detection logic.

### Edited: `SQLParity.Core/Comparison/SchemaComparator.cs`

In `CompareTables` (the block around line 343–360), when both `tableA.Columns.Count > 0` and `tableB.Columns.Count > 0` regenerate canonical DDL via `CreateTableGenerator.Generate(...)` for both `DdlSideA` / `DdlSideB` on the emitted `Change`, and for the `ddlDiffers` text comparison. When either side has zero columns (parse failure on that side) fall back to raw `tableA.Ddl` / `tableB.Ddl` — preserves today's behavior for unparseable files.

### Edited: `SQLParity.Core/SQLParity.Core.csproj`

Add `<PackageReference Include="Microsoft.SqlServer.TransactSql.ScriptDom" Version="180.18.1" />`. Verified compatible with the existing `net48;net8.0` multi-target.

### Vsix layer

No changes. The diff panel binds to `DdlSideA` / `DdlSideB` on `Change`; once the comparator emits canonical text there, the existing `SimpleDiffHighlighter` renders it correctly. The `Open in WinMerge` button continues to launch raw-file comparison if the user wants the underlying source.

## ScriptDom → TableModel mapping

### Table-level

| ScriptDom node | TableModel field |
|---|---|
| `CreateTableStatement.SchemaObjectName` | `Schema`, `Name`, `Id` |
| Each `ColumnDefinition` | `Columns[i]` (see column mapping) |
| `UniqueConstraintDefinition` with `IsPrimaryKey = true` (inline-on-column or table-level) | `Indexes[i]` with `IsPrimaryKey = true`, `IsClustered` from `Clustered` |
| `UniqueConstraintDefinition` with `IsPrimaryKey = false` | `Indexes[i]` with `IsUniqueConstraint = true` |
| `CheckConstraintDefinition` (table-level or column-level) | `CheckConstraints[i]` |
| `ForeignKeyConstraintDefinition` (table-level or column-level inline `REFERENCES`) | `ForeignKeys[i]` |
| Sibling `CreateIndexStatement` with matching `OnName` | appended to `Indexes` |
| Sibling `AlterTableAddTableElementStatement` w/ `CheckConstraintDefinition` | appended to `CheckConstraints` |
| Sibling `AlterTableAddTableElementStatement` w/ `ForeignKeyConstraintDefinition` | appended to `ForeignKeys` |

### Column-level (`ColumnDefinition` → `ColumnModel`)

- `Name` ← `ColumnIdentifier.Value`
- `DataType` ← `DataTypeReference.Name.BaseIdentifier.Value` (lowercased to match `SchemaReader` output)
- `MaxLength` / `Precision` / `Scale` ← from `ParameterizedDataTypeReference.Parameters`; `varchar(max)` / `nvarchar(max)` / `varbinary(max)` map to `MaxLength = -1`
- `IsNullable` ← scan `Constraints` for `NullableConstraintDefinition`; default `true` when no nullability constraint is present (T-SQL default for non-`PRIMARY KEY` columns)
- `IsIdentity` / `IdentitySeed` / `IdentityIncrement` ← from `IdentityOptions`
- `IsComputed` / `ComputedText` / `IsPersisted` ← from `ComputedColumnDefinition` (round-trip `ComputedColumnExpression` via `Sql160ScriptGenerator`)
- `Collation` ← `Collation.Value`
- `DefaultConstraint` ← child `DefaultConstraintDefinition`: `{ Name = ConstraintIdentifier?.Value, Definition = round-trip-script of Expression via Sql160ScriptGenerator }`
- Inline `UniqueConstraintDefinition` on a column → also add a single-column entry to `Indexes` (so `ID INT PRIMARY KEY` and `CONSTRAINT [PK_x] PRIMARY KEY ([ID])` map identically)

### Round-trip canonical text

CHECK predicates, computed expressions, and DEFAULT expressions are round-tripped through `Sql160ScriptGenerator` with default `SqlScriptGeneratorOptions`. This ensures:

- `DEFAULT 1` and `DEFAULT ((1))` produce the same canonical text
- `CHECK (Age > 0)` and `CHECK ([Age] > (0))` produce the same canonical text
- Whitespace and casing inside expressions are normalized

`ColumnComparator.DefaultConstraintsEqual` and `CheckConstraintModel.Definition` comparisons already use ordinal string equality; canonicalizing both sides through ScriptDom makes that work.

## Failure handling

| Scenario | Behavior |
|---|---|
| ScriptDom returns parse errors | `Parse` returns `null` with warning `"Could not parse table file: <first error> at line N. Falling back to text-only diff."`; `FolderSchemaReader` keeps the existing stub-with-Ddl-text fallback so the table still appears in results, comparing raw text only |
| No `CreateTableStatement` found in batch | Same fallback; warning `"No CREATE TABLE statement found in batch."` |
| Sibling `ALTER TABLE` / `CREATE INDEX` references a different table | Silently skipped (not bound to this table) |
| `CREATE INDEX` references the same table but parsing the index fails | Warning emitted, index omitted from `Indexes`, otherwise table proceeds normally |
| Multiple `CREATE TABLE` statements in one batch | Only the first is mapped; warning `"Multiple CREATE TABLE statements found in one batch; using the first."` |

Warnings flow into `FolderReadResult.Warnings` and surface in the existing results-panel warning channel.

## Testing

### `TableDdlParserTests` (new)

Round-trip cases:

- Inline PK column (`ID INT PRIMARY KEY`)
- Table-level PK constraint (`CONSTRAINT [PK_x] PRIMARY KEY CLUSTERED ([ID] ASC)`)
- Inline DEFAULT (`IsActive BIT NOT NULL DEFAULT 1`)
- Named DEFAULT constraint (`CONSTRAINT [DF_x] DEFAULT 1`)
- Computed column (`FullName AS [First] + ' ' + [Last] PERSISTED`)
- `COLLATE` clause
- `varchar(max)` mapped to `MaxLength = -1`
- `decimal(18, 2)` mapped to `Precision = 18`, `Scale = 2`
- Sibling `CREATE NONCLUSTERED INDEX ... INCLUDE (...)`
- Sibling `ALTER TABLE ... ADD CONSTRAINT ... CHECK (...)`
- Sibling `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY (...) REFERENCES ...`
- `IF OBJECT_ID(...) IS NULL BEGIN CREATE TABLE ... END` wrapper transparent
- Parse-error input → `null` + warning
- Missing CREATE TABLE in batch → `null` + warning

### `CreateTableGeneratorTests` (extend)

- Auto-gen default-constraint name (`DF__Alcohol_Curve_51A50FA1`) is stripped from canonical output
- User-named default constraint (`DF_MyTable_IsActive`) is preserved in canonical output

### `SchemaComparatorTests` (extend)

- Feed in the user's actual AlcoholSimulations.sql parsed via `TableDdlParser` against a hand-built DB-side `TableModel` that adds the `SimulateTampers` column.
- Assert exactly one `ColumnChange` (status `New`, name `SimulateTampers`).
- Assert canonical `DdlSideA` and `DdlSideB` are byte-identical except for the `SimulateTampers` line.

### `FolderSchemaReaderTests` (extend)

- Syntactically-broken table .sql → table appears in result with raw `Ddl`, structural collections empty, warning surfaced
- Valid table .sql → `Columns` / `Indexes` / etc. populated; warning list empty

## Out of scope / future work

- Apply the same approach to sequences, synonyms, UDDTs, UDTTs.
- Support indexes / FKs / checks declared in separate files outside the table file (different folder convention).
- Use ScriptDom for routine bodies — out of scope; the existing whitespace/comment normalizer handles those.

## Rollout

Single PR. No data migration. Folder-mode comparisons that worked before continue to work (text-only fallback path is identical to today's behavior). Folder-mode comparisons that previously over-reported "Modified" with whole-DDL-different panels start showing precise structural diffs.
