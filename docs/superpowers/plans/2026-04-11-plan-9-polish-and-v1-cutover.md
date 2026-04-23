# SQLParity — Plan 9: Polish & v1 Cutover

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the critical gaps that prevent v1 from being genuinely useful: proper ALTER TABLE generation for modified tables, external diff tool (WinMerge) support, duplicate label validation, pre-apply re-read safety check, progress bar during comparison, and direction-flip toast. Package the final VSIX for distribution.

**Architecture:** Mostly changes to existing Core and VSIX code. The ALTER TABLE generator is the largest new component — it lives in Core and produces column-level ALTER/ADD/DROP statements instead of the current full CREATE TABLE output. The external diff tool and progress bar are VSIX-side changes. All other items are small targeted fixes.

**Tech Stack:** C#, WPF/XAML, xUnit, SQLParity.Core, SQLParity.Vsix.

**Spec reference:** [design spec §5 (Script generation)](../specs/2026-04-09-sqlparity-design.md), [§7 (Detail views, External diff tool, Rename hints)](../specs/2026-04-09-sqlparity-design.md), [§4 Steps 6-8](../specs/2026-04-09-sqlparity-design.md).

**What Plan 9 inherits from Plan 6-8:**
- Fully functional end-to-end comparison and sync pipeline
- Results view with change tree, DDL detail panel, and action buttons
- Destructive gauntlet with label confirmation and countdown
- 150 passing Core tests

**Deferred to v1.1 (documented at end of this plan):**
- SSMS-style two-pane table tree view
- Registered-servers integration
- Filter bar (risk tier, status, text search)
- "Mark as ignored" functionality
- Rename hints
- Unsupported objects panel
- Object Explorer right-click context menu

---

## File Structure

```
src/SQLParity.Core/
  Sync/
    AlterTableGenerator.cs              CREATE — generates ALTER TABLE statements for column changes
    ScriptGenerator.cs                  MODIFY — use AlterTableGenerator for modified tables

tests/SQLParity.Core.Tests/
  Sync/
    AlterTableGeneratorTests.cs         CREATE — TDD tests for ALTER generation

src/SQLParity.Vsix/
  ViewModels/
    ComparisonHostViewModel.cs          MODIFY — add pre-apply re-read, progress, direction toast
    ConnectionSetupViewModel.cs         MODIFY — add duplicate label validation
    ResultsViewModel.cs                 MODIFY — add direction flip toast
    ExternalDiffViewModel.cs            CREATE — launch external diff tool
  Views/
    ComparisonHostView.xaml             MODIFY — add progress bar
    ConnectionSetupView.xaml            MODIFY — add duplicate label warning
    ResultsView.xaml                    MODIFY — add external diff button, progress bar
```

---

## Task 1: AlterTableGenerator — proper ALTER TABLE for modified tables (TDD)

**Files:**
- Create: `src/SQLParity.Core/Sync/AlterTableGenerator.cs`
- Create: `tests/SQLParity.Core.Tests/Sync/AlterTableGeneratorTests.cs`

The current ScriptGenerator emits the full SideA `CREATE TABLE` DDL for modified tables, which is useless — you can't run CREATE TABLE on a table that already exists. The AlterTableGenerator takes a `Change` with `ColumnChanges` and produces the correct ALTER statements:

- **New column** → `ALTER TABLE [schema].[table] ADD [col] type NULL/NOT NULL DEFAULT ...`
- **Dropped column** → `ALTER TABLE [schema].[table] DROP COLUMN [col]`
- **Modified column (type/nullability/size change)** → `ALTER TABLE [schema].[table] ALTER COLUMN [col] newtype NULL/NOT NULL`
- **Default constraint added** → `ALTER TABLE [schema].[table] ADD CONSTRAINT [name] DEFAULT (expr) FOR [col]`
- **Default constraint dropped** → `ALTER TABLE [schema].[table] DROP CONSTRAINT [name]`
- **Default constraint changed** → drop old + add new

- [ ] **Step 1: Write the failing tests**

Create `tests/SQLParity.Core.Tests/Sync/AlterTableGeneratorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class AlterTableGeneratorTests
{
    private static ColumnModel MakeCol(string name, string type = "Int", int maxLen = 0,
        bool nullable = false, DefaultConstraintModel dc = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "Orders", name),
        Name = name, DataType = type, MaxLength = maxLen, Precision = 0, Scale = 0,
        IsNullable = nullable, IsIdentity = false, IdentitySeed = 0, IdentityIncrement = 0,
        IsComputed = false, ComputedText = null, IsPersisted = false, Collation = null,
        DefaultConstraint = dc, OrdinalPosition = 0,
    };

    private static ColumnChange MakeColumnChange(ChangeStatus status, ColumnModel sideA, ColumnModel sideB) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "Orders", sideA?.Name ?? sideB?.Name ?? "Col"),
        ColumnName = sideA?.Name ?? sideB?.Name ?? "Col",
        Status = status,
        SideA = sideA,
        SideB = sideB,
    };

    [Fact]
    public void NewNullableColumn_GeneratesAddColumn()
    {
        var col = MakeCol("Notes", "NVarChar", 500, nullable: true);
        var change = MakeColumnChange(ChangeStatus.New, col, null);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER TABLE [dbo].[Orders]", sql);
        Assert.Contains("ADD", sql);
        Assert.Contains("[Notes]", sql);
        Assert.Contains("NULL", sql);
    }

    [Fact]
    public void NewNotNullColumnWithDefault_GeneratesAddWithDefault()
    {
        var dc = new DefaultConstraintModel { Name = "DF_Orders_Status", Definition = "('Active')" };
        var col = MakeCol("Status", "NVarChar", 50, nullable: false, dc: dc);
        var change = MakeColumnChange(ChangeStatus.New, col, null);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ADD", sql);
        Assert.Contains("[Status]", sql);
        Assert.Contains("NOT NULL", sql);
        Assert.Contains("DEFAULT", sql);
    }

    [Fact]
    public void DroppedColumn_GeneratesDropColumn()
    {
        var col = MakeCol("OldCol", "Int");
        var change = MakeColumnChange(ChangeStatus.Dropped, null, col);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER TABLE [dbo].[Orders]", sql);
        Assert.Contains("DROP COLUMN", sql);
        Assert.Contains("[OldCol]", sql);
    }

    [Fact]
    public void ModifiedColumn_TypeChange_GeneratesAlterColumn()
    {
        var colA = MakeCol("Price", "Decimal", maxLen: 0);
        var colB = MakeCol("Price", "Int", maxLen: 0);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER TABLE [dbo].[Orders]", sql);
        Assert.Contains("ALTER COLUMN", sql);
        Assert.Contains("[Price]", sql);
    }

    [Fact]
    public void ModifiedColumn_Widened_GeneratesAlterColumn()
    {
        var colA = MakeCol("Name", "NVarChar", 200, nullable: false);
        var colB = MakeCol("Name", "NVarChar", 100, nullable: false);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER COLUMN", sql);
        Assert.Contains("NVARCHAR(200)", sql.ToUpperInvariant());
    }

    [Fact]
    public void ModifiedColumn_NullabilityChange_GeneratesAlterColumn()
    {
        var colA = MakeCol("Name", "NVarChar", 100, nullable: true);
        var colB = MakeCol("Name", "NVarChar", 100, nullable: false);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER COLUMN", sql);
        Assert.Contains("NULL", sql);
    }

    [Fact]
    public void ModifiedTable_GeneratesAllColumnAlters()
    {
        var changes = new List<ColumnChange>
        {
            MakeColumnChange(ChangeStatus.New, MakeCol("NewCol", "Int", nullable: true), null),
            MakeColumnChange(ChangeStatus.Dropped, null, MakeCol("OldCol")),
        };

        var sql = AlterTableGenerator.GenerateForModifiedTable("dbo", "Orders", changes);

        Assert.Contains("ADD", sql);
        Assert.Contains("[NewCol]", sql);
        Assert.Contains("DROP COLUMN", sql);
        Assert.Contains("[OldCol]", sql);
    }

    [Fact]
    public void DefaultConstraintAdded_GeneratesAddConstraint()
    {
        var dcA = new DefaultConstraintModel { Name = "DF_Price", Definition = "((0))" };
        var colA = MakeCol("Price", "Decimal", dc: dcA);
        var colB = MakeCol("Price", "Decimal");
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ADD CONSTRAINT [DF_Price]", sql);
        Assert.Contains("DEFAULT", sql);
    }

    [Fact]
    public void DefaultConstraintRemoved_GeneratesDropConstraint()
    {
        var dcB = new DefaultConstraintModel { Name = "DF_Price", Definition = "((0))" };
        var colA = MakeCol("Price", "Decimal");
        var colB = MakeCol("Price", "Decimal", dc: dcB);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("DROP CONSTRAINT [DF_Price]", sql);
    }
}
```

- [ ] **Step 2: Implement AlterTableGenerator**

Create `src/SQLParity.Core/Sync/AlterTableGenerator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

/// <summary>
/// Generates ALTER TABLE statements for column-level changes on modified tables.
/// Replaces the naive "emit full CREATE TABLE" approach with proper incremental DDL.
/// </summary>
public static class AlterTableGenerator
{
    /// <summary>
    /// Generates all ALTER statements for a modified table's column changes.
    /// Returns a multi-statement SQL string separated by GO.
    /// </summary>
    public static string GenerateForModifiedTable(string schema, string table, IReadOnlyList<ColumnChange> columnChanges)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- ALTER statements for [{schema}].[{table}]");

        foreach (var colChange in columnChanges)
        {
            var sql = GenerateColumnAlter(schema, table, colChange);
            if (!string.IsNullOrWhiteSpace(sql))
            {
                sb.AppendLine(sql);
                sb.AppendLine("GO");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates the ALTER statement for a single column change.
    /// </summary>
    public static string GenerateColumnAlter(string schema, string table, ColumnChange change)
    {
        var prefix = $"ALTER TABLE [{schema}].[{table}]";

        switch (change.Status)
        {
            case ChangeStatus.New:
                return GenerateAddColumn(prefix, change.SideA);

            case ChangeStatus.Dropped:
                return GenerateDropColumn(prefix, change.SideB);

            case ChangeStatus.Modified:
                return GenerateAlterColumn(prefix, change.SideA, change.SideB);

            default:
                return string.Empty;
        }
    }

    private static string GenerateAddColumn(string prefix, ColumnModel col)
    {
        var sb = new StringBuilder();
        sb.Append($"{prefix} ADD [{col.Name}] {FormatDataType(col)}");
        sb.Append(col.IsNullable ? " NULL" : " NOT NULL");

        if (col.DefaultConstraint != null)
        {
            sb.Append($" CONSTRAINT [{col.DefaultConstraint.Name}] DEFAULT {col.DefaultConstraint.Definition}");
        }

        return sb.ToString();
    }

    private static string GenerateDropColumn(string prefix, ColumnModel col)
    {
        var sb = new StringBuilder();

        // Drop default constraint first if one exists
        if (col.DefaultConstraint != null)
        {
            sb.AppendLine($"{prefix} DROP CONSTRAINT [{col.DefaultConstraint.Name}]");
            sb.AppendLine("GO");
        }

        sb.Append($"{prefix} DROP COLUMN [{col.Name}]");
        return sb.ToString();
    }

    private static string GenerateAlterColumn(string prefix, ColumnModel colA, ColumnModel colB)
    {
        var sb = new StringBuilder();

        // Handle default constraint changes first
        var dcA = colA.DefaultConstraint;
        var dcB = colB.DefaultConstraint;

        if (dcB != null && dcA == null)
        {
            // Default removed
            sb.AppendLine($"{prefix} DROP CONSTRAINT [{dcB.Name}]");
            sb.AppendLine("GO");
        }
        else if (dcB != null && dcA != null
            && !string.Equals(dcA.Definition, dcB.Definition, StringComparison.OrdinalIgnoreCase))
        {
            // Default changed — drop old, add new after ALTER COLUMN
            sb.AppendLine($"{prefix} DROP CONSTRAINT [{dcB.Name}]");
            sb.AppendLine("GO");
        }

        // ALTER COLUMN for type/size/nullability changes
        bool typeChanged = !string.Equals(colA.DataType, colB.DataType, StringComparison.OrdinalIgnoreCase)
            || colA.MaxLength != colB.MaxLength
            || colA.Precision != colB.Precision
            || colA.Scale != colB.Scale
            || colA.IsNullable != colB.IsNullable;

        if (typeChanged)
        {
            sb.AppendLine($"{prefix} ALTER COLUMN [{colA.Name}] {FormatDataType(colA)}{(colA.IsNullable ? " NULL" : " NOT NULL")}");
            sb.AppendLine("GO");
        }

        // Add new default constraint if needed
        if (dcA != null && dcB == null)
        {
            // Default added
            sb.AppendLine($"{prefix} ADD CONSTRAINT [{dcA.Name}] DEFAULT {dcA.Definition} FOR [{colA.Name}]");
        }
        else if (dcA != null && dcB != null
            && !string.Equals(dcA.Definition, dcB.Definition, StringComparison.OrdinalIgnoreCase))
        {
            // Default changed — add new (old was dropped above)
            sb.AppendLine($"{prefix} ADD CONSTRAINT [{dcA.Name}] DEFAULT {dcA.Definition} FOR [{colA.Name}]");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatDataType(ColumnModel col)
    {
        var type = col.DataType.ToUpperInvariant();

        // Types that use MaxLength
        if (type == "NVARCHAR" || type == "VARCHAR" || type == "NCHAR" || type == "CHAR"
            || type == "VARBINARY" || type == "BINARY")
        {
            var len = col.MaxLength <= 0 ? "MAX" : col.MaxLength.ToString();
            return $"{type}({len})";
        }

        // Types that use Precision and Scale
        if (type == "DECIMAL" || type == "NUMERIC")
        {
            return $"{type}({col.Precision},{col.Scale})";
        }

        // Types with no parameters
        return type;
    }
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
```

Expected: all pass (~129 total).

- [ ] **Step 4: Wire into ScriptGenerator**

Modify `src/SQLParity.Core/Sync/ScriptGenerator.cs`. Change the `GetSql` method to use `AlterTableGenerator` for modified tables:

Replace the line:
```csharp
if (change.Status == ChangeStatus.New || change.Status == ChangeStatus.Modified)
{
    return change.DdlSideA ?? string.Empty;
}
```

With:
```csharp
if (change.Status == ChangeStatus.New)
{
    return change.DdlSideA ?? string.Empty;
}

if (change.Status == ChangeStatus.Modified)
{
    // For modified tables with column changes, generate ALTER statements
    if (change.ObjectType == ObjectType.Table && change.ColumnChanges.Count > 0)
    {
        return AlterTableGenerator.GenerateForModifiedTable(
            change.Id.Schema, change.Id.Name, change.ColumnChanges);
    }

    // For routines (views, procs, functions), use SideA DDL
    // which is the CREATE OR ALTER / CREATE statement
    return change.DdlSideA ?? string.Empty;
}
```

Also make the same change in `LiveApplier.cs`'s `GetSqlForChange` method.

- [ ] **Step 5: Run all tests, commit**

```bash
dotnet test SQLParity.sln
git add src/SQLParity.Core/ tests/SQLParity.Core.Tests/
git commit -m "feat(core): add AlterTableGenerator and wire into ScriptGenerator for modified tables"
```

---

## Task 2: Duplicate label validation

**Files:**
- Modify: `src/SQLParity.Vsix/ViewModels/ConnectionSetupViewModel.cs`
- Modify: `src/SQLParity.Vsix/Views/ConnectionSetupView.xaml`

- [ ] **Step 1: Add validation to ConnectionSetupViewModel**

In `ConnectionSetupViewModel`, update the `ContinueCommand` CanExecute to also check that labels are different. Add a `DuplicateLabelWarning` property:

```csharp
public bool HasDuplicateLabels =>
    !string.IsNullOrWhiteSpace(SideA.Label)
    && !string.IsNullOrWhiteSpace(SideB.Label)
    && string.Equals(SideA.Label.Trim(), SideB.Label.Trim(), StringComparison.OrdinalIgnoreCase);

public string DuplicateLabelWarning => HasDuplicateLabels
    ? "Both sides have the same label. Labels must be different to avoid confusion."
    : string.Empty;
```

Update `CanContinue` to include `!HasDuplicateLabels`. Subscribe to both SideA and SideB PropertyChanged to re-evaluate.

- [ ] **Step 2: Add warning text to ConnectionSetupView.xaml**

Add a `TextBlock` near the Continue button that shows the warning in red when labels match:

```xml
<TextBlock Text="{Binding DuplicateLabelWarning}"
           Foreground="Red" FontWeight="Bold"
           Visibility="{Binding HasDuplicateLabels, Converter={StaticResource BoolToVis}}"
           Margin="0,8,0,0" />
```

- [ ] **Step 3: Build, commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add duplicate label validation — both sides must have different labels"
```

---

## Task 3: External diff tool (WinMerge) launcher

**Files:**
- Create: `src/SQLParity.Vsix/Helpers/ExternalDiffLauncher.cs`
- Modify: `src/SQLParity.Vsix/Views/ResultsView.xaml`

- [ ] **Step 1: Create ExternalDiffLauncher**

Create `src/SQLParity.Vsix/Helpers/ExternalDiffLauncher.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;

namespace SQLParity.Vsix.Helpers
{
    /// <summary>
    /// Launches an external diff tool (defaults to WinMerge) with two DDL strings.
    /// Writes DDL to temp files, launches the tool, and schedules cleanup.
    /// </summary>
    public static class ExternalDiffLauncher
    {
        private static readonly string[] WinMergePaths =
        {
            @"C:\Program Files\WinMerge\WinMergeU.exe",
            @"C:\Program Files (x86)\WinMerge\WinMergeU.exe",
        };

        public static bool TryLaunch(string ddlA, string ddlB, string labelA, string labelB)
        {
            var winMergePath = FindWinMerge();
            if (winMergePath == null)
            {
                System.Windows.MessageBox.Show(
                    "WinMerge not found. Install WinMerge or configure an external diff tool.",
                    "SQLParity — External Diff",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return false;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "SQLParity_Diff");
            Directory.CreateDirectory(tempDir);

            var timestamp = DateTime.Now.ToString("HHmmss");
            var fileA = Path.Combine(tempDir, $"{labelA}_{timestamp}.sql");
            var fileB = Path.Combine(tempDir, $"{labelB}_{timestamp}.sql");

            File.WriteAllText(fileA, ddlA ?? string.Empty);
            File.WriteAllText(fileB, ddlB ?? string.Empty);

            try
            {
                var args = $"\"{fileA}\" \"{fileB}\" " +
                           $"/dl \"{labelA}\" /dr \"{labelB}\" " +
                           $"/e /u";
                Process.Start(winMergePath, args);
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to launch WinMerge:\n{ex.Message}",
                    "SQLParity — External Diff",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return false;
            }
        }

        private static string FindWinMerge()
        {
            foreach (var path in WinMergePaths)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}
```

- [ ] **Step 2: Add "Open in WinMerge" button to ResultsView**

In the detail panel section of `ResultsView.xaml`, add a button:

```xml
<Button Content="Open in WinMerge"
        Click="OpenInWinMerge_Click"
        Padding="12,4" Margin="0,4,0,0"
        HorizontalAlignment="Left" />
```

In `ResultsView.xaml.cs`, add the click handler:

```csharp
private void OpenInWinMerge_Click(object sender, RoutedEventArgs e)
{
    var vm = DataContext as ResultsViewModel;
    if (vm?.SelectedChange == null) return;

    var dir = vm.Direction;
    var labelA = dir.LabelA ?? "Side A";
    var labelB = dir.LabelB ?? "Side B";

    ExternalDiffLauncher.TryLaunch(
        vm.SelectedChange.DdlSideA,
        vm.SelectedChange.DdlSideB,
        labelA, labelB);
}
```

- [ ] **Step 3: Add to csproj, build, commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add WinMerge external diff tool launcher"
```

---

## Task 4: Progress bar during comparison

**Files:**
- Modify: `src/SQLParity.Vsix/Views/ComparisonHostView.xaml`

- [ ] **Step 1: Add an indeterminate ProgressBar to the Comparing state**

In `ComparisonHostView.xaml`, find the Comparing state section and add a ProgressBar below the progress text:

```xml
<!-- In the Comparing state area -->
<StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
    <TextBlock Text="{Binding ProgressText}"
               FontSize="14" HorizontalAlignment="Center" Margin="0,0,0,12" />
    <ProgressBar IsIndeterminate="True"
                 Width="300" Height="20" />
</StackPanel>
```

This gives visual feedback that work is happening, even though we can't report precise progress (SMO doesn't give per-object callbacks).

- [ ] **Step 2: Build, commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add indeterminate progress bar during comparison"
```

---

## Task 5: Pre-apply re-read safety check

**Files:**
- Modify: `src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs`

Before either Generate Script or Apply Live runs, re-read the destination schema and check if it changed since the comparison. If anything changed, abort and show a warning.

- [ ] **Step 1: Add re-read check method**

In `ComparisonHostViewModel`, add a method that re-reads the destination schema and compares it to the stored version:

```csharp
private async Task<bool> CheckDestinationUnchangedAsync()
{
    try
    {
        var dir = ResultsViewModel.Direction;
        var destSide = dir.Direction == SyncDirection.AtoB ? SetupViewModel.SideB : SetupViewModel.SideA;
        var connStr = destSide.BuildConnectionString();

        ProgressText = "Verifying destination has not changed...";
        var currentSchema = await Task.Run(() => new SchemaReader(connStr, destSide.DatabaseName).ReadSchema());

        var originalSchema = dir.Direction == SyncDirection.AtoB
            ? ResultsViewModel.ComparisonResult.SideB
            : ResultsViewModel.ComparisonResult.SideA;

        // Quick check: compare table counts and a few key properties
        if (currentSchema.Tables.Count != originalSchema.Tables.Count
            || currentSchema.Views.Count != originalSchema.Views.Count
            || currentSchema.StoredProcedures.Count != originalSchema.StoredProcedures.Count)
        {
            MessageBox.Show(
                "The destination database has changed since you reviewed it.\n\n" +
                "Please run the comparison again before applying changes.",
                "SQLParity — Destination Changed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ProgressText = string.Empty;
            return false;
        }

        ProgressText = string.Empty;
        return true;
    }
    catch (Exception ex)
    {
        MessageBox.Show(
            $"Could not verify destination:\n{ex.Message}\n\nProceeding without verification.",
            "SQLParity — Warning",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        ProgressText = string.Empty;
        return true; // Allow proceeding if re-read fails
    }
}
```

- [ ] **Step 2: Call before GenerateScript and ApplyLive**

Add `if (!await CheckDestinationUnchangedAsync()) return;` as the first line in both `GenerateScript` and `ApplyLive` methods. These methods need to become `async` if they aren't already.

- [ ] **Step 3: Build, commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add pre-apply destination re-read safety check"
```

---

## Task 6: Direction flip toast

**Files:**
- Modify: `src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs`
- Modify: `src/SQLParity.Vsix/Views/ResultsView.xaml`

When the user flips the sync direction, show a brief message indicating how many changes are now destructive.

- [ ] **Step 1: Add toast property and direction-changed handler**

In `ResultsViewModel`, subscribe to `Direction.DirectionChanged` and compute a toast message:

```csharp
private string _toastMessage = string.Empty;

public string ToastMessage
{
    get => _toastMessage;
    set => SetProperty(ref _toastMessage, value);
}

// In Populate() or constructor, subscribe:
Direction.DirectionChanged += (s, e) =>
{
    // Recalculate summary
    OnPropertyChanged(nameof(SummaryText));

    // Show toast
    var destructiveCount = _comparisonResult?.DestructiveCount ?? 0;
    ToastMessage = $"Direction flipped. {destructiveCount} changes are destructive.";

    // Auto-clear toast after 4 seconds
    var timer = new System.Windows.Threading.DispatcherTimer
    {
        Interval = TimeSpan.FromSeconds(4)
    };
    timer.Tick += (_, __) => { ToastMessage = string.Empty; timer.Stop(); };
    timer.Start();
};
```

- [ ] **Step 2: Add toast display to ResultsView.xaml**

Add a TextBlock in the identity bar area that shows the toast:

```xml
<TextBlock Text="{Binding ToastMessage}"
           Foreground="DarkOrange" FontWeight="Bold" FontSize="12"
           HorizontalAlignment="Center"
           Visibility="{Binding ToastMessage, Converter={StaticResource StringToVisibility}}" />
```

If a `StringToVisibilityConverter` is too complex, just let it show as empty text when there's no toast — the space will collapse naturally if the text is empty and the TextBlock has no fixed size.

- [ ] **Step 3: Build, commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add direction-flip toast showing destructive change count"
```

---

## Task 7: Update design spec with v1 status

**Files:**
- Modify: `docs/superpowers/specs/2026-04-09-sqlparity-design.md`

- [ ] **Step 1: Add a "v1 Status" section to the spec**

At the top of the spec (after the header), add:

```markdown
### v1 Implementation Status

**Completed:**
- Schema reading for all v1 object types (SMO-based)
- Schema comparison with column-level diffing
- Four-tier risk classification (Safe, Caution, Risky, Destructive)
- Pre-flight data-loss quantification queries
- ALTER TABLE generation for column changes
- Script generation with dependency ordering and header banner
- Live apply with per-change transactions and stop-on-failure
- Project file persistence (.sqlparity JSON)
- Environment tag management with label-based auto-suggestion
- SSMS 22 VSIX package with Tools menu entry
- Connection setup with Windows + SQL auth
- Confirmation screen with color-coded labels
- Results view with change tree and DDL detail panel
- Sync direction selection with PROD live-apply block
- Destructive gauntlet (review + label typing + 3-second countdown)
- External diff tool (WinMerge) launcher
- Pre-apply destination re-read safety check
- Auto-saved history (scripts + apply records)

**Deferred to v1.1:**
- SSMS-style two-pane table tree view (using DDL diff for now)
- Registered-servers integration
- Database dropdown auto-population
- Filter bar (by risk tier, status, text search)
- "Mark as ignored" functionality
- Rename hints
- Unsupported objects panel
- Object Explorer right-click context menu
- Azure AD Interactive authentication
- Synchronized scrolling in detail views
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/
git commit -m "docs(spec): add v1 implementation status section"
```

---

## Task 8: Final build, test, and tag

**Files:** none (verification only)

- [ ] **Step 1: Full Core tests**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: all pass (now ~159+ with new AlterTableGenerator tests).

- [ ] **Step 2: Full solution build**

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SQLParity.sln -t:Rebuild -p:Configuration=Debug -v:minimal
```

- [ ] **Step 3: Reinstall VSIX and verify**

PowerShell:
```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /uninstall:SQLParity.214618a2-e13a-49d0-a25a-ac0f2ae6e811
# Wait, then:
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /instanceIds:919b8d66 "C:\Code\SQLCompare\src\SQLParity.Vsix\bin\Debug\SQLParity.Vsix.vsix"
```

Verify in SSMS 22:
1. Compare two databases with column differences
2. The generated script should contain ALTER TABLE statements (not CREATE TABLE)
3. Entering the same label for both sides shows a red warning and blocks Continue
4. Progress bar shows during comparison
5. "Open in WinMerge" button works on a selected change
6. Direction flip shows a toast message

- [ ] **Step 4: Clean git status, tag**

```bash
git status  # should be clean
git tag v1.0-rc1
```

---

## Plan 9 Acceptance Criteria

- ✅ Modified tables generate ALTER TABLE/ADD/DROP COLUMN/ALTER COLUMN statements
- ✅ Default constraint changes generate proper DROP + ADD CONSTRAINT
- ✅ Duplicate labels blocked with red warning
- ✅ WinMerge external diff tool launches with DDL temp files
- ✅ Indeterminate progress bar during comparison
- ✅ Pre-apply re-read verifies destination hasn't changed
- ✅ Direction flip shows toast with destructive count
- ✅ Design spec updated with v1 implementation status
- ✅ All Core tests pass
- ✅ Full solution builds
- ✅ Git tagged `v1.0-rc1`

---

## v1.1 Wishlist (documented, not implemented)

These features were designed in the spec but deferred from v1 to keep the initial release focused:

1. **SSMS-style two-pane table tree view** — Replace DDL diff for tables with a tree that mirrors SSMS Object Explorer (Columns, Keys, Constraints, Triggers, Indexes) with green/red/yellow highlighting and strikethrough for deleted items.
2. **Registered-servers integration** — Populate the server picker from SSMS's registered servers list via `RegisteredServersGroup` API.
3. **Database dropdown** — After connecting to a server, auto-populate a dropdown of available databases.
4. **Filter bar** — Filter the change tree by risk tier, status (new/modified/dropped), and text search.
5. **"Mark as ignored"** — Right-click a change to permanently ignore it; persisted in the .sqlparity project file.
6. **Rename hints** — When a dropped column and a new column in the same table have matching type/nullability, show an inline hint suggesting it might be a rename.
7. **Unsupported objects panel** — List objects SQLParity encountered but can't compare (CLR, Service Broker, etc.) so the user knows they weren't silently skipped.
8. **Object Explorer right-click** — "Compare with..." context menu on database nodes.
9. **Azure AD Interactive auth** — Requires end-to-end verification with an Azure AD-enabled target.
10. **Synchronized scrolling** — In the DDL diff view, scrolling one side scrolls the other.
11. **Configurable external diff tool** — Settings page under Tools → Options → SQLParity instead of hardcoded WinMerge path.
