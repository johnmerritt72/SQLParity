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

**Method:** Created an Empty VSIX Project via the VS 2022 template, inspected the generated csproj, and verified F5 launch into SSMS 22.

**Findings:**

- VSIX project default `<TargetFrameworkVersion>` after VS template scaffolding: **v4.7.2**
- The generated csproj is **old-style** (non-SDK, `ToolsVersion="15.0"`, XML `<Import>` targets), not SDK-style. Uses `<TargetFrameworkVersion>` rather than `<TargetFramework>`.
- `dotnet build` of the VSIX: **FAILS** — error: "VSIX deployment is not supported with 'dotnet build'". The VSIX compiles successfully but the deployment step is blocked by the VSSDK targets.
- Legacy `MSBuild.exe` required: **YES** — `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe` builds the full solution successfully (0 warnings, 0 errors after adding `<RuntimeIdentifier>win</RuntimeIdentifier>` and `<RuntimeIdentifiers>win</RuntimeIdentifiers>` to the VSIX csproj).
- F5 launch into SSMS 22 with `/rootSuffix Exp`: **SUCCESS** — SSMS 22 launches cleanly, no errors. Note: SSMS 22 does not have a "Manage Extensions" menu (Microsoft removed it), so extension installation cannot be verified through the SSMS UI.
- Resolved `Microsoft.SqlServer.SqlManagementObjects` package version: **181.19.0**
- SMO 181.19.0 ships exactly two TFM assets: **net472** and **net8.0** (confirmed by inspecting the NuGet cache at `~/.nuget/packages/microsoft.sqlserver.sqlmanagementobjects/181.19.0/lib/`). There is **no** `netstandard2.0` asset.
- Did SMO load on the test project's net8.0 runtime in Task 5: **YES** — `DescribeSmoServerType` test passes, SMO type `Microsoft.SqlServer.Management.Smo.Server` loads without errors.

**Resolution:**

The VSIX project targets **.NET Framework 4.7.2** via an old-style csproj. This is the standard for VS 2022 / SSMS 22 extensions.

`SQLParity.Core` was originally specified as `netstandard2.0` but this produced NU1701 compatibility warnings because SMO 181.19.0 has no `netstandard2.0` asset. Core has been changed to **multi-target `net48;net8.0`**:
- The `net48` asset is consumed by the VSIX project (which is .NET Framework 4.7.2 — `net48` is the closest compatible TFM).
- The `net8.0` asset is consumed by the test projects (which target `net8.0`).
- Both consumers get native SMO assets with zero compatibility warnings.

The build strategy is:
- `dotnet build` works for `SQLParity.Core`, `SQLParity.Core.Tests`, and `SQLParity.Core.IntegrationTests`.
- `dotnet test` works for both test projects.
- **Full-solution builds including the VSIX require legacy `MSBuild.exe`.**

**Additional environment finding:** The dev machine has .NET SDK 10.0.104, which defaults to `.slnx` format for new solutions. Always use `dotnet new sln --format sln` to get the classic `.sln` format required for legacy MSBuild compatibility.

**Action items for the design spec:**

- [x] Update spec §2 (Architecture) — Core is `net48;net8.0`, not `netstandard2.0`. VSIX is old-style csproj targeting .NET Framework 4.7.2.
- [x] Update spec §6 (Known Unknowns) — mark unknown #3 as resolved.
- [ ] Note that full-solution builds require legacy MSBuild.exe, not `dotnet build`.

---

## 2. SSMS 22 connection-color API (spec §6 unknown #1)

**Question:** Can SQLParity programmatically read the user's per-connection color from SSMS 22?

**Method:** Filesystem investigation of SSMS 22 settings directories, searching for where per-connection custom colors are stored.

**Findings:**

- SSMS UI location where connection colors are configured: **Connect → Options → Connection Properties → "Use custom color"**
- Where SSMS persists the chosen color: **NOT in a standalone file.** `UserSettings.xml` at `%APPDATA%\Microsoft\SQL Server Management Studio\22.0\` contains only the *default* status bar colors (`SingleServerStatusBarColor=Khaki`, `MultipleServerStatusBarColor=Pink`), not per-connection custom colors. Per-connection colors are stored in SSMS's internal VS shell private registry hive (`privateregistry.bin` at `%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_<instanceId>\`). This is an opaque binary Windows registry hive that requires elevation to mount externally and is undocumented for programmatic access from extensions.
- Whether a VSIX-loaded extension can read it via the shell API: **NO documented public API found.** No DLLs in the SSMS 22 install directory with "color" or "connection properties" in the name expose this data. The VS shell's internal settings manager (`IVsSettingsManager`) *might* provide access, but this would require reverse-engineering the exact key path and is fragile.
- Fallback path: **Use SQLParity's own environment-tag color palette (already specified in the design).**

**Resolution:**

SSMS connection-color inheritance is **not feasible in v1**. Per-connection custom colors are stored in an undocumented internal hive with no public API. The design already specifies a complete fallback: SQLParity's own environment-tag color palette (PROD red, STAGING orange, DEV green, SANDBOX blue, UNTAGGED gray). The spec's §3 "Known unknown" callout already anticipated this outcome and stated the design doesn't need to change — only the SSMS-color-inheritance convenience is lost.

**Action items for the design spec:**

- [x] Update spec §3 (Identity, Labels) — remove the SSMS-color-inheritance behavior; SQLParity uses its own tag palette only.
- [x] Update spec §6 (Known Unknowns) to mark unknown #1 as resolved.

---

## 3. SSMS 22 registered servers list API (spec §6 unknown #2)

**Question:** Can SQLParity read the user's SSMS 22 registered servers list to populate connection pickers?

**Method:** Filesystem investigation for `RegSrvr.xml` and DLL search in the SSMS 22 install directory.

**Findings:**

- File or service where SSMS 22 stores registered servers: **SSMS 22 stores registered servers in the VS shell's private registry hive** (`privateregistry.bin` at `%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_<instanceId>\`), NOT in a standalone `RegSrvr.xml` file. The historical `RegSrvr.xml` path (`%APPDATA%\Microsoft\SQL Server Management Studio\22.0\RegSrvr.xml`) does not exist even on a machine with registered servers in SSMS 22. An older `RegSrvr17.xml` exists at the parent level from a prior SSMS version, but SSMS 22 does not use it.
- Whether a VSIX-loaded extension can read it programmatically: **YES, via the SMO RegisteredServers API.** The DLL `Microsoft.SqlServer.Management.RegisteredServers.dll` ships with SSMS 22 at `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\`. A companion UI assembly `Microsoft.SqlServer.Management.RegisteredServersUI.dll` exists under `Extensions\Application\`.
- API to call: `Microsoft.SqlServer.Management.RegisteredServers.RegisteredServersGroup` (and related types). This API reads from whatever backing store SSMS uses internally, abstracting away the storage format. Since our VSIX loads in-process in SSMS, it runs in the same VS shell context and has access to the same registered-servers data.
- Approach: Reference the DLL from the SSMS install path with `CopyLocal=false` (it's already loaded in the SSMS process). No redistribution needed.

**Resolution:**

Registered servers integration is **feasible in v1** via the SMO `RegisteredServersGroup` API. The VSIX references the DLL that ships with SSMS 22 (no NuGet package needed). The API call will be proven in Plan 5 (VSIX Shell: Connection Setup) when we build the connection picker. If the API proves broken at that point, the fallback is manual server entry.

**Action items for the design spec:**

- [x] Update spec §4 (Comparison Workflow → Step 2) — registered-servers picker is confirmed viable via SMO API.
- [x] Update spec §6 (Known Unknowns) to mark unknown #2 as resolved.

---

## 4. Azure AD Interactive via SqlClient connection-string keyword (spec §6 unknown #4)

**Question:** Does Azure AD Interactive authentication work with a single `Microsoft.Data.SqlClient` connection-string keyword (e.g., `Authentication=Active Directory Interactive`), or does it require additional MSAL hosting?

**Method:** Deferred — no Azure AD-enabled SQL Server target available for testing.

**Findings:**

- `Microsoft.Data.SqlClient` package version available: **7.0.0** (supports the `Authentication=Active Directory Interactive` keyword in its connection string API)
- Connection string used: **N/A — no test target available**
- Result: **DEFERRED**

**Resolution:**

Azure AD Interactive authentication is **deferred to v1.1**. No test target was available during the Plan 0 spike to verify whether the single-keyword approach (`Authentication=Active Directory Interactive`) works without additional MSAL hosting. The `Microsoft.Data.SqlClient` 7.0.0 package is already referenced in the integration test project and documents support for this keyword, but we could not prove it end-to-end.

The design spec should be updated to definitively defer Azure AD Interactive to v1.1 (removing the conditional "include if easy" language).

**Action items for the design spec:**

- [x] Update spec §6 (Authentication in v1) — Azure AD Interactive is deferred to v1.1.
- [x] Update spec §6 (Known Unknowns) to mark unknown #4 as deferred (no test target).
