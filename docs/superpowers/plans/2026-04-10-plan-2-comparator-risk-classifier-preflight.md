# SQLParity — Plan 2: Comparator, Risk Classifier, Pre-Flight Checker

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Given two `DatabaseSchema` instances (from Plan 1's `SchemaReader`), produce a fully classified `ComparisonResult` — a list of `Change` records, each tagged with object type, change status (New / Modified / Dropped), risk tier (Safe / Caution / Risky / Destructive), risk reasons, and optional pre-flight impact data.

**Architecture:** Three pure-logic components in `SQLParity.Core`, no database or UI dependencies:
1. **Comparator** — takes two `DatabaseSchema` instances, matches objects by `SchemaQualifiedName`, produces a list of `Change` records with status (New/Modified/Dropped). For modified tables, also diffs columns, indexes, constraints, and triggers as sub-changes.
2. **Risk Classifier** — pure function: takes a `Change`, returns `(RiskTier, Reason[])`. Heavily unit-tested against every classification rule in the spec.
3. **Pre-Flight Checker** — builds read-only SQL queries to quantify impact for Risky and Destructive changes. Does NOT execute them (execution requires a connection, handled later in Plan 3's Live Applier or the VSIX shell). Produces query text + a human-readable description of what it measures.

**Tech Stack:** C#, xUnit. No SMO dependency — this is pure logic over the model types from Plan 1.

**Spec reference:** [design spec §2 (Comparator, Risk Classifier, Pre-Flight Checker)](../specs/2026-04-09-sqlparity-design.md) and [§5 (Risk Classification)](../specs/2026-04-09-sqlparity-design.md).

**What Plan 2 inherits from Plan 1:**
- Complete object model in `src/SQLParity.Core/Model/`
- `SchemaQualifiedName` identity keys on every object
- `DatabaseSchema` as the top-level container
- 29 passing tests (7 unit + 22 integration)

---

## File Structure

```
src/SQLParity.Core/
  Model/
    ChangeStatus.cs                     Enum: New, Modified, Dropped
    RiskTier.cs                         Enum: Safe, Caution, Risky, Destructive
    RiskReason.cs                       A single reason with description
    Change.cs                           A detected difference between two schemas
    ColumnChange.cs                     A column-level sub-change within a table change
    ComparisonResult.cs                 Top-level result container
  Comparison/
    SchemaComparator.cs                 Matches objects across two DatabaseSchemas
    ColumnComparator.cs                 Diffs columns within a table pair
    RiskClassifier.cs                   Pure function: Change → (RiskTier, RiskReason[])
    ColumnRiskClassifier.cs             Risk classification for column-level changes
    PreFlightQueryBuilder.cs            Builds read-only SQL for impact quantification

tests/SQLParity.Core.Tests/
  Comparison/
    SchemaComparatorTests.cs            Tests for top-level object matching
    ColumnComparatorTests.cs            Tests for column-level diffing
    RiskClassifierTests.cs              Tests for every risk tier classification rule
    ColumnRiskClassifierTests.cs        Tests for column risk classification
    PreFlightQueryBuilderTests.cs       Tests for generated SQL queries
```

---

## Task 1: Change model types (ChangeStatus, RiskTier, RiskReason, Change, ColumnChange, ComparisonResult)

**Files:**
- Create: `src/SQLParity.Core/Model/ChangeStatus.cs`
- Create: `src/SQLParity.Core/Model/RiskTier.cs`
- Create: `src/SQLParity.Core/Model/RiskReason.cs`
- Create: `src/SQLParity.Core/Model/ColumnChange.cs`
- Create: `src/SQLParity.Core/Model/Change.cs`
- Create: `src/SQLParity.Core/Model/ComparisonResult.cs`

- [ ] **Step 1: Create ChangeStatus enum**

Create `src/SQLParity.Core/Model/ChangeStatus.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// The kind of difference detected for an object.
/// </summary>
public enum ChangeStatus
{
    /// <summary>Object exists only on SideA (will be created on SideB if syncing A→B).</summary>
    New,

    /// <summary>Object exists on both sides but differs.</summary>
    Modified,

    /// <summary>Object exists only on SideB (will be dropped from SideB if syncing A→B).</summary>
    Dropped
}
```

- [ ] **Step 2: Create RiskTier enum**

Create `src/SQLParity.Core/Model/RiskTier.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// Risk classification for a change. Drives UI presentation and the
/// destructive-change gauntlet.
/// </summary>
public enum RiskTier
{
    /// <summary>No data loss possible.</summary>
    Safe,

    /// <summary>No direct data loss, but operational risk (locks, validation failures).</summary>
    Caution,

    /// <summary>Data modification but not outright loss.</summary>
    Risky,

    /// <summary>Data loss is certain or very likely, or an existing object is being removed.</summary>
    Destructive
}
```

- [ ] **Step 3: Create RiskReason**

Create `src/SQLParity.Core/Model/RiskReason.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// A single reason explaining why a change was classified at its risk tier.
/// </summary>
public sealed class RiskReason
{
    public required RiskTier Tier { get; init; }
    public required string Description { get; init; }

    public override string ToString() => $"[{Tier}] {Description}";
}
```

- [ ] **Step 4: Create ColumnChange**

Create `src/SQLParity.Core/Model/ColumnChange.cs`:

```csharp
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// A column-level sub-change within a modified table. Tracks what specifically
/// changed about the column (type, nullability, default, etc.).
/// </summary>
public sealed class ColumnChange
{
    public required SchemaQualifiedName Id { get; init; }
    public required string ColumnName { get; init; }
    public required ChangeStatus Status { get; init; }

    /// <summary>Column definition on SideA (null if Dropped).</summary>
    public required ColumnModel? SideA { get; init; }

    /// <summary>Column definition on SideB (null if New).</summary>
    public required ColumnModel? SideB { get; init; }

    /// <summary>Risk tier for this column change.</summary>
    public required RiskTier Risk { get; init; }

    /// <summary>Reasons explaining the risk classification.</summary>
    public required IReadOnlyList<RiskReason> Reasons { get; init; }

    /// <summary>Pre-flight query SQL (null if not applicable).</summary>
    public string? PreFlightSql { get; set; }

    /// <summary>Human-readable description of what the pre-flight query measures.</summary>
    public string? PreFlightDescription { get; set; }
}
```

- [ ] **Step 5: Create Change**

Create `src/SQLParity.Core/Model/Change.cs`:

```csharp
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// The type of database object that changed.
/// </summary>
public enum ObjectType
{
    Table,
    View,
    StoredProcedure,
    UserDefinedFunction,
    Schema,
    Sequence,
    Synonym,
    UserDefinedDataType,
    UserDefinedTableType,
    Index,
    ForeignKey,
    CheckConstraint,
    Trigger
}

/// <summary>
/// A single detected difference between two database schemas.
/// </summary>
public sealed class Change
{
    public required SchemaQualifiedName Id { get; init; }
    public required ObjectType ObjectType { get; init; }
    public required ChangeStatus Status { get; init; }

    /// <summary>DDL on SideA (null if Dropped).</summary>
    public required string? DdlSideA { get; init; }

    /// <summary>DDL on SideB (null if New).</summary>
    public required string? DdlSideB { get; init; }

    /// <summary>Risk tier for this change.</summary>
    public required RiskTier Risk { get; init; }

    /// <summary>Reasons explaining the risk classification.</summary>
    public required IReadOnlyList<RiskReason> Reasons { get; init; }

    /// <summary>
    /// For modified tables: the column-level sub-changes. Empty for non-table
    /// objects and for New/Dropped tables.
    /// </summary>
    public required IReadOnlyList<ColumnChange> ColumnChanges { get; init; }

    /// <summary>Pre-flight query SQL (null if not applicable).</summary>
    public string? PreFlightSql { get; set; }

    /// <summary>Human-readable description of what the pre-flight query measures.</summary>
    public string? PreFlightDescription { get; set; }
}
```

- [ ] **Step 6: Create ComparisonResult**

Create `src/SQLParity.Core/Model/ComparisonResult.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace SQLParity.Core.Model;

/// <summary>
/// The result of comparing two database schemas. Contains all detected
/// changes, classified by risk tier.
/// </summary>
public sealed class ComparisonResult
{
    public required DatabaseSchema SideA { get; init; }
    public required DatabaseSchema SideB { get; init; }
    public required IReadOnlyList<Change> Changes { get; init; }

    public int SafeCount => Changes.Count(c => c.Risk == RiskTier.Safe);
    public int CautionCount => Changes.Count(c => c.Risk == RiskTier.Caution);
    public int RiskyCount => Changes.Count(c => c.Risk == RiskTier.Risky);
    public int DestructiveCount => Changes.Count(c => c.Risk == RiskTier.Destructive);
    public int TotalCount => Changes.Count;
}
```

- [ ] **Step 7: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 8: Commit**

```bash
git add src/SQLParity.Core/Model/
git commit -m "feat(model): add Change, ColumnChange, ComparisonResult, RiskTier, ChangeStatus types"
```

---

## Task 2: SchemaComparator — top-level object matching

**Files:**
- Create: `src/SQLParity.Core/Comparison/SchemaComparator.cs`
- Create: `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`

The comparator matches objects between two `DatabaseSchema` instances by `SchemaQualifiedName` (or by `Name` for schemas). It produces a list of `Change` records with `New`, `Modified`, or `Dropped` status. For now, risk classification is deferred to Tasks 5–7 — changes are all created with `RiskTier.Safe` as a placeholder that the classifier will later replace.

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class SchemaComparatorTests
{
    private static DatabaseSchema EmptySchema(string name = "TestDb") => new()
    {
        ServerName = "localhost",
        DatabaseName = name,
        ReadAtUtc = DateTime.UtcNow,
        Schemas = Array.Empty<SchemaModel>(),
        Tables = Array.Empty<TableModel>(),
        Views = Array.Empty<ViewModel>(),
        StoredProcedures = Array.Empty<StoredProcedureModel>(),
        Functions = Array.Empty<UserDefinedFunctionModel>(),
        Sequences = Array.Empty<SequenceModel>(),
        Synonyms = Array.Empty<SynonymModel>(),
        UserDefinedDataTypes = Array.Empty<UserDefinedDataTypeModel>(),
        UserDefinedTableTypes = Array.Empty<UserDefinedTableTypeModel>(),
    };

    private static TableModel MakeTable(string schema, string name, string ddl = "CREATE TABLE ...",
        IReadOnlyList<ColumnModel>? columns = null) => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Ddl = ddl,
        Columns = columns ?? Array.Empty<ColumnModel>(),
        Indexes = Array.Empty<IndexModel>(),
        ForeignKeys = Array.Empty<ForeignKeyModel>(),
        CheckConstraints = Array.Empty<CheckConstraintModel>(),
        Triggers = Array.Empty<TriggerModel>(),
    };

    private static ViewModel MakeView(string schema, string name, string ddl = "CREATE VIEW ...") => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        IsSchemaBound = false,
        Ddl = ddl,
    };

    private static StoredProcedureModel MakeProc(string schema, string name, string ddl = "CREATE PROC ...") => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Ddl = ddl,
    };

    [Fact]
    public void IdenticalSchemas_NoChanges()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema() with { Tables = new[] { table } };
        var b = EmptySchema() with { Tables = new[] { table } };

        var result = SchemaComparator.Compare(a, b);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void TableOnlyInSideA_IsNew()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema() with { Tables = new[] { table } };
        var b = EmptySchema();

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.New, change.Status);
        Assert.Equal(ObjectType.Table, change.ObjectType);
        Assert.Equal("[dbo].[Orders]", change.Id.ToString());
        Assert.NotNull(change.DdlSideA);
        Assert.Null(change.DdlSideB);
    }

    [Fact]
    public void TableOnlyInSideB_IsDropped()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema();
        var b = EmptySchema() with { Tables = new[] { table } };

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Dropped, change.Status);
        Assert.Equal(ObjectType.Table, change.ObjectType);
        Assert.Null(change.DdlSideA);
        Assert.NotNull(change.DdlSideB);
    }

    [Fact]
    public void TableWithDifferentDdl_IsModified()
    {
        var tableA = MakeTable("dbo", "Orders", "CREATE TABLE v1");
        var tableB = MakeTable("dbo", "Orders", "CREATE TABLE v2");
        var a = EmptySchema() with { Tables = new[] { tableA } };
        var b = EmptySchema() with { Tables = new[] { tableB } };

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Equal("CREATE TABLE v1", change.DdlSideA);
        Assert.Equal("CREATE TABLE v2", change.DdlSideB);
    }

    [Fact]
    public void ComparesViewsBySchemaQualifiedName()
    {
        var viewA = MakeView("dbo", "MyView", "CREATE VIEW v1");
        var viewB = MakeView("dbo", "MyView", "CREATE VIEW v2");
        var a = EmptySchema() with { Views = new[] { viewA } };
        var b = EmptySchema() with { Views = new[] { viewB } };

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Equal(ObjectType.View, change.ObjectType);
    }

    [Fact]
    public void ComparesProcsBySchemaQualifiedName()
    {
        var proc = MakeProc("dbo", "GetOrders");
        var a = EmptySchema() with { StoredProcedures = new[] { proc } };
        var b = EmptySchema();

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.New, change.Status);
        Assert.Equal(ObjectType.StoredProcedure, change.ObjectType);
    }

    [Fact]
    public void MultipleObjectTypes_AllDetected()
    {
        var table = MakeTable("dbo", "Orders");
        var view = MakeView("dbo", "OrdersView");
        var proc = MakeProc("dbo", "GetOrders");

        var a = EmptySchema() with
        {
            Tables = new[] { table },
            Views = new[] { view },
            StoredProcedures = new[] { proc },
        };
        var b = EmptySchema();

        var result = SchemaComparator.Compare(a, b);

        Assert.Equal(3, result.TotalCount);
        Assert.Contains(result.Changes, c => c.ObjectType == ObjectType.Table);
        Assert.Contains(result.Changes, c => c.ObjectType == ObjectType.View);
        Assert.Contains(result.Changes, c => c.ObjectType == ObjectType.StoredProcedure);
    }

    [Fact]
    public void ComparisonResult_HasBothSchemas()
    {
        var a = EmptySchema("DbA");
        var b = EmptySchema("DbB");

        var result = SchemaComparator.Compare(a, b);

        Assert.Equal("DbA", result.SideA.DatabaseName);
        Assert.Equal("DbB", result.SideB.DatabaseName);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure — `SchemaComparator` does not exist.

- [ ] **Step 3: Implement SchemaComparator**

Create `src/SQLParity.Core/Comparison/SchemaComparator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Compares two <see cref="DatabaseSchema"/> instances and produces a
/// <see cref="ComparisonResult"/> containing all detected differences.
/// </summary>
public static class SchemaComparator
{
    public static ComparisonResult Compare(DatabaseSchema sideA, DatabaseSchema sideB)
    {
        var changes = new List<Change>();

        CompareTopLevel(sideA.Tables, sideB.Tables, ObjectType.Table, t => t.Id, t => t.Ddl, changes);
        CompareTopLevel(sideA.Views, sideB.Views, ObjectType.View, v => v.Id, v => v.Ddl, changes);
        CompareTopLevel(sideA.StoredProcedures, sideB.StoredProcedures, ObjectType.StoredProcedure, s => s.Id, s => s.Ddl, changes);
        CompareTopLevel(sideA.Functions, sideB.Functions, ObjectType.UserDefinedFunction, f => f.Id, f => f.Ddl, changes);
        CompareTopLevel(sideA.Sequences, sideB.Sequences, ObjectType.Sequence, s => s.Id, s => s.Ddl, changes);
        CompareTopLevel(sideA.Synonyms, sideB.Synonyms, ObjectType.Synonym, s => s.Id, s => s.Ddl, changes);
        CompareTopLevel(sideA.UserDefinedDataTypes, sideB.UserDefinedDataTypes, ObjectType.UserDefinedDataType, u => u.Id, u => u.Ddl, changes);
        CompareTopLevel(sideA.UserDefinedTableTypes, sideB.UserDefinedTableTypes, ObjectType.UserDefinedTableType, u => u.Id, u => u.Ddl, changes);

        // Schemas use Name as identity (no SchemaQualifiedName — they have no schema prefix)
        CompareSchemas(sideA.Schemas, sideB.Schemas, changes);

        return new ComparisonResult
        {
            SideA = sideA,
            SideB = sideB,
            Changes = changes,
        };
    }

    private static void CompareTopLevel<T>(
        IReadOnlyList<T> listA,
        IReadOnlyList<T> listB,
        ObjectType objectType,
        Func<T, SchemaQualifiedName> getId,
        Func<T, string> getDdl,
        List<Change> changes)
    {
        var dictA = listA.ToDictionary(getId);
        var dictB = listB.ToDictionary(getId);

        // New: in A but not in B
        foreach (var item in listA)
        {
            var id = getId(item);
            if (!dictB.ContainsKey(id))
            {
                changes.Add(MakeChange(id, objectType, ChangeStatus.New, getDdl(item), null));
            }
        }

        // Dropped: in B but not in A
        foreach (var item in listB)
        {
            var id = getId(item);
            if (!dictA.ContainsKey(id))
            {
                changes.Add(MakeChange(id, objectType, ChangeStatus.Dropped, null, getDdl(item)));
            }
        }

        // Modified: in both, but DDL differs
        foreach (var itemA in listA)
        {
            var id = getId(itemA);
            if (dictB.TryGetValue(id, out var itemB))
            {
                var ddlA = getDdl(itemA);
                var ddlB = getDdl(itemB);
                if (!string.Equals(ddlA, ddlB, StringComparison.Ordinal))
                {
                    changes.Add(MakeChange(id, objectType, ChangeStatus.Modified, ddlA, ddlB));
                }
            }
        }
    }

    private static void CompareSchemas(
        IReadOnlyList<SchemaModel> listA,
        IReadOnlyList<SchemaModel> listB,
        List<Change> changes)
    {
        var dictA = listA.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var dictB = listB.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var s in listA)
        {
            // Use a synthetic SchemaQualifiedName for schemas (schema = "", name = schema name)
            var id = SchemaQualifiedName.TopLevel(s.Name, s.Name);
            if (!dictB.ContainsKey(s.Name))
                changes.Add(MakeChange(id, ObjectType.Schema, ChangeStatus.New, s.Ddl, null));
        }

        foreach (var s in listB)
        {
            var id = SchemaQualifiedName.TopLevel(s.Name, s.Name);
            if (!dictA.ContainsKey(s.Name))
                changes.Add(MakeChange(id, ObjectType.Schema, ChangeStatus.Dropped, null, s.Ddl));
        }

        foreach (var sA in listA)
        {
            if (dictB.TryGetValue(sA.Name, out var sB))
            {
                if (!string.Equals(sA.Ddl, sB.Ddl, StringComparison.Ordinal))
                {
                    var id = SchemaQualifiedName.TopLevel(sA.Name, sA.Name);
                    changes.Add(MakeChange(id, ObjectType.Schema, ChangeStatus.Modified, sA.Ddl, sB.Ddl));
                }
            }
        }
    }

    private static Change MakeChange(
        SchemaQualifiedName id,
        ObjectType objectType,
        ChangeStatus status,
        string? ddlA,
        string? ddlB) => new()
    {
        Id = id,
        ObjectType = objectType,
        Status = status,
        DdlSideA = ddlA,
        DdlSideB = ddlB,
        Risk = RiskTier.Safe,  // Placeholder — RiskClassifier sets the real value
        Reasons = Array.Empty<RiskReason>(),
        ColumnChanges = Array.Empty<ColumnChange>(),
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: 15 passed (7 SchemaQualifiedName + 8 SchemaComparator), 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Comparison/SchemaComparator.cs tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs
git commit -m "feat(comparison): add SchemaComparator with top-level object matching"
```

---

## Task 3: ColumnComparator — column-level diffing within tables

**Files:**
- Create: `src/SQLParity.Core/Comparison/ColumnComparator.cs`
- Create: `tests/SQLParity.Core.Tests/Comparison/ColumnComparatorTests.cs`

When a table exists on both sides, we need to diff its columns to detect added, dropped, and modified columns. The `ColumnComparator` produces `ColumnChange` records. Risk classification for column changes is deferred to Task 6.

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Comparison/ColumnComparatorTests.cs`:

```csharp
using System;
using System.Linq;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class ColumnComparatorTests
{
    private static ColumnModel MakeColumn(string table, string name, string dataType = "Int",
        int maxLen = 0, bool nullable = false, bool identity = false,
        int ordinal = 0, string? collation = null, DefaultConstraintModel? defaultConstraint = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", table, name),
        Name = name,
        DataType = dataType,
        MaxLength = maxLen,
        Precision = 0,
        Scale = 0,
        IsNullable = nullable,
        IsIdentity = identity,
        IdentitySeed = 0,
        IdentityIncrement = 0,
        IsComputed = false,
        ComputedText = null,
        IsPersisted = false,
        Collation = collation,
        DefaultConstraint = defaultConstraint,
        OrdinalPosition = ordinal,
    };

    [Fact]
    public void IdenticalColumns_NoChanges()
    {
        var cols = new[] { MakeColumn("T", "Id"), MakeColumn("T", "Name", "NVarChar", 100) };

        var result = ColumnComparator.Compare("dbo", "T", cols, cols);

        Assert.Empty(result);
    }

    [Fact]
    public void NewColumn_DetectedAsNew()
    {
        var colsA = new[] { MakeColumn("T", "Id"), MakeColumn("T", "Name", "NVarChar", 100) };
        var colsB = new[] { MakeColumn("T", "Id") };

        var result = ColumnComparator.Compare("dbo", "T", colsA, colsB);

        var change = Assert.Single(result);
        Assert.Equal(ChangeStatus.New, change.Status);
        Assert.Equal("Name", change.ColumnName);
        Assert.NotNull(change.SideA);
        Assert.Null(change.SideB);
    }

    [Fact]
    public void DroppedColumn_DetectedAsDropped()
    {
        var colsA = new[] { MakeColumn("T", "Id") };
        var colsB = new[] { MakeColumn("T", "Id"), MakeColumn("T", "OldCol", "NVarChar", 50) };

        var result = ColumnComparator.Compare("dbo", "T", colsA, colsB);

        var change = Assert.Single(result);
        Assert.Equal(ChangeStatus.Dropped, change.Status);
        Assert.Equal("OldCol", change.ColumnName);
        Assert.Null(change.SideA);
        Assert.NotNull(change.SideB);
    }

    [Fact]
    public void ModifiedColumn_DifferentDataType_DetectedAsModified()
    {
        var colA = MakeColumn("T", "Price", "Decimal");
        var colB = MakeColumn("T", "Price", "Int");

        var result = ColumnComparator.Compare("dbo", "T", new[] { colA }, new[] { colB });

        var change = Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Equal("Price", change.ColumnName);
        Assert.NotNull(change.SideA);
        Assert.NotNull(change.SideB);
    }

    [Fact]
    public void ModifiedColumn_DifferentNullability_DetectedAsModified()
    {
        var colA = MakeColumn("T", "Name", "NVarChar", 100, nullable: true);
        var colB = MakeColumn("T", "Name", "NVarChar", 100, nullable: false);

        var result = ColumnComparator.Compare("dbo", "T", new[] { colA }, new[] { colB });

        var change = Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, change.Status);
    }

    [Fact]
    public void ModifiedColumn_DifferentMaxLength_DetectedAsModified()
    {
        var colA = MakeColumn("T", "Name", "NVarChar", 200);
        var colB = MakeColumn("T", "Name", "NVarChar", 100);

        var result = ColumnComparator.Compare("dbo", "T", new[] { colA }, new[] { colB });

        var change = Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, change.Status);
    }

    [Fact]
    public void UnchangedColumn_NotDetected()
    {
        var col = MakeColumn("T", "Id", "Int");

        var result = ColumnComparator.Compare("dbo", "T", new[] { col }, new[] { col });

        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure.

- [ ] **Step 3: Implement ColumnComparator**

Create `src/SQLParity.Core/Comparison/ColumnComparator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Compares columns between two versions of the same table. Produces
/// <see cref="ColumnChange"/> records for added, dropped, and modified columns.
/// </summary>
public static class ColumnComparator
{
    public static IReadOnlyList<ColumnChange> Compare(
        string tableSchema,
        string tableName,
        IReadOnlyList<ColumnModel> columnsA,
        IReadOnlyList<ColumnModel> columnsB)
    {
        var changes = new List<ColumnChange>();
        var dictA = columnsA.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var dictB = columnsB.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        // New: in A but not in B
        foreach (var col in columnsA)
        {
            if (!dictB.ContainsKey(col.Name))
            {
                changes.Add(new ColumnChange
                {
                    Id = SchemaQualifiedName.Child(tableSchema, tableName, col.Name),
                    ColumnName = col.Name,
                    Status = ChangeStatus.New,
                    SideA = col,
                    SideB = null,
                    Risk = RiskTier.Safe, // Placeholder — ColumnRiskClassifier sets real value
                    Reasons = Array.Empty<RiskReason>(),
                });
            }
        }

        // Dropped: in B but not in A
        foreach (var col in columnsB)
        {
            if (!dictA.ContainsKey(col.Name))
            {
                changes.Add(new ColumnChange
                {
                    Id = SchemaQualifiedName.Child(tableSchema, tableName, col.Name),
                    ColumnName = col.Name,
                    Status = ChangeStatus.Dropped,
                    SideA = null,
                    SideB = col,
                    Risk = RiskTier.Safe, // Placeholder
                    Reasons = Array.Empty<RiskReason>(),
                });
            }
        }

        // Modified: in both, but properties differ
        foreach (var colA in columnsA)
        {
            if (dictB.TryGetValue(colA.Name, out var colB))
            {
                if (ColumnsAreDifferent(colA, colB))
                {
                    changes.Add(new ColumnChange
                    {
                        Id = SchemaQualifiedName.Child(tableSchema, tableName, colA.Name),
                        ColumnName = colA.Name,
                        Status = ChangeStatus.Modified,
                        SideA = colA,
                        SideB = colB,
                        Risk = RiskTier.Safe, // Placeholder
                        Reasons = Array.Empty<RiskReason>(),
                    });
                }
            }
        }

        return changes;
    }

    private static bool ColumnsAreDifferent(ColumnModel a, ColumnModel b)
    {
        return !string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase)
            || a.MaxLength != b.MaxLength
            || a.Precision != b.Precision
            || a.Scale != b.Scale
            || a.IsNullable != b.IsNullable
            || a.IsIdentity != b.IsIdentity
            || a.IsComputed != b.IsComputed
            || !string.Equals(a.ComputedText, b.ComputedText, StringComparison.OrdinalIgnoreCase)
            || a.IsPersisted != b.IsPersisted
            || !string.Equals(a.Collation, b.Collation, StringComparison.OrdinalIgnoreCase)
            || DefaultsDiffer(a.DefaultConstraint, b.DefaultConstraint);
    }

    private static bool DefaultsDiffer(DefaultConstraintModel? a, DefaultConstraintModel? b)
    {
        if (a is null && b is null) return false;
        if (a is null || b is null) return true;
        return !string.Equals(a.Definition, b.Definition, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: 22 passed (7 + 8 + 7), 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Comparison/ColumnComparator.cs tests/SQLParity.Core.Tests/Comparison/ColumnComparatorTests.cs
git commit -m "feat(comparison): add ColumnComparator for column-level diffing"
```

---

## Task 4: Wire ColumnComparator into SchemaComparator for modified tables

**Files:**
- Modify: `src/SQLParity.Core/Comparison/SchemaComparator.cs`
- Modify: `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`

When a table is `Modified`, the SchemaComparator should also produce `ColumnChanges` by calling `ColumnComparator.Compare` on the two versions of the table's columns.

- [ ] **Step 1: Add a test for column changes on modified tables**

Append to `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void ModifiedTable_IncludesColumnChanges()
    {
        var colA = new ColumnModel
        {
            Id = SchemaQualifiedName.Child("dbo", "T", "Id"),
            Name = "Id", DataType = "Int", MaxLength = 0, Precision = 0, Scale = 0,
            IsNullable = false, IsIdentity = true, IdentitySeed = 1, IdentityIncrement = 1,
            IsComputed = false, ComputedText = null, IsPersisted = false, Collation = null,
            DefaultConstraint = null, OrdinalPosition = 0,
        };
        var colNew = new ColumnModel
        {
            Id = SchemaQualifiedName.Child("dbo", "T", "Name"),
            Name = "Name", DataType = "NVarChar", MaxLength = 100, Precision = 0, Scale = 0,
            IsNullable = true, IsIdentity = false, IdentitySeed = 0, IdentityIncrement = 0,
            IsComputed = false, ComputedText = null, IsPersisted = false, Collation = null,
            DefaultConstraint = null, OrdinalPosition = 1,
        };

        var tableA = MakeTable("dbo", "T", "DDL v1", new[] { colA, colNew });
        var tableB = MakeTable("dbo", "T", "DDL v2", new[] { colA });
        var a = EmptySchema() with { Tables = new[] { tableA } };
        var b = EmptySchema() with { Tables = new[] { tableB } };

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Single(change.ColumnChanges);
        Assert.Equal("Name", change.ColumnChanges[0].ColumnName);
        Assert.Equal(ChangeStatus.New, change.ColumnChanges[0].Status);
    }
```

- [ ] **Step 2: Update SchemaComparator to populate ColumnChanges for modified tables**

In `src/SQLParity.Core/Comparison/SchemaComparator.cs`, update the `CompareTopLevel` call for tables to use a specialized method. Replace the tables line:

```csharp
CompareTopLevel(sideA.Tables, sideB.Tables, ObjectType.Table, t => t.Id, t => t.Ddl, changes);
```

with:

```csharp
CompareTables(sideA.Tables, sideB.Tables, changes);
```

And add the `CompareTables` method:

```csharp
    private static void CompareTables(
        IReadOnlyList<TableModel> listA,
        IReadOnlyList<TableModel> listB,
        List<Change> changes)
    {
        var dictA = listA.ToDictionary(t => t.Id);
        var dictB = listB.ToDictionary(t => t.Id);

        foreach (var t in listA)
        {
            if (!dictB.ContainsKey(t.Id))
                changes.Add(MakeChange(t.Id, ObjectType.Table, ChangeStatus.New, t.Ddl, null));
        }

        foreach (var t in listB)
        {
            if (!dictA.ContainsKey(t.Id))
                changes.Add(MakeChange(t.Id, ObjectType.Table, ChangeStatus.Dropped, null, t.Ddl));
        }

        foreach (var tA in listA)
        {
            if (dictB.TryGetValue(tA.Id, out var tB))
            {
                if (!string.Equals(tA.Ddl, tB.Ddl, StringComparison.Ordinal))
                {
                    var columnChanges = ColumnComparator.Compare(tA.Schema, tA.Name, tA.Columns, tB.Columns);

                    changes.Add(new Change
                    {
                        Id = tA.Id,
                        ObjectType = ObjectType.Table,
                        Status = ChangeStatus.Modified,
                        DdlSideA = tA.Ddl,
                        DdlSideB = tB.Ddl,
                        Risk = RiskTier.Safe,
                        Reasons = Array.Empty<RiskReason>(),
                        ColumnChanges = columnChanges,
                    });
                }
            }
        }
    }
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: 23 passed, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Core/Comparison/SchemaComparator.cs tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs
git commit -m "feat(comparison): wire ColumnComparator into SchemaComparator for modified tables"
```

---

## Task 5: RiskClassifier — top-level object risk classification

**Files:**
- Create: `src/SQLParity.Core/Comparison/RiskClassifier.cs`
- Create: `tests/SQLParity.Core.Tests/Comparison/RiskClassifierTests.cs`

The Risk Classifier is a pure function: given a `Change`, it returns `(RiskTier, RiskReason[])`. This task covers top-level objects (tables, views, procs, functions, schemas, sequences, synonyms, UDTs, UDTTs). Column-level risk is covered in Task 6.

**Rules from the spec:**
- **New** anything → Safe
- **Dropped** table → Destructive ("Table will be dropped")
- **Dropped** view, proc, function, trigger, sequence, synonym, UDT, UDTT → Destructive ("Object will be removed")
- **Dropped** schema → Destructive
- **Dropped** index → Risky ("Index removal may impact performance")
- **Dropped** FK → Destructive ("Foreign key constraint will be removed")
- **Dropped** check constraint → Destructive ("Check constraint will be removed")
- **Modified** routine (view, proc, function) → Caution ("Routine definition changed; may affect consumers")
- **Modified** table → depends on column changes (handled in Task 6)
- **Modified** index → Caution ("Index definition changed; drop and recreate required")
- **Modified** anything else → Safe (definition change without risk)

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Comparison/RiskClassifierTests.cs`:

```csharp
using System;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class RiskClassifierTests
{
    private static Change MakeChange(ObjectType type, ChangeStatus status) => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", "TestObj"),
        ObjectType = type,
        Status = status,
        DdlSideA = status == ChangeStatus.Dropped ? null : "DDL A",
        DdlSideB = status == ChangeStatus.New ? null : "DDL B",
        Risk = RiskTier.Safe,
        Reasons = Array.Empty<RiskReason>(),
        ColumnChanges = Array.Empty<ColumnChange>(),
    };

    // --- New objects are all Safe ---

    [Theory]
    [InlineData(ObjectType.Table)]
    [InlineData(ObjectType.View)]
    [InlineData(ObjectType.StoredProcedure)]
    [InlineData(ObjectType.UserDefinedFunction)]
    [InlineData(ObjectType.Schema)]
    [InlineData(ObjectType.Sequence)]
    [InlineData(ObjectType.Synonym)]
    [InlineData(ObjectType.UserDefinedDataType)]
    [InlineData(ObjectType.UserDefinedTableType)]
    public void NewObject_IsSafe(ObjectType type)
    {
        var change = MakeChange(type, ChangeStatus.New);

        var (tier, reasons) = RiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Safe, tier);
    }

    // --- Dropped objects ---

    [Fact]
    public void DroppedTable_IsDestructive()
    {
        var change = MakeChange(ObjectType.Table, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Theory]
    [InlineData(ObjectType.View)]
    [InlineData(ObjectType.StoredProcedure)]
    [InlineData(ObjectType.UserDefinedFunction)]
    [InlineData(ObjectType.Sequence)]
    [InlineData(ObjectType.Synonym)]
    [InlineData(ObjectType.UserDefinedDataType)]
    [InlineData(ObjectType.UserDefinedTableType)]
    public void DroppedCodeObject_IsDestructive(ObjectType type)
    {
        var change = MakeChange(type, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedSchema_IsDestructive()
    {
        var change = MakeChange(ObjectType.Schema, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedIndex_IsRisky()
    {
        var change = MakeChange(ObjectType.Index, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DroppedForeignKey_IsDestructive()
    {
        var change = MakeChange(ObjectType.ForeignKey, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedCheckConstraint_IsDestructive()
    {
        var change = MakeChange(ObjectType.CheckConstraint, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedTrigger_IsDestructive()
    {
        var change = MakeChange(ObjectType.Trigger, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    // --- Modified objects ---

    [Theory]
    [InlineData(ObjectType.View)]
    [InlineData(ObjectType.StoredProcedure)]
    [InlineData(ObjectType.UserDefinedFunction)]
    public void ModifiedRoutine_IsCaution(ObjectType type)
    {
        var change = MakeChange(type, ChangeStatus.Modified);
        var (tier, reasons) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Caution, tier);
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void ModifiedIndex_IsCaution()
    {
        var change = MakeChange(ObjectType.Index, ChangeStatus.Modified);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void ClassifyReturnsReasons()
    {
        var change = MakeChange(ObjectType.Table, ChangeStatus.Dropped);
        var (_, reasons) = RiskClassifier.Classify(change);
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure.

- [ ] **Step 3: Implement RiskClassifier**

Create `src/SQLParity.Core/Comparison/RiskClassifier.cs`:

```csharp
using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Pure function that classifies a <see cref="Change"/> into a <see cref="RiskTier"/>
/// with explanatory reasons.
/// </summary>
public static class RiskClassifier
{
    public static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) Classify(Change change)
    {
        var reasons = new List<RiskReason>();

        if (change.Status == ChangeStatus.New)
        {
            reasons.Add(new RiskReason { Tier = RiskTier.Safe, Description = $"New {change.ObjectType} will be created." });
            return (RiskTier.Safe, reasons);
        }

        if (change.Status == ChangeStatus.Dropped)
        {
            return ClassifyDropped(change, reasons);
        }

        // Modified
        return ClassifyModified(change, reasons);
    }

    private static (RiskTier, IReadOnlyList<RiskReason>) ClassifyDropped(Change change, List<RiskReason> reasons)
    {
        switch (change.ObjectType)
        {
            case ObjectType.Index:
                reasons.Add(new RiskReason { Tier = RiskTier.Risky, Description = "Index removal may impact query performance." });
                return (RiskTier.Risky, reasons);

            case ObjectType.Table:
                reasons.Add(new RiskReason { Tier = RiskTier.Destructive, Description = "Table will be dropped. All data will be lost." });
                return (RiskTier.Destructive, reasons);

            case ObjectType.ForeignKey:
                reasons.Add(new RiskReason { Tier = RiskTier.Destructive, Description = "Foreign key constraint will be removed. Referential integrity will no longer be enforced." });
                return (RiskTier.Destructive, reasons);

            case ObjectType.CheckConstraint:
                reasons.Add(new RiskReason { Tier = RiskTier.Destructive, Description = "Check constraint will be removed. Data validation will no longer be enforced." });
                return (RiskTier.Destructive, reasons);

            default:
                // All other dropped objects (views, procs, functions, triggers, schemas, sequences, synonyms, UDTs, UDTTs)
                reasons.Add(new RiskReason { Tier = RiskTier.Destructive, Description = $"{change.ObjectType} will be removed." });
                return (RiskTier.Destructive, reasons);
        }
    }

    private static (RiskTier, IReadOnlyList<RiskReason>) ClassifyModified(Change change, List<RiskReason> reasons)
    {
        switch (change.ObjectType)
        {
            case ObjectType.View:
            case ObjectType.StoredProcedure:
            case ObjectType.UserDefinedFunction:
                reasons.Add(new RiskReason { Tier = RiskTier.Caution, Description = $"{change.ObjectType} definition changed. May affect dependent objects or consumers." });
                return (RiskTier.Caution, reasons);

            case ObjectType.Index:
                reasons.Add(new RiskReason { Tier = RiskTier.Caution, Description = "Index definition changed. Will require drop and recreate." });
                return (RiskTier.Caution, reasons);

            case ObjectType.Table:
                return ClassifyModifiedTable(change, reasons);

            default:
                reasons.Add(new RiskReason { Tier = RiskTier.Safe, Description = $"{change.ObjectType} definition changed." });
                return (RiskTier.Safe, reasons);
        }
    }

    private static (RiskTier, IReadOnlyList<RiskReason>) ClassifyModifiedTable(Change change, List<RiskReason> reasons)
    {
        // A modified table's risk is the highest risk among its column changes,
        // plus any sub-object changes (indexes, constraints, triggers).
        // Column risk classification is handled by ColumnRiskClassifier (Task 6).
        // For now, if there are column changes, the table inherits the max column risk.

        var maxTier = RiskTier.Safe;

        foreach (var colChange in change.ColumnChanges)
        {
            if (colChange.Risk > maxTier)
                maxTier = colChange.Risk;

            foreach (var reason in colChange.Reasons)
            {
                reasons.Add(reason);
            }
        }

        if (maxTier == RiskTier.Safe && change.ColumnChanges.Count == 0)
        {
            reasons.Add(new RiskReason { Tier = RiskTier.Safe, Description = "Table DDL changed without column modifications." });
        }

        return (maxTier, reasons);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~40 total).

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Comparison/RiskClassifier.cs tests/SQLParity.Core.Tests/Comparison/RiskClassifierTests.cs
git commit -m "feat(comparison): add RiskClassifier for top-level object risk classification"
```

---

## Task 6: ColumnRiskClassifier — column-level risk classification

**Files:**
- Create: `src/SQLParity.Core/Comparison/ColumnRiskClassifier.cs`
- Create: `tests/SQLParity.Core.Tests/Comparison/ColumnRiskClassifierTests.cs`

**Rules from the spec:**
- **New nullable column** → Safe
- **New NOT NULL column with default** → Caution (lock on large tables)
- **New NOT NULL column without default** → Risky (will fail if table has rows)
- **Dropped column** → Destructive
- **Widened column** (maxLength increased, same type) → Caution
- **Narrowed column** (maxLength decreased) → Risky (pre-flight determines if data fits; promoted to Destructive by pre-flight if not)
- **Data type changed** → Risky
- **Collation changed** → Risky
- **Default added/changed/removed** → Risky (changing a default) or Safe (adding one)
- **Nullability changed: nullable→NOT NULL** → Caution (may fail if NULLs exist)
- **Nullability changed: NOT NULL→nullable** → Safe

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Comparison/ColumnRiskClassifierTests.cs`:

```csharp
using System;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class ColumnRiskClassifierTests
{
    private static ColumnModel MakeCol(string name = "Col", string type = "Int", int maxLen = 0,
        bool nullable = false, DefaultConstraintModel? dc = null, string? collation = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "T", name),
        Name = name, DataType = type, MaxLength = maxLen, Precision = 0, Scale = 0,
        IsNullable = nullable, IsIdentity = false, IdentitySeed = 0, IdentityIncrement = 0,
        IsComputed = false, ComputedText = null, IsPersisted = false, Collation = collation,
        DefaultConstraint = dc, OrdinalPosition = 0,
    };

    private static ColumnChange MakeColumnChange(ChangeStatus status, ColumnModel? sideA, ColumnModel? sideB) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "T", sideA?.Name ?? sideB?.Name ?? "Col"),
        ColumnName = sideA?.Name ?? sideB?.Name ?? "Col",
        Status = status,
        SideA = sideA,
        SideB = sideB,
        Risk = RiskTier.Safe,
        Reasons = Array.Empty<RiskReason>(),
    };

    [Fact]
    public void NewNullableColumn_IsSafe()
    {
        var col = MakeCol(nullable: true);
        var change = MakeColumnChange(ChangeStatus.New, col, null);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Safe, tier);
    }

    [Fact]
    public void NewNotNullColumnWithDefault_IsCaution()
    {
        var col = MakeCol(nullable: false, dc: new DefaultConstraintModel { Name = "DF", Definition = "(0)" });
        var change = MakeColumnChange(ChangeStatus.New, col, null);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void NewNotNullColumnWithoutDefault_IsRisky()
    {
        var col = MakeCol(nullable: false);
        var change = MakeColumnChange(ChangeStatus.New, col, null);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DroppedColumn_IsDestructive()
    {
        var col = MakeCol();
        var change = MakeColumnChange(ChangeStatus.Dropped, null, col);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void WidenedColumn_IsCaution()
    {
        var colA = MakeCol(type: "NVarChar", maxLen: 200);
        var colB = MakeCol(type: "NVarChar", maxLen: 100);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void NarrowedColumn_IsRisky()
    {
        var colA = MakeCol(type: "NVarChar", maxLen: 50);
        var colB = MakeCol(type: "NVarChar", maxLen: 100);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DataTypeChanged_IsRisky()
    {
        var colA = MakeCol(type: "Decimal");
        var colB = MakeCol(type: "Int");
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void CollationChanged_IsRisky()
    {
        var colA = MakeCol(type: "NVarChar", maxLen: 100, collation: "Latin1_General_CI_AS");
        var colB = MakeCol(type: "NVarChar", maxLen: 100, collation: "SQL_Latin1_General_CP1_CI_AS");
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void NullableToNotNull_IsCaution()
    {
        var colA = MakeCol(type: "Int", nullable: false);
        var colB = MakeCol(type: "Int", nullable: true);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void NotNullToNullable_IsSafe()
    {
        var colA = MakeCol(type: "Int", nullable: true);
        var colB = MakeCol(type: "Int", nullable: false);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Safe, tier);
    }

    [Fact]
    public void DefaultChanged_IsRisky()
    {
        var colA = MakeCol(dc: new DefaultConstraintModel { Name = "DF", Definition = "(1)" });
        var colB = MakeCol(dc: new DefaultConstraintModel { Name = "DF", Definition = "(0)" });
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DefaultAdded_IsSafe()
    {
        var colA = MakeCol(dc: new DefaultConstraintModel { Name = "DF", Definition = "(0)" });
        var colB = MakeCol();
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Safe, tier);
    }

    [Fact]
    public void DefaultRemoved_IsRisky()
    {
        var colA = MakeCol();
        var colB = MakeCol(dc: new DefaultConstraintModel { Name = "DF", Definition = "(0)" });
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var (tier, _) = ColumnRiskClassifier.Classify(change);

        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void ClassifyAlwaysReturnsReasons()
    {
        var col = MakeCol();
        var change = MakeColumnChange(ChangeStatus.Dropped, null, col);

        var (_, reasons) = ColumnRiskClassifier.Classify(change);

        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure.

- [ ] **Step 3: Implement ColumnRiskClassifier**

Create `src/SQLParity.Core/Comparison/ColumnRiskClassifier.cs`:

```csharp
using System;
using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Pure function that classifies a <see cref="ColumnChange"/> into a
/// <see cref="RiskTier"/> with explanatory reasons.
/// </summary>
public static class ColumnRiskClassifier
{
    public static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) Classify(ColumnChange change)
    {
        var reasons = new List<RiskReason>();

        if (change.Status == ChangeStatus.New)
            return ClassifyNewColumn(change, reasons);

        if (change.Status == ChangeStatus.Dropped)
            return ClassifyDroppedColumn(change, reasons);

        return ClassifyModifiedColumn(change, reasons);
    }

    private static (RiskTier, IReadOnlyList<RiskReason>) ClassifyNewColumn(ColumnChange change, List<RiskReason> reasons)
    {
        var col = change.SideA!;

        if (col.IsNullable)
        {
            reasons.Add(new RiskReason { Tier = RiskTier.Safe, Description = $"New nullable column '{col.Name}' will be added." });
            return (RiskTier.Safe, reasons);
        }

        if (col.DefaultConstraint is not null)
        {
            reasons.Add(new RiskReason { Tier = RiskTier.Caution, Description = $"New NOT NULL column '{col.Name}' with default. May take a lock on large tables." });
            return (RiskTier.Caution, reasons);
        }

        reasons.Add(new RiskReason { Tier = RiskTier.Risky, Description = $"New NOT NULL column '{col.Name}' without a default. Will fail if the table contains rows." });
        return (RiskTier.Risky, reasons);
    }

    private static (RiskTier, IReadOnlyList<RiskReason>) ClassifyDroppedColumn(ColumnChange change, List<RiskReason> reasons)
    {
        var col = change.SideB!;
        reasons.Add(new RiskReason { Tier = RiskTier.Destructive, Description = $"Column '{col.Name}' will be dropped. Data in this column will be lost." });
        return (RiskTier.Destructive, reasons);
    }

    private static (RiskTier, IReadOnlyList<RiskReason>) ClassifyModifiedColumn(ColumnChange change, List<RiskReason> reasons)
    {
        var colA = change.SideA!;
        var colB = change.SideB!;
        var maxTier = RiskTier.Safe;

        // Data type change
        if (!string.Equals(colA.DataType, colB.DataType, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(new RiskReason { Tier = RiskTier.Risky, Description = $"Column '{colA.Name}' data type changes from '{colB.DataType}' to '{colA.DataType}'. May require data conversion." });
            maxTier = Max(maxTier, RiskTier.Risky);
        }
        else if (colA.MaxLength != colB.MaxLength)
        {
            // Same type, different length
            if (colA.MaxLength > colB.MaxLength)
            {
                reasons.Add(new RiskReason { Tier = RiskTier.Caution, Description = $"Column '{colA.Name}' widened from {colB.MaxLength} to {colA.MaxLength}." });
                maxTier = Max(maxTier, RiskTier.Caution);
            }
            else
            {
                reasons.Add(new RiskReason { Tier = RiskTier.Risky, Description = $"Column '{colA.Name}' narrowed from {colB.MaxLength} to {colA.MaxLength}. Existing data may be truncated." });
                maxTier = Max(maxTier, RiskTier.Risky);
            }
        }

        // Nullability change
        if (colA.IsNullable != colB.IsNullable)
        {
            if (!colA.IsNullable && colB.IsNullable)
            {
                // nullable → NOT NULL
                reasons.Add(new RiskReason { Tier = RiskTier.Caution, Description = $"Column '{colA.Name}' changed from nullable to NOT NULL. May fail if NULL values exist." });
                maxTier = Max(maxTier, RiskTier.Caution);
            }
            else
            {
                // NOT NULL → nullable
                reasons.Add(new RiskReason { Tier = RiskTier.Safe, Description = $"Column '{colA.Name}' changed from NOT NULL to nullable." });
            }
        }

        // Collation change
        if (!string.Equals(colA.Collation, colB.Collation, StringComparison.OrdinalIgnoreCase)
            && colA.Collation is not null && colB.Collation is not null)
        {
            reasons.Add(new RiskReason { Tier = RiskTier.Risky, Description = $"Column '{colA.Name}' collation changes from '{colB.Collation}' to '{colA.Collation}'." });
            maxTier = Max(maxTier, RiskTier.Risky);
        }

        // Default constraint change
        var dcA = colA.DefaultConstraint;
        var dcB = colB.DefaultConstraint;
        if (dcA is not null && dcB is null)
        {
            // Default added
            reasons.Add(new RiskReason { Tier = RiskTier.Safe, Description = $"Default constraint added to column '{colA.Name}'." });
        }
        else if (dcA is null && dcB is not null)
        {
            // Default removed
            reasons.Add(new RiskReason { Tier = RiskTier.Risky, Description = $"Default constraint removed from column '{colA.Name}'." });
            maxTier = Max(maxTier, RiskTier.Risky);
        }
        else if (dcA is not null && dcB is not null
            && !string.Equals(dcA.Definition, dcB.Definition, StringComparison.OrdinalIgnoreCase))
        {
            // Default changed
            reasons.Add(new RiskReason { Tier = RiskTier.Risky, Description = $"Default constraint on column '{colA.Name}' changed from '{dcB.Definition}' to '{dcA.Definition}'." });
            maxTier = Max(maxTier, RiskTier.Risky);
        }

        if (reasons.Count == 0)
        {
            reasons.Add(new RiskReason { Tier = RiskTier.Safe, Description = $"Column '{colA.Name}' definition changed." });
        }

        return (maxTier, reasons);
    }

    private static RiskTier Max(RiskTier a, RiskTier b) => a > b ? a : b;
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~55 total).

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Comparison/ColumnRiskClassifier.cs tests/SQLParity.Core.Tests/Comparison/ColumnRiskClassifierTests.cs
git commit -m "feat(comparison): add ColumnRiskClassifier for column-level risk classification"
```

---

## Task 7: Wire risk classification into comparator pipeline

**Files:**
- Modify: `src/SQLParity.Core/Comparison/SchemaComparator.cs`
- Modify: `src/SQLParity.Core/Comparison/ColumnComparator.cs`
- Modify: `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`

After comparison, run the Risk Classifier on each change and the Column Risk Classifier on each column change. The comparator should set the real `Risk` and `Reasons` on each `Change` and `ColumnChange`.

- [ ] **Step 1: Add a test that verifies risk classification is wired in**

Append to `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`:

```csharp
    [Fact]
    public void DroppedTable_HasDestructiveRisk()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema();
        var b = EmptySchema() with { Tables = new[] { table } };

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(RiskTier.Destructive, change.Risk);
        Assert.NotEmpty(change.Reasons);
    }

    [Fact]
    public void NewTable_HasSafeRisk()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema() with { Tables = new[] { table } };
        var b = EmptySchema();

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(RiskTier.Safe, change.Risk);
    }

    [Fact]
    public void ModifiedTable_DroppedColumn_HasDestructiveRisk()
    {
        var col = new ColumnModel
        {
            Id = SchemaQualifiedName.Child("dbo", "T", "OldCol"),
            Name = "OldCol", DataType = "Int", MaxLength = 0, Precision = 0, Scale = 0,
            IsNullable = false, IsIdentity = false, IdentitySeed = 0, IdentityIncrement = 0,
            IsComputed = false, ComputedText = null, IsPersisted = false, Collation = null,
            DefaultConstraint = null, OrdinalPosition = 0,
        };

        var tableA = MakeTable("dbo", "T", "DDL v1");
        var tableB = MakeTable("dbo", "T", "DDL v2", new[] { col });
        var a = EmptySchema() with { Tables = new[] { tableA } };
        var b = EmptySchema() with { Tables = new[] { tableB } };

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(RiskTier.Destructive, change.Risk);
        Assert.Single(change.ColumnChanges);
        Assert.Equal(RiskTier.Destructive, change.ColumnChanges[0].Risk);
    }
```

- [ ] **Step 2: Update SchemaComparator to run risk classification**

In `src/SQLParity.Core/Comparison/SchemaComparator.cs`, update the `Compare` method. After building the changes list, run the classifiers:

Replace the return statement at the end of `Compare` with:

```csharp
        // Run risk classification on all changes
        for (int i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var (tier, reasons) = RiskClassifier.Classify(change);
            changes[i] = change with { Risk = tier, Reasons = reasons };
        }

        return new ComparisonResult
        {
            SideA = sideA,
            SideB = sideB,
            Changes = changes,
        };
```

**Wait — `Change` is a `sealed class`, not a `record`, so `with` won't work.** Instead, create a new `Change` or make `Risk` and `Reasons` settable. The simplest approach: change `Risk` and `Reasons` properties on `Change` from `required init` to `{ get; set; }`:

In `src/SQLParity.Core/Model/Change.cs`, change:
```csharp
    public required RiskTier Risk { get; init; }
    public required IReadOnlyList<RiskReason> Reasons { get; init; }
```
to:
```csharp
    public RiskTier Risk { get; set; }
    public IReadOnlyList<RiskReason> Reasons { get; set; } = Array.Empty<RiskReason>();
```

And similarly in `src/SQLParity.Core/Model/ColumnChange.cs`, change:
```csharp
    public required RiskTier Risk { get; init; }
    public required IReadOnlyList<RiskReason> Reasons { get; init; }
```
to:
```csharp
    public RiskTier Risk { get; set; }
    public IReadOnlyList<RiskReason> Reasons { get; set; } = Array.Empty<RiskReason>();
```

Then update `SchemaComparator.Compare` to run classification after building changes:

```csharp
        // Run risk classification
        foreach (var change in changes)
        {
            // Classify column changes first (for modified tables)
            for (int i = 0; i < change.ColumnChanges.Count; i++)
            {
                var colChange = change.ColumnChanges[i];
                var (colTier, colReasons) = ColumnRiskClassifier.Classify(colChange);
                colChange.Risk = colTier;
                colChange.Reasons = colReasons;
            }

            var (tier, reasons) = RiskClassifier.Classify(change);
            change.Risk = tier;
            change.Reasons = reasons;
        }

        return new ComparisonResult
        {
            SideA = sideA,
            SideB = sideB,
            Changes = changes,
        };
```

Also update `ColumnComparator.cs` — remove the `Risk = RiskTier.Safe` and `Reasons = Array.Empty<RiskReason>()` initializers in each `ColumnChange` construction since they're now auto-initialized.

And update `MakeChange` in `SchemaComparator.cs` similarly — remove the `Risk` and `Reasons` assignments since they're auto-initialized and will be set by the classifier.

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~58 total).

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Core/ tests/SQLParity.Core.Tests/
git commit -m "feat(comparison): wire risk classification into comparator pipeline"
```

---

## Task 8: PreFlightQueryBuilder — build impact-quantification SQL

**Files:**
- Create: `src/SQLParity.Core/Comparison/PreFlightQueryBuilder.cs`
- Create: `tests/SQLParity.Core.Tests/Comparison/PreFlightQueryBuilderTests.cs`

Builds read-only SQL queries to quantify the impact of Risky and Destructive changes. Does NOT execute them. Produces `(string sql, string description)` pairs.

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Comparison/PreFlightQueryBuilderTests.cs`:

```csharp
using System;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class PreFlightQueryBuilderTests
{
    [Fact]
    public void DroppedTable_ProducesRowCountQuery()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "Orders"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.Dropped,
            DdlSideA = null,
            DdlSideB = "CREATE TABLE ...",
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var result = PreFlightQueryBuilder.Build(change);

        Assert.NotNull(result);
        Assert.Contains("SELECT COUNT(*)", result.Value.Sql);
        Assert.Contains("[dbo].[Orders]", result.Value.Sql);
        Assert.Contains("rows", result.Value.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DroppedColumn_ProducesNonNullCountQuery()
    {
        var colChange = new ColumnChange
        {
            Id = SchemaQualifiedName.Child("dbo", "Orders", "Notes"),
            ColumnName = "Notes",
            Status = ChangeStatus.Dropped,
            SideA = null,
            SideB = new ColumnModel
            {
                Id = SchemaQualifiedName.Child("dbo", "Orders", "Notes"),
                Name = "Notes", DataType = "NVarChar", MaxLength = 500,
                Precision = 0, Scale = 0, IsNullable = true, IsIdentity = false,
                IdentitySeed = 0, IdentityIncrement = 0, IsComputed = false,
                ComputedText = null, IsPersisted = false, Collation = null,
                DefaultConstraint = null, OrdinalPosition = 0,
            },
            Risk = RiskTier.Destructive,
        };

        var result = PreFlightQueryBuilder.BuildForColumn("dbo", "Orders", colChange);

        Assert.NotNull(result);
        Assert.Contains("SELECT COUNT(*)", result.Value.Sql);
        Assert.Contains("[Notes]", result.Value.Sql);
        Assert.Contains("IS NOT NULL", result.Value.Sql);
    }

    [Fact]
    public void NarrowedColumn_ProducesLengthCheckQuery()
    {
        var colChange = new ColumnChange
        {
            Id = SchemaQualifiedName.Child("dbo", "Orders", "Name"),
            ColumnName = "Name",
            Status = ChangeStatus.Modified,
            SideA = new ColumnModel
            {
                Id = SchemaQualifiedName.Child("dbo", "Orders", "Name"),
                Name = "Name", DataType = "NVarChar", MaxLength = 50,
                Precision = 0, Scale = 0, IsNullable = false, IsIdentity = false,
                IdentitySeed = 0, IdentityIncrement = 0, IsComputed = false,
                ComputedText = null, IsPersisted = false, Collation = null,
                DefaultConstraint = null, OrdinalPosition = 0,
            },
            SideB = new ColumnModel
            {
                Id = SchemaQualifiedName.Child("dbo", "Orders", "Name"),
                Name = "Name", DataType = "NVarChar", MaxLength = 100,
                Precision = 0, Scale = 0, IsNullable = false, IsIdentity = false,
                IdentitySeed = 0, IdentityIncrement = 0, IsComputed = false,
                ComputedText = null, IsPersisted = false, Collation = null,
                DefaultConstraint = null, OrdinalPosition = 0,
            },
            Risk = RiskTier.Risky,
        };

        var result = PreFlightQueryBuilder.BuildForColumn("dbo", "Orders", colChange);

        Assert.NotNull(result);
        Assert.Contains("LEN(", result.Value.Sql);
        Assert.Contains("50", result.Value.Sql);
    }

    [Fact]
    public void SafeChange_ReturnsNull()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "Orders"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE ...",
            DdlSideB = null,
            Risk = RiskTier.Safe,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var result = PreFlightQueryBuilder.Build(change);

        Assert.Null(result);
    }

    [Fact]
    public void DroppedForeignKey_ProducesChildRowCountQuery()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.Child("dbo", "Orders", "FK_Orders_Categories"),
            ObjectType = ObjectType.ForeignKey,
            Status = ChangeStatus.Dropped,
            DdlSideA = null,
            DdlSideB = "ALTER TABLE ...",
            Risk = RiskTier.Destructive,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var result = PreFlightQueryBuilder.Build(change);

        Assert.NotNull(result);
        Assert.Contains("referential integrity", result.Value.Description, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure.

- [ ] **Step 3: Implement PreFlightQueryBuilder**

Create `src/SQLParity.Core/Comparison/PreFlightQueryBuilder.cs`:

```csharp
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Builds read-only SQL queries that quantify the impact of Risky and
/// Destructive changes. Does NOT execute them — execution is the
/// responsibility of the caller (e.g., the VSIX shell or Live Applier).
/// </summary>
public static class PreFlightQueryBuilder
{
    public readonly record struct PreFlightQuery(string Sql, string Description);

    /// <summary>
    /// Builds a pre-flight query for a top-level change. Returns null if
    /// no pre-flight check is applicable (e.g., Safe changes).
    /// </summary>
    public static PreFlightQuery? Build(Change change)
    {
        if (change.Risk == RiskTier.Safe || change.Risk == RiskTier.Caution)
            return null;

        return change switch
        {
            { ObjectType: ObjectType.Table, Status: ChangeStatus.Dropped }
                => new PreFlightQuery(
                    $"SELECT COUNT(*) FROM [{change.Id.Schema}].[{change.Id.Name}]",
                    $"Count rows in table [{change.Id.Schema}].[{change.Id.Name}] that will be lost."),

            { ObjectType: ObjectType.ForeignKey, Status: ChangeStatus.Dropped }
                => new PreFlightQuery(
                    $"-- Foreign key [{change.Id.Name}] on [{change.Id.Schema}].[{change.Id.Parent}] will be removed.",
                    $"Foreign key [{change.Id.Name}] will be removed. Referential integrity will no longer be enforced."),

            { ObjectType: ObjectType.CheckConstraint, Status: ChangeStatus.Dropped }
                => new PreFlightQuery(
                    $"-- Check constraint [{change.Id.Name}] on [{change.Id.Schema}].[{change.Id.Parent}] will be removed.",
                    $"Check constraint [{change.Id.Name}] will be removed. Data validation will no longer be enforced."),

            _ => null,
        };
    }

    /// <summary>
    /// Builds a pre-flight query for a column-level change. Returns null if
    /// no pre-flight check is applicable.
    /// </summary>
    public static PreFlightQuery? BuildForColumn(string tableSchema, string tableName, ColumnChange colChange)
    {
        if (colChange.Risk == RiskTier.Safe || colChange.Risk == RiskTier.Caution)
            return null;

        if (colChange.Status == ChangeStatus.Dropped)
        {
            return new PreFlightQuery(
                $"SELECT COUNT(*) FROM [{tableSchema}].[{tableName}] WHERE [{colChange.ColumnName}] IS NOT NULL",
                $"Count rows with non-null values in column [{colChange.ColumnName}] that will be lost.");
        }

        if (colChange.Status == ChangeStatus.Modified && colChange.SideA is not null && colChange.SideB is not null)
        {
            // Narrowed column — check for data that would be truncated
            if (string.Equals(colChange.SideA.DataType, colChange.SideB.DataType, System.StringComparison.OrdinalIgnoreCase)
                && colChange.SideA.MaxLength < colChange.SideB.MaxLength
                && colChange.SideA.MaxLength > 0)
            {
                return new PreFlightQuery(
                    $"SELECT COUNT(*) FROM [{tableSchema}].[{tableName}] WHERE LEN([{colChange.ColumnName}]) > {colChange.SideA.MaxLength}",
                    $"Count rows where [{colChange.ColumnName}] exceeds the new max length of {colChange.SideA.MaxLength}.");
            }

            // Data type change — generic count of non-null values
            if (!string.Equals(colChange.SideA.DataType, colChange.SideB.DataType, System.StringComparison.OrdinalIgnoreCase))
            {
                return new PreFlightQuery(
                    $"SELECT COUNT(*) FROM [{tableSchema}].[{tableName}] WHERE [{colChange.ColumnName}] IS NOT NULL",
                    $"Count rows with non-null values in [{colChange.ColumnName}] that will be converted from {colChange.SideB.DataType} to {colChange.SideA.DataType}.");
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~63 total).

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Comparison/PreFlightQueryBuilder.cs tests/SQLParity.Core.Tests/Comparison/PreFlightQueryBuilderTests.cs
git commit -m "feat(comparison): add PreFlightQueryBuilder for impact quantification SQL"
```

---

## Task 9: Wire PreFlightQueryBuilder into comparator pipeline

**Files:**
- Modify: `src/SQLParity.Core/Comparison/SchemaComparator.cs`
- Modify: `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`

After risk classification, run the PreFlightQueryBuilder on Risky and Destructive changes and attach the queries.

- [ ] **Step 1: Add a test**

Append to `tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs`:

```csharp
    [Fact]
    public void DroppedTable_HasPreFlightQuery()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema();
        var b = EmptySchema() with { Tables = new[] { table } };

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.NotNull(change.PreFlightSql);
        Assert.Contains("SELECT COUNT(*)", change.PreFlightSql);
    }
```

- [ ] **Step 2: Update SchemaComparator to attach pre-flight queries**

In `src/SQLParity.Core/Comparison/SchemaComparator.cs`, after the risk classification loop, add:

```csharp
        // Attach pre-flight queries for Risky and Destructive changes
        foreach (var change in changes)
        {
            var pf = PreFlightQueryBuilder.Build(change);
            if (pf is not null)
            {
                change.PreFlightSql = pf.Value.Sql;
                change.PreFlightDescription = pf.Value.Description;
            }

            // Column-level pre-flight
            if (change.ObjectType == ObjectType.Table)
            {
                foreach (var colChange in change.ColumnChanges)
                {
                    var cpf = PreFlightQueryBuilder.BuildForColumn(
                        change.Id.Schema, change.Id.Name, colChange);
                    if (cpf is not null)
                    {
                        colChange.PreFlightSql = cpf.Value.Sql;
                        colChange.PreFlightDescription = cpf.Value.Description;
                    }
                }
            }
        }
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~64 total).

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Core/Comparison/SchemaComparator.cs tests/SQLParity.Core.Tests/Comparison/SchemaComparatorTests.cs
git commit -m "feat(comparison): wire PreFlightQueryBuilder into comparator pipeline"
```

---

## Task 10: Integration test — end-to-end comparison of two real databases

**Files:**
- Create: `tests/SQLParity.Core.IntegrationTests/ComparatorIntegrationTests.cs`

This test creates two throwaway databases with known schema differences and verifies the full pipeline: SchemaReader → SchemaComparator → risk-classified ComparisonResult.

- [ ] **Step 1: Create the integration test**

Create `tests/SQLParity.Core.IntegrationTests/ComparatorIntegrationTests.cs`:

```csharp
using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class ComparatorSideAFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[Products] (
    [ProductId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(200) NOT NULL,
    [Price] DECIMAL(18,2) NOT NULL,
    [Description] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([ProductId])
)
GO

CREATE VIEW [dbo].[ExpensiveProducts]
AS
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [Price] > 100
GO
";
}

public sealed class ComparatorSideBFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[Products] (
    [ProductId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(100) NOT NULL,
    [Price] DECIMAL(18,2) NOT NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([ProductId])
)
GO

CREATE TABLE [dbo].[OldTable] (
    [Id] INT NOT NULL PRIMARY KEY
)
GO
";
}

public class ComparatorIntegrationTests : IClassFixture<ComparatorSideAFixture>, IClassFixture<ComparatorSideBFixture>
{
    private readonly ComparisonResult _result;

    public ComparatorIntegrationTests(ComparatorSideAFixture sideA, ComparatorSideBFixture sideB)
    {
        var schemaA = new SchemaReader(sideA.ConnectionString, sideA.DatabaseName).ReadSchema();
        var schemaB = new SchemaReader(sideB.ConnectionString, sideB.DatabaseName).ReadSchema();
        _result = SchemaComparator.Compare(schemaA, schemaB);
    }

    [Fact]
    public void DetectsNewView()
    {
        var viewChange = _result.Changes.SingleOrDefault(
            c => c.ObjectType == ObjectType.View && c.Status == ChangeStatus.New);
        Assert.NotNull(viewChange);
        Assert.Equal("ExpensiveProducts", viewChange.Id.Name);
        Assert.Equal(RiskTier.Safe, viewChange.Risk);
    }

    [Fact]
    public void DetectsDroppedTable()
    {
        var dropChange = _result.Changes.SingleOrDefault(
            c => c.ObjectType == ObjectType.Table && c.Status == ChangeStatus.Dropped && c.Id.Name == "OldTable");
        Assert.NotNull(dropChange);
        Assert.Equal(RiskTier.Destructive, dropChange.Risk);
        Assert.NotNull(dropChange.PreFlightSql);
    }

    [Fact]
    public void DetectsModifiedTable_WithColumnChanges()
    {
        var modChange = _result.Changes.SingleOrDefault(
            c => c.ObjectType == ObjectType.Table && c.Status == ChangeStatus.Modified && c.Id.Name == "Products");
        Assert.NotNull(modChange);

        // SideA has Description column (new), and Name is widened (200 vs 100)
        Assert.True(modChange.ColumnChanges.Count > 0);
    }

    [Fact]
    public void ComparisonResult_HasTierCounts()
    {
        Assert.True(_result.TotalCount > 0);
    }
}
```

- [ ] **Step 2: Run the full test suite**

```bash
dotnet test SQLParity.sln
```

Expected: all tests pass — unit tests + integration tests including the new comparator tests.

- [ ] **Step 3: Commit**

```bash
git add tests/SQLParity.Core.IntegrationTests/ComparatorIntegrationTests.cs
git commit -m "test: add end-to-end comparator integration test with two real databases"
```

---

## Task 11: Final verification and tag

**Files:** none (verification only)

- [ ] **Step 1: Full build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj --no-incremental
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: Full test run**

```bash
dotnet test SQLParity.sln
```

Expected: all tests pass. Record the exact count.

- [ ] **Step 3: Verify no placeholders remain**

```bash
grep -r "TBD\|TODO\|PLACEHOLDER" src/SQLParity.Core/*.cs src/SQLParity.Core/**/*.cs
```

Expected: no output.

- [ ] **Step 4: Verify clean git status**

```bash
git status
```

Expected: clean working tree.

- [ ] **Step 5: Verify commit history**

```bash
git log --oneline plan-1-complete..HEAD
```

Expected: ~10-11 commits since Plan 1.

- [ ] **Step 6: Tag**

```bash
git tag plan-2-complete
```

---

## Plan 2 Acceptance Criteria

- ✅ `SQLParity.Core` builds cleanly (0 warnings, 0 errors)
- ✅ `SchemaComparator.Compare()` detects New, Modified, and Dropped objects across all supported types
- ✅ Modified tables include `ColumnChanges` with column-level diffs
- ✅ `RiskClassifier` classifies every `Change` per the spec's four-tier system
- ✅ `ColumnRiskClassifier` classifies column changes (new nullable/not-null, dropped, widened, narrowed, type change, collation change, nullability change, default change)
- ✅ `PreFlightQueryBuilder` produces impact SQL for Risky and Destructive changes
- ✅ Risk classification and pre-flight queries are wired into the comparator pipeline
- ✅ End-to-end integration test verifies the full pipeline against real databases
- ✅ Git history is clean and tagged `plan-2-complete`

---

## What Plan 3 inherits from Plan 2

- A complete comparison pipeline: `SchemaReader` → `SchemaComparator` → risk-classified `ComparisonResult` with pre-flight queries.
- Pure-function classifiers that can be unit-tested without a database.
- Pre-flight queries ready to be executed against a real database (the caller runs them).
- Plan 3 (Script Generator & Live Applier) consumes `ComparisonResult` and its `Change` list to produce dependency-ordered sync scripts.
