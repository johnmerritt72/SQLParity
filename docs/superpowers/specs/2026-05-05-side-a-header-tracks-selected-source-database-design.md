# Side A Header Tracks Selected Change's Source Database

**Date:** 2026-05-05
**Branch:** feature/folder-mode

## Problem

In folder-mode multi-database comparisons, Side A reads multiple databases on the same server (per `USE` statements in the folder's `.sql` files). The schema reader merges results across those databases, but Side A's pane header on the results screen still shows the database name picked by the user in Setup — which is misleading when the actual `Change.SourceDatabase` of the currently displayed object differs.

User-reported scenario: Setup picked `Centurion`. Folder's `.sql` files reference `Protech`. Tree shows `StoredProcedure (Protech)` group. Header shows `Side A: DEV (Centurion)`. The mismatch makes it look like the wrong database is being read.

The just-shipped static `LabelAWithDb` property pulls from `SideA.DatabaseName` and so cannot reflect the actual source database of the change being inspected.

## Goal

Make Side A's pane header dynamic so the database name in the suffix matches the `SourceDatabase` of whichever change is currently selected in the tree. When no change is selected, fall back to a sensible default that depends on the comparison's source-database shape.

Side B's header is unaffected — `SourceDatabase` describes Side A's source database, not Side B's.

## Non-goals

- No change to the change-tree grouping or labels (already shipped — groups stay labelled `Type (DB)` per change's `SourceDatabase`).
- No change to the arrow / direction display, `SourceLabel`, `DestinationLabel`, or `LabelA`/`LabelB`.
- No change to Side B's header (`LabelBWithDb` stays as-is).
- No change to the data model, comparison engine, or schema readers.

## Design

### Where the dynamic label lives

Add a new computed property `CurrentSideALabel` on [ResultsViewModel](../../../src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs). The property reads `SelectedChange?.SourceDatabase`, a precomputed `_defaultDbNameA` field, and `Direction.LabelA`. The XAML header at [ResultsView.xaml:323](../../../src/SQLParity.Vsix/Views/ResultsView.xaml#L323) is rebound from `Direction.LabelAWithDb` (just-shipped, static) to `CurrentSideALabel`. The static `LabelAWithDb` property and its backing field are deleted from `SyncDirectionViewModel`. `LabelBWithDb` and the `ComposeLabelWithDb` helper stay (Side B still uses the static suffix).

### Default DB name (the pre-selection / fallback)

Compute once at `Populate` time:

```
distinctSourceDbs = result.Changes
    .Select(c => c.SourceDatabase)
    .Where(s => !string.IsNullOrEmpty(s))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList()

if distinctSourceDbs.Count == 0:           # single-DB live-vs-live
    _defaultDbNameA = sideA.DatabaseName
elif distinctSourceDbs.Count == 1:         # folder mode targeting one DB
    _defaultDbNameA = distinctSourceDbs[0]
else:                                       # folder mode spanning multiple DBs
    _defaultDbNameA = string.Empty
```

The single-DB-folder case shows the **actual** DB the folder targets, even when it differs from what the user picked in Setup. The multi-DB-folder case drops the suffix because no single name is accurate before the user clicks a specific change.

### `CurrentSideALabel` formula

```csharp
public string CurrentSideALabel
{
    get
    {
        var activeDbName = !string.IsNullOrEmpty(SelectedChange?.SourceDatabase)
            ? SelectedChange.SourceDatabase
            : _defaultDbNameA;

        return string.IsNullOrEmpty(activeDbName)
            ? Direction.LabelA
            : $"{Direction.LabelA} ({activeDbName})";
    }
}
```

### Property-change wiring

Two triggers fire `OnPropertyChanged(nameof(CurrentSideALabel))`:

1. **`SelectedChange` setter** — already exists in the file with the standard `SetProperty(...)` pattern. Add the explicit `OnPropertyChanged(nameof(CurrentSideALabel))` call in the same `if (SetProperty(...))` block where the existing `OnPropertyChanged` notifications fire. This makes the header react instantly to tree clicks.

2. **End of `Populate`** — after `Direction.PopulateFrom` runs and `_defaultDbNameA` is assigned, fire `OnPropertyChanged(nameof(CurrentSideALabel))` once so the initial render is correct (no leaf is selected yet, so the default-DB branch is exercised).

`Direction.LabelA` itself never changes after `PopulateFrom`, and direction A↔B flips don't re-call `PopulateFrom`, so no other PropertyChanged hookup is needed.

### What gets removed

- `_labelAWithDb` field
- `LabelAWithDb` property + XML doc
- `LabelAWithDb = ComposeLabelWithDb(sideA);` line in `PopulateFrom`

That's it — `LabelBWithDb` and `ComposeLabelWithDb` stay in place for Side B.

## Behavior trace

| Compare flavor | `_defaultDbNameA` | Pre-selection | Change selected with `SourceDatabase = "Protech"` | Change selected with `SourceDatabase = "Centurion"` |
|---|---|---|---|---|
| Live A vs Live B | `SideA.DatabaseName` (e.g. `Centurion`) | `Side A: DEV (Centurion)` | n/a (SourceDatabase always null) | n/a |
| Folder targets one DB (`Protech`), user picked `Centurion` | `Protech` | `Side A: DEV (Protech)` | `Side A: DEV (Protech)` | n/a |
| Folder targets two DBs (`Protech`, `Centurion`) | `""` | `Side A: DEV` | `Side A: DEV (Protech)` | `Side A: DEV (Centurion)` |

Tree groups are already correct per the prior change — this design only fixes the header.

## Edge cases

- **Empty result (`result.Changes` empty):** `distinctSourceDbs.Count == 0`, so `_defaultDbNameA = SideA.DatabaseName`. Pre-selection header reads `Side A: DEV (Centurion)`. No leaves to select. Acceptable — empty result means single-DB-style display by default.
- **`SelectedChange` reset to `null` (e.g., tree refresh):** active DB falls through to default, header reverts to default state. ✓
- **Direction flip (A↔B):** `PopulateFrom` is **not** re-called; `_defaultDbNameA` and `Direction.LabelA` stay put; header text unchanged. (Direction flip changes the arrow only; identifying which side is which is unrelated.) ✓
- **`SelectedChange.SourceDatabase` is `""` rather than `null`:** `string.IsNullOrEmpty` treats both as no-suffix → falls through to default. Defensive.
- **`Direction.LabelA` is empty (user didn't enter a label):** ternary still works — `"" + " (Protech)"` becomes `" (Protech)"`. With the `Side A: ` `StringFormat` prefix that yields `Side A:  (Protech)`. Pre-existing behavior with the old static label too; not made worse.
- **Folder-multi-DB case where user picks `SourceDatabase` that case-differs from another change's** — `Distinct(OrdinalIgnoreCase)` collapses them, so `count == 1` and the suffix appears. The selected change's exact-cased name is what the suffix shows.

## Testing

Manual, in SSMS 22 with the VSIX deployed:

| Scenario | Expected |
|---|---|
| Live A vs Live B | Side A header reads `Side A: <Label> (<DBName-A>)` regardless of which change is clicked. |
| Folder mode, single DB referenced (matches Setup pick) | Side A header reads `(<DBName>)` pre-selection and per-change. |
| Folder mode, single DB referenced (differs from Setup pick) | Side A header reads `(<actual folder DB>)` pre-selection and per-change — **not** the Setup-picked name. |
| Folder mode, two DBs referenced — pre-selection | Side A header reads `Side A: <Label>` with no suffix. |
| Folder mode, two DBs referenced — click change in DB1 | Side A header reads `(DB1)`. |
| Folder mode, two DBs — click change in DB2 | Side A header reads `(DB2)`. |
| Click direction-flip arrow | Side A header text unchanged. |
| Empty result | Side A header reads `Side A: <Label> (<DBName-A>)` (single-DB-style default since no changes have `SourceDatabase`). |

No automated tests — VSIX project has no test framework wired.

## Rollback

Revert the three files:
- `src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs`
- `src/SQLParity.Vsix/ViewModels/SyncDirectionViewModel.cs`
- `src/SQLParity.Vsix/Views/ResultsView.xaml`

The just-shipped static `LabelAWithDb` removal is part of this change; reverting brings it back. No data migrations.
