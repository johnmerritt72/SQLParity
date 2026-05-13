# Compare Selected Database — default to saved-connection credentials

**Date:** 2026-05-04
**Branch:** feature/folder-mode (working branch at time of writing)

## Problem

The `Tools → Compare Selected Database` command (right-click → Compare in SSMS Object Explorer) reads the selected node's server and database, opens the SQLParity tool window, and pre-fills `Side A`'s `ServerName` and `DatabaseName`. It does **not** populate the credentials for that database.

The `ConnectionSideViewModel.ServerName` setter has an auto-fill path that runs whenever the server name changes: it calls `_historyStore.FindByServer(server)`, which returns the **most recently used** saved row for that server (regardless of database), then applies its credentials and triggers an auto-connect. This means:

- If the user has saved multiple databases on the same server, the credentials applied may be from a different DB's row.
- The auto-fill also sets `DatabaseName` from the saved row before the menu command's explicit `DatabaseName` assignment runs, so for a brief window an unrelated DB name is in the field and the auto-connect query may use it.
- If no saved row exists for the server at all, no credentials are populated.

## Goal

When the user invokes `Compare Selected Database`, look up the **exact** `(server, database)` pair in the saved-connection store and pre-fill Side A — including credentials — from that row. If no exact row exists, fall back to today's server-only behavior.

## Non-goals

- No change to the `ServerName` setter's auto-fill path (typing into the server dropdown still uses `FindByServer`).
- No change to the saved-connection schema, persistence, or DPAPI handling.
- No change to Side B or to the folder-mode flow.
- No new UI.

## Design

### Components touched

| File | Change |
|---|---|
| `src/SQLParity.Vsix/CompareWithCommand.cs` | Look up exact saved connection; route to new VM method when found. |
| `src/SQLParity.Vsix/ViewModels/ConnectionSideViewModel.cs` | Add `LoadFromSavedConnection(SavedConnection saved, string overrideServer, string overrideDatabase)`. |
| `src/SQLParity.Vsix/Helpers/ConnectionHistoryStore.cs` | No change — `FindByServerAndDatabase` already exists. |

### Flow

1. `CompareWithCommand.Execute` reads `(server, database)` from the OE node (unchanged).
2. After locating the host view model and switching to `WorkflowState.ConnectionSetup`:
   - If `database` is non-empty, call `_historyStore.FindByServerAndDatabase(server, database)`.
   - **Match found:** call `hostVm.SetupViewModel.SideA.LoadFromSavedConnection(saved, server, database)`. Skip the existing `ServerName`/`DatabaseName` assignments.
   - **No match (or no database from OE):** fall through to today's behavior — set `SideA.ServerName = server` (which fires the existing `FindByServer` auto-fill) then `SideA.DatabaseName = database`.
3. Show the tool window (unchanged).

### `LoadFromSavedConnection` method

New public method on `ConnectionSideViewModel`. Signature:

```csharp
public void LoadFromSavedConnection(SavedConnection saved, string overrideServer, string overrideDatabase)
```

Body, in order:

1. Set `_isAutoFilling = true` in a `try`/`finally` so the `ServerName` setter does not race a second `FindByServer` lookup against the partially populated state.
2. Apply fields from `saved`, but use the override server/database for the visible names:
   - `ServerName = overrideServer`
   - `DatabaseName = overrideDatabase`
   - `Label = saved.Label` (if non-blank)
   - `UseWindowsAuth = saved.UseWindowsAuth`
   - If `!saved.UseWindowsAuth`: `SqlLogin = saved.SqlLogin`; if `PasswordSavingEnabled()`, `SqlPassword = saved.GetPassword()`.
3. Outside the `try`/`finally`, call `ConnectAsyncNoRefresh()` to populate the database list (mirrors what the existing auto-fill does on a hit).

`PasswordSavingEnabled()` is currently a `private static` helper on `ConnectionSideViewModel`; it stays private — `LoadFromSavedConnection` calls it directly.

### Why a new method rather than reusing the setter path

The `ServerName` setter is shared with the user-typing flow in the dropdown. Changing it to prefer `FindByServerAndDatabase` would require also passing the database name through, and would change behavior for every code path that assigns `ServerName`. Keeping the menu-driven path explicit isolates the change to the one entry point that has both pieces of information at once.

## Edge cases

- **Saved row uses Windows auth:** ignore `SqlLogin`/`SqlPassword` from the row (today's `SaveConnection` already blanks the login in that case, but be defensive).
- **Saved row uses SQL auth but `EncryptedPassword` is empty** (password saving was off when saved, or DPAPI decrypt failed): apply login, leave password blank. Same as today's auto-fill.
- **Password saving currently disabled in options:** do not apply `saved.GetPassword()` even if it's present. Matches the existing setter's behavior.
- **OE node is a server node (no database name):** skip the exact lookup; fall through to today's behavior so the server-only auto-fill still runs.
- **Server name from OE has the `" (SQL Server …)"` suffix:** stripping already happens in `CompareWithCommand` before the lookup.
- **Database from OE no longer exists on the server:** the existing post-connect logic in `DoConnectAsync` clears `DatabaseName` if the connect-then-`sys.databases` query doesn't include it. Same behavior — user picks a valid DB from the list.

## Testing

Manual, in SSMS 22 with the VSIX deployed:

| Scenario | Expected |
|---|---|
| Saved row exists for the picked `(server, db)` with SQL auth + saved password | Server, DB, login, password all populated; auth radio on SQL; database list loads. |
| Saved row exists for the picked `(server, db)` with Windows auth | Server, DB populated; auth radio on Windows; login/password blank; database list loads. |
| Saved row exists only for the server (different DB) | Today's behavior — server + DB populated, credentials from most recent row on that server. |
| No saved row for the server at all | Server + DB populated, credentials blank, no auto-connect. |
| Right-click server node (no DB) | Server populated, today's flow runs. |
| Saved row has SQL auth, password saving disabled in options | Login populated, password blank. |

Out of scope for automated tests — the menu command and the VM auto-fill both depend on SSMS / VS hosting; the existing project has no test coverage for these paths.

## Rollback

Revert the two changed files; no data, no schema, no settings to migrate.
