# SQLParity — Plan 3: Script Generator, Live Applier, History Writer

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Given a `ComparisonResult` with selected `Change` records, produce a dependency-ordered SQL sync script with an informative header banner, and optionally apply those changes live (one transaction per change, stop on failure). Auto-save every generated script and apply record to a timestamped history folder.

**Architecture:** Three components in `SQLParity.Core`:
1. **DependencyOrderer** — sorts a list of changes into a safe execution order (schemas first, then tables, then child objects; drops in reverse order).
2. **ScriptGenerator** — takes ordered changes + metadata (labels, server names, etc.) and produces a complete SQL script string with a header banner.
3. **LiveApplier** — executes changes one-by-one against a target database, each in its own transaction, stopping on first failure and reporting results.
4. **HistoryWriter** — auto-saves scripts and apply results to a timestamped `history/` folder.

**Tech Stack:** C#, xUnit, `Microsoft.Data.SqlClient` for live apply, LocalDB for integration tests.

**Spec reference:** [design spec §5 (Script generation details)](../specs/2026-04-09-sqlparity-design.md) and [§6 (Persistence — history)](../specs/2026-04-09-sqlparity-design.md).

**What Plan 3 inherits from Plan 2:**
- `ComparisonResult` with risk-classified `Change` records
- `Change` has `DdlSideA`, `DdlSideB`, `ObjectType`, `Status`, `Risk`, `ColumnChanges`
- `ObjectType` enum, `ChangeStatus` enum, `RiskTier` enum
- 99 passing tests (73 unit + 26 integration)

---

## File Structure

```
src/SQLParity.Core/
  Sync/
    DependencyOrderer.cs                Sorts changes into safe execution order
    SyncScript.cs                       Data class: the generated script + metadata
    ScriptGenerator.cs                  Produces the full SQL script from ordered changes
    LiveApplier.cs                      Executes changes live, one transaction each
    ApplyResult.cs                      Data class: result of a live apply operation
    HistoryWriter.cs                    Saves scripts and apply results to disk

tests/SQLParity.Core.Tests/
  Sync/
    DependencyOrdererTests.cs           Tests for execution ordering
    ScriptGeneratorTests.cs             Tests for script output format and content
    HistoryWriterTests.cs               Tests for file I/O

tests/SQLParity.Core.IntegrationTests/
  LiveApplierTests.cs                   Integration tests against LocalDB
```

---

## Task 1: DependencyOrderer — safe execution order

**Files:**
- Create: `src/SQLParity.Core/Sync/DependencyOrderer.cs`
- Create: `tests/SQLParity.Core.Tests/Sync/DependencyOrdererTests.cs`

The dependency orderer sorts changes so that:
- **Creates (New)** run in order: schemas → UDTs/UDTTs → tables → indexes/constraints/triggers → views → procs → functions → sequences → synonyms
- **Drops (Dropped)** run in reverse order: synonyms → sequences → functions → procs → views → triggers/constraints/indexes → tables → UDTs/UDTTs → schemas
- **Modifies** follow the create order (they alter existing objects)
- Within a status group, drops of child objects (FK, index, check, trigger) come before drops of their parent tables

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Sync/DependencyOrdererTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class DependencyOrdererTests
{
    private static Change MakeChange(ObjectType type, ChangeStatus status, string name = "Obj") => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", name),
        ObjectType = type,
        Status = status,
        DdlSideA = status == ChangeStatus.Dropped ? null : "DDL A",
        DdlSideB = status == ChangeStatus.New ? null : "DDL B",
        ColumnChanges = Array.Empty<ColumnChange>(),
    };

    [Fact]
    public void DropsBeforeCreates_WhenMixed()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "NewTable"),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "OldTable"),
        };

        var ordered = DependencyOrderer.Order(changes);

        var dropIdx = ordered.ToList().FindIndex(c => c.Status == ChangeStatus.Dropped);
        var createIdx = ordered.ToList().FindIndex(c => c.Status == ChangeStatus.New);
        Assert.True(dropIdx < createIdx, "Drops should come before creates");
    }

    [Fact]
    public void SchemasCreatedBeforeTables()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1"),
            MakeChange(ObjectType.Schema, ChangeStatus.New, "S1"),
        };

        var ordered = DependencyOrderer.Order(changes).ToList();

        var schemaIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Schema);
        var tableIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Table);
        Assert.True(schemaIdx < tableIdx, "Schemas should be created before tables");
    }

    [Fact]
    public void TablesDroppedBeforeSchemas()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Schema, ChangeStatus.Dropped, "S1"),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "T1"),
        };

        var ordered = DependencyOrderer.Order(changes).ToList();

        var tableIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Table);
        var schemaIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Schema);
        Assert.True(tableIdx < schemaIdx, "Tables should be dropped before schemas");
    }

    [Fact]
    public void ForeignKeysDroppedBeforeTables()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "T1"),
            MakeChange(ObjectType.ForeignKey, ChangeStatus.Dropped, "FK1"),
        };

        var ordered = DependencyOrderer.Order(changes).ToList();

        var fkIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.ForeignKey);
        var tableIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Table);
        Assert.True(fkIdx < tableIdx, "Foreign keys should be dropped before tables");
    }

    [Fact]
    public void EmptyList_ReturnsEmpty()
    {
        var ordered = DependencyOrderer.Order(new List<Change>());
        Assert.Empty(ordered);
    }

    [Fact]
    public void PreservesOriginalList()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1"),
            MakeChange(ObjectType.View, ChangeStatus.New, "V1"),
        };

        var ordered = DependencyOrderer.Order(changes);

        Assert.Equal(2, ordered.Count());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure — `DependencyOrderer` does not exist.

- [ ] **Step 3: Implement DependencyOrderer**

Create `src/SQLParity.Core/Sync/DependencyOrderer.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

/// <summary>
/// Sorts a list of changes into a dependency-safe execution order.
/// Drops run first (child objects before parents), then modifies, then creates
/// (parents before children).
/// </summary>
public static class DependencyOrderer
{
    // Priority for CREATE/MODIFY operations (lower = earlier).
    // Parents must exist before children.
    private static readonly Dictionary<ObjectType, int> CreateOrder = new()
    {
        { ObjectType.Schema, 0 },
        { ObjectType.UserDefinedDataType, 1 },
        { ObjectType.UserDefinedTableType, 2 },
        { ObjectType.Table, 3 },
        { ObjectType.Index, 4 },
        { ObjectType.ForeignKey, 5 },
        { ObjectType.CheckConstraint, 6 },
        { ObjectType.Trigger, 7 },
        { ObjectType.View, 8 },
        { ObjectType.StoredProcedure, 9 },
        { ObjectType.UserDefinedFunction, 10 },
        { ObjectType.Sequence, 11 },
        { ObjectType.Synonym, 12 },
    };

    // Priority for DROP operations (lower = earlier).
    // Children must be dropped before parents — reverse of create order.
    private static readonly Dictionary<ObjectType, int> DropOrder = new()
    {
        { ObjectType.Synonym, 0 },
        { ObjectType.Sequence, 1 },
        { ObjectType.UserDefinedFunction, 2 },
        { ObjectType.StoredProcedure, 3 },
        { ObjectType.View, 4 },
        { ObjectType.Trigger, 5 },
        { ObjectType.CheckConstraint, 6 },
        { ObjectType.ForeignKey, 7 },
        { ObjectType.Index, 8 },
        { ObjectType.Table, 9 },
        { ObjectType.UserDefinedTableType, 10 },
        { ObjectType.UserDefinedDataType, 11 },
        { ObjectType.Schema, 12 },
    };

    /// <summary>
    /// Returns the changes in dependency-safe execution order:
    /// drops first (children before parents), then modifies, then creates
    /// (parents before children).
    /// </summary>
    public static IEnumerable<Change> Order(IEnumerable<Change> changes)
    {
        var list = changes.ToList();
        if (list.Count == 0) return list;

        var drops = list.Where(c => c.Status == ChangeStatus.Dropped)
            .OrderBy(c => GetDropOrder(c.ObjectType))
            .ThenBy(c => c.Id.ToString());

        var modifies = list.Where(c => c.Status == ChangeStatus.Modified)
            .OrderBy(c => GetCreateOrder(c.ObjectType))
            .ThenBy(c => c.Id.ToString());

        var creates = list.Where(c => c.Status == ChangeStatus.New)
            .OrderBy(c => GetCreateOrder(c.ObjectType))
            .ThenBy(c => c.Id.ToString());

        return drops.Concat(modifies).Concat(creates);
    }

    private static int GetCreateOrder(ObjectType type)
        => CreateOrder.TryGetValue(type, out var order) ? order : 99;

    private static int GetDropOrder(ObjectType type)
        => DropOrder.TryGetValue(type, out var order) ? order : 99;
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~79 total).

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Sync/DependencyOrderer.cs tests/SQLParity.Core.Tests/Sync/DependencyOrdererTests.cs
git commit -m "feat(sync): add DependencyOrderer for safe execution ordering"
```

---

## Task 2: SyncScript and ApplyResult data classes

**Files:**
- Create: `src/SQLParity.Core/Sync/SyncScript.cs`
- Create: `src/SQLParity.Core/Sync/ApplyResult.cs`

- [ ] **Step 1: Create SyncScript**

Create `src/SQLParity.Core/Sync/SyncScript.cs`:

```csharp
using System;

namespace SQLParity.Core.Sync;

/// <summary>
/// A generated sync script with metadata. This is what the ScriptGenerator produces.
/// </summary>
public sealed class SyncScript
{
    /// <summary>The complete SQL script text, including the header banner.</summary>
    public required string SqlText { get; init; }

    /// <summary>When the script was generated (UTC).</summary>
    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>The destination database name.</summary>
    public required string DestinationDatabase { get; init; }

    /// <summary>The destination server name.</summary>
    public required string DestinationServer { get; init; }

    /// <summary>Total number of changes in the script.</summary>
    public required int TotalChanges { get; init; }

    /// <summary>Number of destructive changes in the script.</summary>
    public required int DestructiveChanges { get; init; }
}
```

- [ ] **Step 2: Create ApplyResult**

Create `src/SQLParity.Core/Sync/ApplyResult.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SQLParity.Core.Sync;

/// <summary>
/// The outcome of a single change during live apply.
/// </summary>
public sealed class ApplyStepResult
{
    public required string ObjectName { get; init; }
    public required string Sql { get; init; }
    public required bool Succeeded { get; init; }
    public required string? ErrorMessage { get; init; }
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// The result of a live apply operation. Contains the outcome of every
/// change that was attempted, plus the first failure (if any).
/// </summary>
public sealed class ApplyResult
{
    public required DateTime StartedAtUtc { get; init; }
    public required DateTime CompletedAtUtc { get; init; }
    public required string DestinationDatabase { get; init; }
    public required string DestinationServer { get; init; }
    public required IReadOnlyList<ApplyStepResult> Steps { get; init; }
    public required bool FullySucceeded { get; init; }

    public int SucceededCount => Steps is null ? 0 : System.Linq.Enumerable.Count(Steps, s => s.Succeeded);
    public int FailedCount => Steps is null ? 0 : System.Linq.Enumerable.Count(Steps, s => !s.Succeeded);
}
```

- [ ] **Step 3: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Core/Sync/SyncScript.cs src/SQLParity.Core/Sync/ApplyResult.cs
git commit -m "feat(sync): add SyncScript and ApplyResult data classes"
```

---

## Task 3: ScriptGenerator — produce the sync script with header banner

**Files:**
- Create: `src/SQLParity.Core/Sync/ScriptGenerator.cs`
- Create: `tests/SQLParity.Core.Tests/Sync/ScriptGeneratorTests.cs`

The script generator takes a list of ordered changes and produces a complete SQL script. The header banner includes: generation timestamp, both labels, both server names, both database names, number of changes by risk tier, and a `USE [destination_db]` statement.

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Sync/ScriptGeneratorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class ScriptGeneratorTests
{
    private static Change MakeChange(ObjectType type, ChangeStatus status,
        string name = "TestObj", string? ddlA = "CREATE ...", string? ddlB = null,
        RiskTier risk = RiskTier.Safe) => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", name),
        ObjectType = type,
        Status = status,
        DdlSideA = status == ChangeStatus.Dropped ? null : ddlA,
        DdlSideB = status == ChangeStatus.New ? null : (ddlB ?? "OLD DDL"),
        Risk = risk,
        ColumnChanges = Array.Empty<ColumnChange>(),
    };

    private static ScriptGenerationOptions DefaultOptions() => new()
    {
        DestinationServer = "PROD-SERVER",
        DestinationDatabase = "MyDb",
        DestinationLabel = "PROD",
        SourceServer = "DEV-SERVER",
        SourceDatabase = "MyDb_Dev",
        SourceLabel = "DEV",
    };

    [Fact]
    public void GeneratedScript_StartsWithHeaderBanner()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.StartsWith("/*", script.SqlText.TrimStart());
    }

    [Fact]
    public void HeaderBanner_ContainsBothLabels()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.Contains("PROD", script.SqlText);
        Assert.Contains("DEV", script.SqlText);
    }

    [Fact]
    public void HeaderBanner_ContainsBothServerNames()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.Contains("PROD-SERVER", script.SqlText);
        Assert.Contains("DEV-SERVER", script.SqlText);
    }

    [Fact]
    public void HeaderBanner_ContainsUseStatement()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.Contains("USE [MyDb]", script.SqlText);
    }

    [Fact]
    public void HeaderBanner_ContainsRiskTierCounts()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1", risk: RiskTier.Safe),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "T2", risk: RiskTier.Destructive),
        };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.Contains("Safe", script.SqlText);
        Assert.Contains("Destructive", script.SqlText);
    }

    [Fact]
    public void Script_ContainsChangesDdl()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "Orders", "CREATE TABLE [dbo].[Orders] (Id INT)"),
        };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.Contains("CREATE TABLE [dbo].[Orders]", script.SqlText);
    }

    [Fact]
    public void Script_DroppedObject_ContainsDropStatement()
    {
        var change = MakeChange(ObjectType.View, ChangeStatus.Dropped, "OldView");
        change.Risk = RiskTier.Destructive;
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(new[] { change }, options);

        Assert.Contains("DROP VIEW", script.SqlText);
        Assert.Contains("[dbo].[OldView]", script.SqlText);
    }

    [Fact]
    public void Script_EachChangeHasGoSeparator()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1", "CREATE TABLE T1"),
            MakeChange(ObjectType.Table, ChangeStatus.New, "T2", "CREATE TABLE T2"),
        };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.Contains("GO", script.SqlText);
    }

    [Fact]
    public void Script_SetsMetadata()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, risk: RiskTier.Safe),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "Old", risk: RiskTier.Destructive),
        };
        var options = DefaultOptions();

        var script = ScriptGenerator.Generate(changes, options);

        Assert.Equal("MyDb", script.DestinationDatabase);
        Assert.Equal("PROD-SERVER", script.DestinationServer);
        Assert.Equal(2, script.TotalChanges);
        Assert.Equal(1, script.DestructiveChanges);
    }

    [Fact]
    public void EmptyChanges_ProducesHeaderOnly()
    {
        var script = ScriptGenerator.Generate(new List<Change>(), DefaultOptions());

        Assert.Contains("/*", script.SqlText);
        Assert.Equal(0, script.TotalChanges);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure.

- [ ] **Step 3: Implement ScriptGenerationOptions and ScriptGenerator**

Create `src/SQLParity.Core/Sync/ScriptGenerator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

/// <summary>
/// Options for script generation — the metadata that goes into the header banner.
/// </summary>
public sealed class ScriptGenerationOptions
{
    public required string DestinationServer { get; init; }
    public required string DestinationDatabase { get; init; }
    public required string DestinationLabel { get; init; }
    public required string SourceServer { get; init; }
    public required string SourceDatabase { get; init; }
    public required string SourceLabel { get; init; }
}

/// <summary>
/// Produces a complete SQL sync script from a list of changes.
/// Changes should already be dependency-ordered via <see cref="DependencyOrderer"/>.
/// </summary>
public static class ScriptGenerator
{
    public static SyncScript Generate(IEnumerable<Change> changes, ScriptGenerationOptions options)
    {
        var changeList = changes.ToList();
        var now = DateTime.UtcNow;
        var sb = new StringBuilder();

        // Header banner
        sb.AppendLine("/*");
        sb.AppendLine("========================================================================");
        sb.AppendLine("  SQLParity — Schema Sync Script");
        sb.AppendLine("========================================================================");
        sb.AppendLine($"  Generated:    {now:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"  Source:       [{options.SourceLabel}] {options.SourceServer} / {options.SourceDatabase}");
        sb.AppendLine($"  Destination:  [{options.DestinationLabel}] {options.DestinationServer} / {options.DestinationDatabase}");
        sb.AppendLine();
        sb.AppendLine($"  Total changes:      {changeList.Count}");
        sb.AppendLine($"    Safe:             {changeList.Count(c => c.Risk == RiskTier.Safe)}");
        sb.AppendLine($"    Caution:          {changeList.Count(c => c.Risk == RiskTier.Caution)}");
        sb.AppendLine($"    Risky:            {changeList.Count(c => c.Risk == RiskTier.Risky)}");
        sb.AppendLine($"    Destructive:      {changeList.Count(c => c.Risk == RiskTier.Destructive)}");
        sb.AppendLine("========================================================================");
        sb.AppendLine("*/");
        sb.AppendLine();
        sb.AppendLine($"USE [{options.DestinationDatabase}]");
        sb.AppendLine("GO");
        sb.AppendLine();

        // Each change
        foreach (var change in changeList)
        {
            sb.AppendLine($"-- [{change.Risk}] {change.Status} {change.ObjectType}: {change.Id}");

            var sql = GetSqlForChange(change);
            if (!string.IsNullOrWhiteSpace(sql))
            {
                sb.AppendLine(sql);
            }

            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return new SyncScript
        {
            SqlText = sb.ToString(),
            GeneratedAtUtc = now,
            DestinationDatabase = options.DestinationDatabase,
            DestinationServer = options.DestinationServer,
            TotalChanges = changeList.Count,
            DestructiveChanges = changeList.Count(c => c.Risk == RiskTier.Destructive),
        };
    }

    private static string GetSqlForChange(Change change)
    {
        switch (change.Status)
        {
            case ChangeStatus.New:
                // Use SideA DDL (the definition that needs to be created)
                return change.DdlSideA ?? string.Empty;

            case ChangeStatus.Modified:
                // For routines, use SideA DDL (the new definition).
                // For tables, the DDL represents the full table — the caller
                // should have generated ALTER statements. For now, emit SideA DDL
                // as a reference with a comment.
                return change.DdlSideA ?? string.Empty;

            case ChangeStatus.Dropped:
                // Generate a DROP statement
                return GenerateDropSql(change);

            default:
                return string.Empty;
        }
    }

    private static string GenerateDropSql(Change change)
    {
        var objectTypeSql = change.ObjectType switch
        {
            ObjectType.Table => "TABLE",
            ObjectType.View => "VIEW",
            ObjectType.StoredProcedure => "PROCEDURE",
            ObjectType.UserDefinedFunction => "FUNCTION",
            ObjectType.Schema => "SCHEMA",
            ObjectType.Sequence => "SEQUENCE",
            ObjectType.Synonym => "SYNONYM",
            ObjectType.Trigger => "TRIGGER",
            ObjectType.Index => null, // Indexes use DROP INDEX name ON table syntax
            ObjectType.ForeignKey => null, // FKs use ALTER TABLE ... DROP CONSTRAINT
            ObjectType.CheckConstraint => null, // Same as FK
            ObjectType.UserDefinedDataType => "TYPE",
            ObjectType.UserDefinedTableType => "TYPE",
            _ => null,
        };

        if (objectTypeSql is not null)
        {
            return $"DROP {objectTypeSql} [{change.Id.Schema}].[{change.Id.Name}]";
        }

        // Child objects that need special syntax
        if (change.ObjectType == ObjectType.Index)
        {
            return $"DROP INDEX [{change.Id.Name}] ON [{change.Id.Schema}].[{change.Id.Parent}]";
        }

        if (change.ObjectType == ObjectType.ForeignKey || change.ObjectType == ObjectType.CheckConstraint)
        {
            return $"ALTER TABLE [{change.Id.Schema}].[{change.Id.Parent}] DROP CONSTRAINT [{change.Id.Name}]";
        }

        return $"-- Cannot generate DROP for {change.ObjectType} {change.Id}";
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~89 total).

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Sync/ScriptGenerator.cs tests/SQLParity.Core.Tests/Sync/ScriptGeneratorTests.cs
git commit -m "feat(sync): add ScriptGenerator with header banner and dependency-ordered output"
```

---

## Task 4: LiveApplier — execute changes against a real database

**Files:**
- Create: `src/SQLParity.Core/Sync/LiveApplier.cs`
- Create: `tests/SQLParity.Core.IntegrationTests/LiveApplierTests.cs`

The LiveApplier executes each change in its own transaction. On first failure, it stops and returns the result showing what succeeded and what failed.

- [ ] **Step 1: Implement LiveApplier**

Create `src/SQLParity.Core/Sync/LiveApplier.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

/// <summary>
/// Applies changes live against a target database, one transaction per change.
/// Stops on first failure and reports what succeeded.
/// </summary>
public sealed class LiveApplier
{
    private readonly string _connectionString;

    public LiveApplier(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    /// Applies the given changes in order. Each change runs in its own transaction.
    /// On first failure, stops and returns the result.
    /// </summary>
    public ApplyResult Apply(IEnumerable<Change> orderedChanges, ScriptGenerationOptions options)
    {
        var steps = new List<ApplyStepResult>();
        var startedAt = DateTime.UtcNow;
        var allSucceeded = true;

        foreach (var change in orderedChanges)
        {
            var sql = GetSqlForChange(change);
            if (string.IsNullOrWhiteSpace(sql))
                continue;

            var sw = Stopwatch.StartNew();
            try
            {
                ExecuteInTransaction(sql);
                sw.Stop();

                steps.Add(new ApplyStepResult
                {
                    ObjectName = change.Id.ToString(),
                    Sql = sql,
                    Succeeded = true,
                    ErrorMessage = null,
                    Duration = sw.Elapsed,
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                steps.Add(new ApplyStepResult
                {
                    ObjectName = change.Id.ToString(),
                    Sql = sql,
                    Succeeded = false,
                    ErrorMessage = ex.Message,
                    Duration = sw.Elapsed,
                });

                allSucceeded = false;
                break; // Stop on first failure
            }
        }

        return new ApplyResult
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTime.UtcNow,
            DestinationDatabase = options.DestinationDatabase,
            DestinationServer = options.DestinationServer,
            Steps = steps,
            FullySucceeded = allSucceeded,
        };
    }

    private void ExecuteInTransaction(string sql)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static string GetSqlForChange(Change change)
    {
        switch (change.Status)
        {
            case ChangeStatus.New:
                return change.DdlSideA ?? string.Empty;

            case ChangeStatus.Modified:
                return change.DdlSideA ?? string.Empty;

            case ChangeStatus.Dropped:
                return GenerateDropSql(change);

            default:
                return string.Empty;
        }
    }

    private static string GenerateDropSql(Change change)
    {
        var objectTypeSql = change.ObjectType switch
        {
            ObjectType.Table => "TABLE",
            ObjectType.View => "VIEW",
            ObjectType.StoredProcedure => "PROCEDURE",
            ObjectType.UserDefinedFunction => "FUNCTION",
            ObjectType.Schema => "SCHEMA",
            ObjectType.Sequence => "SEQUENCE",
            ObjectType.Synonym => "SYNONYM",
            ObjectType.Trigger => "TRIGGER",
            ObjectType.Index => null,
            ObjectType.ForeignKey => null,
            ObjectType.CheckConstraint => null,
            ObjectType.UserDefinedDataType => "TYPE",
            ObjectType.UserDefinedTableType => "TYPE",
            _ => null,
        };

        if (objectTypeSql is not null)
            return $"DROP {objectTypeSql} [{change.Id.Schema}].[{change.Id.Name}]";

        if (change.ObjectType == ObjectType.Index)
            return $"DROP INDEX [{change.Id.Name}] ON [{change.Id.Schema}].[{change.Id.Parent}]";

        if (change.ObjectType == ObjectType.ForeignKey || change.ObjectType == ObjectType.CheckConstraint)
            return $"ALTER TABLE [{change.Id.Schema}].[{change.Id.Parent}] DROP CONSTRAINT [{change.Id.Name}]";

        return string.Empty;
    }
}
```

- [ ] **Step 2: Write the integration tests**

Create `tests/SQLParity.Core.IntegrationTests/LiveApplierTests.cs`:

```csharp
using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class LiveApplierFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[ExistingTable] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(100) NOT NULL
)
GO
";
}

public class LiveApplierTests : IClassFixture<LiveApplierFixture>
{
    private readonly LiveApplierFixture _fixture;
    private readonly ScriptGenerationOptions _options;

    public LiveApplierTests(LiveApplierFixture fixture)
    {
        _fixture = fixture;
        _options = new ScriptGenerationOptions
        {
            DestinationServer = "(localdb)\\MSSQLLocalDB",
            DestinationDatabase = fixture.DatabaseName,
            DestinationLabel = "TEST",
            SourceServer = "source",
            SourceDatabase = "sourceDb",
            SourceLabel = "SRC",
        };
    }

    [Fact]
    public void Apply_NewTable_Succeeds()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "NewTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[NewTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.True(result.FullySucceeded);
        Assert.Equal(1, result.SucceededCount);

        // Verify the table exists
        Assert.True(TableExists("dbo", "NewTable"));
    }

    [Fact]
    public void Apply_InvalidSql_StopsOnFailure()
    {
        var goodChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "GoodTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[GoodTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };
        var badChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "BadTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "THIS IS NOT VALID SQL",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };
        var afterBadChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "AfterBadTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[AfterBadTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { goodChange, badChange, afterBadChange }, _options);

        Assert.False(result.FullySucceeded);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        // AfterBadTable should NOT have been attempted (stop on first failure)
        Assert.Equal(2, result.Steps.Count);
        Assert.NotNull(result.Steps[1].ErrorMessage);
    }

    [Fact]
    public void Apply_DroppedTable_Succeeds()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "ExistingTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.Dropped,
            DdlSideA = null,
            DdlSideB = "CREATE TABLE ...",
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.True(result.FullySucceeded);
        Assert.False(TableExists("dbo", "ExistingTable"));
    }

    [Fact]
    public void Apply_SetsResultMetadata()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "MetaTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[MetaTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.Equal(_fixture.DatabaseName, result.DestinationDatabase);
        Assert.True(result.StartedAtUtc <= result.CompletedAtUtc);
        Assert.True(result.Steps[0].Duration.TotalMilliseconds >= 0);
    }

    private bool TableExists(string schema, string name)
    {
        using var conn = new SqlConnection(_fixture.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{name}'";
        return (int)cmd.ExecuteScalar()! > 0;
    }
}
```

- [ ] **Step 3: Run the tests**

```bash
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: all integration tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Core/Sync/LiveApplier.cs tests/SQLParity.Core.IntegrationTests/LiveApplierTests.cs
git commit -m "feat(sync): add LiveApplier with per-change transactions and stop-on-failure"
```

---

## Task 5: HistoryWriter — auto-save scripts and apply results

**Files:**
- Create: `src/SQLParity.Core/Sync/HistoryWriter.cs`
- Create: `tests/SQLParity.Core.Tests/Sync/HistoryWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Sync/HistoryWriterTests.cs`:

```csharp
using System;
using System.IO;
using System.Collections.Generic;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class HistoryWriterTests : IDisposable
{
    private readonly string _tempDir;

    public HistoryWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SQLParity_Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SaveScript_CreatesFileInHistoryFolder()
    {
        var writer = new HistoryWriter(_tempDir);
        var script = new SyncScript
        {
            SqlText = "USE [MyDb]\nGO\nCREATE TABLE ...",
            GeneratedAtUtc = new DateTime(2026, 4, 10, 14, 30, 0, DateTimeKind.Utc),
            DestinationDatabase = "MyDb",
            DestinationServer = "Server1",
            TotalChanges = 5,
            DestructiveChanges = 1,
        };

        var path = writer.SaveScript(script);

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE", content);
    }

    [Fact]
    public void SaveScript_FilenameContainsTimestamp()
    {
        var writer = new HistoryWriter(_tempDir);
        var script = new SyncScript
        {
            SqlText = "SELECT 1",
            GeneratedAtUtc = new DateTime(2026, 4, 10, 14, 30, 0, DateTimeKind.Utc),
            DestinationDatabase = "MyDb",
            DestinationServer = "Server1",
            TotalChanges = 1,
            DestructiveChanges = 0,
        };

        var path = writer.SaveScript(script);

        Assert.Contains("2026", Path.GetFileName(path));
        Assert.EndsWith(".sql", path);
    }

    [Fact]
    public void SaveApplyResult_CreatesFile()
    {
        var writer = new HistoryWriter(_tempDir);
        var result = new ApplyResult
        {
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            CompletedAtUtc = DateTime.UtcNow,
            DestinationDatabase = "MyDb",
            DestinationServer = "Server1",
            Steps = new List<ApplyStepResult>
            {
                new ApplyStepResult
                {
                    ObjectName = "[dbo].[Orders]",
                    Sql = "CREATE TABLE ...",
                    Succeeded = true,
                    ErrorMessage = null,
                    Duration = TimeSpan.FromMilliseconds(42),
                },
            },
            FullySucceeded = true,
        };

        var path = writer.SaveApplyResult(result);

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("dbo", content);
        Assert.Contains("Succeeded", content);
    }

    [Fact]
    public void HistoryFolder_CreatedIfNotExists()
    {
        var subDir = Path.Combine(_tempDir, "history");
        Assert.False(Directory.Exists(subDir));

        var writer = new HistoryWriter(subDir);
        var script = new SyncScript
        {
            SqlText = "SELECT 1",
            GeneratedAtUtc = DateTime.UtcNow,
            DestinationDatabase = "Db",
            DestinationServer = "Srv",
            TotalChanges = 0,
            DestructiveChanges = 0,
        };

        writer.SaveScript(script);

        Assert.True(Directory.Exists(subDir));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure.

- [ ] **Step 3: Implement HistoryWriter**

Create `src/SQLParity.Core/Sync/HistoryWriter.cs`:

```csharp
using System;
using System.IO;
using System.Text;

namespace SQLParity.Core.Sync;

/// <summary>
/// Saves generated scripts and apply results to a timestamped history folder.
/// </summary>
public sealed class HistoryWriter
{
    private readonly string _historyFolder;

    public HistoryWriter(string historyFolder)
    {
        _historyFolder = historyFolder ?? throw new ArgumentNullException(nameof(historyFolder));
    }

    /// <summary>
    /// Saves a generated sync script to the history folder. Returns the full path
    /// of the saved file.
    /// </summary>
    public string SaveScript(SyncScript script)
    {
        EnsureDirectoryExists();

        var timestamp = script.GeneratedAtUtc.ToString("yyyy-MM-dd_HHmmss");
        var fileName = $"script_{timestamp}_{script.DestinationDatabase}.sql";
        var path = Path.Combine(_historyFolder, fileName);

        File.WriteAllText(path, script.SqlText, Encoding.UTF8);
        return path;
    }

    /// <summary>
    /// Saves a live-apply result to the history folder. Returns the full path
    /// of the saved file.
    /// </summary>
    public string SaveApplyResult(ApplyResult result)
    {
        EnsureDirectoryExists();

        var timestamp = result.StartedAtUtc.ToString("yyyy-MM-dd_HHmmss");
        var fileName = $"apply_{timestamp}_{result.DestinationDatabase}.txt";
        var path = Path.Combine(_historyFolder, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("SQLParity — Live Apply Result");
        sb.AppendLine("========================================");
        sb.AppendLine($"Started:      {result.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Completed:    {result.CompletedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Destination:  {result.DestinationServer} / {result.DestinationDatabase}");
        sb.AppendLine($"Succeeded:    {result.SucceededCount}");
        sb.AppendLine($"Failed:       {result.FailedCount}");
        sb.AppendLine($"Fully OK:     {result.FullySucceeded}");
        sb.AppendLine("========================================");
        sb.AppendLine();

        foreach (var step in result.Steps)
        {
            var status = step.Succeeded ? "Succeeded" : "FAILED";
            sb.AppendLine($"[{status}] {step.ObjectName} ({step.Duration.TotalMilliseconds:F0}ms)");
            if (!step.Succeeded && step.ErrorMessage is not null)
            {
                sb.AppendLine($"  Error: {step.ErrorMessage}");
            }
            sb.AppendLine($"  SQL: {step.Sql}");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_historyFolder))
            Directory.CreateDirectory(_historyFolder);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~93 total).

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Sync/HistoryWriter.cs tests/SQLParity.Core.Tests/Sync/HistoryWriterTests.cs
git commit -m "feat(sync): add HistoryWriter for auto-saving scripts and apply results"
```

---

## Task 6: Final verification and tag

**Files:** none (verification only)

- [ ] **Step 1: Full build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj --no-incremental
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: Full test run**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: all tests pass. Record the exact counts.

- [ ] **Step 3: Verify clean git status**

```bash
git status
```

Expected: clean working tree.

- [ ] **Step 4: Verify commit history**

```bash
git log --oneline plan-2-complete..HEAD
```

Expected: ~6 commits since Plan 2.

- [ ] **Step 5: Tag**

```bash
git tag plan-3-complete
```

---

## Plan 3 Acceptance Criteria

- ✅ `SQLParity.Core` builds cleanly on both `net48` and `net8.0` (0 warnings, 0 errors)
- ✅ `DependencyOrderer` sorts changes into safe execution order (drops before creates, child before parent for drops, parent before child for creates)
- ✅ `ScriptGenerator` produces a complete SQL script with header banner (timestamp, both labels, both servers, both databases, risk tier counts, `USE` statement)
- ✅ `ScriptGenerator` emits `DROP` statements for dropped objects, `CREATE` DDL for new objects, and `GO` separators
- ✅ `LiveApplier` executes changes with per-change transactions, stops on first failure, reports what succeeded
- ✅ `HistoryWriter` auto-saves scripts as `.sql` files and apply results as `.txt` files to a timestamped history folder
- ✅ Integration tests verify live apply against LocalDB (create, drop, stop-on-failure)
- ✅ Git history is clean and tagged `plan-3-complete`

---

## What Plan 4 inherits from Plan 3

- A complete sync pipeline: `SchemaReader` → `SchemaComparator` → `DependencyOrderer` → `ScriptGenerator` or `LiveApplier` → `HistoryWriter`.
- The full Core library is now functionally complete for v1's non-UI features.
- Plan 4 (Project File I/O) adds `.sqlparity` project file read/write for persisting comparison settings, labels, filters, and ignored-differences across sessions.
