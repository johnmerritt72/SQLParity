# Block B→A Direction When Side B Is Empty

**Date:** 2026-05-07
**Branch:** feature/folder-mode

## Problem

Folder-mode comparisons against a folder with no `.sql` files (or any other situation where Side B has zero parsed objects) produce a `ComparisonResult` where every `Change` is "exists on Side A only". If the user then flips the direction to **Side B → Side A** and applies the changes, every object on Side A is dropped.

There is no legitimate user intent for this: creating a blank database is trivial in SSMS without SQLParity, and accidentally wiping a populated database via a misconfigured folder comparison is a catastrophic data-loss event the tool should prevent at source.

## Goal

When Side B has zero schema objects in the loaded comparison result, **disable** the B→A direction button and disable the Flip button while the current direction is A→B. The user should not be able to set `SyncDirection.BtoA` until Side B has at least one object.

A→B (the comparator's default after a fresh comparison) stays available so the legitimate "seed an empty repo / scaffold an empty DB from a live source" workflow is unaffected.

## Non-goals

- No change to the comparison engine, schema readers, or apply pipeline.
- No automatic warning dialog or confirm-to-proceed flow — the design choice is a hard block, not a soft warning.
- No protection for the partial case (Side B has 1 object, Side A has 100, comparison is 99 drops + 1 modify). That case has legitimate uses (selectively pruning Side A) and the user retains responsibility.
- No retroactive validation of already-loaded comparisons across `Start Over` cycles beyond what `Populate` already handles — every fresh comparison re-evaluates the flag in `Populate`.

## Design

### Empty-Side-B detection

Add a single computed property to `DatabaseSchema`:

```csharp
/// <summary>
/// True when this schema contains at least one user object (table, view,
/// proc, function, sequence, synonym, or user-defined type). Excludes the
/// Schemas list, because the dbo schema is always present on a live DB
/// even when the database is otherwise empty — so a non-empty Schemas
/// list is not evidence of any user-authored content.
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
```

This handles every empty-Side-B path uniformly:

| Source | When `HasObjects` is false |
|---|---|
| Folder mode, no `.sql` files | `FolderSchemaReader` returns empty lists. ✓ |
| Folder mode, files exist but all fail to parse | Same — `FolderSchemaReader` adds no entries on parse failure. ✓ |
| Live mode, brand-new empty database | Live reader returns empty content lists; only `dbo` schema is in the `Schemas` list, which is excluded from `HasObjects`. ✓ |

### `SyncDirectionViewModel` gating

Add two new properties:

```csharp
private bool _isBtoADangerous;
public bool IsBtoADangerous
{
    get => _isBtoADangerous;
    set
    {
        if (SetProperty(ref _isBtoADangerous, value))
        {
            // Direction commands re-evaluate via CommandManager.RequerySuggested
            // (RelayCommand wires CanExecuteChanged to that), so no manual command
            // requery is needed beyond the standard WPF event flow.
            OnPropertyChanged(nameof(BtoAToolTip));
        }
    }
}

private string _btoADangerExplanation = string.Empty;
public string BtoADangerExplanation
{
    get => _btoADangerExplanation;
    set
    {
        if (SetProperty(ref _btoADangerExplanation, value))
            OnPropertyChanged(nameof(BtoAToolTip));
    }
}

/// <summary>
/// Tooltip text exposed to the XAML for the B→A and Flip buttons.
/// Returns the explanation string when B→A is dangerous, otherwise
/// returns null so WPF skips the tooltip entirely.
/// </summary>
public string BtoAToolTip => IsBtoADangerous ? _btoADangerExplanation : null;
```

Update the existing command initializers in the constructor:

```csharp
SetAtoBCommand = new RelayCommand(_ => Direction = SyncDirection.AtoB);

SetBtoACommand = new RelayCommand(
    _ => Direction = SyncDirection.BtoA,
    _ => !IsBtoADangerous);

FlipCommand = new RelayCommand(_ =>
{
    if (Direction == SyncDirection.AtoB)
        Direction = SyncDirection.BtoA;
    else if (Direction == SyncDirection.BtoA)
        Direction = SyncDirection.AtoB;
}, _ => Direction != SyncDirection.Unset
        && !(Direction == SyncDirection.AtoB && IsBtoADangerous));
```

### `ResultsViewModel.Populate` wiring

After the existing call to `Direction.PopulateFrom(sideA, sideB)` and before setting the default direction, set the danger flag and explanation string from the comparison result:

```csharp
Direction.PopulateFrom(sideA, sideB);
Direction.IsBtoADangerous = !result.SideB.HasObjects;
Direction.BtoADangerExplanation = Direction.IsBtoADangerous
    ? "Side B has no objects — applying B → A would drop everything on Side A. " +
      "Pick a non-empty folder or database before reversing the direction."
    : string.Empty;
Direction.Direction = SyncDirection.AtoB; // Default to A→B (existing line)
```

### `ResultsView.xaml` tooltip

Add `ToolTip` and `ToolTipService.ShowOnDisabled="True"` to the B→A button so users can hover over the disabled button to read the explanation. Same on the Flip button. The A→B button is unaffected.

```xml
<Button Command="{Binding Direction.SetBtoACommand}"
        Padding="8,4" Margin="4,0"
        ToolTip="{Binding Direction.BtoAToolTip}"
        ToolTipService.ShowOnDisabled="True">
    ...
</Button>
```

The `BtoAToolTip` getter returns `null` in the safe case, which makes WPF render no tooltip at all — no visual difference from today.

## Behavior trace

| Side B state | A→B button | B→A button | Flip button | Tooltip on B→A / Flip |
|---|---|---|---|---|
| Side B has objects (current normal case) | enabled | enabled | enabled | none |
| Side B empty (folder with no .sql / live empty DB) | enabled | **disabled** | **disabled** when at A→B; enabled when at B→A (impossible to reach today, but if a future code path lands the user there, Flip lets them out) | "Side B has no objects — applying B → A would drop everything on Side A …" |
| Side B empty AND Side A also empty | enabled | disabled | disabled | same tooltip; harmless because no apply would do anything |

## Edge cases

- **Apply pipeline:** untouched. The block is at the direction-selection layer; if a user somehow lands at `Direction.BtoA` with an empty Side B (e.g., via a future code path), the apply step still proceeds. We trust the UI gate. If we wanted defense-in-depth at the apply step too, that's a separate change.
- **Direction default:** `Direction.PopulateFrom` clears `Direction` to `Unset`. `ResultsViewModel.Populate` then sets it to `AtoB`. Both happen after `IsBtoADangerous` is set, so the gate is in place before `AtoB` is selected. If the gate were ever made to also block `AtoB` (it isn't, but defensively), the default-set would still work because `AtoB` is always allowed.
- **Start Over followed by re-comparison:** every fresh `Populate` recomputes `IsBtoADangerous` from the new result. No state leaks across runs.
- **`PopulateFrom` resets `Direction.Direction = Unset` but not the new flag.** Setting the flag after `PopulateFrom` overrides whatever was there. If a future caller invokes `PopulateFrom` standalone (without setting the flag), the previous comparison's flag value persists — fine for now, since the only call site is `ResultsViewModel.Populate`.
- **Race with WPF's CanExecute requery:** `CommandManager.RequerySuggested` fires on focus, mouse-move, keyboard input — so within at most a few ms of `IsBtoADangerous` flipping, the buttons re-query. No explicit `CommandManager.InvalidateRequerySuggested()` call needed; the existing `RelayCommand` wiring is sufficient.

## Testing

Manual, in SSMS 22 with the VSIX deployed:

| Scenario | Expected |
|---|---|
| Side A live DB with objects, Side B folder with several `.sql` files | All three buttons enabled. Today's behavior. |
| Side A live DB with objects, Side B folder with **zero** `.sql` files | A→B enabled. B→A button greyed out. Hovering over B→A shows the warning tooltip. Flip button greyed out (current direction is A→B). |
| Side A live DB with objects, Side B folder with files that all fail to parse | Same as the zero-files row — `HasObjects` is false. |
| Side A live DB with objects, Side B brand-new empty live DB | Same as zero-files — gate triggers off `HasObjects`. |
| Both sides empty | A→B enabled, B→A and Flip greyed out. Result.Changes is empty so nothing to apply anyway; harmless. |
| Re-run comparison after switching to a non-empty Side B | All buttons enabled again. |

No automated tests — VSIX project has no test framework. The `HasObjects` getter on `DatabaseSchema` *is* in `SQLParity.Core` which has a test project, so a small unit test could be added later if desired (out of scope here).

## Rollback

Revert the four files:
- `src/SQLParity.Core/Model/DatabaseSchema.cs`
- `src/SQLParity.Vsix/ViewModels/SyncDirectionViewModel.cs`
- `src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs`
- `src/SQLParity.Vsix/Views/ResultsView.xaml`

No data, no schema, no settings to migrate.
