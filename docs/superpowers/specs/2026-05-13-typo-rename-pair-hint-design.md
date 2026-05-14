# Typo-rename pair hint for folder-mode comparisons

**Date:** 2026-05-13
**Branch:** main (new feature branch to be created)
**Status:** Approved (sections 1-3)

## Problem

In folder-mode comparison, the SQL file parser uses the `CREATE [OR ALTER] <type> <name>` statement to identify each object. When a `.sql` file's CREATE statement contains a typo (e.g., `EndOfCalllEvent_Insert` with three Ls) but the file is named correctly (`PROC_Protech.centurion.EndOfCallEvent_Insert.sql` with two Ls), the comparator silently treats the file's object and the actual DB object as two unrelated entities — producing a misleading **DROP** for the file's typo'd name (looks like the DB doesn't have it) and (potentially) a **NEW** for the DB's correct name (looks like the folder doesn't have it).

Real-world incident: confirmed in production today against the `Protech` database — three procs in `centurion` schema flagged for deletion when in fact they exist (just under correct names that the file CREATE statements got wrong).

## Goals

- Detect filename-vs-CREATE-name mismatches in folder-mode and surface them as warnings before the user has to dig.
- Offer a one-click "looks like a typo, pair them as one Modified change" action that mirrors the existing column-rename UX precedent.
- At apply time, rewrite the CREATE-statement name token to the DB's correct name so the user can sync without first hand-editing the .sql file.
- Keep the safety net: pairing is opt-in, never silent, easy to undo.

## Non-goals

- Fuzzy/Levenshtein name matching across orphan-pairs (too many false positives).
- Body-content fingerprint matching (silently masks real renames or different procs that share boilerplate).
- Auto-rewriting `.sql` files on disk to fix the typo.
- Persisting pairing decisions across compares (re-detect each compare; matches column-rename precedent).
- Detection on files containing 2+ CREATE statements (filename can't represent multiple objects; signal is meaningless).

## Architecture

### Detection

[SqlFileParser](../../../src/SQLParity.Core/Parsing/SqlFileParser.cs) currently extracts an object's identifier from the `CREATE [OR ALTER] <type> <name>` statement and ignores the file path. [ParsedSqlObject](../../../src/SQLParity.Core/Parsing/ParsedSqlObject.cs) gains a new field:

```csharp
/// <summary>
/// Logical name extracted from the file path (e.g., "EndOfCallEvent_Insert"
/// from "PROC_Protech.centurion.EndOfCallEvent_Insert.sql"). Populated by
/// FolderSchemaReader when the file produced exactly one parsed object;
/// null when the file contained multiple CREATE batches (filename can't
/// be the source of truth for any single object in that case).
/// </summary>
public string? FileName { get; init; }
```

[FolderSchemaReader](../../../src/SQLParity.Core/Parsing/FolderSchemaReader.cs) computes `FileName` from the file path after parsing. The convention is `<TYPE>_<DB>.<schema>.<name>.sql` — `FileName` extracts the `<name>` segment. If the file produces 2+ `ParsedSqlObject`s, set `FileName = null` on each.

### Surfacing the hint (comparator)

[SchemaComparator](../../../src/SQLParity.Core/Comparison/SchemaComparator.cs) already produces NEW/DROP/MODIFIED Changes by matching on `Id`. Add a post-pass for each top-level routine type (procs, views, functions, sequences, synonyms, UDTs, table types):

1. Collect folder-side orphan DROP changes whose underlying `ParsedSqlObject.FileName` is non-null AND differs from `Id.Name` (case-insensitive ordinal).
2. Collect DB-side orphan NEW changes whose `Id.Name` matches one of those `FileName`s, in the same schema, and same `TargetDatabase` for multi-DB folder mode.
3. Wire `RenameCandidateNames` on both sides of each match.

[Change](../../../src/SQLParity.Core/Model/Change.cs) (new property):

```csharp
/// <summary>
/// File-name-derived names of orphan-DB objects that look like a typo-pair
/// candidate for this orphan-folder change. Empty when no candidates.
/// On the DB-side orphan, the candidates are the file-CREATE names of folder
/// orphans that point at this DB object.
/// </summary>
public List<string> RenameCandidateNames { get; set; } = new();

/// <summary>
/// When the user has paired this change with another via the typo-rename hint,
/// the original schema-qualified name of the partner that was collapsed in.
/// Null when this change isn't a pair result.
/// </summary>
public string? PairedFromName { get; set; }
```

### Pair action (view-model)

[ResultsViewModel](../../../src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs) gets a `PairAsTypoRenameCommand` analogous to the column rename's `ApplyRenameCommand` ([TableDiffTreeBuilder.cs:30-50](../../../src/SQLParity.Vsix/ViewModels/TableDiffTreeBuilder.cs#L30-L50)). Given two Change objects (the orphan DROP from folder + the orphan NEW from DB), replace them with a single Change:

```
Status         = Modified  (or Same if normalized DDLs match)
Id             = the DB-side Id (the correct name)
DdlSideA       = DB DDL
DdlSideB       = file DDL
PairedFromName = "<file CREATE name>"
```

`UndoPairCommand` reverses by re-emitting the original DROP and NEW.

### UI

In [ResultsView.xaml](../../../src/SQLParity.Vsix/Views/ResultsView.xaml)'s change-tree leaf template:

- DROP node with non-empty `RenameCandidateNames`: small button `↻ pair with [foo]?`. ToolTip: *"File CREATE statement says `[orig.name]`, but a DB object exists with the file's name `[foo]` and no folder counterpart. Likely typo — click to compare them as the same object."*
- NEW node with non-empty `RenameCandidateNames`: mirror button `↻ pair with [foo]?` where `[foo]` is the file CREATE name.
- The collapsed Modified result: small `(undo pair)` link, mirroring the column-rename undo at [TableDetailView.xaml:75-82](../../../src/SQLParity.Vsix/Views/TableDetailView.xaml#L75-L82).
- Visual: yellow ⚠ glyph (color `#F9A825` to match the existing CAUTION risk tier from [TableDetailView.xaml:23-25](../../../src/SQLParity.Vsix/Views/TableDetailView.xaml#L23-L25)) on the unpaired warning state — noticeable but not alarming.

### Apply path

When a `Change` has `PairedFromName != null`, [ScriptGenerator](../../../src/SQLParity.Core/Sync/ScriptGenerator.cs) rewrites only the `CREATE [OR ALTER] <type> [<schema>.]<oldname>` token in `DdlSideB` to use `Id.Name` (the DB name). Anchored regex against the first CREATE batch in the file:

```
\bCREATE\s+(OR\s+ALTER\s+)?(PROC|PROCEDURE|VIEW|FUNCTION|SEQUENCE|SYNONYM|TYPE)\s+(\[?<schema>\]?\s*\.\s*)?\[?<oldname>\]?
```

Replace only the matched `<oldname>` token. The rest of the body (recursive references, comments, etc.) is left alone — user sees them in the diff and can decide if they need separate fixing.

If the regex fails to match (defensive — shouldn't happen for well-formed files), fall back to inserting a leading comment in the generated script:
```
-- WARNING: paired typo-rename — could not auto-rewrite CREATE name. Fix the .sql file's CREATE statement before applying.
```

## Tests

- **`SqlFileParserTests` / `FolderSchemaReaderTests`**: single-object file populates `FileName`; multi-object file leaves it null on every parsed object; bracketed/quoted CREATE names extract correctly; case-insensitive comparison works.
- **`SchemaComparatorTests`**: orphan-folder DROP + orphan-DB NEW with file-name match → both get `RenameCandidateNames` populated. Multi-DB: same name in different `TargetDatabase` doesn't pair. No partner → DROP gets empty candidates. Folder orphan with `FileName == Id.Name` → no candidates (no false-positive on non-typo orphans).
- **`ResultsViewModelTests`** (or new `PairAsTypoRenameTests`): pairing collapses two Changes to a single Modified; bodies match → Same; `UndoPair` restores originals; pairing the same change twice doesn't duplicate.
- **`ScriptGeneratorTests`**: paired Modified rewrites the CREATE name token only; rest of body unchanged; `OR ALTER` form preserved; bracketed names handled; multiple CREATE forms (PROCEDURE/PROC, VIEW, FUNCTION) all rewrite; regex no-match path emits the warning comment.

## Out of scope

See "Non-goals" above.

## Open questions

None at design time.
