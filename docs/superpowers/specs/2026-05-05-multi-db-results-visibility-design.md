# Multi-Database Visibility on the Results Screen

**Date:** 2026-05-05
**Branch:** feature/folder-mode

## Problem

Folder-mode comparisons let Side A read multiple databases on the same server when the source `.sql` files contain `USE [DbName];` statements that span more than one database. The schema reader merges results across those databases, but the comparison-results UI does not surface which database each object belongs to:

1. The change tree groups objects by `ObjectType` only, so a `StoredProcedure` branch may contain procs from `Db1`, `Db2`, etc. with no way to distinguish them.
2. The two side-pane headers ("Side A: Dev" / "Side B: Test") show only the user-chosen label and never the actual database name being read.

`Change.SourceDatabase` is already populated by `ComparisonHostViewModel` during multi-DB folder runs (see [Change.cs:73](../../../src/SQLParity.Core/Model/Change.cs#L73)), so the data is in place — only the UI needs to use it.

## Goal

Two complementary visibility tweaks on the results screen:

1. **Tree segregation** — split the existing `ObjectType` branches so each `(ObjectType, SourceDatabase)` pair gets its own group node, labelled `"{ObjectType} ({DatabaseName})"`.
2. **Side-header DB suffix** — append `" ({DatabaseName})"` to each side's pane header when that side is a live database with a non-blank `DatabaseName`. Side B in folder mode and any side with a blank `DatabaseName` keep their unsuffixed label.

## Non-goals

- No change to the arrow / direction display ("Dev ──▶ Test") or to `SourceLabel` / `DestinationLabel`. Those keep showing only the user's chosen Label so the arrow stays compact even when a future multi-DB-to-DB comparison touches many databases.
- No change to the data model, comparison engine, schema readers, or `ComparisonHostViewModel` per-DB merge logic.
- No new UI controls, icons, or filters for databases.
- No change to the table-detail tree in the right pane.

## Design

### Part 1 — Tree segregation by `(ObjectType, SourceDatabase)`

**File:** [src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs](../../../src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs), method `Populate` around line 510.

Replace:

```csharp
var grouped = result.Changes
    .GroupBy(c => c.ObjectType)
    .OrderBy(g => g.Key.ToString());
```

…with:

```csharp
var grouped = result.Changes
    .GroupBy(c => new { c.ObjectType, c.SourceDatabase })
    .OrderBy(g => g.Key.ObjectType.ToString(), StringComparer.Ordinal)
    .ThenBy(g => g.Key.SourceDatabase ?? string.Empty, StringComparer.OrdinalIgnoreCase);
```

The group-node label switches from `group.Key.ToString()` to:

```csharp
string groupLabel = string.IsNullOrEmpty(group.Key.SourceDatabase)
    ? group.Key.ObjectType.ToString()
    : $"{group.Key.ObjectType} ({group.Key.SourceDatabase})";
```

Sort order: primary by `ObjectType` (today's alphabetical), secondary by `SourceDatabase` case-insensitively. Null `SourceDatabase` sorts before any non-null value but in practice never co-occurs with non-null in the same comparison.

**Behavior trace:**

| Compare flavor | `SourceDatabase` values across changes | Resulting groups |
|---|---|---|
| Live A vs Live B (no folder) | All `null` | `Table`, `View`, `StoredProcedure`, … (today, unchanged) |
| Folder mode, single DB referenced | All set to `"Db1"` | `Table (Db1)`, `View (Db1)`, … |
| Folder mode, two DBs referenced | Mix of `"Db1"`, `"Db2"` | `StoredProcedure (Db1)`, `StoredProcedure (Db2)`, `Table (Db1)`, `Table (Db2)`, … |

Always-show-suffix when `SourceDatabase` is non-null (user-picked option 1) gives a consistent multi-DB look even when only one DB ended up touched.

### Part 2 — Side-header DB suffix

**Files:**
- [src/SQLParity.Vsix/ViewModels/SyncDirectionViewModel.cs](../../../src/SQLParity.Vsix/ViewModels/SyncDirectionViewModel.cs) — add two new properties.
- [src/SQLParity.Vsix/Views/ResultsView.xaml](../../../src/SQLParity.Vsix/Views/ResultsView.xaml) — repoint the two pane headers (`DdlHeaderA`, `DdlHeaderB`) to the new properties.

**New properties** on `SyncDirectionViewModel`, alongside the existing `LabelA`/`LabelB`:

```csharp
private string _labelAWithDb = string.Empty;
public string LabelAWithDb
{
    get => _labelAWithDb;
    private set => SetProperty(ref _labelAWithDb, value);
}

private string _labelBWithDb = string.Empty;
public string LabelBWithDb
{
    get => _labelBWithDb;
    private set => SetProperty(ref _labelBWithDb, value);
}
```

**`PopulateFrom`** at line 162 gains the new computation:

```csharp
public void PopulateFrom(ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
{
    LabelA = sideA.Label;
    TagA = sideA.Tag;
    LabelB = sideB.Label;
    TagB = sideB.Tag;
    LabelAWithDb = ComposeLabelWithDb(sideA);
    LabelBWithDb = ComposeLabelWithDb(sideB);
    Direction = SyncDirection.Unset;
}

private static string ComposeLabelWithDb(ConnectionSideViewModel side)
{
    if (side.IsFolderMode || string.IsNullOrWhiteSpace(side.DatabaseName))
        return side.Label;
    return $"{side.Label} ({side.DatabaseName})";
}
```

**`ResultsView.xaml`** — change the two header bindings (lines ~323 and ~328):

```xml
<TextBlock x:Name="DdlHeaderA" ...
           Text="{Binding Direction.LabelAWithDb, StringFormat='Side A: {0}'}" />
<TextBlock x:Name="DdlHeaderB" ...
           Text="{Binding Direction.LabelBWithDb, StringFormat='Side B: {0}'}" />
```

Existing `LabelA` / `LabelB` keep driving:
- The arrow display (`DirectionLabel` at lines 88-99).
- `SourceLabel` / `DestinationLabel` (lines 102, 128) used in confirmation prompts and elsewhere.
- Any other consumer that doesn't want the DB suffix.

**Behavior:**

| Side A | Side B | Side A header | Side B header | Arrow display |
|---|---|---|---|---|
| Live (DB1, label "Dev") | Live (DB2, label "Test") | `Side A: Dev (DB1)` | `Side B: Test (DB2)` | `Dev  ──▶  Test` |
| Live (DB1, label "Dev") | Folder, label "Repo" | `Side A: Dev (DB1)` | `Side B: Repo` | `Dev  ──▶  Repo` |
| Live, blank DB (edge case) | Live (DB2, label "Test") | `Side A: Dev` | `Side B: Test (DB2)` | `Dev  ──▶  Test` |

## Edge cases

- **`SourceDatabase` mix of null and non-null in the same comparison** — shouldn't happen (the host VM either runs single-DB or multi-DB), but the GroupBy / sort comparator handles it: nulls sort first, non-nulls sort case-insensitively.
- **Empty change list** — same as today; no groups appear regardless of which keying is used.
- **Database name with parens or special characters** — interpolated as-is (the existing `Label` already permits arbitrary text). XAML `StringFormat` does not interpret braces in the bound string. No escape concerns.
- **`Direction.LabelA*` re-firing when SyncDirection flips A↔B** — `Direction` flipping changes which side is source vs destination; it does **not** re-call `PopulateFrom`. The labels stay accurate because they describe the side identity, not the role. ✓
- **External diff launcher** ([ExternalDiffLauncher.cs:77-78](../../../src/SQLParity.Vsix/Helpers/ExternalDiffLauncher.cs#L77-L78)) uses `labelA` / `labelB` placeholders — fed from elsewhere, not affected by this change.

## Testing

Manual, in SSMS 22 with the VSIX deployed:

| Scenario | Expected |
|---|---|
| Live A vs Live B | Headers show `(DBName)` on both sides; tree groups have no DB suffix (single-DB compare → all `SourceDatabase` null). |
| Folder mode, single DB referenced via `USE` | Side A header shows `(DBName)`; Side B header shows just the label; tree groups labelled `Table (DB1)` etc. |
| Folder mode, two DBs referenced | Side A header shows the user-picked DB; tree shows separate `StoredProcedure (DB1)` and `StoredProcedure (DB2)` groups, each containing only that DB's procs. |
| Switch direction (A↔B) | Headers and tree unchanged (direction toggle is unrelated). |
| Empty result | Headers still render; tree empty. |

No automated tests — VSIX project has no test framework wired.

## Rollback

Revert the three files. No data migrations.
