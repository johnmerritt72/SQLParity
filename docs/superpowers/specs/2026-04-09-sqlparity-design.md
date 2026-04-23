# SQLParity — Design

**Date:** 2026-04-09
**Status:** v1.0 RC1 — implemented and verified in SSMS 22

### v1 Implementation Status

**Completed:**
- Schema reading for all v1 object types (SMO-based)
- Schema comparison with column-level diffing
- Four-tier risk classification (Safe, Caution, Risky, Destructive)
- Pre-flight data-loss quantification queries
- ALTER TABLE generation for column changes (ADD/DROP/ALTER COLUMN, default constraints)
- Script generation with dependency ordering and header banner
- Live apply with per-change transactions and stop-on-failure
- Project file persistence (.sqlparity JSON)
- Environment tag management with label-based auto-suggestion
- SSMS 22 VSIX package with Tools menu entry
- Connection setup with Windows + SQL authentication
- Duplicate label validation
- Confirmation screen with color-coded labels
- Results view with change tree and DDL detail panel
- Sync direction selection with PROD live-apply block
- Destructive gauntlet (review + label typing + 3-second countdown)
- External diff tool (WinMerge) launcher
- Pre-apply destination re-read safety check
- Direction flip toast with destructive count
- Progress bar during comparison
- Auto-saved history (scripts + apply records)
- 159 automated tests (129 unit + 30 integration)

**Deferred to v1.1:**
- SSMS-style two-pane table tree view (using DDL diff for all types in v1)
- Registered-servers integration / database dropdown
- Filter bar (by risk tier, status, text search)
- "Mark as ignored" functionality
- Rename hints
- Unsupported objects panel
- Object Explorer right-click context menu
- Azure AD Interactive authentication
- Synchronized scrolling in detail views
- Configurable external diff tool (hardcoded to WinMerge in v1)

---

## 1. Product Summary

**SQLParity** is an SSMS 22 extension that compares the *schema* of two SQL Server databases and helps the user sync them by generating a SQL script or (for non-production targets) applying changes live. It covers tables, views, procedures, functions, triggers, indexes, constraints, schemas, sequences, synonyms, user-defined types, and user-defined table types. It does **not** compare data.

### Non-negotiable promises

1. **The user always knows which database is which.** There is no "source" or "target" in the UI. Every comparison uses user-chosen labels, a tag-based color palette, and displays both identities persistently and loudly.
2. **Data is never lost by accident.** Every change is classified into one of four risk tiers. Destructive changes trigger pre-flight data-loss quantification, a consolidated review screen, and require typing the destination database's label to proceed. Destructive changes against PROD-tagged databases can never be live-applied in v1 — only scripted.

### Platform choices

- **Host:** SSMS 22 VSIX extension. SSMS 22 is rebased on the modern Visual Studio 2022 shell, which makes extensibility comparable to building a standard VS 2022 extension.
- **Language / UI:** C# + WPF.
- **Schema engine:** SMO (SQL Server Management Objects) for reading schema and emitting DDL, with custom diff, risk classification, and safety logic layered on top. SMO's built-in `Transfer` class is **not** used for applying changes — SQLParity drives its own dependency-ordered execution to preserve control over the safety gauntlet.
- **Name:** SQLParity. User confirmed no obvious trademark conflict (brief web search turned up nothing). "SQL" in the name is a requirement.

---

## 2. Architecture

Two-project split to keep the core testable and the shell thin.

### `SQLParity.Core` (multi-target `net48;net8.0` class library, no VS/SSMS dependencies)

- **Schema Reader** — wraps SMO; produces an in-memory object model of a database's schema. One reader per side of a comparison.
- **Object Model** — plain data classes for tables, columns, indexes, constraints, views, procs, functions, triggers, schemas, sequences, synonyms, UDTs, UDTTs. Immutable where practical. Each object carries its source DDL and a stable identity key for diffing.
- **Comparator** — takes two object models and produces a `ComparisonResult`: a list of `Change` records, each tagged with object type, status (New / Modified / Dropped), and both sides' definitions.
- **Risk Classifier** — pure function taking a `Change` and returning `(RiskTier, Reason[])`. Heavily unit-tested.
- **Pre-Flight Checker** — for Risky and Destructive changes, builds and runs read-only `SELECT COUNT`-style queries that quantify impact (rows with data in a dropped column, rows that would be truncated, etc.). Attaches findings to the change. If a pre-flight query fails (permission denied, timeout), the change is marked "impact unknown" and treated as *more* dangerous, not less.
- **Script Generator** — takes a set of selected changes and emits a dependency-ordered SQL script with a header banner. Computes execution order (drop FKs before dropping referenced tables, create schemas before contained objects, recreate dependent views after base-table alters, etc.).
- **Live Applier** — runs the same dependency-ordered change set directly, wrapping each change in its own transaction, stopping on first failure and reporting what succeeded.
- **Project File I/O** — reads and writes `.sqlparity` JSON project files. Never writes credentials.
- **History Writer** — auto-saves generated scripts and live-apply records to a `history/` folder next to the project file.

### `SQLParity.Vsix` (thin VSIX shell — old-style non-SDK csproj, .NET Framework 4.7.2)

- Package registration, Tools-menu entry, Object Explorer right-click menu item.
- WPF tool window hosting the three-region comparison UI.
- Connection picker with SSMS registered-servers integration and tag-based color assignment.
- Dialogs: project open/save, destructive-change gauntlet, refresh prompts.
- Progress reporting and cancellation plumbing between the UI thread and Core's background work.
- Wires WPF view-models to Core services via a small DI container.

**Architectural rule:** any feature that could plausibly live in either project goes in `Core`. The shell stays as thin as possible.

### Build system note (resolved during Plan 0 spike)

- `dotnet build` and `dotnet test` work for `SQLParity.Core`, `SQLParity.Core.Tests`, and `SQLParity.Core.IntegrationTests`.
- **Full-solution builds including the VSIX require legacy `MSBuild.exe`:** `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`. (`dotnet build` of the VSIX fails at the VSSDK deployment step.)

### Test projects

- `SQLParity.Core.Tests` — xUnit unit tests for the comparator, risk classifier, script generator, project file I/O. Fast, no database required.
- `SQLParity.Core.IntegrationTests` — xUnit tests that spin up throwaway databases on LocalDB, apply known schemas, run full comparisons end-to-end, assert on generated scripts and live-apply outcomes.
- `SQLParity.Vsix` — manual testing via F5 experimental SSMS instance. No automated UI tests in v1.

---

## 3. Identity, Labels, and the "Which DB Is Which" System

### Labels

- Every side of every comparison has a **user-chosen label** — short, human-readable (e.g., `PROD`, `DEV-Jane`, `STAGE-release-42`).
- The label is required; the user cannot start a comparison without naming both sides.
- The words **"Source," "Target," "Left," and "Right" never appear in the UI as database identifiers.** Internally, Core uses `SideA` / `SideB` as neutral identifiers; these never surface to users.

### Colors

- SQLParity uses its own environment-tag color palette: PROD red, STAGING orange, DEV green, SANDBOX blue, UNTAGGED gray.
- The user can override the color per-comparison.
- **Resolved during Plan 0 spike:** SSMS 22 stores per-connection colors in an undocumented private registry hive (`privateregistry.bin`) with no public API. Reading SSMS connection colors from a VSIX extension is not feasible in v1. SQLParity's own tag-based palette is the complete solution — no SSMS-color inheritance.

### Environment tags

- Each side carries an environment tag: `PROD`, `STAGING`, `DEV`, `SANDBOX`, or `UNTAGGED`.
- Tags are sticky per (server + database) combination, stored in user settings (not in the project file, since tags are about environments, not comparisons).
- Tags drive hard rules elsewhere in the system.

### Persistent identity bar

The top region of the comparison tool window shows, always and without scrolling:

- Both sides' labels, large.
- Both sides' color swatches, large.
- Both sides' server and database name, smaller but legible.
- Current sync direction as an explicit arrow: `LABEL_A ──▶ LABEL_B`.
- A toggle button to flip direction — not a dropdown, a deliberate click with a confirmation toast.
- A **colored border around the entire tool window** matching the *destination* side's color, as an ambient cue.

### Sync direction

- Always explicit. When a comparison first opens, neither direction is selected.
- The user must pick a direction before any write action (script generate, live apply) becomes enabled.
- Flipping direction mid-session recomputes the change set and shows a toast like: *"Direction flipped. 23 changes are now destructive."*

### Hard rules tied to environment tags

- If the destination side is tagged **PROD**, the "Apply Live" button is **disabled**, full stop. Script generation remains available.
- This is stricter than the minimum requirement (which would only block destructive changes to PROD), but the strictness keeps the rule simple and unambiguous: **PROD is never live-applied from SQLParity in v1.**

---

## 4. Comparison Workflow

### Step 1 — Entry points

Three entry points all land in the same comparison tool window:

1. **Object Explorer right-click** on a database node → "Compare with..." → small modal to pick the other side → opens the comparison window.
2. **Tools menu → SQLParity → New Comparison** → opens the comparison window with both sides empty.
3. **File → Open → `.sqlparity` project file** → opens the comparison window with both sides and settings pre-populated (but not yet connected).

### Step 2 — Connection setup

Two large panels, one per side, each requiring:

- **Server** (picker defaults to SSMS registered servers list; "New connection" option for manual entry). Confirmed feasible via the SMO `RegisteredServersGroup` API — uses `Microsoft.SqlServer.Management.RegisteredServers.dll` shipped with SSMS 22, referenced with `CopyLocal=false`.
- **Authentication method** (Windows / SQL; Azure AD Interactive deferred to v1.1).
- **Database** (populated after the server connects).
- **Label** (required, user-typed, cannot be blank).
- **Environment tag** (auto-suggested from label text when possible — e.g., `PROD` in the label suggests the PROD tag; user confirms).
- **Color** (assigned from the tag palette; user can override per-comparison).

Credentials are captured here and held in memory only. Nothing on disk contains credentials.

### Step 3 — Confirmation screen

After both sides are configured and before any schema is read, a full-window confirmation screen shows: both labels (large), both colors (large), both server names, both database names, both tags, side by side, with a single **Begin Comparison** button and a **Back** button. Cannot be skipped. No "don't show this again" option.

### Step 4 — Schema read and comparison

- Background thread, cancelable progress bar with per-object-type progress (*"Scripting tables... 847/1204"*).
- Both sides are read **sequentially**, not in parallel. SMO thread-safety is not relied on in v1.
- Once both object models exist, the Comparator runs, then Risk Classification per change, then Pre-Flight checks for any Risky or Destructive changes.

### Step 5 — Results view (three-region layout)

- **Top bar:** persistent identity bar (Section 3).
- **Left panel:** grouped change tree (by object type → schema → object name). Each leaf shows: object name, risk-tier icon (🟢🟡🟠🔴), status icon (`+` new, `~` modified, `−` dropped), and a checkbox. Filter bar at the top (by risk tier, status, text search). Summary strip at the bottom: *"247 differences — 180 safe, 40 caution, 20 risky, 7 destructive. 234 selected for sync."*
- **Right panel:** detail view for the selected change. See Section 7 for type-specific detail-view rules.
- **Bottom bar:** **Generate Script** (primary / default) and **Apply Live** (secondary, disabled for PROD destinations).
- A dedicated **"Unsupported objects"** panel accessible from the left panel lists every object SQLParity encountered that it does not know how to compare, so users are never misled into thinking silent skips count as parity.

### Step 6 — Direction selection

Direction is unset until the user picks it. Picking direction enables the bottom-bar buttons.

### Step 7 — Selection and filtering

- User checks/unchecks changes in the tree.
- Filter bar narrows the view.
- "Mark as ignored" on a change removes it from the active set and saves it to the project file's ignored-differences list.

### Step 8 — Generate Script or Apply Live

- Before either action runs, SQLParity performs a **mandatory pre-apply re-read** of the destination side's schema. If any object changed since the user's last view, the action is aborted with: *"The destination has changed since you reviewed it. Please re-review."* and the comparison is refreshed.
- If the change set contains any Destructive changes, the **destructive gauntlet** (Section 5) runs.
- **Generate Script** writes a dependency-ordered script with header banner to a user-chosen location, auto-copies it to the project's `history/` folder with a timestamp, and offers "Open in SSMS" as a follow-up.
- **Apply Live** runs each change in its own transaction, stops on first failure, reports results, and writes a record to `history/`.

### Step 9 — Refresh

Manual Refresh button re-reads both sides and recomputes. The identity bar shows "Last refreshed N minutes ago" per side.

---

## 5. Risk Classification and the Destructive Gauntlet

### Risk tiers

Every `Change` is classified into exactly one tier by a pure-function Risk Classifier.

**🟢 SAFE — No data loss possible.**
- New table, new view, new proc, new function, new trigger, new schema, new sequence, new synonym, new UDT, new UDTT.
- Adding a nullable column (no default required).
- Adding a new index.
- Adding a new constraint where no existing row violates it (checked by pre-flight; if violations exist, promoted to Risky).
- `CREATE OR ALTER` on any routine where the signature stays compatible.
- Adding a new permission.

**🟡 CAUTION — No direct data loss, but operational risk.**
- Adding a NOT NULL column with a default (safe in content but can take a long lock on large tables).
- Adding a foreign key (validates existing data; can fail if data is dirty).
- Widening a column (e.g., `varchar(50)` → `varchar(100)`, `int` → `bigint`).
- Changing an index definition (drop + recreate; may be slow).
- Changing a view or routine in a way that alters its return shape (might break consumers, not data).

**🟠 RISKY — Data modification but not loss.**
- Narrowing a column where pre-flight confirms current data fits.
- Changing a column's data type in a way that requires conversion.
- Changing collation on a column or table.
- Dropping an index (not destructive to data, but may hurt performance or break plans).
- Changing a default value or check constraint.

**🔴 DESTRUCTIVE — Data loss is certain or very likely, OR an existing object is being removed.**
- Dropping a column.
- Dropping a table.
- Dropping a constraint (PK, FK, unique).
- Narrowing a column where pre-flight finds rows that would be truncated.
- Changing a column type in a lossy way (`datetime` → `date`, `nvarchar(max)` → `nvarchar(50)` with violations, `decimal(18,4)` → `decimal(18,2)`, etc.).
- Dropping a schema.
- **Dropping a view, procedure, function, trigger, sequence, synonym, UDT, or UDTT.** Strictly not *data* loss, but classified Destructive so it surfaces in the gauntlet — nothing disappears silently. (User confirmed this is the correct classification.)

### Pre-flight data checks

For every Risky and Destructive change, the Pre-Flight Checker runs a read-only query against the destination to quantify impact. Examples:

- **Drop column** → `SELECT COUNT(*) FROM [s].[t] WHERE [col] IS NOT NULL` → *"12,847 rows have non-null values that will be lost."*
- **Narrow varchar** → `SELECT COUNT(*) FROM [s].[t] WHERE LEN([col]) > 50` → *"3 rows have values that will be truncated."*
- **Drop table** → `SELECT COUNT(*) FROM [s].[t]` → *"Table contains 204,551 rows."*
- **Drop FK** → `SELECT COUNT(*) FROM [child] WHERE [fk_col] IS NOT NULL` → *"89 child rows currently depend on this relationship."*
- **`datetime` → `date`** → `SELECT COUNT(*) FROM [s].[t] WHERE CAST([col] AS TIME) <> '00:00:00'` → *"47 rows have a non-zero time component that will be lost."*

Pre-flight queries are always read-only. Failures (permissions, timeouts) mark the change as "impact unknown" and treat it as *more* dangerous.

### The destructive gauntlet

Triggered when the selected change set contains at least one Destructive change and the destination is non-PROD. (PROD destructive sync is blocked entirely and never reaches the gauntlet.)

1. **Consolidated review screen.** Full-window modal listing every Destructive change in the batch, grouped by object type, showing object name, what will happen, pre-flight findings, classifier reasons, and a link back to each change in the main view. A loud identity bar at the top of the modal matches the main window.
2. **Label confirmation.** A text input: *"Type **[destination label]** to confirm you want to destructively modify this database."* The user types the destination's label exactly once (case-sensitive) for the whole batch. Confirm button disabled until the typed value matches.
3. **Final last-chance dialog.** Small modal showing destination label, color, server, database, and change count. **Three-second forced wait** before the confirm button enables. One final Proceed / Cancel.
4. **Execute.** Script generation or live apply runs. Result is written to `history/`.

Canceling at any point writes nothing and applies nothing. The gauntlet is skipped entirely when the change set contains no Destructive changes.

### Script generation details

- Scripts are **dependency-ordered** (drop FKs before dropping referenced tables, create schemas before contained objects, etc.).
- Every script starts with an informative header comment: generation timestamp, both labels, both colors, both server names, both database names, number of changes by risk tier, and a `USE [destination_db]` statement.
- No `THROW`/`RAISERROR` booby-trap in the header — user confirmed that typing the label during the gauntlet is sufficient friction.
- Per-change transactions on live apply (not a single mega-transaction). On failure, SQLParity stops and reports what succeeded.

---

## 6. Scope, Distribution, Known Unknowns

### In scope for v1 — on by default

Tables (columns, data types, nullability, defaults, computed columns), table constraints (PK, FK, unique, check, default), indexes (clustered, nonclustered, filtered, included columns), views, stored procedures, scalar functions, table-valued functions (inline and multi-statement), DML triggers, schemas, synonyms, sequences, user-defined types, **user-defined table types**.

### In scope for v1 — off by default

Object-level permissions, users and roles, DDL triggers, full-text catalogs and indexes, XML schema collections, statistics.

### Out of scope for v1 — loudly surfaced as "unsupported, not compared"

CLR assemblies, Service Broker objects, memory-optimized tables and natively compiled procs, temporal table history linkage, FILESTREAM / FileTable, external tables and data sources (PolyBase), database-level settings (collation, compatibility level), server-level objects (linked servers, logins, credentials).

### Filtering dimensions (all supported in v1)

- By object type.
- By schema.
- By name pattern (wildcard).
- By ignore-rules stored in the project file (persist across runs).

### Authentication in v1

- **Windows Authentication:** required.
- **SQL Authentication:** required.
- **Azure AD Interactive:** **deferred to v1.1.** Could not be tested during Plan 0 spike (no Azure AD-enabled target available). `Microsoft.Data.SqlClient` 7.0.0 documents support for the `Authentication=Active Directory Interactive` keyword but end-to-end verification is needed before shipping.
- **Azure AD service principal, managed identity, Azure AD Password:** out of scope for v1.

### Sync modes

- **Script generation** is the default primary action for all comparisons.
- **Live apply** is available for non-PROD destinations only. PROD is never live-applied in v1.

### Persistence

- `.sqlparity` project files (JSON) store: both sides' server, database, label, environment tag, filter settings, ignored-differences list.
- **Credentials are never written to disk.**
- A `history/` folder adjacent to the project file auto-saves every generated script and every live-apply record with timestamps.

### Distribution

- Built as a VSIX targeting SSMS 22.
- Installed by double-clicking the VSIX file.
- No marketplace submission in v1 — internal distribution only.

### Testing

- `SQLParity.Core`: xUnit unit tests (comparator, risk classifier, script generator, project file I/O) + xUnit integration tests against LocalDB with throwaway databases.
- `SQLParity.Vsix`: manual testing via F5 experimental instance. No automated UI tests in v1.

### Resolved by Plan 0 spike

See [spike findings](../spikes/2026-04-09-plan-0-spike-findings.md) for full details.

1. **SSMS 22 connection colors:** Not reachable via public API. SQLParity uses its own tag-based palette.
2. **SSMS 22 registered servers list:** Reachable via SMO `RegisteredServersGroup` API.
3. **VSIX .NET target framework:** Old-style csproj, .NET Framework 4.7.2. Core multi-targets `net48;net8.0`. Full build requires MSBuild.exe.
4. **Azure AD Interactive:** Deferred to v1.1 (no test target available during spike).

### Still open

5. SMO thread-safety limits. Avoided in v1 by reading both sides sequentially.
6. How dependency ordering handles edge cases like circular view references. Worst-case fallback: surface the problem with a "could not determine safe order for these N objects — please script manually" message rather than hiding it.

### Explicitly NOT in scope for v1

Data comparison. Data sync. Scheduled/unattended comparisons. CI/CD integration. Command-line interface. Multi-database comparisons (v1 is strictly 1:1). Cross-server object moves. Snapshot/baseline files independent of a live database. A settings UI beyond what's needed for core flows. Telemetry. Manual rename mapping (see Section 7).

---

## 7. Detail View UI (per object type)

The right panel's detail view behaves differently depending on what the selected change is.

### Tables (modified)

A **two-pane tree view**, one per side, each rooted at the table name and expanding to the familiar SSMS Object Explorer child nodes: **Columns, Keys, Constraints, Triggers, Indexes, Statistics**. Layout and icon style mirror SSMS as closely as possible so muscle memory carries over.

**Highlighting rules:**
- **New** (exists on one side, will be added to the other) → green background, green `+` gutter icon.
- **Deleted** (will be removed from the destination) → red background, **strikethrough** on the name, red `−` gutter icon. The deleted item stays *in place* in the destination tree so the two panes remain visually paired.
- **Modified** (exists on both sides but differs) → yellow background, yellow `~` gutter icon, expandable to show the specific property that differs (type, nullability, default, collation, etc.).
- **Unchanged** → no highlight, default color.

**Behaviors:**
- **"Show unchanged"** toggle at the top of the detail view (default **off**) so the eye goes straight to differences.
- **Synchronized scrolling** between the two trees: selecting a node on one side scrolls the other side to its counterpart or to the gap where it should be.
- **Inline destructive-impact tooltips:** for a dropped column, the tooltip shows the pre-flight finding (e.g., *"Will be dropped. 12,847 rows have non-null values that will be lost."*).

**Icon note:** icons must *look like* SSMS Object Explorer icons without redistributing Microsoft's assets. Use a freely licensed icon pack that matches the style, or draw custom ones. Flagged as a small implementation task — resolve during UI work.

### Routines (views, stored procedures, functions, triggers) — modified

A **full side-by-side text diff** of the complete object definition, not just the changed lines:

- Both sides' complete DDL shown in full.
- Changed lines highlighted (standard green/red/yellow diff colors).
- **Synchronized scrolling.**
- **T-SQL syntax highlighting.**
- Headers on each side show the **label**, never "source/target."

The built-in diff view must be complete and usable on its own. The external tool (below) is a convenience, not a crutch.

### New or dropped objects (any type)

- **New:** the full DDL that will be created, with a "this is new, no impact" banner.
- **Dropped:** the full DDL of what will be removed, with a red banner and (for tables) a row-count impact line.

### "Open in external diff tool" — available for every change

Because every change SQLParity handles ultimately produces SQL, the **Open in external diff tool** button is available on every modified object's detail view *and* on New/Dropped details (one side will just be empty).

**Configuration:**
- Setting under **Tools → Options → SQLParity → External diff tool**.
- Command-line template: `path-to-exe {leftFile} {rightFile} /t "{leftLabel} vs {rightLabel}"` (substitution tokens documented in the settings UI).
- Defaults to **WinMerge** if detected at its standard install path; otherwise empty (user configures manually).

**Behavior:**
- When invoked, SQLParity writes both sides' DDL to temp files, substitutes them into the template, and launches the process.
- Temp files are cleaned up when the comparison window closes.

### Renamed objects (columns, tables, routines)

Renames **cannot be reliably detected** from schema alone. There is no stable internal ID that survives a rename, and heuristics produce false positives that are worse than missing a rename. Consequence:

- A renamed column always appears as one **Destructive drop** + one **Safe add**.
- To mitigate the danger of a user not realizing they're looking at a rename, SQLParity shows a **rename hint** when it sees a dropped column and a new column in the same table with identical data type, nullability, default, and either identical or similar name:

  > *"This looks like it might be a rename of `old_col` → `new_col`. SQLParity cannot confirm renames and will execute this as a drop + add, which will lose data. If this is actually a rename, exclude both of these from the sync and run `sp_rename` manually."*

- Manual rename mapping (where the user explicitly tells SQLParity "treat X as a rename of Y" and it emits `sp_rename`) is noted in the v1.1 wishlist but **out of scope for v1**.

---

## 8. Auto-saved History (minimal)

- Every generated script is auto-saved to a `history/` folder next to the `.sqlparity` project file, with a timestamped filename.
- Every live-apply operation writes a record to the same folder (what was run, what succeeded, what failed).
- No in-UI history browser in v1. Users open the `history/` folder in Explorer if they want to look.

---

## 9. Open questions the user flagged or I flagged for future resolution

- The icon pack choice (Section 7, Tables detail view).
- Whether a manual rename mapping feature graduates from v1.1 wishlist to v1.1 commitment after v1 ships.
