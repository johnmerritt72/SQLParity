# Catalog-driven table DDL generation + script-error UI banner

**Date:** 2026-05-13
**Branch:** feature/folder-mode
**Status:** Approved (sections 1-4)

## Problem

When a user (with `db_datareader` but lacking `VIEW DATABASE STATE`) opens a DB-to-DB comparison and clicks a `Modified` table, the comparison pane shows the Side A DDL but Side B reports:

> Could not script table [dbo].[MTDParameters]: An exception occurred while executing a Transact-SQL statement or batch. Root cause: VIEW DATABASE STATE permission denied in database 'Protech'.

Two issues stem from this:

1. **The error message is buried mid-diff.** The error string is written into `Change.DdlSideB` and fed through the line-by-line diff aligner ([ResultsView.xaml.cs:200-206](../../../src/SQLParity.Vsix/Views/ResultsView.xaml.cs#L200-L206)), so a one-line error gets stretched out alongside hundreds of Side A lines.

2. **`VIEW DATABASE STATE` is required at all** for what should be a read-only schema-comparison view. The tree determined `Modified` status from catalog-derived column metadata ([BulkTableMetadataReader](../../../src/SQLParity.Core/BulkTableMetadataReader.cs)) which only needs `VIEW DEFINITION`. The DDL view goes through SMO's `Table.Script()`, which internally hits DMVs requiring `VIEW DATABASE STATE` — even though we already have everything we need in memory to render the table.

## Goals

- Eliminate the `VIEW DATABASE STATE` requirement for table DDL viewing and Sync-path CREATE TABLE generation.
- Surface DDL load errors prominently above the comparison pane, not inside the diff.
- Preserve the current scope of what the table-DDL view shows (table + inline column constraints + PK + table-level CHECKs; indexes/FKs/triggers continue to live as separate tree nodes).
- Keep generic `-- Could not script ...` handling for object types still going through SMO (views, procs, functions, sequences, synonyms, UDTs).

## Non-goals

- Indexes, FKs, triggers, or extended properties as part of CREATE TABLE output.
- A settings toggle to fall back to SMO scripting.
- Any change to `AlterTableGenerator` or the apply-time ALTER path.
- Migrating views/procs/functions off SMO.

## Architecture

### New unit: `CreateTableGenerator`

`SQLParity.Core.Comparison.CreateTableGenerator` — sibling to [AlterTableGenerator](../../../src/SQLParity.Core/Sync/AlterTableGenerator.cs).

```
Input:  TableModel (Schema, Name, Columns, Indexes (for PK only), CheckConstraints)
Output: string — CREATE TABLE [schema].[name] (...) DDL
Pure function. No I/O, no SMO, no DB connection.
```

### Wiring (eager population)

[BulkTableMetadataReader.cs:71-82](../../../src/SQLParity.Core/BulkTableMetadataReader.cs#L71-L82) constructs each `TableModel` with `Ddl = string.Empty`. Change to call `CreateTableGenerator.Generate(...)` so the DDL is populated at read time.

After this change:
- `change.DdlSideA` / `change.DdlSideB` are populated immediately for tables.
- No lazy DDL load for tables.
- No SMO call for tables.
- No `VIEW DATABASE STATE` requirement.

### Deletions cascading from the change

- [SchemaReader.ScriptTable()](../../../src/SQLParity.Core/SchemaReader.cs#L91-L164) — delete; only caller was lazy-load.
- [SchemaReader.ReadTables()](../../../src/SQLParity.Core/SchemaReader.cs#L439-L501) — delete; dead code (no callers — production uses `BulkTableMetadataReader.ReadAllTables`). Its comment referenced the now-deleted `ScriptTable()` lazy-load path.
- [ResultsViewModel.LoadDdlAsync()](../../../src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs#L403-L473) — delete; tables already have DDL after bulk read.
- The three `change.DdlSideA = reader.ScriptTable(...)` sites in [ComparisonHostViewModel.cs](../../../src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs) at lines 551, 743, 889 — these populated folder-mode Side A from the live DB. Replace with the value already produced by `BulkTableMetadataReader` (DDL is on the model). If a path produces a `TableModel` outside the bulk reader, it must also call the generator.
- `IsLoadingDdl` property and any bindings to it.

## CreateTableGenerator details

### Output format

Matches existing SMO output at `CreateTableScriptingOptions` scope:

```sql
CREATE TABLE [dbo].[MTDParameters](
    [Id] [int] IDENTITY(1,1) NOT NULL,
    [Name] [nvarchar](100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    [Value] [decimal](18, 4) NULL,
    [CreatedAt] [datetime] NOT NULL CONSTRAINT [DF_MTDParameters_CreatedAt] DEFAULT (getdate()),
    [Computed] AS ([Value] * 2) PERSISTED,
    CONSTRAINT [PK_MTDParameters] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [CK_MTDParameters_Value] CHECK ([Value] >= 0)
)
```

### Per-column rules

Driven by `ColumnModel`:
- `[name] [type](size)` — type-aware sizing:
  - No length for `int`, `bit`, `bigint`, `smallint`, `tinyint`, `datetime`, `date`, `smalldatetime`, `money`, `smallmoney`, `real`, `float`, `uniqueidentifier`, `xml`, `geography`, `geometry`, `hierarchyid`, `sql_variant`, `sysname`, `image`, `text`, `ntext`
  - `(max)` when MaxLength = -1 for varchar/nvarchar/varbinary
  - Precision + scale for `decimal`, `numeric`
  - Precision only for `datetime2`, `time`, `datetimeoffset` (when precision differs from default)
  - Length for `char`, `nchar`, `varchar`, `nvarchar`, `binary`, `varbinary` — explicit
- `IDENTITY(seed, increment)` when `IsIdentity`
- `COLLATE <name>` only when `Collation` is non-null and column is a string type
- `NULL` / `NOT NULL`
- `CONSTRAINT [name] DEFAULT (def)` when `DefaultConstraint` is set — default definition already includes its own parens from `sys.default_constraints.definition`; do not double-wrap
- Computed columns: `[name] AS (computed_text) PERSISTED?` — no type, no null spec, no default

### Table-level constraints (after columns)

- PK: `CONSTRAINT [name] PRIMARY KEY [CLUSTERED|NONCLUSTERED] ([col1] ASC, [col2] DESC, ...)` — sourced from `IndexModel` where `IsPrimaryKey == true`
- CHECK: `CONSTRAINT [name] CHECK (definition)` from each `CheckConstraintModel`
- Not emitted: non-PK indexes, FKs, triggers, extended properties

### Edge cases

- Zero-column table → throw `InvalidOperationException` (defensively; should not occur for real tables).
- Always bracket type names (`[int]`, `[nvarchar]`) to match SMO output.

### Tests

`tests/SQLParity.Core.Tests/Comparison/CreateTableGeneratorTests.cs` — pure xUnit, no DB:

- Simple two-column table
- IDENTITY column
- Multiple numeric type variants (int, bigint, smallint, decimal(p,s), numeric, money, real, float)
- String types: char, nchar, varchar(n), nvarchar(n), varchar(max), nvarchar(max)
- Date/time: date, datetime, datetime2(p), time(p), datetimeoffset(p), smalldatetime
- Binary: binary(n), varbinary(n), varbinary(max)
- COLLATE clause emitted only for string columns
- DEFAULT constraint emitted with constraint name
- Computed column (persisted and non-persisted)
- Single-column PK clustered
- Multi-column PK with mixed ASC/DESC
- Nonclustered PK
- CHECK constraint
- Multiple CHECK constraints
- Nullable / NOT NULL mix
- Schema and table names with special characters → bracketed correctly

## Issue 1 — error banner UI

### Detection

A DDL string starting with `-- Could not script ` flags an error. After the catalog-driven change, tables won't produce this sentinel anymore, but views/procs/functions/sequences/synonyms/UDTs still can (see [SchemaReader.cs:660, 708, 753, 797, 842, 879, 916, 955](../../../src/SQLParity.Core/SchemaReader.cs)). The banner handler is generic.

### Where it lives

[ResultsView.xaml.cs:181-210](../../../src/SQLParity.Vsix/Views/ResultsView.xaml.cs#L181-L210) `UpdateDdlDiff()`. Top of method:

```csharp
var ddlA = vm.SelectedDdlA ?? string.Empty;
var ddlB = vm.SelectedDdlB ?? string.Empty;
var errorA = ExtractScriptError(ddlA);  // returns text after the sentinel, or null
var errorB = ExtractScriptError(ddlB);

if (errorA != null || errorB != null)
{
    ErrorBanner.Visibility = Visibility.Visible;
    ErrorBanner.Text = BuildBannerText(errorA, errorB, vm.Direction.LabelA, vm.Direction.LabelB);
    DdlBoxA.Document = errorA != null ? PlaceholderDoc(errorA) : RawDoc(ddlA);
    DdlBoxB.Document = errorB != null ? PlaceholderDoc(errorB) : RawDoc(ddlB);
    return;
}

ErrorBanner.Visibility = Visibility.Collapsed;
// ...existing diff path unchanged
```

Helpers:
- `ExtractScriptError(ddl)` — returns the text after the sentinel if `ddl` begins with `-- Could not script `, else `null`. Single-line: stop at first newline.
- `RawDoc(ddl)` — builds a plain `FlowDocument` from the DDL string, monospace, no diff alignment, no red/green coloring.
- `PlaceholderDoc(errorText)` — emits `(could not load — see error above)` in muted gray.
- `BuildBannerText(errorA, errorB, labelA, labelB)` — concatenates per-side messages e.g. `Could not load DDL for [Production]: <error text>\nCould not load DDL for [Staging]: <error text>`.

### Banner XAML

New `Border` in [ResultsView.xaml](../../../src/SQLParity.Vsix/Views/ResultsView.xaml) directly above the row containing `DdlBoxA`/`DdlBoxB`:

- Background: light red (`#FFEEEE`)
- BorderBrush + foreground: `#C62828`
- Padding: 8
- Contains a `TextBlock` named `ErrorBanner`, `TextWrapping="Wrap"`, `FontFamily="Segoe UI"`
- Spans full width of the comparison region
- `Visibility="Collapsed"` by default

## Data flow / risk notes

- **`IsBtoADangerous` gate** at [ResultsViewModel.cs:558](../../../src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs#L558) reads `result.SideB.HasObjects` — unaffected (doesn't depend on DDL strings).
- **Sync path:** [ScriptGenerator.cs](../../../src/SQLParity.Core/Sync/ScriptGenerator.cs) uses `change.DdlSideA` for `New` tables. Post-fix, that comes from the generator. Generator output must be execute-safe — verified by integration test that round-trips via apply + re-read.
- **Folder mode:** [ComparisonHostViewModel.cs:889-892](../../../src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs#L889-L892) loads Side A DDL from a live DB to diff against on-disk file DDL. Diff output will differ cosmetically (no SMO header noise, cleaner formatting). Expected improvement, not regression.
- **Tests:** existing tests that asserted `TableModel.Ddl == string.Empty` after bulk read need updating to assert the generator output. Fix expectations, don't weaken assertions.
- **New integration test:** read schema → apply generator output to an empty DB → re-read schema → assert columns match (round-trip safety).

## Out of scope

- Indexes, FKs, triggers as part of CREATE TABLE
- Settings toggle for SMO fallback
- Any change to ALTER generation or apply path beyond benefitting from new CREATE source
- Views/procs/functions/etc. — still SMO

## Open questions

None at design time.
