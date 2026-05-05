# Save Connection on Successful Connect

**Date:** 2026-05-05
**Branch:** feature/folder-mode

## Problem

A successful connection to a database does not always persist that `(server, database)` pair to the saved-connections list. The list only updates if either:

1. `DatabaseName` was already non-blank when `DoConnectAsync` finished — covered by the existing post-connect save in [ConnectionSideViewModel.cs:499-506](../../../src/SQLParity.Vsix/ViewModels/ConnectionSideViewModel.cs#L499-L506).
2. The user reaches the Continue gate, which calls `SaveToHistory()` for both sides — [ConnectionSetupViewModel.cs:200-201](../../../src/SQLParity.Vsix/ViewModels/ConnectionSetupViewModel.cs#L200-L201).

The common interactive flow falls in the gap: type a server name, click Connect with `DatabaseName` blank, wait for the dropdown to populate, then pick a database. No save happens at the moment of database selection — the user must press Continue (which triggers a comparison) before history is updated.

## Goal

When the user picks a database after a successful connect, immediately persist `(server, database, auth, login, optional password, label)` to the saved-connections store.

## Non-goals

- No new validation that the chosen database is reachable beyond what `sys.databases` already implies.
- No change to the dropdown UI, the Continue gate, or the comparison flow.
- No change to `ConnectionHistoryStore`, `SavedConnection`, or the file format.

## Design

### Trigger

In `ConnectionSideViewModel.DatabaseName`'s setter, when the new value is non-blank **and** `AvailableDatabases.Contains(value, StringComparer.OrdinalIgnoreCase)`, call a new private helper `SaveCurrentConnection()`. Case-insensitive matches the existing post-connect "DB no longer exists on server" check at line 494.

`AvailableDatabases.Contains(value)` is the proof-of-successful-connect: the list is cleared at the start of every `DoConnectAsync` (line 447) and is only populated after the `sys.databases` query against the server with the current credentials succeeds. If the picked value is in the list, the credentials are valid for that server.

### Helper

Extract the SaveConnection-call logic now duplicated in `SaveToHistory` and `DoConnectAsync` into a single private method:

```csharp
private void SaveCurrentConnection()
{
    if (string.IsNullOrWhiteSpace(ServerName) || string.IsNullOrWhiteSpace(DatabaseName))
        return;
    try
    {
        var savePassword = !UseWindowsAuth && PasswordSavingEnabled();
        _historyStore.SaveConnection(ServerName, DatabaseName, UseWindowsAuth, SqlLogin,
            savePassword ? SqlPassword : null, Label);
    }
    catch { }
}
```

`SaveToHistory` becomes a one-line wrapper around this. `DoConnectAsync`'s save block becomes a single call. The `DatabaseName` setter calls it inside the new gate.

### Two non-overlapping save sites remain

| Scenario | Setter save fires? | `DoConnectAsync` save fires? |
|---|---|---|
| DB pre-set, click Connect, DB still in list | No (value unchanged across connect) | Yes |
| DB pre-set, click Connect, DB **not** in list (cleared at line 496) | No (set to empty, gate fails) | No (DB blank at end of connect) |
| Blank DB, click Connect, pick DB from dropdown | Yes | No (DB was blank when connect ended) |
| Pick DB A, then pick DB B post-connect | Yes (twice — once per pick, with different `(server, db)` pairs) | No |

Both rows survive option-1 semantics: each distinct `(server, db)` pair the user touches becomes its own saved row.

### Auto-fill paths are unaffected

Every code path that sets `DatabaseName` *before* a connect (the `ServerName`-setter auto-fill at lines 144-145, `LoadFromSavedConnection`, `CompareWithCommand.Execute`) does so while `AvailableDatabases` is empty. The new gate fails, the setter does nothing extra, and the existing `DoConnectAsync` post-connect save still runs once the list populates. Behavior identical to today.

### Re-entrancy

`SaveConnection` is a synchronous file-backed upsert. Calling it from a property setter on the UI thread is consistent with the existing pattern (`DoConnectAsync` already calls it on the UI thread after `await`-ing the async `Task.Run`). No re-entrancy concerns.

## Edge cases

- **Blank `Label`:** `SaveConnection` accepts a null/blank label for new rows. Continue's `SaveToHistory` will later update the row when the user has typed a label. Same as today's pre-Continue save in `DoConnectAsync`.
- **`DatabaseName` set to empty:** gate fails, no save.
- **Setter called with the current value:** `SetProperty` short-circuits (returns false), so the gate is never reached. No spurious re-saves.
- **Password saving disabled in options:** `savePassword` is false, password is not persisted. Same as the existing two save sites.
- **Connect throws after `AvailableDatabases.Clear()` but before population:** list is empty, gate fails on any subsequent assignment, no save.

## Testing

Manual, in SSMS 22:

| Scenario | Expected save behavior |
|---|---|
| Type new server, blank DB, click Connect, pick DB X from dropdown | New row `(server, X)` appears in history immediately (verify by closing/reopening the connection setup screen — server dropdown shows the new server, database dropdown shows X without a re-connect). |
| Same connection, change DB to Y from dropdown without disconnecting | New row `(server, Y)` appears alongside `(server, X)`. Both rows present. |
| Existing flow: server with DB pre-filled from auto-fill, click Connect | Row updated as today (timestamp bump). No new behavior. |
| Connect fails (bad credentials) | No row written. |
| Right-click DB in OE → Compare Selected Database (uses `LoadFromSavedConnection`) | Row updated by `DoConnectAsync` post-connect save as today. New trigger does not fire because the autofill sets `DatabaseName` before the list populates. |

No automated tests — VSIX project has no test framework wired.

## Rollback

Revert `ConnectionSideViewModel.cs`. No data migrations.
