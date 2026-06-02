# Object Permissions Comparison — Design

**Date:** 2026-06-02
**Status:** Approved (pre-implementation)

## Summary

Add a new comparison dimension to SQLParity: **object and schema-level
permissions**. Today the tool reads object DDL only (`ScriptingOptions.Permissions
= false`) and diffs objects as normalized T-SQL text. This feature reads granted
permissions from the catalog, diffs them per target object, surfaces the
differences as sub-changes on each object's existing `Change`, classifies their
risk, and emits `GRANT`/`DENY`/`REVOKE` statements in the sync script.

Motivation: coworkers testing the tool asked whether it compares permissions on
stored procedures. Rather than special-case procs, this scopes the feature to
object permissions generally.

## Scope decisions

| Decision | Choice |
|---|---|
| Permission granularity | Object-level **and** schema-level grants. **No** role membership. |
| Object coverage | All object types the tool reads (tables, views, stored procedures, functions, sequences, user-defined data/table types) + schemas (for schema-level grants). |
| Read sources | **Live-vs-live only.** Folder/source-control mode skips the dimension; the toggle is disabled there. |
| Permission states | Distinguish `GRANT`, `GRANT WITH GRANT OPTION`, `DENY`. `REVOKE` = absence of a grant. |
| Surfacing | Sub-changes on the object's `Change` (a `PermissionChanges` list mirroring `ColumnChanges`). An object is Modified if DDL **or** permissions differ. |
| Sync | Generate `GRANT`/`DENY`/`REVOKE` with risk tiers. |
| Default | **On by default** in live-vs-live. |
| Missing destination principal | **Hard-fail loudly** — emit a per-grantee existence check that `RAISERROR`s a clear message and aborts. |

### Explicitly out of scope

- Column-level grants (`sys.database_permissions.minor_id > 0`) — excluded at
  read time; documented as a known limitation.
- Database-level permissions (`class = 0`), role membership, server-level
  permissions.
- Reading/writing permissions to folder `.sql` files (source-control
  round-tripping). Folder mode skips permissions entirely.

## Architecture & data flow

```
SchemaReader.ReadSchema
  └─ PermissionReader.Read(serverConn, db, readObjectLists)  ── one catalog query
        → DatabaseSchema.Permissions : IReadOnlyList<PermissionModel>

SchemaComparator.Compare(sideA, sideB, options, …, eitherSideIsFolder)
  ├─ existing per-type DDL comparison passes (unchanged)
  └─ permission post-pass (only when both sides live AND options.IncludePermissions)
        PermissionComparator.Compare(sideA.Permissions, sideB.Permissions)
          → attaches PermissionChange[] to existing Change,
            or emits a new Modified Change when DDL identical but perms differ
  └─ RiskClassifier folds PermissionChanges into each Change's tier

ScriptGenerator.Generate
  └─ Permissions section (after object creation / routine stub-fill passes)
        PermissionScriptGenerator → GRANT / DENY / REVOKE statements,
        each carrying its PermissionChange risk tier
```

## Section 1 — Data model

New types in `SQLParity.Core.Model`:

```csharp
public enum PermissionClass { Object, Schema }
public enum PermissionState { Grant, GrantWithGrant, Deny }

/// One granted permission read from the catalog (immutable).
public sealed class PermissionModel
{
    public required PermissionClass Class { get; init; }
    public required string GranteeName { get; init; }      // principal (user/role) — cross-DB match key
    public required string PermissionName { get; init; }   // EXECUTE, SELECT, INSERT, REFERENCES, ...
    public required PermissionState State { get; init; }
    // Target: for Object class, the object's schema + name; for Schema class, the schema name.
    public required string TargetSchema { get; init; }
    public required string TargetName { get; init; }
}

/// One detected permission difference — sibling of ColumnChange.
public sealed class PermissionChange
{
    public required string GranteeName { get; init; }
    public required string PermissionName { get; init; }
    public PermissionState? StateSideA { get; init; }   // null = absent on A (source)
    public PermissionState? StateSideB { get; init; }   // null = absent on B (destination)
    public RiskTier Risk { get; set; }
    public IReadOnlyList<RiskReason> Reasons { get; set; } = System.Array.Empty<RiskReason>();
}
```

Permission identity is the tuple `(GranteeName, PermissionName)`; `State` is the
comparable value. A GRANT→DENY flip is one `PermissionChange` with both states
populated; an added grant has `StateSideB == null`.

Additions to existing types:

- `DatabaseSchema` gains `IReadOnlyList<PermissionModel> Permissions { get; init; }`
  (default empty). Flat list on the snapshot; the comparator indexes it by target.
- `Change` gains `IList<PermissionChange> PermissionChanges { get; set; }`
  (default empty), mirroring `ColumnChanges`.

**System-principal / scope rules (enforced at read time):**

- `public` is **kept** — real grants commonly land on it.
- Fixed/system principals excluded: `dbo`, `sys`, `INFORMATION_SCHEMA`, `guest`,
  and any `is_fixed_role = 1` principal.
- Grants on system objects (`is_ms_shipped = 1`) excluded.
- Principals matched by **name**, case-insensitively (SIDs differ across DBs).

## Section 2 — Reader

New `PermissionReader` class (separate from `SchemaReader`'s SMO loop — decoupled
and testable). `SchemaReader.ReadSchema` calls it once after the object lists are
built (only when `options.IncludePermissions` and the connection is live), and
populates `DatabaseSchema.Permissions`.

One query through the existing `ServerConnection` (reuses auth/timeout config):

```sql
SELECT
    dp.class,                      -- 1 = object, 3 = schema
    dp.permission_name,
    dp.state_desc,                 -- GRANT, GRANT_WITH_GRANT_OPTION, DENY
    pr.name        AS grantee_name,
    pr.type_desc   AS grantee_type,
    s_obj.name     AS object_schema,
    o.name         AS object_name,
    s_sch.name     AS schema_name
FROM sys.database_permissions dp
JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
LEFT JOIN sys.objects  o     ON dp.class = 1 AND o.object_id = dp.major_id
LEFT JOIN sys.schemas  s_obj ON o.schema_id = s_obj.schema_id
LEFT JOIN sys.schemas  s_sch ON dp.class = 3 AND s_sch.schema_id = dp.major_id
WHERE dp.class IN (1, 3)
  AND dp.minor_id = 0                          -- whole-object grants, not column-level
  AND ISNULL(o.is_ms_shipped, 0) = 0
  AND pr.name NOT IN ('sys','INFORMATION_SCHEMA','guest')
  AND pr.is_fixed_role = 0
  AND pr.sid <> 0x01;                          -- exclude dbo
```

Mapping rules:

- `class = 1` (with `minor_id = 0`) → `PermissionClass.Object`, target =
  `object_schema` + `object_name`. Kept only if the target object's type is one
  the tool reads — filtered against the already-read object lists, so we never
  emit a permission for an object the comparison doesn't show.
- `class = 3` → `PermissionClass.Schema`, target = `schema_name`.
- `state_desc` → `PermissionState`; `GRANT_WITH_GRANT_OPTION` → `GrantWithGrant`.
- `minor_id > 0` (column-level) excluded — out of scope.

**Folder mode:** `PermissionReader` runs only for live connections. A folder
side has an empty `Permissions` list, and the comparator skips the dimension
(Section 3), so no false drift.

## Section 3 — Comparator

New `PermissionComparator` (static, like `ColumnComparator`) plus wiring in
`SchemaComparator.Compare`.

`PermissionComparator.Compare` takes the two `Permissions` lists, indexes each
by target identity, and for a given object/schema produces `List<PermissionChange>`:

- Group each side's permissions for that target into a dict keyed by
  `(GranteeName, PermissionName)`, case-insensitive on grantee.
- For each key present on either side, compare `State`:
  - present on A only → `StateSideB == null` (exists on source, missing on dest)
  - present on B only → `StateSideA == null`
  - both present, states differ → both populated (e.g. Grant→Deny)
  - both present, same state → no change
- Returns empty when the target's permission sets match.

Wiring into `SchemaComparator.Compare`:

1. **Gate the dimension.** Permission comparison runs only when **both** sides
   are live **and** `options.IncludePermissions` is true. A new
   `eitherSideIsFolder` signal (parallel to the existing `sideBIsFolder`) lets
   the comparator detect folder involvement and skip permissions. When skipped,
   no `PermissionChanges` are attached anywhere — current behavior is untouched.

2. **Object-level.** After the existing per-type passes build the `Change` list,
   a post-pass walks each compared object type, computes `PermissionChanges` for
   that target, and:
   - attaches them to the **existing** `Change` if one exists, **or**
   - emits a **new `Modified` change** when DDL is identical on both sides
     (`DdlSideA == DdlSideB`) but permissions differ (empty `ColumnChanges`,
     non-empty `PermissionChanges`).
   - **New** objects (source only) carry their grants as `PermissionChange`s with
     `StateSideA` set / `StateSideB == null`, so applying a new object also
     applies its permissions.
   - **Dropped** objects leave `PermissionChanges` empty (the object is being
     dropped; grants are moot).

3. **Schema-level.** Schema-level grants attach to the `Schema` change the same
   way. Reading/attaching is **independent of the `IncludeSchemas` DDL toggle** —
   if permissions are on, schema-level permission diffs surface even when schema
   DDL comparison is off, attaching to a (possibly newly-created) `Schema` change.

All permission logic lives in `PermissionComparator` plus this one post-pass; the
existing per-type comparison code is unchanged except for the gate.

## Section 4 — Risk classification

A new `PermissionRiskClassifier` (sibling of `ColumnRiskClassifier`) classifies
each `PermissionChange`. `RiskClassifier` aggregates them into the parent
`Change`'s tier — the parent tier is the **max** of its DDL tier, its
`ColumnChanges` tiers, and its `PermissionChanges` tiers. A body-identical proc
whose only change is a REVOKE therefore surfaces as Destructive.

Rules, framed by what applying the change to the destination would do (A=source,
B=destination):

| Transition | Apply action | Tier |
|---|---|---|
| Granted on A, absent on B | `GRANT` (add access) | **Caution** |
| `GrantWithGrant` on A, absent/plain on B | `GRANT ... WITH GRANT OPTION` | **Risky** (grantee can re-grant) |
| DENY on B that source doesn't have | `REVOKE` the DENY | **Caution** |
| Source state is Deny | `DENY` | **Risky** (actively blocks access) |
| Granted on B, absent on A | `REVOKE` (remove access) | **Destructive** (may break a running app) |
| Grant ↔ GrantWithGrant downgrade | `REVOKE` + `GRANT` | **Risky** |

Governing principle (safety-first): **adding access = Caution, removing or
denying access = Risky/Destructive.** When a transition decomposes into more
than one action (e.g. source `GRANT` vs destination `DENY` requires `REVOKE` the
deny **then** `GRANT`), the `PermissionChange`'s tier is the **max** of its
component-action tiers, and the sync emits all needed statements. Each
`PermissionChange` carries `RiskReason`s explaining its tier (e.g. "Revoking
EXECUTE may break applications that call this procedure"). New `RiskReason` enum
entries added as needed.

## Section 5 — Sync script generation

`ScriptGenerator` gains a permissions pass. A small `PermissionScriptGenerator`
helper (unit-testable in isolation) renders each `PermissionChange`:

| Intent | Emitted T-SQL |
|---|---|
| Add a grant | `GRANT <perm> ON <class>::<target> TO [<grantee>]` |
| Add with-grant | `GRANT <perm> ON ... TO [<grantee>] WITH GRANT OPTION` |
| Source says Deny | `DENY <perm> ON ... TO [<grantee>]` |
| Remove extra (dest-only) | `REVOKE <perm> ON ... FROM [<grantee>]` |
| Flip / downgrade | `REVOKE` then re-`GRANT`/`DENY` |

- `<class>` is `OBJECT` or `SCHEMA`; targets are bracket-quoted and
  schema-qualified.
- Each statement inherits its `PermissionChange`'s risk tier, so it flows through
  the existing risk-gating/banner/gauntlet machinery — a REVOKE is gated like a
  dropped column.
- **Ordering:** permission statements are emitted in a dedicated, clearly
  labeled "Permissions" section **after** the object-creation passes and the
  routine stub/fill passes, so every target exists before its grants. New
  objects' own grants are emitted here too.
- **Missing destination principal — hard-fail loudly:** each grantee is preceded
  by an existence check that `RAISERROR`s a clear, actionable message
  (e.g. "Principal [AppRole] does not exist on destination — cannot apply its
  permissions") and aborts the script, rather than letting SQL Server throw a
  cryptic native error or silently partial-applying.
- `LiveApplier` needs no special-casing — it executes whatever the script
  contains.

## Section 6 — Options & UI

- `SchemaReadOptions` gains `IncludePermissions` (default **true**), alongside the
  existing per-type include flags.
- The filter UI (`ObjectTypeFilterViewModel`) gets an "Include permissions"
  checkbox. In folder-mode comparisons it is **disabled with a tooltip**
  ("Permission comparison requires two live databases"), matching the
  live-vs-live gate so the UI never offers something the comparator silently
  skips.
- Persisted in the project file like the other filter settings.
- **Results UI:** reuses the existing detail-view pattern. A **Permissions**
  section/grid is added to the object detail view showing each `PermissionChange`:
  grantee, permission, source state, dest state, resulting action
  (GRANT/REVOKE/DENY), and risk badge — same visual language as column changes.
  Objects whose only difference is permissions appear in the tree as Modified
  with a permissions indicator, so they aren't mistaken for body changes.

## Section 7 — Testing

- **`PermissionComparator`** — pure unit tests over tuple sets: added, removed,
  state-flip, with-grant up/downgrade, identical-sets (no change), case-insensitive
  grantee match, body-identical-but-permissions-differ emits a Modified change.
- **`PermissionRiskClassifier`** — table-driven over every transition in the
  Section 4 matrix.
- **`PermissionScriptGenerator`** — exact GRANT/DENY/REVOKE text incl. WITH GRANT
  OPTION, schema-qualification, OBJECT vs SCHEMA class, and the RAISERROR
  existence guard.
- **`PermissionReader`** — integration test against the existing throwaway-DB
  fixture: create principals + grants (object & schema; GRANT/DENY/WITH GRANT),
  read, assert the model; confirm system principals/objects and column-level
  grants are excluded.
- **`SchemaComparator`** — folder-mode test asserting permissions are skipped
  (no `PermissionChanges`, no false Modified).

## Known limitations

- Column-level grants are not compared.
- Database-level permissions, role membership, and server-level permissions are
  out of scope.
- Permissions are not represented in folder/source-control `.sql` files, so
  permission comparison is unavailable in folder mode.
