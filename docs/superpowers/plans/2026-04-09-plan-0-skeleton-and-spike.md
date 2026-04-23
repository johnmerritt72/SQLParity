# SQLParity — Plan 0: Skeleton & Spike

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a buildable, testable .NET solution for SQLParity (Core library, two test projects, and a thin VSIX shell), initialize git, and run a focused investigation spike to answer the four "known unknowns" listed in §6 of the design spec. Output: a green build, a red-to-green smoke test in each test project, a working `F5` debug launch of SSMS 22 with the empty VSIX loaded, and a spike findings document that we feed back into the design spec before Plan 1 begins.

**Architecture:** Two production projects (`SQLParity.Core` as `netstandard2.0` to load into both legacy VSIX and modern hosts; `SQLParity.Vsix` as a SSMS 22-targeted VSIX) plus two `xUnit` test projects on `net8.0`. SMO is referenced from Core via the `Microsoft.SqlServer.SqlManagementObjects` NuGet package. The VSIX project is created via the Visual Studio "Empty VSIX Project" template because there is no `dotnet new` template for it. The spike tasks are investigations, not feature work — each one ends by writing findings into a single spike document.

**Tech Stack:** .NET (Core: `netstandard2.0`, Tests: `net8.0`, VSIX: whatever SSMS 22 requires — confirmed by the spike), C#, WPF (deferred to later plans), xUnit, SMO (`Microsoft.SqlServer.SqlManagementObjects`), `Microsoft.Data.SqlClient`, LocalDB for integration tests, Visual Studio 2022 with the "Visual Studio extension development" workload + SSMS 22 installed.

**Spec reference:** [docs/superpowers/specs/2026-04-09-sqlparity-design.md](../specs/2026-04-09-sqlparity-design.md)

**Known unknowns this plan resolves (from spec §6):**
1. Whether SSMS 22's connection colors are reachable through the shell API.
2. Whether SSMS 22's registered servers list is reachable through the shell API.
3. The exact .NET target framework SSMS 22's extension loader expects.
4. Whether Azure AD Interactive works with a single `Microsoft.Data.SqlClient` connection-string keyword.

**Out of scope for this plan:** Any feature work. No comparator, no risk classifier, no UI beyond an empty VSIX shell. Each later plan owns its own slice.

---

## File Structure (created by this plan)

```
SQLParity.sln                                       (solution file)
.gitignore                                          (standard .NET gitignore)
src/
  SQLParity.Core/
    SQLParity.Core.csproj                           (netstandard2.0, references SMO)
    SchemaReaderSmokeProbe.cs                       (placeholder used to verify SMO + build wiring)
  SQLParity.Vsix/
    SQLParity.Vsix.csproj                           (created via VS template, then edited)
    source.extension.vsixmanifest                   (created by template)
    SQLParityPackage.cs                             (created by template; minimal package class)
tests/
  SQLParity.Core.Tests/
    SQLParity.Core.Tests.csproj                     (net8.0 xUnit, references Core)
    SchemaReaderSmokeProbeTests.cs                  (smoke unit test)
  SQLParity.Core.IntegrationTests/
    SQLParity.Core.IntegrationTests.csproj          (net8.0 xUnit, references Core, LocalDB-bound)
    LocalDbConnectionFixture.cs                     (per-class fixture that ensures LocalDB is reachable)
    SmokeIntegrationTests.cs                        (smoke integration test that SELECTs against LocalDB)
docs/superpowers/spikes/
  2026-04-09-plan-0-spike-findings.md               (filled by spike tasks 11-14, committed in task 15)
```

The placeholder `SchemaReaderSmokeProbe` exists only so Plan 0 has something concrete to compile, test, and prove the SMO reference is wired correctly. Plan 1 deletes it and replaces it with the real `SchemaReader`.

---

## Task 1: Initialize git, gitignore, commit existing docs

**Files:**
- Create: `.gitignore`
- Modify: `c:\Code\SQLCompare\` (turn into git repo)

- [ ] **Step 1: Initialize the git repository**

Run from `c:\Code\SQLCompare\`:

```bash
git init
git branch -M main
```

Expected: `Initialized empty Git repository in c:/Code/SQLCompare/.git/`

- [ ] **Step 2: Create the .gitignore file**

Create `c:\Code\SQLCompare\.gitignore` with the following contents:

```gitignore
# Build outputs
[Bb]in/
[Oo]bj/
[Oo]ut/
[Ll]og/
[Ll]ogs/

# VS / VSIX
.vs/
*.user
*.suo
*.userosscache
*.sln.docstates
*.vsix

# NuGet
*.nupkg
*.snupkg
**/packages/*
!**/packages/build/
*.nuget.props
*.nuget.targets

# ReSharper / Rider
_ReSharper*/
*.[Rr]e[Ss]harper
*.DotSettings.user
.idea/

# Test results
[Tt]est[Rr]esult*/
coverage/
*.coverage
*.coveragexml

# Temp / OS
*.tmp
*.swp
Thumbs.db
.DS_Store
```

- [ ] **Step 3: Commit the existing design spec and the new gitignore**

Run:

```bash
git add .gitignore docs/superpowers/specs/2026-04-09-sqlparity-design.md
git commit -m "chore: initialize repo with design spec and gitignore"
```

Expected: a single commit on `main` containing two files. Verify with `git log --oneline` — should show one commit.

---

## Task 2: Create the solution file and folder structure

**Files:**
- Create: `SQLParity.sln`
- Create: `src/` and `tests/` directories (implicitly via project creation in later tasks)

- [ ] **Step 1: Create the empty solution**

Run from `c:\Code\SQLCompare\`:

```bash
dotnet new sln --name SQLParity
```

Expected: `The template "Solution File" was created successfully.` A file `SQLParity.sln` exists in the working directory.

- [ ] **Step 2: Verify dotnet CLI and SDK availability**

Run:

```bash
dotnet --info
```

Expected: output lists at least one installed .NET SDK of version `8.0.x` or newer. If not, install the .NET 8 SDK before continuing.

- [ ] **Step 3: Commit**

```bash
git add SQLParity.sln
git commit -m "chore: add empty solution file"
```

---

## Task 3: Create the SQLParity.Core class library

**Files:**
- Create: `src/SQLParity.Core/SQLParity.Core.csproj`
- Create: `src/SQLParity.Core/SchemaReaderSmokeProbe.cs`

- [ ] **Step 1: Create the class library project targeting netstandard2.0**

Run from `c:\Code\SQLCompare\`:

```bash
dotnet new classlib --name SQLParity.Core --output src/SQLParity.Core --framework netstandard2.0
```

Expected: project files created under `src/SQLParity.Core/`. The auto-generated `Class1.cs` will exist — delete it in step 2.

- [ ] **Step 2: Delete the auto-generated Class1.cs**

Delete the file `src/SQLParity.Core/Class1.cs`.

- [ ] **Step 3: Replace the project file contents**

Open `src/SQLParity.Core/SQLParity.Core.csproj` and replace its contents with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>SQLParity.Core</RootNamespace>
    <AssemblyName>SQLParity.Core</AssemblyName>
  </PropertyGroup>

</Project>
```

- [ ] **Step 4: Create the SchemaReaderSmokeProbe placeholder**

Create `src/SQLParity.Core/SchemaReaderSmokeProbe.cs` with these contents:

```csharp
namespace SQLParity.Core;

/// <summary>
/// Placeholder class used by Plan 0 to verify the build, NuGet references,
/// and test wiring. Plan 1 deletes this and introduces the real SchemaReader.
/// </summary>
public static class SchemaReaderSmokeProbe
{
    public static string Greet(string label)
    {
        if (label is null) throw new System.ArgumentNullException(nameof(label));
        return $"SQLParity.Core is alive: {label}";
    }
}
```

- [ ] **Step 5: Add the project to the solution**

Run:

```bash
dotnet sln SQLParity.sln add src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Project 'src\SQLParity.Core\SQLParity.Core.csproj' added to the solution.`

- [ ] **Step 6: Verify the build**

Run:

```bash
dotnet build SQLParity.sln
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/SQLParity.Core/ SQLParity.sln
git commit -m "feat(core): scaffold SQLParity.Core class library with smoke probe"
```

---

## Task 4: Create the SQLParity.Core.Tests project and wire the smoke test (TDD)

**Files:**
- Create: `tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj`
- Create: `tests/SQLParity.Core.Tests/SchemaReaderSmokeProbeTests.cs`

- [ ] **Step 1: Create the xUnit test project targeting net8.0**

Run from `c:\Code\SQLCompare\`:

```bash
dotnet new xunit --name SQLParity.Core.Tests --output tests/SQLParity.Core.Tests --framework net8.0
```

Expected: project files created under `tests/SQLParity.Core.Tests/`. Auto-generated `UnitTest1.cs` will exist — delete it in step 2.

- [ ] **Step 2: Delete the auto-generated UnitTest1.cs**

Delete the file `tests/SQLParity.Core.Tests/UnitTest1.cs`.

- [ ] **Step 3: Add a project reference to SQLParity.Core**

Run:

```bash
dotnet add tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj reference src/SQLParity.Core/SQLParity.Core.csproj
```

Expected: `Reference '..\..\src\SQLParity.Core\SQLParity.Core.csproj' added to the project.`

- [ ] **Step 4: Add the test project to the solution**

```bash
dotnet sln SQLParity.sln add tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

- [ ] **Step 5: Write the failing test**

Create `tests/SQLParity.Core.Tests/SchemaReaderSmokeProbeTests.cs` with these contents:

```csharp
using SQLParity.Core;
using Xunit;

namespace SQLParity.Core.Tests;

public class SchemaReaderSmokeProbeTests
{
    [Fact]
    public void Greet_ReturnsLabeledMessage()
    {
        var result = SchemaReaderSmokeProbe.Greet("Plan0");

        Assert.Equal("SQLParity.Core is alive: Plan0", result);
    }

    [Fact]
    public void Greet_NullLabel_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => SchemaReaderSmokeProbe.Greet(null!));
    }
}
```

- [ ] **Step 6: Verify the tests fail before they should pass — wait, they should already pass**

Because `SchemaReaderSmokeProbe` was created in Task 3, these tests should already pass. This is intentional: Task 3 introduced the placeholder so Task 4 could prove the *test wiring* works. Run:

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: `Passed!  - Failed: 0, Passed: 2, Skipped: 0`. If you see compilation errors instead, the project reference from Step 3 didn't take — re-run Step 3.

- [ ] **Step 7: Prove the test would actually fail if the implementation was wrong**

Temporarily edit `src/SQLParity.Core/SchemaReaderSmokeProbe.cs` and change `SQLParity.Core is alive: {label}` to `SQLParity.Core is alive: WRONG`. Then run:

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: `Greet_ReturnsLabeledMessage` fails with an `Assert.Equal()` failure showing the wrong string. This confirms the test wiring is real and not silently passing.

- [ ] **Step 8: Revert the temporary change**

Restore the `Greet` method to its correct form: `return $"SQLParity.Core is alive: {label}";`. Re-run `dotnet test` and confirm both tests pass again.

- [ ] **Step 9: Commit**

```bash
git add tests/SQLParity.Core.Tests/ SQLParity.sln
git commit -m "test(core): add SchemaReaderSmokeProbeTests and wire xUnit project"
```

---

## Task 5: Add SMO NuGet reference to SQLParity.Core and prove it loads

**Files:**
- Modify: `src/SQLParity.Core/SQLParity.Core.csproj`
- Modify: `src/SQLParity.Core/SchemaReaderSmokeProbe.cs`
- Modify: `tests/SQLParity.Core.Tests/SchemaReaderSmokeProbeTests.cs`

- [ ] **Step 1: Add the SMO package**

Run from `c:\Code\SQLCompare\`:

```bash
dotnet add src/SQLParity.Core/SQLParity.Core.csproj package Microsoft.SqlServer.SqlManagementObjects
```

Expected: `info : PackageReference for package 'Microsoft.SqlServer.SqlManagementObjects' version '...' added to file 'src\SQLParity.Core\SQLParity.Core.csproj'.` The package version that resolves should be the latest stable (170.x or newer at time of execution). Note the resolved version — it goes into the spike findings doc later.

- [ ] **Step 2: Extend the placeholder to actually instantiate an SMO type**

Edit `src/SQLParity.Core/SchemaReaderSmokeProbe.cs` to:

```csharp
using Microsoft.SqlServer.Management.Smo;

namespace SQLParity.Core;

/// <summary>
/// Placeholder class used by Plan 0 to verify the build, NuGet references,
/// and test wiring. Plan 1 deletes this and introduces the real SchemaReader.
/// </summary>
public static class SchemaReaderSmokeProbe
{
    public static string Greet(string label)
    {
        if (label is null) throw new System.ArgumentNullException(nameof(label));
        return $"SQLParity.Core is alive: {label}";
    }

    /// <summary>
    /// Instantiates an SMO Server type *without connecting*. This proves the
    /// SMO assemblies are referenced and load on this target framework.
    /// </summary>
    public static string DescribeSmoServerType()
    {
        var serverType = typeof(Server);
        return $"SMO type loaded: {serverType.FullName}";
    }
}
```

- [ ] **Step 3: Add a test for the new method**

Append to `tests/SQLParity.Core.Tests/SchemaReaderSmokeProbeTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void DescribeSmoServerType_LoadsSmoAssembly()
    {
        var result = SchemaReaderSmokeProbe.DescribeSmoServerType();

        Assert.StartsWith("SMO type loaded: Microsoft.SqlServer.Management.Smo.Server", result);
    }
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: 3 passed, 0 failed.

If this step fails with an `assembly not found` or `BadImageFormatException` error, it tells us the resolved SMO package does not support `netstandard2.0` for our chosen test runtime. **Do not edit project files to fix this in Plan 0** — instead, record the failure and the exact error in the spike findings doc (Task 15) and continue. The .NET-target spike (Task 11) will reconcile this.

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Core/ tests/SQLParity.Core.Tests/
git commit -m "feat(core): reference SMO and verify it loads via smoke probe"
```

---

## Task 6: Create the SQLParity.Core.IntegrationTests project against LocalDB

**Files:**
- Create: `tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj`
- Create: `tests/SQLParity.Core.IntegrationTests/LocalDbConnectionFixture.cs`
- Create: `tests/SQLParity.Core.IntegrationTests/SmokeIntegrationTests.cs`

- [ ] **Step 1: Verify LocalDB is installed**

Run:

```bash
sqllocaldb info
```

Expected: a list including at least `MSSQLLocalDB`. If `sqllocaldb` is not found or no instances exist, install SQL Server Express LocalDB before continuing. Note the exact instance name returned — the tests will use it.

- [ ] **Step 2: Start the default LocalDB instance if not already running**

```bash
sqllocaldb start MSSQLLocalDB
```

Expected: `LocalDB instance "MSSQLLocalDB" started.` (or "is already running")

- [ ] **Step 3: Create the integration test project**

```bash
dotnet new xunit --name SQLParity.Core.IntegrationTests --output tests/SQLParity.Core.IntegrationTests --framework net8.0
```

- [ ] **Step 4: Delete the auto-generated UnitTest1.cs**

Delete `tests/SQLParity.Core.IntegrationTests/UnitTest1.cs`.

- [ ] **Step 5: Add references to Core and Microsoft.Data.SqlClient**

```bash
dotnet add tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj reference src/SQLParity.Core/SQLParity.Core.csproj
dotnet add tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj package Microsoft.Data.SqlClient
```

- [ ] **Step 6: Add the project to the solution**

```bash
dotnet sln SQLParity.sln add tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

- [ ] **Step 7: Create the LocalDbConnectionFixture**

Create `tests/SQLParity.Core.IntegrationTests/LocalDbConnectionFixture.cs`:

```csharp
using Microsoft.Data.SqlClient;

namespace SQLParity.Core.IntegrationTests;

/// <summary>
/// xUnit fixture that provides a connection string to the local default
/// LocalDB instance and verifies it is reachable. Tests that need a real
/// SQL Server use this fixture so failures point at LocalDB rather than
/// at test logic.
/// </summary>
public sealed class LocalDbConnectionFixture
{
    public string ConnectionString { get; } =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";

    public LocalDbConnectionFixture()
    {
        using var conn = new SqlConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = cmd.ExecuteScalar();
        if (result is not int i || i != 1)
        {
            throw new System.InvalidOperationException(
                $"LocalDB smoke check returned unexpected value: {result}");
        }
    }
}
```

- [ ] **Step 8: Write the failing integration test**

Create `tests/SQLParity.Core.IntegrationTests/SmokeIntegrationTests.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public class SmokeIntegrationTests : IClassFixture<LocalDbConnectionFixture>
{
    private readonly LocalDbConnectionFixture _fixture;

    public SmokeIntegrationTests(LocalDbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CanQueryServerVersionFromLocalDb()
    {
        using var conn = new SqlConnection(_fixture.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION";
        var version = cmd.ExecuteScalar() as string;

        Assert.NotNull(version);
        Assert.Contains("SQL Server", version);
    }
}
```

- [ ] **Step 9: Run the integration tests**

```bash
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0`. If LocalDB is unreachable, the fixture constructor throws and the failure message points at the connection step, not the test body.

- [ ] **Step 10: Commit**

```bash
git add tests/SQLParity.Core.IntegrationTests/ SQLParity.sln
git commit -m "test(core): add LocalDB integration test project with smoke fixture"
```

---

## Task 7: Create the SQLParity.Vsix project (manual VS step)

**Files:**
- Create: `src/SQLParity.Vsix/SQLParity.Vsix.csproj` (via VS template)
- Create: `src/SQLParity.Vsix/source.extension.vsixmanifest` (via VS template)
- Create: `src/SQLParity.Vsix/SQLParityPackage.cs` (via VS template)

> ⚠️ **This task requires Visual Studio 2022 with the "Visual Studio extension development" workload installed.** There is no `dotnet new` template for VSIX projects. If the workload is not installed, install it first via the Visual Studio Installer before proceeding.

- [ ] **Step 1: Open the solution in Visual Studio 2022**

Open `c:\Code\SQLCompare\SQLParity.sln` in Visual Studio 2022.

- [ ] **Step 2: Add a new "Empty VSIX Project"**

In Solution Explorer, right-click the solution → **Add → New Project...** → search for "VSIX" → select **Empty VSIX Project** → click Next.

- [ ] **Step 3: Configure the project**

- **Project name:** `SQLParity.Vsix`
- **Location:** `c:\Code\SQLCompare\src\` (so the resulting path is `c:\Code\SQLCompare\src\SQLParity.Vsix\`)
- Click **Create**

- [ ] **Step 4: Verify the generated files**

Confirm that the following files exist after Visual Studio finishes scaffolding:

- `src/SQLParity.Vsix/SQLParity.Vsix.csproj`
- `src/SQLParity.Vsix/source.extension.vsixmanifest`
- `src/SQLParity.Vsix/SQLParityPackage.cs` (or similarly named package class — VS may name it `Package1.cs` or use the project name)

Note the exact target framework declared in the generated `.csproj` (e.g., `net472`, `net48`). **Record this value** — it is one of the data points the spike findings doc needs.

- [ ] **Step 5: Confirm the project is in the solution**

In Solution Explorer the new project should appear under the solution. Save All (`Ctrl+Shift+S`) and close Visual Studio.

- [ ] **Step 6: Build the VSIX from the command line**

Back in the terminal:

```bash
dotnet build SQLParity.sln
```

Expected: `Build succeeded`. The output should include a `.vsix` artifact under `src/SQLParity.Vsix/bin/Debug/`. If `dotnet build` fails because the VSIX project requires the legacy `MSBuild.exe` rather than `dotnet build`, fall back to:

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SQLParity.sln /t:Build /p:Configuration=Debug
```

(Adjust path for your VS edition: Community / Professional / Enterprise.) **Note in the spike findings whether `dotnet build` worked or whether legacy MSBuild was required.** This is a real data point for later automation.

- [ ] **Step 7: Commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): scaffold empty VSIX project for SSMS 22"
```

---

## Task 8: Configure F5 to launch SSMS 22 as the experimental host

**Files:**
- Modify: `src/SQLParity.Vsix/SQLParity.Vsix.csproj` (debug settings)

- [ ] **Step 1: Locate the SSMS 22 installation directory**

Find the path to `Ssms.exe` for SSMS 22. The default is one of:

- `C:\Program Files (x86)\Microsoft SQL Server Management Studio 22\Common7\IDE\Ssms.exe`
- `C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\Ssms.exe`

Record the actual path you find.

- [ ] **Step 2: Open the VSIX project's debug properties**

Reopen the solution in Visual Studio 2022. Right-click the **SQLParity.Vsix** project → **Properties** → **Debug** tab.

- [ ] **Step 3: Configure the external program launch**

- Set **Start external program** to the SSMS 22 `Ssms.exe` path you recorded in Step 1.
- Set **Command line arguments** to: `/rootSuffix Exp`

This makes F5 launch SSMS 22 with an isolated "experimental" hive so your dev VSIX is loaded into a clean SSMS environment that does not affect your normal SSMS configuration.

Save All (`Ctrl+Shift+S`).

- [ ] **Step 4: F5 launch and verify**

In Visual Studio, select the SQLParity.Vsix project as the startup project (right-click → **Set as Startup Project**), then press **F5**.

Expected: SSMS 22 launches with an experimental hive. The empty VSIX has no UI yet, so there is nothing to "see" inside SSMS — but the launch must succeed without errors and SSMS must report the extension as installed under **Tools → Extensions and Updates** (or its SSMS 22 equivalent menu name; record the actual menu path you find).

- [ ] **Step 5: If F5 fails to launch SSMS 22**

If F5 errors with a message like "extension is not compatible" or "host not supported," **stop and record the exact error** in the spike findings doc. Do not attempt to patch around it in Plan 0 — Task 11 (the .NET target spike) will address it.

- [ ] **Step 6: Close SSMS, return to VS, stop debugging, commit any changed files**

Some VS configurations store debug settings in the `.csproj`, others in a `.csproj.user` file (which is gitignored). If `git status` shows changes to `SQLParity.Vsix.csproj`, commit them:

```bash
git status
git add src/SQLParity.Vsix/SQLParity.Vsix.csproj
git commit -m "chore(vsix): configure F5 to launch SSMS 22 with /rootSuffix Exp"
```

If `git status` shows nothing tracked changed, no commit is needed for this task.

---

## Task 9: Create the spike findings document skeleton

**Files:**
- Create: `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md`

- [ ] **Step 1: Create the directory and file**

Create `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` with this skeleton — Tasks 10 through 13 fill the sections in:

```markdown
# Plan 0 Spike Findings

**Date:** 2026-04-09
**Plan:** Plan 0 — Skeleton & Spike
**Spec:** docs/superpowers/specs/2026-04-09-sqlparity-design.md (§6 known unknowns)

This document records the answers to the four "known unknowns" that Plan 0
was designed to resolve. Findings here feed back into the design spec
before Plan 1 begins.

---

## 1. SSMS 22 .NET target framework (spec §6 unknown #3)

**Question:** What .NET target framework does the SSMS 22 extension loader expect?

**Method:** [filled by Task 10]

**Findings:**

- VSIX project default `<TargetFramework>` after VS template scaffolding: **TBD**
- `dotnet build` of the VSIX: **WORKS / FAILS** — TBD
- Legacy MSBuild.exe required: **YES / NO** — TBD
- F5 launch into SSMS 22 with `/rootSuffix Exp`: **SUCCESS / FAIL** — TBD
- Resolved `Microsoft.SqlServer.SqlManagementObjects` package version: **TBD**
- Did SMO load on the test project's net8.0 runtime in Task 5: **YES / NO** — TBD

**Resolution:**

[A short paragraph stating the chosen target framework for SQLParity.Core
(currently netstandard2.0) and SQLParity.Vsix (set by template), and
whether anything in the design spec needs to change.]

**Action items for the design spec:**

- [ ] Update spec §2 (Architecture) with the confirmed VSIX target framework if it differs from the assumed default.
- [ ] Update spec §6 (Known Unknowns) to mark unknown #3 as resolved.

---

## 2. SSMS 22 connection-color API (spec §6 unknown #1)

**Question:** Can SQLParity programmatically read the user's per-connection color from SSMS 22?

**Method:** [filled by Task 11]

**Findings:**

- SSMS UI location where connection colors are configured: **TBD** (e.g., "Connect → Database Engine → Options → Connection Properties → Use custom color")
- Where SSMS persists the chosen color (registry key, file path, or in-process API surface): **TBD**
- Whether a VSIX-loaded extension can read it via the shell API: **YES / NO / UNCLEAR** — TBD
- If yes, the API or service to call: **TBD**
- If no, the fallback path identified: **Use SQLParity's own environment-tag color palette (already specified)**

**Resolution:**

[Short paragraph: either "we can read SSMS colors via X" or "we cannot,
and we will rely on our own tag-based palette." Either outcome is fine —
the spec already designed for both.]

**Action items for the design spec:**

- [ ] Update spec §3 (Identity, Labels) — confirm or remove the SSMS-color-inheritance behavior.
- [ ] Update spec §6 (Known Unknowns) to mark unknown #1 as resolved.

---

## 3. SSMS 22 registered servers list API (spec §6 unknown #2)

**Question:** Can SQLParity read the user's SSMS 22 registered servers list to populate connection pickers?

**Method:** [filled by Task 12]

**Findings:**

- File or service where SSMS 22 stores registered servers: **TBD** (historically `RegSrvr.xml` under `%APPDATA%\Microsoft\SQL Server Management Studio\<version>\`)
- Whether a VSIX-loaded extension can read it programmatically: **YES / NO / UNCLEAR** — TBD
- If yes, the API or file path: **TBD**
- If no, fallback: **Manual server entry only**
- License/redistribution concerns for parsing the SSMS file directly: **TBD**

**Resolution:**

[Short paragraph.]

**Action items for the design spec:**

- [ ] Update spec §4 (Comparison Workflow → Step 2 Connection Setup).
- [ ] Update spec §6 (Known Unknowns) to mark unknown #2 as resolved.

---

## 4. Azure AD Interactive via SqlClient connection-string keyword (spec §6 unknown #4)

**Question:** Does Azure AD Interactive authentication work with a single `Microsoft.Data.SqlClient` connection-string keyword (e.g., `Authentication=Active Directory Interactive`), or does it require additional MSAL hosting?

**Method:** [filled by Task 13]

**Findings:**

- `Microsoft.Data.SqlClient` package version tested: **TBD**
- Connection string used: **TBD**
- Result on first run: **SUCCESS / FAIL** — TBD
- If success: did it open a browser pop-up automatically? **YES / NO**
- If success: did the connection complete and a `SELECT 1` succeed? **YES / NO**
- If failure: error message, full text: **TBD**

**Resolution:**

- [ ] Azure AD Interactive is **IN SCOPE for v1** (single keyword works)
- [ ] Azure AD Interactive is **DEFERRED to v1.1** (requires MSAL or browser hosting work beyond a single keyword)

**Action items for the design spec:**

- [ ] Update spec §6 (Authentication in v1) with the resolution.
- [ ] Update spec §6 (Known Unknowns) to mark unknown #4 as resolved.
```

- [ ] **Step 2: Commit the skeleton**

```bash
git add docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md
git commit -m "docs: add Plan 0 spike findings skeleton"
```

---

## Task 10: Spike — confirm .NET target framework SSMS 22 expects

**Files:**
- Modify: `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` (Section 1)

- [ ] **Step 1: Gather the data points**

You already have most of the data points needed for Section 1 from earlier tasks. Collect:

- The `<TargetFramework>` value VS chose when scaffolding the VSIX project in Task 7, Step 4.
- Whether `dotnet build` worked on the VSIX in Task 7, Step 6, or whether legacy `MSBuild.exe` was required.
- Whether F5 successfully launched SSMS 22 in Task 8, Step 4.
- The resolved `Microsoft.SqlServer.SqlManagementObjects` version from Task 5, Step 1.
- Whether the SMO smoke test in Task 5, Step 4 passed on `net8.0`.

- [ ] **Step 2: Cross-check the SMO package's supported target frameworks**

Run from `c:\Code\SQLCompare\`:

```bash
dotnet list src/SQLParity.Core/SQLParity.Core.csproj package --include-transitive
```

Find the `Microsoft.SqlServer.SqlManagementObjects` line and note the resolved version. Then look up that version on nuget.org (`https://www.nuget.org/packages/Microsoft.SqlServer.SqlManagementObjects/<version>`) and record which target frameworks it lists. The relevant question: **does it support `netstandard2.0`?** If yes, our Core target is fine. If it only supports `net6.0`/`net8.0` and `net472`/`net48`, we may need to multi-target Core.

- [ ] **Step 3: Fill in Section 1 of the spike findings doc**

Replace every `TBD` in Section 1 of `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` with the actual values you collected. Write the **Resolution** paragraph stating:

- The confirmed VSIX target framework
- Whether `SQLParity.Core` should remain `netstandard2.0`, multi-target (e.g., `netstandard2.0;net48`), or change entirely
- Whether the design spec §2 needs an update

If a change to the spec is required, mark the action-item checkboxes accordingly. Do NOT edit the spec yet — Task 14 batches all spec updates.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md
git commit -m "docs(spike): record .NET target framework findings"
```

---

## Task 11: Spike — SSMS 22 connection-color API investigation

**Files:**
- Modify: `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` (Section 2)

- [ ] **Step 1: Set up a known connection color in SSMS 22**

Open SSMS 22 (your normal instance, not the experimental hive) and connect to any database. In the connection dialog use **Options >> → Connection Properties → Use custom color** and set a recognizable color (e.g., bright magenta). Save the connection. Confirm the color appears in the query window status bar.

- [ ] **Step 2: Find where SSMS persists the color**

Search the following locations in order, stopping when you find a match:

1. `%APPDATA%\Microsoft\SQL Server Management Studio\22.0\` and subfolders — look for files modified after you saved the color in Step 1. Likely candidates: `SqlStudio.bin`, `UserSettings.xml`, or files under a `Settings` subfolder.
2. The Windows registry under `HKEY_CURRENT_USER\Software\Microsoft\SQL Server Management Studio\22.0\` — use `regedit` and look for keys touched after your save.
3. Search the web for `"SSMS 22" custom connection color storage` to confirm.

Record exactly where the color lives and in what format (XML, binary, registry value, etc.).

- [ ] **Step 3: Determine whether a VSIX extension can read it**

Two paths to investigate:

- **Path A — official API.** Search the VS SDK / SSMS SDK assemblies for a service that exposes the active connection's color. Likely places to look: `Microsoft.SqlServer.Management.UI.ConnectionDlg`, `Microsoft.SqlServer.Management.SqlStudio.Explorer`. Use Visual Studio's Object Browser pointed at the assemblies under the SSMS 22 install folder (`Common7\IDE\` and subfolders). Search for type names containing `Color`, `Connection`, or `RegisteredServer`.
- **Path B — read the storage file/registry directly.** If no API exists, can the VSIX read the file or registry value identified in Step 2? Note any file-locking or permission concerns.

- [ ] **Step 4: Fill in Section 2 of the spike findings doc**

Replace `TBD` in Section 2 with what you found. The Resolution paragraph should commit to one of three outcomes:

- **API available** → name the API and mark spec §3 unchanged
- **No API, but file/registry readable** → describe the read approach, flag licensing concerns
- **Unreachable** → mark the SSMS-color-inheritance feature as removed; spec §3 needs an update to drop that bullet

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md
git commit -m "docs(spike): record SSMS connection-color API findings"
```

---

## Task 12: Spike — SSMS 22 registered servers list API investigation

**Files:**
- Modify: `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` (Section 3)

- [ ] **Step 1: Register a known server in SSMS 22**

In SSMS 22, open **View → Registered Servers**. Under "Local Server Groups," register a recognizable server (e.g., the LocalDB instance: `(localdb)\MSSQLLocalDB`) with a memorable name like `SPARITY-SPIKE-TEST`.

- [ ] **Step 2: Locate the storage**

Historically SSMS stores registered servers in:

- `%APPDATA%\Microsoft\SQL Server Management Studio\22.0\RegSrvr.xml`

Check whether that file exists and contains your `SPARITY-SPIKE-TEST` entry. If not, search `%APPDATA%\Microsoft\SQL Server Management Studio\22.0\` for files modified after your registration in Step 1. Record the exact file path and format.

- [ ] **Step 3: Determine whether a VSIX extension can read it**

As with Task 11, investigate two paths:

- **Path A — official API.** Search the SSMS SDK assemblies for `RegisteredServer`, `RegisteredServersStore`, `ServerGroup`. There has historically been a `Microsoft.SqlServer.Management.RegisteredServers` namespace — check whether it ships with SSMS 22 and whether it is a redistributable package.
- **Path B — direct file read.** Open `RegSrvr.xml` (or the file you identified) in a text editor and confirm the format is parseable. Note file-locking concerns.

- [ ] **Step 4: Fill in Section 3 of the spike findings doc**

Replace `TBD` in Section 3 with findings. The Resolution should commit to one of:

- **API available** → use it
- **File parse path** → use it, document the exact path and any redistribution / licensing notes
- **Unreachable** → spec §4 (Step 2 Connection Setup) needs an update to drop the "registered servers picker" feature; manual entry is the only path

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md
git commit -m "docs(spike): record SSMS registered-servers API findings"
```

---

## Task 13: Spike — Azure AD Interactive via SqlClient

**Files:**
- Create: `tests/SQLParity.Core.IntegrationTests/AzureAdInteractiveSpike.cs` (temporary, deleted in Step 5)
- Modify: `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` (Section 4)

> ⚠️ This spike requires access to an Azure SQL database or an on-prem SQL Server configured for Azure AD authentication, **plus** an Entra ID account that can authenticate to it. If neither is available, mark this spike as **deferred** in Section 4 of the findings doc and skip Steps 1-4 — the Plan 0 cannot answer this without a real target.

- [ ] **Step 1: Identify a target server**

Pin down the server name, database name, and Entra ID account you will use for the test. Record these in the spike notes (NOT in the committed findings doc — these are credentials-adjacent).

- [ ] **Step 2: Create a temporary spike test**

Create `tests/SQLParity.Core.IntegrationTests/AzureAdInteractiveSpike.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

/// <summary>
/// Plan 0 spike test — DELETED at the end of Task 13. Verifies whether
/// Azure AD Interactive auth works with a single SqlClient connection-string
/// keyword. Requires manual configuration of the connection string below.
/// Marked Skip by default so it does not run in CI.
/// </summary>
public class AzureAdInteractiveSpike
{
    [Fact(Skip = "Plan 0 spike — enable manually with a real Azure AD connection string")]
    public void CanConnectWithActiveDirectoryInteractive()
    {
        // EDIT THIS before running. Format:
        //   Server=tcp:<server>.database.windows.net,1433;Database=<db>;
        //   Authentication=Active Directory Interactive;
        //   Encrypt=true;TrustServerCertificate=false;
        const string connectionString =
            "Server=tcp:REPLACE_ME.database.windows.net,1433;Database=REPLACE_ME;" +
            "Authentication=Active Directory Interactive;" +
            "Encrypt=true;TrustServerCertificate=false;";

        using var conn = new SqlConnection(connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = cmd.ExecuteScalar();

        Assert.Equal(1, result);
    }
}
```

- [ ] **Step 3: Run the spike test manually**

Edit the connection string in the file with your real target. Temporarily remove `Skip = "..."` from the `[Fact]` attribute. Run:

```bash
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj --filter FullyQualifiedName~AzureAdInteractiveSpike
```

Expected outcomes:
- A browser window pops up for sign-in → after sign-in, the test passes → Azure AD Interactive **works with one keyword**.
- The test fails with an error like "Microsoft.IdentityModel.Clients.ActiveDirectory missing" or similar → it does **not** work out of the box.
- Any other failure → record the exact message verbatim.

- [ ] **Step 4: Fill in Section 4 of the spike findings doc**

Record the result. Make sure the connection string with the real server name is **NOT** committed — Step 5 deletes the file entirely.

- [ ] **Step 5: Delete the spike test file**

This file held a connection string, even after editing back to placeholders. Delete it cleanly:

```bash
git rm tests/SQLParity.Core.IntegrationTests/AzureAdInteractiveSpike.cs
```

Verify the file is gone:

```bash
git status
```

Expected: the file is staged for deletion and no other untracked changes contain the connection string.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md
git commit -m "docs(spike): record Azure AD Interactive findings and remove spike test"
```

---

## Task 14: Update the design spec with spike resolutions

**Files:**
- Modify: `docs/superpowers/specs/2026-04-09-sqlparity-design.md`

- [ ] **Step 1: Re-read the spike findings doc**

Open `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` and read all four resolution sections. Make a list of the action-item checkboxes that are checked.

- [ ] **Step 2: Apply the resolutions to the spec**

For each checked action item, edit `docs/superpowers/specs/2026-04-09-sqlparity-design.md`:

- **§2 Architecture:** if the .NET target framework changed, update the description of the project split.
- **§3 Identity, Labels:** if SSMS color inheritance is unreachable, replace the "attempts to read SSMS connection colors" paragraph with a clear statement that SQLParity uses its own tag palette only. Move the "Known unknown" callout into a "Resolved during Plan 0 spike" line.
- **§4 Comparison Workflow → Step 2:** if registered-servers integration is unreachable, replace the "picker defaults to SSMS registered servers list" line with "manual server entry only."
- **§6 Authentication in v1:** if Azure AD Interactive does not work with a single keyword, change "included if it works..." to a definitive "deferred to v1.1."
- **§6 Known Unknowns:** convert resolved items from "Known unknown" bullets to "Resolved by Plan 0 spike — see [spike doc](../spikes/2026-04-09-plan-0-spike-findings.md): <one-line summary>."

For action items that turned out to require **no** spec change (e.g., "API available, design unchanged"), simply update the Known Unknowns section to mark the item resolved.

- [ ] **Step 3: Re-read the spec for internal consistency**

After editing, skim §3, §4, and §6 once more. Check that no part of the spec still claims something the spike disproved.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-04-09-sqlparity-design.md
git commit -m "docs(spec): apply Plan 0 spike resolutions to SQLParity design"
```

---

## Task 15: Final verification — green build, green tests, clean tree

**Files:** none (verification only)

- [ ] **Step 1: Clean rebuild from scratch**

Run from `c:\Code\SQLCompare\`:

```bash
dotnet build SQLParity.sln --no-incremental
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. If the VSIX project requires legacy MSBuild, run that instead per Task 7, Step 6.

- [ ] **Step 2: Run all tests**

```bash
dotnet test SQLParity.sln
```

Expected: all unit tests and integration tests pass. The skipped Azure AD spike test (if not deleted) is the only acceptable skip.

- [ ] **Step 3: Verify clean git status**

```bash
git status
```

Expected: `nothing to commit, working tree clean`.

- [ ] **Step 4: Verify commit history is sane**

```bash
git log --oneline
```

Expected: a linear history of ~13-15 commits, one per task (or close to it), with descriptive messages. No "WIP," "fix," or "oops" commits.

- [ ] **Step 5: Tag the Plan 0 completion point**

```bash
git tag plan-0-complete
```

This gives Plan 1 a clear baseline to branch from and lets us roll back to a known-good state if anything in Plan 1 goes sideways.

---

## Plan 0 Acceptance Criteria

- ✅ `SQLParity.sln` builds cleanly (`dotnet build` or legacy MSBuild, per spike result)
- ✅ `SQLParity.Core.Tests` passes (`dotnet test` — 3 unit tests)
- ✅ `SQLParity.Core.IntegrationTests` passes (`dotnet test` — 1 LocalDB integration test)
- ✅ `SQLParity.Vsix` builds and the F5 launch into SSMS 22 works (or the spike doc explains why not)
- ✅ All four spike sections of `docs/superpowers/spikes/2026-04-09-plan-0-spike-findings.md` are filled in with no `TBD` markers remaining
- ✅ `docs/superpowers/specs/2026-04-09-sqlparity-design.md` reflects spike resolutions
- ✅ Git history is clean and tagged `plan-0-complete`
- ✅ Working tree is empty

---

## What Plan 1 inherits from Plan 0

- A solution that builds.
- A test harness with a working LocalDB integration fixture.
- Confirmed answers to .NET target, SMO compatibility, SSMS API reach, and Azure AD path.
- An updated spec that no longer hand-waves about known unknowns.
- The placeholder `SchemaReaderSmokeProbe` (deleted by Plan 1 Task 1).
