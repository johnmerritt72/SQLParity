# SQLParity

**A schema comparison and sync tool for SQL Server, delivered as an SSMS 22 extension.**

SQLParity compares the schema of two SQL Server databases and helps you bring them into parity — by generating a dependency-ordered T-SQL script, or (for non-production targets) applying the changes live. It covers tables, views, procedures, functions, triggers, indexes, constraints, schemas, sequences, synonyms, user-defined types, and user-defined table types. It does **not** compare data.

## Design Principles

SQLParity is built around two non-negotiable promises:

1. **You always know which database is which.** There is no "source" or "target" in the UI. Every comparison uses *your* labels, tag-based colors, and shows both identities loudly and persistently. Words like *source*, *target*, *left*, and *right* never appear as database identifiers.
2. **Data is never lost by accident.** Every change is classified into one of four risk tiers. Destructive changes trigger pre-flight impact queries, a consolidated review screen, a label-typing confirmation, and a timed final dialog. Destructive changes against PROD-tagged databases can never be live-applied — only scripted.

## Features

- Schema comparison for the full set of v1 object types (see [What's Compared](#whats-compared))
- Four-tier risk classification (Safe / Caution / Risky / Destructive) with human-readable reasons
- Pre-flight data-loss quantification for every risky or destructive change (e.g. *"12,847 rows have non-null values in this column"*)
- Dependency-ordered script generation with an informative header banner
- Live-apply mode with per-change transactions and stop-on-failure
- Project files (`.sqlparity` JSON) that persist both sides' connection metadata, filters, and ignore-rules — **never credentials**
- Auto-saved history of every generated script and live-apply run
- Environment tags (PROD / STAGING / DEV / SANDBOX / UNTAGGED) with color-coded UI and hard safety rules
- Destructive Gauntlet: review screen → label confirmation → 3-second timed final dialog
- Pre-apply re-read of the destination schema to detect drift since review
- External diff-tool integration (WinMerge by default; any tool configurable)

## Requirements

- **SQL Server Management Studio 22** (x64)
- **.NET Framework 4.8** (ships with SSMS 22)
- Windows 10/11 or Windows Server 2019+

SSMS 21 is not currently supported even though the manifest targets `[21.0, 23.0)`; the extension has only been verified against SSMS 22.

## Installation

Download the latest `SQLParity.Vsix.vsix` from the [Releases](../../releases) page.

### Recommended — install via command line

Double-clicking the `.vsix` often resolves to **SSMS 21's** VSIXInstaller (via file association), which won't list SSMS 22 as a target. Run SSMS 22's installer explicitly:

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /instanceIds:919b8d66 `
    "path\to\SQLParity.Vsix.vsix"
```

Notes:

- `919b8d66` is SSMS 22's instance ID (from `Common7\IDE\SSMS.isolation.ini`). Confirm this matches your install.
- The installer requires **elevation** because SSMS 22 installs extensions into `Program Files`.
- Close SSMS first; reopen it after the installer GUI reports completion.

### Uninstall

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /uninstall:SQLParity.214618a2-e13a-49d0-a25a-ac0f2ae6e811
```

If the uninstaller leaves files behind in `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\<random>\`, delete that folder manually (with admin rights).

### Upgrading between identical versions

VSIXInstaller keys on the manifest's `Identity Version`. If a new build carries the same version number, the installer will report *"already installed"* and `/force` will no-op. Either bump the manifest version or uninstall-then-reinstall.

## Usage

### Entry points

Three ways to start a comparison; all land in the same tool window:

1. **Tools → SQLParity → New Comparison** — blank slate, pick both sides manually.
2. **Tools → SQLParity → Compare Selected Database** — pre-fills Side A from the currently selected server or database node in Object Explorer.
3. **File → Open** a `.sqlparity` project file — loads both sides and their settings; you still supply credentials.

### Workflow

1. **Connection setup.** Configure both sides:
   - Server (free text or picked from SSMS Registered Servers)
   - Authentication (Windows or SQL Server)
   - Database (populated after the server connects)
   - Label (required — e.g. `PROD`, `DEV-Jane`, `STAGE-release-42`)
   - Environment tag (auto-suggested from the label)
   - Color (assigned from the tag palette; overridable per comparison)

   Credentials are held in memory only. Nothing is written to disk.

2. **Confirmation screen.** Full-window sanity check showing both labels, colors, servers, databases, and tags side by side. Cannot be skipped by default (see [options](#options-reference)).

3. **Schema read.** Both sides are read sequentially with a cancelable progress bar. The comparator, risk classifier, and pre-flight checker run next.

4. **Results view.** Three regions:
   - **Top bar**: persistent identity — both labels, colors, server/database names, and the current sync direction arrow. A colored border around the tool window matches the destination's color.
   - **Left panel**: grouped change tree (object type → schema → name) with risk-tier and status icons, filter bar, and a summary strip.
   - **Right panel**: detail view for the selected change — a two-pane table tree for table changes, a side-by-side DDL diff for routines, full DDL for new/dropped objects.

5. **Pick a direction.** Neither direction is selected when the window first opens. Picking one enables the bottom-bar buttons. Flipping direction mid-session recomputes the change set.

6. **Select changes, then act.** Tick/untick changes in the tree. Then:
   - **Generate Script** — writes a dependency-ordered `.sql` file and copies it to `history/` with a timestamp.
   - **Apply Live** — runs each change in its own transaction; stops on first failure; writes a record to `history/`. Disabled when the destination is tagged PROD.

   If the change set contains any Destructive change against a non-PROD destination, the **Destructive Gauntlet** runs first: review screen → type the destination label → 3-second timed Proceed.

## Options Reference

**Tools → Options → SQLParity → General**

### About

| Option | Description |
|---|---|
| **Version** | Installed SQLParity extension version (read-only). |

### Connection Defaults

| Option | Default | Description |
|---|---|---|
| **Default Authentication** | Windows Authentication | Authentication method pre-selected for new connection panels. |
| **Default SQL Username** | *(empty)* | SQL Server login pre-filled when SQL Authentication is selected. |
| **Save Passwords** | On | Persist encrypted passwords (DPAPI) with connection history. Turn off if your security policy forbids stored credentials. |

### Comparison

| Option | Default | Description |
|---|---|---|
| **Expand Tree by Default** | On | When results load, automatically expand all nodes in the change tree. Turn off if you prefer to drill down manually. |
| **Skip Confirmation Page** | Off | Go directly from connection setup into the comparison run, skipping the side-by-side identity confirmation screen. Leave off unless you know what you're doing. |
| **Show Line Numbers** | On | Show line numbers in the DDL diff panels on the right side of the results view. |

### Performance

| Option | Default | Description |
|---|---|---|
| **Schema Cache TTL (minutes)** | 5 | How long to cache a successful schema read in memory. A refresh within this window reuses the cached read instead of re-querying the server. Set to `0` to disable caching. |

### External Diff Tool

| Option | Default | Description |
|---|---|---|
| **Diff Tool Path** | WinMerge (auto-detected) | Full path to an external diff tool executable. Used by the "Open in external diff tool" button in the detail view. |
| **Diff Tool Arguments** | `"{leftFile}" "{rightFile}" /dl "{leftLabel}" /dr "{rightLabel}" /e /u` | Argument template. Supported tokens: `{leftFile}`, `{rightFile}`, `{leftLabel}`, `{rightLabel}`. Defaults are tuned for WinMerge; adjust if you use Beyond Compare, KDiff3, etc. |

## What's Compared

**On by default:** tables (columns, data types, nullability, defaults, computed columns), table constraints (PK / FK / unique / check / default), indexes (clustered, nonclustered, filtered, included columns), views, stored procedures, scalar functions, table-valued functions (inline and multi-statement), DML triggers, schemas, synonyms, sequences, user-defined types, user-defined table types.

**Off by default:** object-level permissions, users and roles, DDL triggers, full-text catalogs/indexes, XML schema collections, statistics.

**Not compared — surfaced as "unsupported":** CLR assemblies, Service Broker objects, memory-optimized tables and natively compiled procs, temporal history linkage, FILESTREAM / FileTable, external tables and data sources (PolyBase), database-level settings (collation, compatibility level), server-level objects (linked servers, logins, credentials).

SQLParity **does not compare data** and does not generate data-migration scripts.

## Risk Classification

Every change falls into exactly one tier:

- **SAFE (green)** — no data loss possible. New objects, nullable column adds, new indexes, etc.
- **CAUTION (yellow)** — no direct data loss but operational risk. Adding NOT NULL with default, adding FKs, widening columns, reshaping routine return types.
- **RISKY (orange)** — data modification but not loss. Narrowing a column where pre-flight confirms data fits; type conversions; collation changes; dropping an index.
- **DESTRUCTIVE (red)** — data loss certain or very likely, OR an existing object is being removed. Dropping columns/tables/constraints; lossy type changes; dropping any routine, view, sequence, synonym, UDT, or UDTT (classified Destructive so nothing disappears silently).

For every Risky and Destructive change, SQLParity runs a read-only pre-flight query against the destination to quantify impact (row counts, truncation counts, orphaned children, etc.). Pre-flight failures mark the change *"impact unknown"* and treat it as more dangerous.

## The Destructive Gauntlet

Triggered when the selected change set contains one or more Destructive changes *and* the destination is non-PROD (PROD destructive live-apply is blocked entirely):

1. **Consolidated review screen** — every Destructive change grouped by object type, with pre-flight findings and the classifier's reasons.
2. **Label confirmation** — type the destination's label exactly (case-sensitive) to enable Confirm.
3. **Final dialog** — shows destination identity once more; Proceed is disabled for 3 seconds.
4. **Execute** — script or live apply runs; results land in `history/`.

Canceling at any step writes and applies nothing.

## Project Files

A `.sqlparity` file is a JSON document describing a comparison setup:

- Both sides' server, database, label, and environment tag
- Filter settings (object types, schemas, name patterns)
- Ignored-differences list (changes the user has explicitly chosen to ignore)

**Credentials are never written.** When a project file is opened, the user supplies credentials in the normal connection panels.

A `history/` folder next to the project file auto-saves:

- Every generated script, named with a timestamp
- A record of every live-apply run (what was attempted, what succeeded, what failed)

## Building from Source

```bash
git clone <this-repo>
cd SQLCompare
```

The solution must be built with **legacy MSBuild**, not `dotnet build` (the VSIX project uses the old-style csproj format required by the VSSDK).

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" `
    SQLParity.sln -t:Build -p:Configuration=Debug -v:minimal
```

Adjust the VS 2022 edition path (`Community`, `Professional`, or `Enterprise`) to match your install. The built `.vsix` will be at `src/SQLParity.Vsix/bin/Debug/SQLParity.Vsix.vsix`.

`SQLParity.Core` and the test projects work fine with `dotnet build` and `dotnet test`:

```bash
dotnet test tests/SQLParity.Core.Tests
dotnet test tests/SQLParity.Core.IntegrationTests   # requires LocalDB
```

For the full SSMS 22 extensibility story, see [docs/ssms-22-extension-development-guide.md](docs/ssms-22-extension-development-guide.md).

## Project Layout

```
src/
  SQLParity.Core/          Engine (schema reader, comparator, risk classifier,
                           pre-flight, script generator, live applier, project I/O).
                           No VS/SSMS dependencies. Multi-targets net48 + net8.0.
  SQLParity.Vsix/          Thin VSIX shell: package, menu commands, tool window,
                           WPF views and view-models, options page. .NET 4.8.

tests/
  SQLParity.Core.Tests/                xUnit unit tests (no database).
  SQLParity.Core.IntegrationTests/     xUnit tests against LocalDB.

docs/
  ssms-22-extension-development-guide.md    Hard-won notes on SSMS 22 extensibility.
  superpowers/specs/                        Design documents.
  superpowers/plans/                        Implementation plans.
```

## License

SQLParity is released under the [MIT License](LICENSE).
