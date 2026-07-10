# Schema Filter for Comparison — Design

**Date:** 2026-07-10
**Status:** Approved

## Problem

Comparing large databases is slow because every object in every schema is read and
scripted. Users often only care about one schema. Limiting the comparison to a
selected schema should skip loading and comparing everything else, making the
compare substantially faster.

## Decision Summary

A single-select schema dropdown on the comparison setup screen, populated from
Side A, applying to both sides. Filtering happens at read time in `SchemaReader`
(the speedup), in memory for cached schemas and folder mode. Scoped reads are
cached under a schema-qualified key.

## UI (Connection Setup screen)

- New row at the bottom of the existing "Compare Object Types" group box in
  `ConnectionSetupView.xaml`: label `Schema:` + a non-editable ComboBox.
- First item is always **"(All schemas)"** and is the default selection.
- Populated with user schemas from **Side A** whenever Side A has a connected
  server and a selected database, via a lightweight query:
  `SELECT name FROM sys.schemas` excluding system schemas (`sys`,
  `INFORMATION_SCHEMA`, `guest`, and the fixed database-role schemas
  `db_owner` … `db_denydatawriter`). Follows the same async populate pattern
  `ConnectionSideViewModel` uses for `AvailableDatabases`.
- If Side A is not connected, the dropdown contains only "(All schemas)".
- Changing the Side A database repopulates the list; if the previously selected
  schema no longer exists, selection resets to "(All schemas)".
- The same selected schema applies to both sides; there is no Side B dropdown.

## Read Path (the speedup)

- `SchemaReadOptions` gains `public string SchemaFilter { get; set; }` —
  null/empty means no filter.
- In `SchemaReader`, every per-object enumeration loop (schemas, tables, views,
  stored procedures, functions, sequences, synonyms, user-defined data types,
  user-defined table types) skips objects whose schema does not match the filter
  (case-insensitive, `OrdinalIgnoreCase`). Skipped objects are never scripted —
  per-object SMO scripting is where the SQL round-trips happen, so this is the
  source of the speedup.
- The object-count pass applies the same filter so progress reporting stays
  accurate.
- The bulk table-prefetch call (`db.PrefetchObjects`) stays unfiltered — it is a
  single bulk operation and filtering it is not worth the complexity.
- The `Schemas` collection itself is filtered to the single matching schema, so
  a schema-filtered compare still compares that schema's `CREATE SCHEMA`.

### Permissions

When a filter is active, `PermissionReader` returns:

- Object-level permissions only for objects in the selected schema.
- Schema-level permissions only for the selected schema.
- **No** database-level permissions (they are not schema-scoped).

## Cache

`SchemaCache` (VSIX helper) key gains a schema component:

- Full reads: keyed as today (empty schema part) — `server|database`.
- Scoped reads: `server|database|schema`.

Lookup order for a schema-filtered compare:

1. Fresh full-DB cache entry exists → filter it in memory (instant), done.
2. Fresh schema-keyed entry exists → use it.
3. Otherwise do a scoped DB read and store it under the schema key.

Unfiltered compares read/write only the full-DB key. A scoped read can never be
served as (or overwrite) the full database.

### In-memory filtering helper

`DatabaseSchemaFilter.FilterToSchema(DatabaseSchema schema, string schemaName)`
— a small pure function in `SQLParity.Core` returning a new `DatabaseSchema`
containing only objects (and permissions) belonging to the named schema.
Unit-testable; shared by the cache path and folder mode.

## Folder Mode (Side B)

After `FolderSchemaReader` parses the .sql files, apply
`DatabaseSchemaFilter.FilterToSchema` to the parsed `DatabaseSchema`. Side A
still gets the read-time speedup. The dropdown remains enabled in folder mode.

## Comparison and Downstream

Both sides arrive at `SchemaComparator` already filtered — no changes to the
comparator, results view, script generation, or apply flow.

Known consequence: with a filter active, objects in other schemas simply do not
appear in results (they are not reported as missing), and a generated sync
script may reference cross-schema dependencies (e.g., an FK to another schema's
table) that it assumes exist on the destination — the same assumption the tool
already makes about pre-existing objects today.

## Out of Scope (YAGNI)

- Multi-schema selection, exclude lists, name patterns.
- Persisting the selection to the project file.
- Wiring up the dormant `Project.FilterSettings` — `SchemaFilter` semantics stay
  compatible with its `IncludedSchemas` concept if wired later.

## Testing

- **Unit:** `DatabaseSchemaFilter` (objects and permissions filtered, other
  schemas dropped, case-insensitivity); `ObjectTypeFilterViewModel` →
  `SchemaReadOptions.SchemaFilter` plumbing.
- **Integration** (existing pattern in `SQLParity.Core.IntegrationTests`):
  a scoped `SchemaReader` read returns only the selected schema's objects;
  permissions scoped correctly (object + schema level present, database level
  absent).
- **Cache:** a scoped read is stored under the schema-qualified key and never
  pollutes or satisfies the full-DB entry; a fresh full-DB entry satisfies a
  filtered compare via in-memory filtering.
