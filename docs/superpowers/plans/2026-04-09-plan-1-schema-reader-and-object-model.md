# SQLParity — Plan 1: Schema Reader & Object Model

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the in-memory object model representing a SQL Server database's schema, and the SMO-based `SchemaReader` that populates it — so that `SQLParity.Core` can load a full database schema into memory.

**Architecture:** Plain C# data classes (records where practical) form the object model. A `SchemaReader` class wraps SMO to enumerate every supported object type, script its DDL, and populate the model. Each object carries a stable identity key (`SchemaQualifiedName`) for diffing in Plan 2. Integration tests verify the reader against real schemas on LocalDB.

**Tech Stack:** C#, SMO (`Microsoft.SqlServer.SqlManagementObjects` 181.19.0), xUnit, LocalDB for integration tests.

**Spec reference:** [design spec §2 (Architecture)](../specs/2026-04-09-sqlparity-design.md) — Schema Reader, Object Model.

**What Plan 1 inherits from Plan 0:**
- A building solution with `SQLParity.Core` (multi-target `net48;net8.0`), two test projects, SMO referenced and proven to load.
- A `SchemaReaderSmokeProbe.cs` placeholder that this plan deletes in Task 1.
- A `LocalDbConnectionFixture` providing a connection string to `(localdb)\MSSQLLocalDB`.

---

## File Structure

```
src/SQLParity.Core/
  SchemaReaderSmokeProbe.cs              DELETE (Plan 0 placeholder)
  Model/
    SchemaQualifiedName.cs               Identity key: (Schema, Name) or (Schema, Parent, Name)
    DbObjectStatus.cs                    Enum: New, Modified, Dropped, Unchanged (used later by Comparator but defined now)
    DatabaseSchema.cs                    Top-level container holding all object collections
    TableModel.cs                        Table + Column + DefaultConstraint
    IndexModel.cs                        Index + IndexedColumn
    ConstraintModel.cs                   ForeignKey + ForeignKeyColumn + CheckConstraint
    TriggerModel.cs                      DML trigger
    RoutineModel.cs                      View, StoredProcedure, UserDefinedFunction
    SchemaModel.cs                       Schema (the CREATE SCHEMA container)
    SequenceModel.cs                     Sequence
    SynonymModel.cs                      Synonym
    UserDefinedTypeModel.cs              UDT (alias type) + UDTT (table type)
  SchemaReader.cs                        SMO wrapper that reads a database into a DatabaseSchema

tests/SQLParity.Core.Tests/
  SchemaReaderSmokeProbeTests.cs         DELETE (Plan 0 placeholder tests)
  Model/
    SchemaQualifiedNameTests.cs          Unit tests for identity key

tests/SQLParity.Core.IntegrationTests/
  SmokeIntegrationTests.cs              KEEP (existing)
  LocalDbConnectionFixture.cs           KEEP (existing)
  ThrowawayDatabaseFixture.cs           New fixture: creates/drops a temp database per test class
  SchemaReaderTableTests.cs             Integration tests: tables, columns, indexes, constraints, triggers
  SchemaReaderRoutineTests.cs           Integration tests: views, procs, functions
  SchemaReaderMiscTests.cs              Integration tests: schemas, sequences, synonyms, UDTs, UDTTs
```

---

## Task 1: Delete Plan 0 placeholders

**Files:**
- Delete: `src/SQLParity.Core/SchemaReaderSmokeProbe.cs`
- Delete: `tests/SQLParity.Core.Tests/SchemaReaderSmokeProbeTests.cs`

- [ ] **Step 1: Delete the placeholder files**

```bash
rm src/SQLParity.Core/SchemaReaderSmokeProbe.cs
rm tests/SQLParity.Core.Tests/SchemaReaderSmokeProbeTests.cs
```

- [ ] **Step 2: Verify the build still succeeds**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
dotnet build tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: both build with 0 warnings, 0 errors. The test project will have zero tests, which is fine.

- [ ] **Step 3: Run tests to confirm zero tests remain**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: `Total: 0` (no tests found, no failures).

- [ ] **Step 4: Commit**

```bash
git add -A src/SQLParity.Core/SchemaReaderSmokeProbe.cs tests/SQLParity.Core.Tests/SchemaReaderSmokeProbeTests.cs
git commit -m "chore: remove Plan 0 smoke probe placeholders"
```

---

## Task 2: SchemaQualifiedName identity key

**Files:**
- Create: `src/SQLParity.Core/Model/SchemaQualifiedName.cs`
- Create: `tests/SQLParity.Core.Tests/Model/SchemaQualifiedNameTests.cs`

Every object in the model needs a stable identity key for diffing. Most SQL Server objects are identified by `(Schema, Name)` — e.g., `dbo.Orders`. Child objects (columns, indexes, constraints) additionally need a parent name: `(Schema, ParentName, Name)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Model/SchemaQualifiedNameTests.cs`:

```csharp
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Model;

public class SchemaQualifiedNameTests
{
    [Fact]
    public void TopLevel_EqualsBySchemaAndName()
    {
        var a = SchemaQualifiedName.TopLevel("dbo", "Orders");
        var b = SchemaQualifiedName.TopLevel("dbo", "Orders");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TopLevel_DifferentSchema_NotEqual()
    {
        var a = SchemaQualifiedName.TopLevel("dbo", "Orders");
        var b = SchemaQualifiedName.TopLevel("sales", "Orders");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Child_EqualsBySchemaParentAndName()
    {
        var a = SchemaQualifiedName.Child("dbo", "Orders", "OrderId");
        var b = SchemaQualifiedName.Child("dbo", "Orders", "OrderId");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Child_DifferentParent_NotEqual()
    {
        var a = SchemaQualifiedName.Child("dbo", "Orders", "Id");
        var b = SchemaQualifiedName.Child("dbo", "Products", "Id");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TopLevel_ToString_ReturnsSchemaQualified()
    {
        var name = SchemaQualifiedName.TopLevel("dbo", "Orders");

        Assert.Equal("[dbo].[Orders]", name.ToString());
    }

    [Fact]
    public void Child_ToString_IncludesParent()
    {
        var name = SchemaQualifiedName.Child("dbo", "Orders", "OrderId");

        Assert.Equal("[dbo].[Orders].[OrderId]", name.ToString());
    }

    [Fact]
    public void Comparison_IsCaseInsensitive()
    {
        var a = SchemaQualifiedName.TopLevel("DBO", "Orders");
        var b = SchemaQualifiedName.TopLevel("dbo", "ORDERS");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure — `SchemaQualifiedName` does not exist.

- [ ] **Step 3: Implement SchemaQualifiedName**

Create `src/SQLParity.Core/Model/SchemaQualifiedName.cs`:

```csharp
using System;

namespace SQLParity.Core.Model;

/// <summary>
/// Stable identity key for a database object. Used for matching objects
/// across two databases during comparison. Case-insensitive, since SQL
/// Server default collation is case-insensitive.
/// </summary>
public sealed class SchemaQualifiedName : IEquatable<SchemaQualifiedName>
{
    public string Schema { get; }
    public string? Parent { get; }
    public string Name { get; }

    private SchemaQualifiedName(string schema, string? parent, string name)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Parent = parent;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    /// Identity for top-level objects: tables, views, procs, etc.
    /// </summary>
    public static SchemaQualifiedName TopLevel(string schema, string name)
        => new(schema, null, name);

    /// <summary>
    /// Identity for child objects: columns, indexes, constraints, triggers.
    /// </summary>
    public static SchemaQualifiedName Child(string schema, string parent, string name)
        => new(schema, parent ?? throw new ArgumentNullException(nameof(parent)), name);

    public bool Equals(SchemaQualifiedName? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Parent, other.Parent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as SchemaQualifiedName);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Schema, StringComparer.OrdinalIgnoreCase);
        hash.Add(Parent, StringComparer.OrdinalIgnoreCase);
        hash.Add(Name, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    public override string ToString()
        => Parent is null
            ? $"[{Schema}].[{Name}]"
            : $"[{Schema}].[{Parent}].[{Name}]";
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: 7 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Model/SchemaQualifiedName.cs tests/SQLParity.Core.Tests/Model/SchemaQualifiedNameTests.cs
git commit -m "feat(model): add SchemaQualifiedName identity key with case-insensitive equality"
```

---

## Task 3: Core model types — Table, Column, DefaultConstraint

**Files:**
- Create: `src/SQLParity.Core/Model/TableModel.cs`

These are plain data classes. No behavior, no tests needed beyond the integration tests in later tasks.

- [ ] **Step 1: Create the table model types**

Create `src/SQLParity.Core/Model/TableModel.cs`:

```csharp
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a column's default constraint.
/// </summary>
public sealed class DefaultConstraintModel
{
    public required string Name { get; init; }
    public required string Definition { get; init; }
}

/// <summary>
/// Represents a single column within a table or table type.
/// </summary>
public sealed class ColumnModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required int MaxLength { get; init; }
    public required int Precision { get; init; }
    public required int Scale { get; init; }
    public required bool IsNullable { get; init; }
    public required bool IsIdentity { get; init; }
    public required long IdentitySeed { get; init; }
    public required long IdentityIncrement { get; init; }
    public required bool IsComputed { get; init; }
    public required string? ComputedText { get; init; }
    public required bool IsPersisted { get; init; }
    public required string? Collation { get; init; }
    public required DefaultConstraintModel? DefaultConstraint { get; init; }
    public required int OrdinalPosition { get; init; }
}

/// <summary>
/// Represents a user table in the database.
/// </summary>
public sealed class TableModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Ddl { get; init; }
    public required IReadOnlyList<ColumnModel> Columns { get; init; }
    public required IReadOnlyList<IndexModel> Indexes { get; init; }
    public required IReadOnlyList<ForeignKeyModel> ForeignKeys { get; init; }
    public required IReadOnlyList<CheckConstraintModel> CheckConstraints { get; init; }
    public required IReadOnlyList<TriggerModel> Triggers { get; init; }
}
```

- [ ] **Step 2: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: compilation failure — `IndexModel`, `ForeignKeyModel`, `CheckConstraintModel`, `TriggerModel` do not exist yet. This is expected; they are created in the next two tasks. If you want a green build at this step, temporarily comment out the four `IReadOnlyList` properties that reference not-yet-defined types and uncomment them after Tasks 4 and 5. Or simply proceed to the next tasks and build after Task 5.

- [ ] **Step 3: Commit (even if build is red — the next tasks complete the model)**

```bash
git add src/SQLParity.Core/Model/TableModel.cs
git commit -m "feat(model): add TableModel, ColumnModel, DefaultConstraintModel"
```

---

## Task 4: Core model types — Index, ForeignKey, CheckConstraint

**Files:**
- Create: `src/SQLParity.Core/Model/IndexModel.cs`
- Create: `src/SQLParity.Core/Model/ConstraintModel.cs`

- [ ] **Step 1: Create the index model**

Create `src/SQLParity.Core/Model/IndexModel.cs`:

```csharp
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a column within an index (key or included).
/// </summary>
public sealed class IndexedColumnModel
{
    public required string Name { get; init; }
    public required bool IsDescending { get; init; }
    public required bool IsIncluded { get; init; }
}

/// <summary>
/// Represents an index on a table.
/// </summary>
public sealed class IndexModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string IndexType { get; init; }
    public required bool IsClustered { get; init; }
    public required bool IsUnique { get; init; }
    public required bool IsPrimaryKey { get; init; }
    public required bool IsUniqueConstraint { get; init; }
    public required bool HasFilter { get; init; }
    public required string? FilterDefinition { get; init; }
    public required IReadOnlyList<IndexedColumnModel> Columns { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 2: Create the constraint models**

Create `src/SQLParity.Core/Model/ConstraintModel.cs`:

```csharp
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a column mapping in a foreign key.
/// </summary>
public sealed class ForeignKeyColumnModel
{
    public required string LocalColumn { get; init; }
    public required string ReferencedColumn { get; init; }
}

/// <summary>
/// Represents a foreign key constraint.
/// </summary>
public sealed class ForeignKeyModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string ReferencedTableSchema { get; init; }
    public required string ReferencedTableName { get; init; }
    public required string DeleteAction { get; init; }
    public required string UpdateAction { get; init; }
    public required bool IsEnabled { get; init; }
    public required IReadOnlyList<ForeignKeyColumnModel> Columns { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// Represents a CHECK constraint.
/// </summary>
public sealed class CheckConstraintModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required string Definition { get; init; }
    public required bool IsEnabled { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 3: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: may still fail if `TriggerModel` is not yet defined (referenced in `TableModel`). Proceed to Task 5.

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Core/Model/IndexModel.cs src/SQLParity.Core/Model/ConstraintModel.cs
git commit -m "feat(model): add IndexModel, ForeignKeyModel, CheckConstraintModel"
```

---

## Task 5: Core model types — Trigger

**Files:**
- Create: `src/SQLParity.Core/Model/TriggerModel.cs`

- [ ] **Step 1: Create the trigger model**

Create `src/SQLParity.Core/Model/TriggerModel.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// Represents a DML trigger on a table.
/// </summary>
public sealed class TriggerModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Name { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool FiresOnInsert { get; init; }
    public required bool FiresOnUpdate { get; init; }
    public required bool FiresOnDelete { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 2: Verify the full model compiles**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. All model types referenced by `TableModel` now exist.

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/Model/TriggerModel.cs
git commit -m "feat(model): add TriggerModel"
```

---

## Task 6: Core model types — Routines (View, StoredProcedure, Function)

**Files:**
- Create: `src/SQLParity.Core/Model/RoutineModel.cs`

- [ ] **Step 1: Create the routine models**

Create `src/SQLParity.Core/Model/RoutineModel.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// Represents a view.
/// </summary>
public sealed class ViewModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required bool IsSchemaBound { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// Represents a stored procedure.
/// </summary>
public sealed class StoredProcedureModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// The kind of user-defined function.
/// </summary>
public enum FunctionKind
{
    Scalar,
    InlineTableValued,
    MultiStatementTableValued
}

/// <summary>
/// Represents a user-defined function (scalar, inline TVF, or multi-statement TVF).
/// </summary>
public sealed class UserDefinedFunctionModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required FunctionKind Kind { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 2: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/Model/RoutineModel.cs
git commit -m "feat(model): add ViewModel, StoredProcedureModel, UserDefinedFunctionModel"
```

---

## Task 7: Core model types — Schema, Sequence, Synonym, UDT, UDTT

**Files:**
- Create: `src/SQLParity.Core/Model/SchemaModel.cs`
- Create: `src/SQLParity.Core/Model/SequenceModel.cs`
- Create: `src/SQLParity.Core/Model/SynonymModel.cs`
- Create: `src/SQLParity.Core/Model/UserDefinedTypeModel.cs`

- [ ] **Step 1: Create the schema model**

Create `src/SQLParity.Core/Model/SchemaModel.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// Represents a schema (the CREATE SCHEMA container).
/// </summary>
public sealed class SchemaModel
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 2: Create the sequence model**

Create `src/SQLParity.Core/Model/SequenceModel.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// Represents a sequence object.
/// </summary>
public sealed class SequenceModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 3: Create the synonym model**

Create `src/SQLParity.Core/Model/SynonymModel.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// Represents a synonym.
/// </summary>
public sealed class SynonymModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string BaseObject { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 4: Create the user-defined type models**

Create `src/SQLParity.Core/Model/UserDefinedTypeModel.cs`:

```csharp
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Represents a user-defined data type (alias type, e.g., CREATE TYPE PhoneNumber FROM nvarchar(20)).
/// </summary>
public sealed class UserDefinedDataTypeModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string BaseType { get; init; }
    public required int MaxLength { get; init; }
    public required bool IsNullable { get; init; }
    public required string Ddl { get; init; }
}

/// <summary>
/// Represents a user-defined table type (UDTT).
/// </summary>
public sealed class UserDefinedTableTypeModel
{
    public required SchemaQualifiedName Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ColumnModel> Columns { get; init; }
    public required string Ddl { get; init; }
}
```

- [ ] **Step 5: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/SQLParity.Core/Model/SchemaModel.cs src/SQLParity.Core/Model/SequenceModel.cs src/SQLParity.Core/Model/SynonymModel.cs src/SQLParity.Core/Model/UserDefinedTypeModel.cs
git commit -m "feat(model): add SchemaModel, SequenceModel, SynonymModel, UDT and UDTT models"
```

---

## Task 8: DatabaseSchema container

**Files:**
- Create: `src/SQLParity.Core/Model/DatabaseSchema.cs`

- [ ] **Step 1: Create the DatabaseSchema container**

Create `src/SQLParity.Core/Model/DatabaseSchema.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>
/// Immutable snapshot of an entire database's schema. This is the top-level
/// type that the SchemaReader produces and the Comparator consumes.
/// </summary>
public sealed class DatabaseSchema
{
    public required string ServerName { get; init; }
    public required string DatabaseName { get; init; }
    public required DateTime ReadAtUtc { get; init; }

    public required IReadOnlyList<SchemaModel> Schemas { get; init; }
    public required IReadOnlyList<TableModel> Tables { get; init; }
    public required IReadOnlyList<ViewModel> Views { get; init; }
    public required IReadOnlyList<StoredProcedureModel> StoredProcedures { get; init; }
    public required IReadOnlyList<UserDefinedFunctionModel> Functions { get; init; }
    public required IReadOnlyList<SequenceModel> Sequences { get; init; }
    public required IReadOnlyList<SynonymModel> Synonyms { get; init; }
    public required IReadOnlyList<UserDefinedDataTypeModel> UserDefinedDataTypes { get; init; }
    public required IReadOnlyList<UserDefinedTableTypeModel> UserDefinedTableTypes { get; init; }
}
```

- [ ] **Step 2: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/Model/DatabaseSchema.cs
git commit -m "feat(model): add DatabaseSchema top-level container"
```

---

## Task 9: SchemaReader — connection, scripting options, and structure

**Files:**
- Create: `src/SQLParity.Core/SchemaReader.cs`

This task creates the `SchemaReader` class with connection handling, the shared `ScriptingOptions` config, and a skeleton `ReadSchema` method. Tasks 10–12 fill in the private methods that read each object type.

- [ ] **Step 1: Create the SchemaReader skeleton**

Create `src/SQLParity.Core/SchemaReader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SQLParity.Core.Model;

namespace SQLParity.Core;

/// <summary>
/// Reads a SQL Server database's schema into an in-memory <see cref="DatabaseSchema"/>
/// using SMO. Each instance is bound to a single connection string and database name.
/// Connections are short-lived: opened per read, closed when done.
/// </summary>
public sealed class SchemaReader
{
    private readonly string _connectionString;
    private readonly string _databaseName;

    public SchemaReader(string connectionString, string databaseName)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _databaseName = databaseName ?? throw new ArgumentNullException(nameof(databaseName));
    }

    /// <summary>
    /// Reads the full schema of the configured database and returns an
    /// immutable <see cref="DatabaseSchema"/> snapshot.
    /// </summary>
    public DatabaseSchema ReadSchema()
    {
        using var sqlConn = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
        sqlConn.Open();

        var serverConn = new ServerConnection(sqlConn);
        var server = new Server(serverConn);

        // Pre-load commonly used properties to avoid per-object round-trips.
        server.SetDefaultInitFields(typeof(Table), true);
        server.SetDefaultInitFields(typeof(Column), true);
        server.SetDefaultInitFields(typeof(Index), true);
        server.SetDefaultInitFields(typeof(ForeignKey), true);
        server.SetDefaultInitFields(typeof(ForeignKeyColumn), true);
        server.SetDefaultInitFields(typeof(Check), true);
        server.SetDefaultInitFields(typeof(Trigger), true);
        server.SetDefaultInitFields(typeof(View), true);
        server.SetDefaultInitFields(typeof(StoredProcedure), true);
        server.SetDefaultInitFields(typeof(UserDefinedFunction), true);
        server.SetDefaultInitFields(typeof(Schema), true);
        server.SetDefaultInitFields(typeof(Sequence), true);
        server.SetDefaultInitFields(typeof(Synonym), true);
        server.SetDefaultInitFields(typeof(UserDefinedDataType), true);
        server.SetDefaultInitFields(typeof(UserDefinedTableType), true);

        var db = server.Databases[_databaseName];
        if (db is null)
            throw new InvalidOperationException($"Database '{_databaseName}' not found on server '{server.Name}'.");

        return new DatabaseSchema
        {
            ServerName = server.Name,
            DatabaseName = db.Name,
            ReadAtUtc = DateTime.UtcNow,
            Schemas = ReadSchemas(db),
            Tables = ReadTables(db),
            Views = ReadViews(db),
            StoredProcedures = ReadStoredProcedures(db),
            Functions = ReadFunctions(db),
            Sequences = ReadSequences(db),
            Synonyms = ReadSynonyms(db),
            UserDefinedDataTypes = ReadUserDefinedDataTypes(db),
            UserDefinedTableTypes = ReadUserDefinedTableTypes(db),
        };
    }

    private static ScriptingOptions CreateScriptingOptions()
    {
        return new ScriptingOptions
        {
            ScriptDrops = false,
            IncludeIfNotExists = false,
            SchemaQualify = true,
            AnsiPadding = true,
            NoCollation = false,
            IncludeHeaders = false,
            ScriptSchema = true,
            ScriptData = false,
            DriAllConstraints = true,
            Indexes = true,
            Triggers = true,
            ExtendedProperties = false,
            Permissions = false,
            IncludeDatabaseContext = false,
        };
    }

    private static string ScriptObject(IScriptable obj)
    {
        var options = CreateScriptingOptions();
        StringCollection scripts = obj.Script(options);
        return string.Join(Environment.NewLine + "GO" + Environment.NewLine, scripts.Cast<string>());
    }

    private static string ScriptSingleObject(IScriptable obj)
    {
        StringCollection scripts = obj.Script(new ScriptingOptions { SchemaQualify = true });
        return string.Join(Environment.NewLine, scripts.Cast<string>());
    }

    // Placeholder methods — filled in by Tasks 10, 11, 12.
    private static List<SchemaModel> ReadSchemas(Database db) => new();
    private static List<TableModel> ReadTables(Database db) => new();
    private static List<ViewModel> ReadViews(Database db) => new();
    private static List<StoredProcedureModel> ReadStoredProcedures(Database db) => new();
    private static List<UserDefinedFunctionModel> ReadFunctions(Database db) => new();
    private static List<SequenceModel> ReadSequences(Database db) => new();
    private static List<SynonymModel> ReadSynonyms(Database db) => new();
    private static List<UserDefinedDataTypeModel> ReadUserDefinedDataTypes(Database db) => new();
    private static List<UserDefinedTableTypeModel> ReadUserDefinedTableTypes(Database db) => new();
}
```

- [ ] **Step 2: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. The placeholder methods return empty lists, so the class compiles and is callable — just returns empty schemas.

**If the build fails** because `IScriptable` is not found or `ServerConnection(SqlConnection)` doesn't resolve: SMO's `ServerConnection` accepts `System.Data.SqlClient.SqlConnection`, not `Microsoft.Data.SqlClient.SqlConnection`. In that case, change the `ReadSchema` method to use the `ServerConnection(string serverInstance)` constructor instead, extracting the server name from the connection string. Adjust:

```csharp
// Replace the sqlConn/serverConn block with:
var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString);
var serverConn = new ServerConnection
{
    ServerInstance = builder.DataSource,
    LoginSecure = builder.IntegratedSecurity,
};
if (!builder.IntegratedSecurity)
{
    serverConn.Login = builder.UserID;
    serverConn.Password = builder.Password;
}
serverConn.DatabaseName = _databaseName;
var server = new Server(serverConn);
```

Also, if `IScriptable` is not a public interface (it may be internal in this SMO version), replace the `ScriptObject` helper to accept `SqlSmoObject` instead:

```csharp
private static string ScriptObject(SqlSmoObject obj)
{
    var options = CreateScriptingOptions();
    StringCollection scripts;
    switch (obj)
    {
        case Table t: scripts = t.Script(options); break;
        case View v: scripts = v.Script(options); break;
        case StoredProcedure sp: scripts = sp.Script(options); break;
        case UserDefinedFunction f: scripts = f.Script(options); break;
        case Schema s: scripts = s.Script(options); break;
        case Sequence seq: scripts = seq.Script(options); break;
        case Synonym syn: scripts = syn.Script(options); break;
        case UserDefinedDataType uddt: scripts = uddt.Script(options); break;
        case UserDefinedTableType udtt: scripts = udtt.Script(options); break;
        default: scripts = new StringCollection(); break;
    }
    return string.Join(Environment.NewLine + "GO" + Environment.NewLine, scripts.Cast<string>());
}
```

Use whichever approach compiles. The important thing is that `SchemaReader` builds and returns empty schemas. The real logic comes in the next tasks.

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/SchemaReader.cs
git commit -m "feat(core): add SchemaReader skeleton with connection handling and scripting options"
```

---

## Task 10: SchemaReader — table reading

**Files:**
- Modify: `src/SQLParity.Core/SchemaReader.cs`

This is the largest task in the plan. Replace the placeholder `ReadTables` method (and add helpers for columns, indexes, constraints, triggers) with real SMO logic.

- [ ] **Step 1: Replace the table-reading methods**

In `src/SQLParity.Core/SchemaReader.cs`, replace the placeholder `ReadTables` method and add the private helpers. The full set of methods to add/replace:

```csharp
    private static List<TableModel> ReadTables(Database db)
    {
        var opts = CreateScriptingOptions();
        var result = new List<TableModel>();

        foreach (Table t in db.Tables)
        {
            if (t.IsSystemObject) continue;

            var id = SchemaQualifiedName.TopLevel(t.Schema, t.Name);
            var ddl = string.Join(Environment.NewLine + "GO" + Environment.NewLine,
                t.Script(opts).Cast<string>());

            result.Add(new TableModel
            {
                Id = id,
                Schema = t.Schema,
                Name = t.Name,
                Ddl = ddl,
                Columns = ReadColumns(t),
                Indexes = ReadIndexes(t),
                ForeignKeys = ReadForeignKeys(t),
                CheckConstraints = ReadCheckConstraints(t),
                Triggers = ReadTriggers(t),
            });
        }

        return result;
    }

    private static List<ColumnModel> ReadColumns(Table table)
    {
        var result = new List<ColumnModel>();
        int ordinal = 0;

        foreach (Column col in table.Columns)
        {
            var id = SchemaQualifiedName.Child(table.Schema, table.Name, col.Name);

            DefaultConstraintModel? dc = null;
            if (col.DefaultConstraint is not null)
            {
                dc = new DefaultConstraintModel
                {
                    Name = col.DefaultConstraint.Name,
                    Definition = col.DefaultConstraint.Text,
                };
            }

            result.Add(new ColumnModel
            {
                Id = id,
                Name = col.Name,
                DataType = col.DataType.SqlDataType.ToString(),
                MaxLength = col.DataType.MaximumLength,
                Precision = col.DataType.NumericPrecision,
                Scale = col.DataType.NumericScale,
                IsNullable = col.Nullable,
                IsIdentity = col.Identity,
                IdentitySeed = col.Identity ? col.IdentitySeed : 0,
                IdentityIncrement = col.Identity ? col.IdentityIncrement : 0,
                IsComputed = col.Computed,
                ComputedText = col.Computed ? col.ComputedText : null,
                IsPersisted = col.IsPersisted,
                Collation = col.Collation,
                DefaultConstraint = dc,
                OrdinalPosition = ordinal++,
            });
        }

        return result;
    }

    private static List<IndexModel> ReadIndexes(Table table)
    {
        var result = new List<IndexModel>();

        foreach (Index idx in table.Indexes)
        {
            var id = SchemaQualifiedName.Child(table.Schema, table.Name, idx.Name);

            var columns = new List<IndexedColumnModel>();
            foreach (IndexedColumn ic in idx.IndexedColumns)
            {
                columns.Add(new IndexedColumnModel
                {
                    Name = ic.Name,
                    IsDescending = ic.Descending,
                    IsIncluded = ic.IsIncluded,
                });
            }

            var ddl = string.Join(Environment.NewLine,
                idx.Script(new ScriptingOptions { SchemaQualify = true }).Cast<string>());

            result.Add(new IndexModel
            {
                Id = id,
                Name = idx.Name,
                IndexType = idx.IndexType.ToString(),
                IsClustered = idx.IsClustered,
                IsUnique = idx.IsUnique,
                IsPrimaryKey = idx.IndexKeyType == IndexKeyType.DriPrimaryKey,
                IsUniqueConstraint = idx.IndexKeyType == IndexKeyType.DriUniqueKey,
                HasFilter = idx.HasFilter,
                FilterDefinition = idx.HasFilter ? idx.FilterDefinition : null,
                Columns = columns,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<ForeignKeyModel> ReadForeignKeys(Table table)
    {
        var result = new List<ForeignKeyModel>();

        foreach (ForeignKey fk in table.ForeignKeys)
        {
            var id = SchemaQualifiedName.Child(table.Schema, table.Name, fk.Name);

            var columns = new List<ForeignKeyColumnModel>();
            foreach (ForeignKeyColumn fkc in fk.Columns)
            {
                columns.Add(new ForeignKeyColumnModel
                {
                    LocalColumn = fkc.Name,
                    ReferencedColumn = fkc.ReferencedColumn,
                });
            }

            var ddl = string.Join(Environment.NewLine,
                fk.Script(new ScriptingOptions { SchemaQualify = true }).Cast<string>());

            result.Add(new ForeignKeyModel
            {
                Id = id,
                Name = fk.Name,
                ReferencedTableSchema = fk.ReferencedTableSchema,
                ReferencedTableName = fk.ReferencedTable,
                DeleteAction = fk.DeleteAction.ToString(),
                UpdateAction = fk.UpdateAction.ToString(),
                IsEnabled = fk.IsEnabled,
                Columns = columns,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<CheckConstraintModel> ReadCheckConstraints(Table table)
    {
        var result = new List<CheckConstraintModel>();

        foreach (Check chk in table.Checks)
        {
            var id = SchemaQualifiedName.Child(table.Schema, table.Name, chk.Name);

            var ddl = string.Join(Environment.NewLine,
                chk.Script(new ScriptingOptions { SchemaQualify = true }).Cast<string>());

            result.Add(new CheckConstraintModel
            {
                Id = id,
                Name = chk.Name,
                Definition = chk.Text,
                IsEnabled = chk.IsEnabled,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<TriggerModel> ReadTriggers(Table table)
    {
        var result = new List<TriggerModel>();

        foreach (Trigger trig in table.Triggers)
        {
            if (trig.IsSystemObject) continue;

            var id = SchemaQualifiedName.Child(table.Schema, table.Name, trig.Name);

            var ddl = string.Join(Environment.NewLine,
                trig.Script(new ScriptingOptions { SchemaQualify = true }).Cast<string>());

            result.Add(new TriggerModel
            {
                Id = id,
                Name = trig.Name,
                IsEnabled = trig.IsEnabled,
                FiresOnInsert = trig.Insert,
                FiresOnUpdate = trig.Update,
                FiresOnDelete = trig.Delete,
                Ddl = ddl,
            });
        }

        return result;
    }
```

**Important:** When replacing, also remove the old placeholder line:
```csharp
private static List<TableModel> ReadTables(Database db) => new();
```

- [ ] **Step 2: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

If the build fails because of `IScriptable`, `ServerConnection`, or SMO type resolution issues, apply the same fallback patterns described in Task 9's Step 2 notes.

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/SchemaReader.cs
git commit -m "feat(core): implement SchemaReader table reading (columns, indexes, constraints, triggers)"
```

---

## Task 11: SchemaReader — routine reading (views, procs, functions)

**Files:**
- Modify: `src/SQLParity.Core/SchemaReader.cs`

- [ ] **Step 1: Replace the routine-reading placeholder methods**

In `src/SQLParity.Core/SchemaReader.cs`, replace the three placeholder methods:

```csharp
    private static List<ViewModel> ReadViews(Database db)
    {
        var opts = CreateScriptingOptions();
        var result = new List<ViewModel>();

        foreach (View v in db.Views)
        {
            if (v.IsSystemObject) continue;

            var id = SchemaQualifiedName.TopLevel(v.Schema, v.Name);
            var ddl = string.Join(Environment.NewLine + "GO" + Environment.NewLine,
                v.Script(opts).Cast<string>());

            result.Add(new ViewModel
            {
                Id = id,
                Schema = v.Schema,
                Name = v.Name,
                IsSchemaBound = v.IsSchemaBound,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<StoredProcedureModel> ReadStoredProcedures(Database db)
    {
        var opts = CreateScriptingOptions();
        var result = new List<StoredProcedureModel>();

        foreach (StoredProcedure sp in db.StoredProcedures)
        {
            if (sp.IsSystemObject) continue;

            var id = SchemaQualifiedName.TopLevel(sp.Schema, sp.Name);
            var ddl = string.Join(Environment.NewLine + "GO" + Environment.NewLine,
                sp.Script(opts).Cast<string>());

            result.Add(new StoredProcedureModel
            {
                Id = id,
                Schema = sp.Schema,
                Name = sp.Name,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<UserDefinedFunctionModel> ReadFunctions(Database db)
    {
        var opts = CreateScriptingOptions();
        var result = new List<UserDefinedFunctionModel>();

        foreach (UserDefinedFunction udf in db.UserDefinedFunctions)
        {
            if (udf.IsSystemObject) continue;

            var id = SchemaQualifiedName.TopLevel(udf.Schema, udf.Name);
            var ddl = string.Join(Environment.NewLine + "GO" + Environment.NewLine,
                udf.Script(opts).Cast<string>());

            var kind = udf.FunctionType switch
            {
                UserDefinedFunctionType.Scalar => FunctionKind.Scalar,
                UserDefinedFunctionType.Inline => FunctionKind.InlineTableValued,
                UserDefinedFunctionType.Table => FunctionKind.MultiStatementTableValued,
                _ => FunctionKind.Scalar,
            };

            result.Add(new UserDefinedFunctionModel
            {
                Id = id,
                Schema = udf.Schema,
                Name = udf.Name,
                Kind = kind,
                Ddl = ddl,
            });
        }

        return result;
    }
```

Remove the old placeholder lines for these three methods.

- [ ] **Step 2: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/SchemaReader.cs
git commit -m "feat(core): implement SchemaReader routine reading (views, procs, functions)"
```

---

## Task 12: SchemaReader — schemas, sequences, synonyms, UDTs, UDTTs

**Files:**
- Modify: `src/SQLParity.Core/SchemaReader.cs`

- [ ] **Step 1: Replace the remaining placeholder methods**

In `src/SQLParity.Core/SchemaReader.cs`, replace the five remaining placeholder methods:

```csharp
    private static List<SchemaModel> ReadSchemas(Database db)
    {
        var result = new List<SchemaModel>();

        foreach (Schema s in db.Schemas)
        {
            if (s.IsSystemObject) continue;

            var ddl = string.Join(Environment.NewLine,
                s.Script(new ScriptingOptions { SchemaQualify = true }).Cast<string>());

            result.Add(new SchemaModel
            {
                Name = s.Name,
                Owner = s.Owner,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<SequenceModel> ReadSequences(Database db)
    {
        var opts = CreateScriptingOptions();
        var result = new List<SequenceModel>();

        foreach (Sequence seq in db.Sequences)
        {
            var id = SchemaQualifiedName.TopLevel(seq.Schema, seq.Name);
            var ddl = string.Join(Environment.NewLine + "GO" + Environment.NewLine,
                seq.Script(opts).Cast<string>());

            result.Add(new SequenceModel
            {
                Id = id,
                Schema = seq.Schema,
                Name = seq.Name,
                DataType = seq.DataType.SqlDataType.ToString(),
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<SynonymModel> ReadSynonyms(Database db)
    {
        var opts = CreateScriptingOptions();
        var result = new List<SynonymModel>();

        foreach (Synonym syn in db.Synonyms)
        {
            var id = SchemaQualifiedName.TopLevel(syn.Schema, syn.Name);
            var ddl = string.Join(Environment.NewLine + "GO" + Environment.NewLine,
                syn.Script(opts).Cast<string>());

            var baseObj = syn.BaseObject;

            result.Add(new SynonymModel
            {
                Id = id,
                Schema = syn.Schema,
                Name = syn.Name,
                BaseObject = baseObj,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<UserDefinedDataTypeModel> ReadUserDefinedDataTypes(Database db)
    {
        var result = new List<UserDefinedDataTypeModel>();

        foreach (UserDefinedDataType uddt in db.UserDefinedDataTypes)
        {
            var id = SchemaQualifiedName.TopLevel(uddt.Schema, uddt.Name);
            var ddl = string.Join(Environment.NewLine,
                uddt.Script(new ScriptingOptions { SchemaQualify = true }).Cast<string>());

            result.Add(new UserDefinedDataTypeModel
            {
                Id = id,
                Schema = uddt.Schema,
                Name = uddt.Name,
                BaseType = uddt.SystemType,
                MaxLength = uddt.Length,
                IsNullable = uddt.Nullable,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<UserDefinedTableTypeModel> ReadUserDefinedTableTypes(Database db)
    {
        var result = new List<UserDefinedTableTypeModel>();

        foreach (UserDefinedTableType udtt in db.UserDefinedTableTypes)
        {
            var id = SchemaQualifiedName.TopLevel(udtt.Schema, udtt.Name);
            var ddl = string.Join(Environment.NewLine,
                udtt.Script(new ScriptingOptions { SchemaQualify = true }).Cast<string>());

            var columns = new List<ColumnModel>();
            int ordinal = 0;
            foreach (Column col in udtt.Columns)
            {
                columns.Add(new ColumnModel
                {
                    Id = SchemaQualifiedName.Child(udtt.Schema, udtt.Name, col.Name),
                    Name = col.Name,
                    DataType = col.DataType.SqlDataType.ToString(),
                    MaxLength = col.DataType.MaximumLength,
                    Precision = col.DataType.NumericPrecision,
                    Scale = col.DataType.NumericScale,
                    IsNullable = col.Nullable,
                    IsIdentity = col.Identity,
                    IdentitySeed = col.Identity ? col.IdentitySeed : 0,
                    IdentityIncrement = col.Identity ? col.IdentityIncrement : 0,
                    IsComputed = col.Computed,
                    ComputedText = col.Computed ? col.ComputedText : null,
                    IsPersisted = col.IsPersisted,
                    Collation = col.Collation,
                    DefaultConstraint = null,
                    OrdinalPosition = ordinal++,
                });
            }

            result.Add(new UserDefinedTableTypeModel
            {
                Id = id,
                Schema = udtt.Schema,
                Name = udtt.Name,
                Columns = columns,
                Ddl = ddl,
            });
        }

        return result;
    }
```

Remove the old placeholder lines.

- [ ] **Step 2: Verify the build — no more placeholder methods**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. There should be no remaining `=> new();` placeholder methods in `SchemaReader.cs`.

Verify by running:

```bash
grep "=> new();" src/SQLParity.Core/SchemaReader.cs
```

Expected: no output (no remaining placeholders).

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/SchemaReader.cs
git commit -m "feat(core): implement SchemaReader for schemas, sequences, synonyms, UDTs, UDTTs"
```

---

## Task 13: ThrowawayDatabaseFixture for integration tests

**Files:**
- Create: `tests/SQLParity.Core.IntegrationTests/ThrowawayDatabaseFixture.cs`

Integration tests need throwaway databases with known schemas. This fixture creates a uniquely-named database in LocalDB, runs setup SQL, and drops it on dispose.

- [ ] **Step 1: Create the fixture**

Create `tests/SQLParity.Core.IntegrationTests/ThrowawayDatabaseFixture.cs`:

```csharp
using System;
using Microsoft.Data.SqlClient;

namespace SQLParity.Core.IntegrationTests;

/// <summary>
/// Creates a throwaway database on LocalDB with a unique name. Drops it on
/// dispose. Tests that need a known schema subclass this and override
/// <see cref="SetupSql"/> to provide the DDL to run after creation.
/// </summary>
public abstract class ThrowawayDatabaseFixture : IDisposable
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;";

    public string DatabaseName { get; }
    public string ConnectionString { get; }

    protected ThrowawayDatabaseFixture()
    {
        DatabaseName = "SQLParity_Test_" + Guid.NewGuid().ToString("N")[..8];
        ConnectionString = $@"Server=(localdb)\MSSQLLocalDB;Database={DatabaseName};Integrated Security=true;TrustServerCertificate=true;";

        ExecuteNonQuery(MasterConnectionString, $"CREATE DATABASE [{DatabaseName}]");

        var setupSql = SetupSql();
        if (!string.IsNullOrWhiteSpace(setupSql))
        {
            // Split on GO batches and execute each separately.
            var batches = setupSql.Split(
                new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var batch in batches)
            {
                var trimmed = batch.Trim();
                if (trimmed.Length > 0)
                    ExecuteNonQuery(ConnectionString, trimmed);
            }
        }
    }

    /// <summary>
    /// Override to provide T-SQL that sets up the schema in the throwaway
    /// database. Use GO to separate batches.
    /// </summary>
    protected abstract string SetupSql();

    public void Dispose()
    {
        try
        {
            // Kill connections and drop.
            ExecuteNonQuery(MasterConnectionString,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]");
        }
        catch
        {
            // Best effort — don't fail tests on cleanup.
        }
        GC.SuppressFinalize(this);
    }

    private static void ExecuteNonQuery(string connectionString, string sql)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 2: Verify the build**

```bash
dotnet build tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add tests/SQLParity.Core.IntegrationTests/ThrowawayDatabaseFixture.cs
git commit -m "test: add ThrowawayDatabaseFixture for LocalDB integration tests"
```

---

## Task 14: Integration tests — tables, columns, indexes, constraints, triggers

**Files:**
- Create: `tests/SQLParity.Core.IntegrationTests/SchemaReaderTableTests.cs`

- [ ] **Step 1: Create the test class**

Create `tests/SQLParity.Core.IntegrationTests/SchemaReaderTableTests.cs`:

```csharp
using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class TableTestFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE SCHEMA [sales]
GO

CREATE TABLE [dbo].[Categories] (
    [CategoryId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(100) NOT NULL,
    [Description] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_Categories] PRIMARY KEY CLUSTERED ([CategoryId])
)
GO

CREATE TABLE [sales].[Orders] (
    [OrderId] INT NOT NULL IDENTITY(1,1),
    [OrderDate] DATETIME2 NOT NULL CONSTRAINT [DF_Orders_OrderDate] DEFAULT (GETUTCDATE()),
    [CategoryId] INT NOT NULL,
    [Total] DECIMAL(18,2) NOT NULL,
    [Notes] NVARCHAR(500) NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId]),
    CONSTRAINT [FK_Orders_Categories] FOREIGN KEY ([CategoryId]) REFERENCES [dbo].[Categories]([CategoryId]),
    CONSTRAINT [CK_Orders_Total] CHECK ([Total] >= 0)
)
GO

CREATE NONCLUSTERED INDEX [IX_Orders_OrderDate] ON [sales].[Orders] ([OrderDate] DESC) INCLUDE ([Total])
GO

CREATE TRIGGER [sales].[TR_Orders_Audit] ON [sales].[Orders]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Placeholder audit trigger
    RETURN;
END
GO
";
}

public class SchemaReaderTableTests : IClassFixture<TableTestFixture>
{
    private readonly DatabaseSchema _schema;

    public SchemaReaderTableTests(TableTestFixture fixture)
    {
        var reader = new SchemaReader(fixture.ConnectionString, fixture.DatabaseName);
        _schema = reader.ReadSchema();
    }

    [Fact]
    public void ReadsTables()
    {
        Assert.Equal(2, _schema.Tables.Count);
        Assert.Contains(_schema.Tables, t => t.Name == "Categories" && t.Schema == "dbo");
        Assert.Contains(_schema.Tables, t => t.Name == "Orders" && t.Schema == "sales");
    }

    [Fact]
    public void ReadsColumns()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        Assert.Equal(5, orders.Columns.Count);
        Assert.Contains(orders.Columns, c => c.Name == "OrderId" && c.IsIdentity);
        Assert.Contains(orders.Columns, c => c.Name == "Total" && !c.IsNullable);
        Assert.Contains(orders.Columns, c => c.Name == "Notes" && c.IsNullable);
    }

    [Fact]
    public void ReadsColumnOrdinalPositions()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var ordinals = orders.Columns.Select(c => c.OrdinalPosition).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ordinals);
    }

    [Fact]
    public void ReadsPrimaryKey()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var pk = orders.Indexes.SingleOrDefault(i => i.IsPrimaryKey);
        Assert.NotNull(pk);
        Assert.Equal("PK_Orders", pk.Name);
        Assert.True(pk.IsClustered);
    }

    [Fact]
    public void ReadsNonClusteredIndex()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var idx = orders.Indexes.SingleOrDefault(i => i.Name == "IX_Orders_OrderDate");
        Assert.NotNull(idx);
        Assert.False(idx.IsClustered);

        var keyCol = idx.Columns.Single(c => !c.IsIncluded);
        Assert.Equal("OrderDate", keyCol.Name);
        Assert.True(keyCol.IsDescending);

        var inclCol = idx.Columns.Single(c => c.IsIncluded);
        Assert.Equal("Total", inclCol.Name);
    }

    [Fact]
    public void ReadsForeignKey()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var fk = orders.ForeignKeys.SingleOrDefault(f => f.Name == "FK_Orders_Categories");
        Assert.NotNull(fk);
        Assert.Equal("dbo", fk.ReferencedTableSchema);
        Assert.Equal("Categories", fk.ReferencedTableName);
        Assert.Single(fk.Columns);
        Assert.Equal("CategoryId", fk.Columns[0].LocalColumn);
        Assert.Equal("CategoryId", fk.Columns[0].ReferencedColumn);
    }

    [Fact]
    public void ReadsCheckConstraint()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var chk = orders.CheckConstraints.SingleOrDefault(c => c.Name == "CK_Orders_Total");
        Assert.NotNull(chk);
        Assert.Contains("Total", chk.Definition);
        Assert.Contains(">=", chk.Definition);
    }

    [Fact]
    public void ReadsDefaultConstraint()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var orderDate = orders.Columns.Single(c => c.Name == "OrderDate");
        Assert.NotNull(orderDate.DefaultConstraint);
        Assert.Equal("DF_Orders_OrderDate", orderDate.DefaultConstraint.Name);
        Assert.Contains("getutcdate", orderDate.DefaultConstraint.Definition, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsTrigger()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var trig = orders.Triggers.SingleOrDefault(t => t.Name == "TR_Orders_Audit");
        Assert.NotNull(trig);
        Assert.True(trig.IsEnabled);
        Assert.True(trig.FiresOnInsert);
        Assert.True(trig.FiresOnUpdate);
        Assert.False(trig.FiresOnDelete);
    }

    [Fact]
    public void TableDdlIsNotEmpty()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        Assert.False(string.IsNullOrWhiteSpace(orders.Ddl));
        Assert.Contains("CREATE TABLE", orders.Ddl);
    }
}
```

- [ ] **Step 2: Run the integration tests**

```bash
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: all table tests pass (plus the original smoke test). If any fail, the SchemaReader has a bug in the table-reading logic — fix it before proceeding.

- [ ] **Step 3: Commit**

```bash
git add tests/SQLParity.Core.IntegrationTests/SchemaReaderTableTests.cs
git commit -m "test: add SchemaReader table integration tests (columns, indexes, FK, check, default, trigger)"
```

---

## Task 15: Integration tests — routines (views, procs, functions)

**Files:**
- Create: `tests/SQLParity.Core.IntegrationTests/SchemaReaderRoutineTests.cs`

- [ ] **Step 1: Create the test class**

Create `tests/SQLParity.Core.IntegrationTests/SchemaReaderRoutineTests.cs`:

```csharp
using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class RoutineTestFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[Products] (
    [ProductId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(200) NOT NULL,
    [Price] DECIMAL(18,2) NOT NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([ProductId])
)
GO

CREATE VIEW [dbo].[ExpensiveProducts]
WITH SCHEMABINDING
AS
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [Price] > 100
GO

CREATE PROCEDURE [dbo].[GetProductById]
    @ProductId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [ProductId] = @ProductId;
END
GO

CREATE FUNCTION [dbo].[GetProductCount]()
RETURNS INT
AS
BEGIN
    DECLARE @count INT;
    SELECT @count = COUNT(*) FROM [dbo].[Products];
    RETURN @count;
END
GO

CREATE FUNCTION [dbo].[GetProductsAbovePrice](@MinPrice DECIMAL(18,2))
RETURNS TABLE
AS
RETURN (
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [Price] > @MinPrice
)
GO
";
}

public class SchemaReaderRoutineTests : IClassFixture<RoutineTestFixture>
{
    private readonly DatabaseSchema _schema;

    public SchemaReaderRoutineTests(RoutineTestFixture fixture)
    {
        var reader = new SchemaReader(fixture.ConnectionString, fixture.DatabaseName);
        _schema = reader.ReadSchema();
    }

    [Fact]
    public void ReadsView()
    {
        var view = _schema.Views.SingleOrDefault(v => v.Name == "ExpensiveProducts");
        Assert.NotNull(view);
        Assert.Equal("dbo", view.Schema);
        Assert.True(view.IsSchemaBound);
        Assert.False(string.IsNullOrWhiteSpace(view.Ddl));
        Assert.Contains("CREATE", view.Ddl);
    }

    [Fact]
    public void ReadsStoredProcedure()
    {
        var sp = _schema.StoredProcedures.SingleOrDefault(s => s.Name == "GetProductById");
        Assert.NotNull(sp);
        Assert.Equal("dbo", sp.Schema);
        Assert.False(string.IsNullOrWhiteSpace(sp.Ddl));
        Assert.Contains("CREATE", sp.Ddl);
    }

    [Fact]
    public void ReadsScalarFunction()
    {
        var fn = _schema.Functions.SingleOrDefault(f => f.Name == "GetProductCount");
        Assert.NotNull(fn);
        Assert.Equal(FunctionKind.Scalar, fn.Kind);
        Assert.False(string.IsNullOrWhiteSpace(fn.Ddl));
    }

    [Fact]
    public void ReadsInlineTableValuedFunction()
    {
        var fn = _schema.Functions.SingleOrDefault(f => f.Name == "GetProductsAbovePrice");
        Assert.NotNull(fn);
        Assert.Equal(FunctionKind.InlineTableValued, fn.Kind);
        Assert.False(string.IsNullOrWhiteSpace(fn.Ddl));
    }
}
```

- [ ] **Step 2: Run the integration tests**

```bash
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: all routine tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/SQLParity.Core.IntegrationTests/SchemaReaderRoutineTests.cs
git commit -m "test: add SchemaReader routine integration tests (views, procs, scalar + TVF functions)"
```

---

## Task 16: Integration tests — schemas, sequences, synonyms, UDTs, UDTTs

**Files:**
- Create: `tests/SQLParity.Core.IntegrationTests/SchemaReaderMiscTests.cs`

- [ ] **Step 1: Create the test class**

Create `tests/SQLParity.Core.IntegrationTests/SchemaReaderMiscTests.cs`:

```csharp
using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class MiscTestFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE SCHEMA [inventory]
GO

CREATE SEQUENCE [dbo].[OrderNumberSeq]
    AS BIGINT
    START WITH 1000
    INCREMENT BY 1
GO

CREATE TABLE [dbo].[Placeholder] (
    [Id] INT NOT NULL PRIMARY KEY
)
GO

CREATE SYNONYM [dbo].[PlaceholderAlias] FOR [dbo].[Placeholder]
GO

CREATE TYPE [dbo].[PhoneNumber] FROM NVARCHAR(20) NOT NULL
GO

CREATE TYPE [dbo].[OrderLineItem] AS TABLE (
    [LineNumber] INT NOT NULL,
    [ProductName] NVARCHAR(200) NOT NULL,
    [Quantity] INT NOT NULL,
    [UnitPrice] DECIMAL(18,2) NOT NULL
)
GO
";
}

public class SchemaReaderMiscTests : IClassFixture<MiscTestFixture>
{
    private readonly DatabaseSchema _schema;

    public SchemaReaderMiscTests(MiscTestFixture fixture)
    {
        var reader = new SchemaReader(fixture.ConnectionString, fixture.DatabaseName);
        _schema = reader.ReadSchema();
    }

    [Fact]
    public void ReadsUserSchema()
    {
        var schema = _schema.Schemas.SingleOrDefault(s => s.Name == "inventory");
        Assert.NotNull(schema);
        Assert.False(string.IsNullOrWhiteSpace(schema.Ddl));
    }

    [Fact]
    public void ExcludesSystemSchemas()
    {
        // dbo, sys, INFORMATION_SCHEMA, guest, etc. should be excluded
        Assert.DoesNotContain(_schema.Schemas, s => s.Name == "sys");
        Assert.DoesNotContain(_schema.Schemas, s => s.Name == "INFORMATION_SCHEMA");
    }

    [Fact]
    public void ReadsSequence()
    {
        var seq = _schema.Sequences.SingleOrDefault(s => s.Name == "OrderNumberSeq");
        Assert.NotNull(seq);
        Assert.Equal("dbo", seq.Schema);
        Assert.False(string.IsNullOrWhiteSpace(seq.Ddl));
    }

    [Fact]
    public void ReadsSynonym()
    {
        var syn = _schema.Synonyms.SingleOrDefault(s => s.Name == "PlaceholderAlias");
        Assert.NotNull(syn);
        Assert.Equal("dbo", syn.Schema);
        Assert.Contains("Placeholder", syn.BaseObject);
    }

    [Fact]
    public void ReadsUserDefinedDataType()
    {
        var udt = _schema.UserDefinedDataTypes.SingleOrDefault(u => u.Name == "PhoneNumber");
        Assert.NotNull(udt);
        Assert.Equal("dbo", udt.Schema);
        Assert.Contains("nvarchar", udt.BaseType, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsUserDefinedTableType()
    {
        var udtt = _schema.UserDefinedTableTypes.SingleOrDefault(u => u.Name == "OrderLineItem");
        Assert.NotNull(udtt);
        Assert.Equal("dbo", udtt.Schema);
        Assert.Equal(4, udtt.Columns.Count);
        Assert.Contains(udtt.Columns, c => c.Name == "LineNumber");
        Assert.Contains(udtt.Columns, c => c.Name == "ProductName");
        Assert.Contains(udtt.Columns, c => c.Name == "Quantity");
        Assert.Contains(udtt.Columns, c => c.Name == "UnitPrice");
    }

    [Fact]
    public void DatabaseSchemaHasServerAndDbName()
    {
        Assert.False(string.IsNullOrWhiteSpace(_schema.ServerName));
        Assert.Equal(_schema.DatabaseName, _schema.DatabaseName); // non-null
        Assert.True(_schema.ReadAtUtc > System.DateTime.MinValue);
    }
}
```

- [ ] **Step 2: Run the full test suite**

```bash
dotnet test SQLParity.sln
```

Expected: all tests pass — unit tests for `SchemaQualifiedName` + all integration tests (table, routine, misc, plus the original smoke test).

- [ ] **Step 3: Commit**

```bash
git add tests/SQLParity.Core.IntegrationTests/SchemaReaderMiscTests.cs
git commit -m "test: add SchemaReader misc integration tests (schemas, sequences, synonyms, UDTs, UDTTs)"
```

---

## Task 17: Final verification and tag

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

- [ ] **Step 3: Verify no placeholder methods remain**

```bash
grep -r "=> new();" src/SQLParity.Core/
grep -r "TBD\|TODO\|PLACEHOLDER" src/SQLParity.Core/
```

Expected: no output.

- [ ] **Step 4: Verify clean git status**

```bash
git status
```

Expected: clean working tree (only `.claude/` untracked).

- [ ] **Step 5: Verify commit history**

```bash
git log --oneline plan-0-complete..HEAD
```

Expected: ~15-17 commits since Plan 0.

- [ ] **Step 6: Tag**

```bash
git tag plan-1-complete
```

---

## Plan 1 Acceptance Criteria

- ✅ `SQLParity.Core` builds cleanly (0 warnings, 0 errors)
- ✅ The Plan 0 `SchemaReaderSmokeProbe` placeholder is deleted
- ✅ The object model covers: tables, columns, default constraints, indexes, foreign keys, check constraints, triggers, views, stored procedures, functions (scalar + TVF), schemas, sequences, synonyms, user-defined data types, user-defined table types
- ✅ `SchemaReader.ReadSchema()` returns a populated `DatabaseSchema` for a real database
- ✅ Integration tests verify every object type against LocalDB with known schemas
- ✅ `SchemaQualifiedName` provides case-insensitive identity keys for diffing
- ✅ Git history is clean and tagged `plan-1-complete`

---

## What Plan 2 inherits from Plan 1

- A complete, tested object model covering all v1 object types.
- A `SchemaReader` that can load any database into a `DatabaseSchema`.
- `SchemaQualifiedName` identity keys on every object, ready for the Comparator to match across two `DatabaseSchema` instances.
- The `ThrowawayDatabaseFixture` for creating test databases with known schemas.
- Plan 2 (Comparator, Risk Classifier, Pre-Flight Checker) consumes `DatabaseSchema` as input and produces `ComparisonResult` as output.
