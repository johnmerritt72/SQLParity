# SQLParity — Plan 4: Project File I/O

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read and write `.sqlparity` JSON project files that persist comparison settings (connection identities, labels, environment tags, filters, and ignored-differences) across sessions, plus manage sticky environment tags per server+database pair in a user-settings file.

**Architecture:** Two focused components in `SQLParity.Core`:
1. **ProjectFile** — data model for what's persisted in a `.sqlparity` JSON file. Serialized with `System.Text.Json`. Never contains credentials.
2. **EnvironmentTagStore** — manages sticky environment tags per (server, database) combination in a separate JSON file. Tags auto-suggest based on label text and persist independently of any project file.

**Tech Stack:** C#, `System.Text.Json`, xUnit.

**Spec reference:** [design spec §3 (Environment tags)](../specs/2026-04-09-sqlparity-design.md), [§4 Step 2 (Connection setup)](../specs/2026-04-09-sqlparity-design.md), [§6 (Persistence, Filtering)](../specs/2026-04-09-sqlparity-design.md).

**What Plan 4 inherits from Plan 3:**
- Complete sync pipeline in Core
- 123 passing tests (93 unit + 30 integration)
- The `Model/` directory with all object types including `ObjectType` enum and `SchemaQualifiedName`

---

## File Structure

```
src/SQLParity.Core/
  Model/
    EnvironmentTag.cs                   Enum: Prod, Staging, Dev, Sandbox, Untagged
  Project/
    ConnectionSide.cs                   Data class: one side of a comparison (server, db, label, tag)
    FilterSettings.cs                   Data class: object type, schema, name pattern filters
    IgnoredDifference.cs                Data class: a specific difference to ignore across runs
    ProjectFile.cs                      Data class: the full .sqlparity project file model
    ProjectFileSerializer.cs            Read/write .sqlparity JSON files
    EnvironmentTagStore.cs              Read/write sticky tags per server+database

tests/SQLParity.Core.Tests/
  Project/
    ProjectFileSerializerTests.cs       Round-trip serialization tests
    EnvironmentTagStoreTests.cs         Sticky tag persistence tests
    FilterSettingsTests.cs              Filter matching logic tests
```

---

## Task 1: EnvironmentTag enum

**Files:**
- Create: `src/SQLParity.Core/Model/EnvironmentTag.cs`

- [ ] **Step 1: Create the enum**

Create `src/SQLParity.Core/Model/EnvironmentTag.cs`:

```csharp
namespace SQLParity.Core.Model;

/// <summary>
/// Environment classification for a database connection side. Drives
/// color assignment and safety rules (e.g., PROD blocks live apply).
/// </summary>
public enum EnvironmentTag
{
    Untagged,
    Dev,
    Sandbox,
    Staging,
    Prod
}
```

- [ ] **Step 2: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/SQLParity.Core/Model/EnvironmentTag.cs
git commit -m "feat(model): add EnvironmentTag enum"
```

---

## Task 2: ConnectionSide, FilterSettings, IgnoredDifference, ProjectFile data classes

**Files:**
- Create: `src/SQLParity.Core/Project/ConnectionSide.cs`
- Create: `src/SQLParity.Core/Project/FilterSettings.cs`
- Create: `src/SQLParity.Core/Project/IgnoredDifference.cs`
- Create: `src/SQLParity.Core/Project/ProjectFile.cs`

- [ ] **Step 1: Create ConnectionSide**

Create `src/SQLParity.Core/Project/ConnectionSide.cs`:

```csharp
using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

/// <summary>
/// One side of a comparison as persisted in a project file. Contains
/// connection identity (server + database), the user-chosen label, and
/// the environment tag. Never contains credentials.
/// </summary>
public sealed class ConnectionSide
{
    public required string ServerName { get; init; }
    public required string DatabaseName { get; init; }
    public required string Label { get; init; }
    public required EnvironmentTag Tag { get; init; }
}
```

- [ ] **Step 2: Create FilterSettings**

Create `src/SQLParity.Core/Project/FilterSettings.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

/// <summary>
/// Filter configuration for a comparison. Controls which objects are
/// included in the comparison results.
/// </summary>
public sealed class FilterSettings
{
    /// <summary>
    /// Object types to include. If empty, all types are included.
    /// </summary>
    public IReadOnlyList<ObjectType> IncludedObjectTypes { get; init; } = System.Array.Empty<ObjectType>();

    /// <summary>
    /// Schema names to include. If empty, all schemas are included.
    /// Case-insensitive matching.
    /// </summary>
    public IReadOnlyList<string> IncludedSchemas { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Schema names to exclude. Takes precedence over IncludedSchemas.
    /// Case-insensitive matching.
    /// </summary>
    public IReadOnlyList<string> ExcludedSchemas { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Wildcard patterns for object names to exclude (e.g., "tmp_*", "*_bak").
    /// Uses SQL LIKE-style wildcards: * matches any sequence, ? matches one character.
    /// Case-insensitive.
    /// </summary>
    public IReadOnlyList<string> ExcludedNamePatterns { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Returns true if the given object should be included based on these filters.
    /// </summary>
    public bool ShouldInclude(ObjectType type, string schema, string name)
    {
        // Object type filter
        if (IncludedObjectTypes.Count > 0 && !IncludedObjectTypes.Contains(type))
            return false;

        // Schema exclusion (takes precedence)
        if (ExcludedSchemas.Count > 0 &&
            ExcludedSchemas.Any(s => string.Equals(s, schema, System.StringComparison.OrdinalIgnoreCase)))
            return false;

        // Schema inclusion
        if (IncludedSchemas.Count > 0 &&
            !IncludedSchemas.Any(s => string.Equals(s, schema, System.StringComparison.OrdinalIgnoreCase)))
            return false;

        // Name pattern exclusion
        if (ExcludedNamePatterns.Count > 0 &&
            ExcludedNamePatterns.Any(p => WildcardMatch(name, p)))
            return false;

        return true;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        // Convert SQL LIKE-style wildcards to regex: * → .*, ? → .
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
```

- [ ] **Step 3: Create IgnoredDifference**

Create `src/SQLParity.Core/Project/IgnoredDifference.cs`:

```csharp
using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

/// <summary>
/// A specific difference that the user has chosen to ignore. Persisted
/// in the project file so it stays ignored across runs.
/// </summary>
public sealed class IgnoredDifference
{
    /// <summary>The identity of the object (e.g., "[dbo].[Orders]").</summary>
    public required string ObjectId { get; init; }

    /// <summary>The object type.</summary>
    public required ObjectType ObjectType { get; init; }

    /// <summary>Optional: user's reason for ignoring this difference.</summary>
    public string? Reason { get; init; }
}
```

- [ ] **Step 4: Create ProjectFile**

Create `src/SQLParity.Core/Project/ProjectFile.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SQLParity.Core.Project;

/// <summary>
/// The data model for a .sqlparity project file. Contains everything
/// needed to re-open a comparison: connection identities, labels, tags,
/// filters, and ignored differences. Never contains credentials.
/// </summary>
public sealed class ProjectFile
{
    /// <summary>File format version for forward compatibility.</summary>
    public int Version { get; init; } = 1;

    /// <summary>When the project was last saved (UTC).</summary>
    public DateTime LastSavedUtc { get; init; }

    /// <summary>SideA connection identity.</summary>
    public required ConnectionSide SideA { get; init; }

    /// <summary>SideB connection identity.</summary>
    public required ConnectionSide SideB { get; init; }

    /// <summary>Filter settings for the comparison.</summary>
    public FilterSettings Filters { get; init; } = new();

    /// <summary>Differences the user has chosen to ignore.</summary>
    public IReadOnlyList<IgnoredDifference> IgnoredDifferences { get; init; } = Array.Empty<IgnoredDifference>();
}
```

- [ ] **Step 5: Verify the build**

```bash
dotnet build src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/SQLParity.Core/Project/ src/SQLParity.Core/Model/EnvironmentTag.cs
git commit -m "feat(project): add ProjectFile, ConnectionSide, FilterSettings, IgnoredDifference data classes"
```

---

## Task 3: FilterSettings unit tests

**Files:**
- Create: `tests/SQLParity.Core.Tests/Project/FilterSettingsTests.cs`

- [ ] **Step 1: Write the tests**

Create `tests/SQLParity.Core.Tests/Project/FilterSettingsTests.cs`:

```csharp
using SQLParity.Core.Model;
using SQLParity.Core.Project;
using Xunit;

namespace SQLParity.Core.Tests.Project;

public class FilterSettingsTests
{
    [Fact]
    public void DefaultFilters_IncludeEverything()
    {
        var filters = new FilterSettings();

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.True(filters.ShouldInclude(ObjectType.View, "sales", "OrdersView"));
    }

    [Fact]
    public void IncludedObjectTypes_FiltersOut_ExcludedTypes()
    {
        var filters = new FilterSettings
        {
            IncludedObjectTypes = new[] { ObjectType.Table, ObjectType.View },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.True(filters.ShouldInclude(ObjectType.View, "dbo", "V1"));
        Assert.False(filters.ShouldInclude(ObjectType.StoredProcedure, "dbo", "GetOrders"));
    }

    [Fact]
    public void ExcludedSchemas_FiltersOut_MatchingSchemas()
    {
        var filters = new FilterSettings
        {
            ExcludedSchemas = new[] { "audit" },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "audit", "Log"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "AUDIT", "Log")); // case-insensitive
    }

    [Fact]
    public void IncludedSchemas_FiltersOut_NonMatchingSchemas()
    {
        var filters = new FilterSettings
        {
            IncludedSchemas = new[] { "dbo", "sales" },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.True(filters.ShouldInclude(ObjectType.Table, "sales", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "audit", "Log"));
    }

    [Fact]
    public void ExcludedSchemas_TakesPrecedence_OverIncludedSchemas()
    {
        var filters = new FilterSettings
        {
            IncludedSchemas = new[] { "dbo", "audit" },
            ExcludedSchemas = new[] { "audit" },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "audit", "Log"));
    }

    [Fact]
    public void ExcludedNamePatterns_WildcardStar()
    {
        var filters = new FilterSettings
        {
            ExcludedNamePatterns = new[] { "tmp_*" },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "tmp_Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "TMP_Orders")); // case-insensitive
    }

    [Fact]
    public void ExcludedNamePatterns_WildcardSuffix()
    {
        var filters = new FilterSettings
        {
            ExcludedNamePatterns = new[] { "*_bak" },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders_bak"));
    }

    [Fact]
    public void ExcludedNamePatterns_QuestionMark()
    {
        var filters = new FilterSettings
        {
            ExcludedNamePatterns = new[] { "T?" },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "T1"));
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "T12")); // ? = one char only
    }

    [Fact]
    public void AllFiltersCombined()
    {
        var filters = new FilterSettings
        {
            IncludedObjectTypes = new[] { ObjectType.Table },
            IncludedSchemas = new[] { "dbo" },
            ExcludedNamePatterns = new[] { "tmp_*" },
        };

        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.View, "dbo", "V1")); // wrong type
        Assert.False(filters.ShouldInclude(ObjectType.Table, "sales", "Orders")); // wrong schema
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "tmp_Orders")); // excluded name
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~102 total).

- [ ] **Step 3: Commit**

```bash
git add tests/SQLParity.Core.Tests/Project/FilterSettingsTests.cs
git commit -m "test(project): add FilterSettings unit tests for all filter dimensions"
```

---

## Task 4: ProjectFileSerializer — JSON round-trip

**Files:**
- Create: `src/SQLParity.Core/Project/ProjectFileSerializer.cs`
- Create: `tests/SQLParity.Core.Tests/Project/ProjectFileSerializerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Project/ProjectFileSerializerTests.cs`:

```csharp
using System;
using System.IO;
using System.Collections.Generic;
using SQLParity.Core.Model;
using SQLParity.Core.Project;
using Xunit;

namespace SQLParity.Core.Tests.Project;

public class ProjectFileSerializerTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectFileSerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SQLParity_Test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }

    private static ProjectFile MakeProject() => new()
    {
        LastSavedUtc = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
        SideA = new ConnectionSide
        {
            ServerName = "PROD-SERVER",
            DatabaseName = "MyDb",
            Label = "PROD",
            Tag = EnvironmentTag.Prod,
        },
        SideB = new ConnectionSide
        {
            ServerName = "DEV-SERVER",
            DatabaseName = "MyDb_Dev",
            Label = "DEV-Jane",
            Tag = EnvironmentTag.Dev,
        },
        Filters = new FilterSettings
        {
            IncludedObjectTypes = new[] { ObjectType.Table, ObjectType.View },
            ExcludedSchemas = new[] { "audit" },
            ExcludedNamePatterns = new[] { "tmp_*" },
        },
        IgnoredDifferences = new[]
        {
            new IgnoredDifference
            {
                ObjectId = "[dbo].[LegacyTable]",
                ObjectType = ObjectType.Table,
                Reason = "Intentionally different",
            },
        },
    };

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        var original = MakeProject();

        ProjectFileSerializer.Save(original, path);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(original.SideA.ServerName, loaded.SideA.ServerName);
        Assert.Equal(original.SideA.DatabaseName, loaded.SideA.DatabaseName);
        Assert.Equal(original.SideA.Label, loaded.SideA.Label);
        Assert.Equal(original.SideA.Tag, loaded.SideA.Tag);
        Assert.Equal(original.SideB.ServerName, loaded.SideB.ServerName);
        Assert.Equal(original.SideB.Label, loaded.SideB.Label);
        Assert.Equal(original.SideB.Tag, loaded.SideB.Tag);
    }

    [Fact]
    public void RoundTrip_PreservesFilters()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        var original = MakeProject();

        ProjectFileSerializer.Save(original, path);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal(2, loaded.Filters.IncludedObjectTypes.Count);
        Assert.Contains(ObjectType.Table, loaded.Filters.IncludedObjectTypes);
        Assert.Contains(ObjectType.View, loaded.Filters.IncludedObjectTypes);
        Assert.Single(loaded.Filters.ExcludedSchemas);
        Assert.Equal("audit", loaded.Filters.ExcludedSchemas[0]);
        Assert.Single(loaded.Filters.ExcludedNamePatterns);
        Assert.Equal("tmp_*", loaded.Filters.ExcludedNamePatterns[0]);
    }

    [Fact]
    public void RoundTrip_PreservesIgnoredDifferences()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        var original = MakeProject();

        ProjectFileSerializer.Save(original, path);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Single(loaded.IgnoredDifferences);
        Assert.Equal("[dbo].[LegacyTable]", loaded.IgnoredDifferences[0].ObjectId);
        Assert.Equal(ObjectType.Table, loaded.IgnoredDifferences[0].ObjectType);
        Assert.Equal("Intentionally different", loaded.IgnoredDifferences[0].Reason);
    }

    [Fact]
    public void Save_CreatesReadableJsonFile()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        ProjectFileSerializer.Save(MakeProject(), path);

        var json = File.ReadAllText(path);

        Assert.Contains("PROD-SERVER", json);
        Assert.Contains("DEV-Jane", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_ProducesIndentedJson()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        ProjectFileSerializer.Save(MakeProject(), path);

        var json = File.ReadAllText(path);

        // Indented JSON contains newlines and leading spaces
        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    [Fact]
    public void Load_NonExistentFile_Throws()
    {
        var path = Path.Combine(_tempDir, "nonexistent.sqlparity");

        Assert.Throws<FileNotFoundException>(() => ProjectFileSerializer.Load(path));
    }

    [Fact]
    public void DefaultProject_HasEmptyFiltersAndIgnoredDiffs()
    {
        var path = Path.Combine(_tempDir, "minimal.sqlparity");
        var minimal = new ProjectFile
        {
            SideA = new ConnectionSide { ServerName = "S", DatabaseName = "D", Label = "L", Tag = EnvironmentTag.Untagged },
            SideB = new ConnectionSide { ServerName = "S2", DatabaseName = "D2", Label = "L2", Tag = EnvironmentTag.Untagged },
        };

        ProjectFileSerializer.Save(minimal, path);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Empty(loaded.Filters.IncludedObjectTypes);
        Assert.Empty(loaded.Filters.ExcludedSchemas);
        Assert.Empty(loaded.Filters.ExcludedNamePatterns);
        Assert.Empty(loaded.IgnoredDifferences);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure — `ProjectFileSerializer` does not exist.

- [ ] **Step 3: Implement ProjectFileSerializer**

Create `src/SQLParity.Core/Project/ProjectFileSerializer.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQLParity.Core.Project;

/// <summary>
/// Reads and writes .sqlparity project files as indented JSON.
/// </summary>
public static class ProjectFileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Saves a project file to the given path as indented JSON.
    /// </summary>
    public static void Save(ProjectFile project, string path)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (path is null) throw new ArgumentNullException(nameof(path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(project, Options);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Loads a project file from the given path.
    /// </summary>
    public static ProjectFile Load(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"Project file not found: {path}", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectFile>(json, Options)
            ?? throw new InvalidOperationException($"Failed to deserialize project file: {path}");
    }
}
```

**Note on `System.Text.Json` for net48:** The `System.Text.Json` package is not part of .NET Framework 4.8 by default. You need to add the NuGet package:

```bash
dotnet add src/SQLParity.Core/SQLParity.Core.csproj package System.Text.Json
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~109 total).

If deserialization fails because `System.Text.Json` can't handle `IReadOnlyList<T>` on `required init` properties with the default constructor, you may need to add `[JsonConstructor]` attributes or change `IReadOnlyList<T>` to `List<T>` on the serializable types. The test will tell you — fix whatever fails.

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Project/ProjectFileSerializer.cs tests/SQLParity.Core.Tests/Project/ProjectFileSerializerTests.cs src/SQLParity.Core/SQLParity.Core.csproj
git commit -m "feat(project): add ProjectFileSerializer for .sqlparity JSON round-trip"
```

---

## Task 5: EnvironmentTagStore — sticky tags per server+database

**Files:**
- Create: `src/SQLParity.Core/Project/EnvironmentTagStore.cs`
- Create: `tests/SQLParity.Core.Tests/Project/EnvironmentTagStoreTests.cs`

Tags are sticky per (server, database) combination and stored in a JSON file separate from the project file (since tags are about environments, not comparisons).

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Project/EnvironmentTagStoreTests.cs`:

```csharp
using System;
using System.IO;
using SQLParity.Core.Model;
using SQLParity.Core.Project;
using Xunit;

namespace SQLParity.Core.Tests.Project;

public class EnvironmentTagStoreTests : IDisposable
{
    private readonly string _tempFile;

    public EnvironmentTagStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), "SQLParity_Test_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_tags.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetTag_UnknownServer_ReturnsUntagged()
    {
        var store = new EnvironmentTagStore(_tempFile);

        var tag = store.GetTag("UnknownServer", "UnknownDb");

        Assert.Equal(EnvironmentTag.Untagged, tag);
    }

    [Fact]
    public void SetAndGetTag_Persists()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("Server1", "MyDb", EnvironmentTag.Prod);

        var tag = store.GetTag("Server1", "MyDb");

        Assert.Equal(EnvironmentTag.Prod, tag);
    }

    [Fact]
    public void SetTag_PersistsAcrossInstances()
    {
        var store1 = new EnvironmentTagStore(_tempFile);
        store1.SetTag("Server1", "MyDb", EnvironmentTag.Staging);

        // New instance reading the same file
        var store2 = new EnvironmentTagStore(_tempFile);
        var tag = store2.GetTag("Server1", "MyDb");

        Assert.Equal(EnvironmentTag.Staging, tag);
    }

    [Fact]
    public void SetTag_OverwritesPrevious()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("Server1", "MyDb", EnvironmentTag.Dev);
        store.SetTag("Server1", "MyDb", EnvironmentTag.Prod);

        Assert.Equal(EnvironmentTag.Prod, store.GetTag("Server1", "MyDb"));
    }

    [Fact]
    public void GetTag_IsCaseInsensitive()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("SERVER1", "MYDB", EnvironmentTag.Prod);

        Assert.Equal(EnvironmentTag.Prod, store.GetTag("server1", "mydb"));
    }

    [Fact]
    public void MultipleTags_IndependentlyStored()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("Server1", "ProdDb", EnvironmentTag.Prod);
        store.SetTag("Server1", "DevDb", EnvironmentTag.Dev);

        Assert.Equal(EnvironmentTag.Prod, store.GetTag("Server1", "ProdDb"));
        Assert.Equal(EnvironmentTag.Dev, store.GetTag("Server1", "DevDb"));
    }

    [Fact]
    public void SuggestTag_FromLabel_Prod()
    {
        Assert.Equal(EnvironmentTag.Prod, EnvironmentTagStore.SuggestTagFromLabel("PROD"));
        Assert.Equal(EnvironmentTag.Prod, EnvironmentTagStore.SuggestTagFromLabel("Production DB"));
        Assert.Equal(EnvironmentTag.Prod, EnvironmentTagStore.SuggestTagFromLabel("my-prod-server"));
    }

    [Fact]
    public void SuggestTag_FromLabel_Dev()
    {
        Assert.Equal(EnvironmentTag.Dev, EnvironmentTagStore.SuggestTagFromLabel("DEV"));
        Assert.Equal(EnvironmentTag.Dev, EnvironmentTagStore.SuggestTagFromLabel("DEV-Jane"));
        Assert.Equal(EnvironmentTag.Dev, EnvironmentTagStore.SuggestTagFromLabel("development"));
    }

    [Fact]
    public void SuggestTag_FromLabel_Staging()
    {
        Assert.Equal(EnvironmentTag.Staging, EnvironmentTagStore.SuggestTagFromLabel("STAGING"));
        Assert.Equal(EnvironmentTag.Staging, EnvironmentTagStore.SuggestTagFromLabel("STAGE-release-42"));
    }

    [Fact]
    public void SuggestTag_FromLabel_Sandbox()
    {
        Assert.Equal(EnvironmentTag.Sandbox, EnvironmentTagStore.SuggestTagFromLabel("SANDBOX"));
        Assert.Equal(EnvironmentTag.Sandbox, EnvironmentTagStore.SuggestTagFromLabel("my-sandbox"));
    }

    [Fact]
    public void SuggestTag_FromLabel_Unknown_ReturnsUntagged()
    {
        Assert.Equal(EnvironmentTag.Untagged, EnvironmentTagStore.SuggestTagFromLabel("MyDatabase"));
        Assert.Equal(EnvironmentTag.Untagged, EnvironmentTagStore.SuggestTagFromLabel("Orders"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: compilation failure.

- [ ] **Step 3: Implement EnvironmentTagStore**

Create `src/SQLParity.Core/Project/EnvironmentTagStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

/// <summary>
/// Manages sticky environment tags per (server, database) combination.
/// Tags persist in a JSON file separate from project files, since tags
/// are about environments, not comparisons.
/// </summary>
public sealed class EnvironmentTagStore
{
    private readonly string _filePath;
    private Dictionary<string, EnvironmentTag> _tags;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public EnvironmentTagStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _tags = LoadFromDisk();
    }

    /// <summary>
    /// Gets the environment tag for a (server, database) pair. Returns
    /// Untagged if no tag has been set.
    /// </summary>
    public EnvironmentTag GetTag(string server, string database)
    {
        var key = MakeKey(server, database);
        return _tags.TryGetValue(key, out var tag) ? tag : EnvironmentTag.Untagged;
    }

    /// <summary>
    /// Sets (or overwrites) the environment tag for a (server, database) pair.
    /// Persists immediately to disk.
    /// </summary>
    public void SetTag(string server, string database, EnvironmentTag tag)
    {
        var key = MakeKey(server, database);
        _tags[key] = tag;
        SaveToDisk();
    }

    /// <summary>
    /// Suggests an environment tag based on the user-entered label text.
    /// Returns Untagged if no match is found.
    /// </summary>
    public static EnvironmentTag SuggestTagFromLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return EnvironmentTag.Untagged;

        var upper = label.ToUpperInvariant();

        if (upper.Contains("PROD"))
            return EnvironmentTag.Prod;
        if (upper.Contains("STAG"))
            return EnvironmentTag.Staging;
        if (upper.Contains("DEV"))
            return EnvironmentTag.Dev;
        if (upper.Contains("SANDBOX"))
            return EnvironmentTag.Sandbox;

        return EnvironmentTag.Untagged;
    }

    private static string MakeKey(string server, string database)
        => $"{server.ToUpperInvariant()}|{database.ToUpperInvariant()}";

    private Dictionary<string, EnvironmentTag> LoadFromDisk()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, EnvironmentTag>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, EnvironmentTag>>(json, JsonOptions)
                ?? new Dictionary<string, EnvironmentTag>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, EnvironmentTag>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveToDisk()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_tags, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all tests pass (~120 total).

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/Project/EnvironmentTagStore.cs tests/SQLParity.Core.Tests/Project/EnvironmentTagStoreTests.cs
git commit -m "feat(project): add EnvironmentTagStore with sticky tags and label-based suggestion"
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
git log --oneline plan-3-complete..HEAD
```

Expected: ~5 commits since Plan 3.

- [ ] **Step 5: Tag**

```bash
git tag plan-4-complete
```

---

## Plan 4 Acceptance Criteria

- ✅ `SQLParity.Core` builds cleanly on both `net48` and `net8.0`
- ✅ `.sqlparity` JSON project files round-trip correctly (save → load preserves all fields)
- ✅ Project files never contain credentials
- ✅ `FilterSettings.ShouldInclude()` filters by object type, schema inclusion/exclusion, and name pattern wildcard
- ✅ `IgnoredDifference` records persist in the project file
- ✅ `EnvironmentTagStore` persists sticky tags per (server, database) pair, case-insensitive
- ✅ `EnvironmentTagStore.SuggestTagFromLabel()` auto-suggests PROD/STAGING/DEV/SANDBOX from label text
- ✅ All tests pass, git tagged `plan-4-complete`

---

## What Plan 5 inherits from Plan 4

- The complete Core library — schema reading, comparison, risk classification, script generation, live apply, history, project file I/O, environment tags, and filtering.
- All of this is testable and runnable without SSMS or any UI.
- Plan 5 begins the VSIX shell work: package registration, tool window, connection picker with registered-servers integration, and the confirmation screen. This is the first plan that touches `SQLParity.Vsix` and requires WPF/XAML.
