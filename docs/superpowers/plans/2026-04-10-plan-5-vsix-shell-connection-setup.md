# SQLParity — Plan 5: VSIX Shell — Package, Connection Setup, Confirmation Screen

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Register the SQLParity VSIX package in SSMS 22 with a Tools-menu entry point, build a WPF tool window with a two-panel connection setup UI and a confirmation screen, so the user can configure both sides of a comparison and proceed to "Begin Comparison."

**Architecture:** The VSIX shell is an old-style non-SDK csproj targeting .NET Framework 4.7.2. It references `SQLParity.Core` (net48 target) for all logic. UI is WPF/XAML with MVVM-lite (view-models with INotifyPropertyChanged, no framework dependency). SSMS registered-servers integration uses `Microsoft.SqlServer.Management.RegisteredServers.dll` from the SSMS 22 install path. All verification is manual via F5 launch into SSMS 22.

**Tech Stack:** C#, WPF/XAML, VS SDK (`Microsoft.VisualStudio.Shell`), SSMS SDK assemblies (referenced from install path, `CopyLocal=false`).

**Spec reference:** [design spec §2 (VSIX shell)](../specs/2026-04-09-sqlparity-design.md), [§3 (Identity, Labels, Colors)](../specs/2026-04-09-sqlparity-design.md), [§4 Steps 1-3 (Entry points, Connection setup, Confirmation)](../specs/2026-04-09-sqlparity-design.md).

**What Plan 5 inherits from Plan 4:**
- Complete Core library (net48 target): SchemaReader, Comparator, RiskClassifier, ScriptGenerator, LiveApplier, HistoryWriter, ProjectFileSerializer, EnvironmentTagStore, FilterSettings
- An empty VSIX project that builds with MSBuild and F5-launches SSMS 22
- 150 passing tests

**⚠️ VSIX plan differences from Core plans:**
- **No unit tests.** WPF code is verified manually via F5. The Core library is already tested.
- **Build requires MSBuild.exe** or building from Visual Studio. `dotnet build` will fail on the VSIX project.
- **Several tasks require Visual Studio GUI interaction** (adding references, setting build properties).
- **The plan includes exact C# and XAML code** but the implementer must adapt to what actually compiles in the old-style csproj + VS SDK environment. SMO and VS SDK types may differ from what's documented.

---

## File Structure

```
src/SQLParity.Vsix/
  SQLParity.Vsix.csproj                  MODIFY (add references, enable XAML, include assembly)
  source.extension.vsixmanifest          MODIFY (update metadata)
  Properties/AssemblyInfo.cs             KEEP (existing)
  SQLParityPackage.cs                    CREATE — AsyncPackage registration, menu commands
  ComparisonToolWindow.cs                CREATE — Tool window host (ToolWindowPane subclass)
  Views/
    ConnectionSetupView.xaml             CREATE — Two-panel connection setup UI
    ConnectionSetupView.xaml.cs          CREATE — Code-behind
    ConfirmationView.xaml                CREATE — Pre-comparison confirmation screen
    ConfirmationView.xaml.cs             CREATE — Code-behind
    ComparisonHostView.xaml              CREATE — Host view that switches between setup/confirm/results
    ComparisonHostView.xaml.cs           CREATE — Code-behind
  ViewModels/
    ConnectionSideViewModel.cs           CREATE — VM for one side of the connection setup
    ConnectionSetupViewModel.cs          CREATE — VM for the full setup (two sides)
    ConfirmationViewModel.cs             CREATE — VM for the confirmation screen
    ComparisonHostViewModel.cs           CREATE — VM that manages the workflow state machine
  Helpers/
    EnvironmentTagColors.cs              CREATE — Maps EnvironmentTag → WPF Color/Brush
    RelayCommand.cs                      CREATE — Simple ICommand implementation
    ViewModelBase.cs                     CREATE — INotifyPropertyChanged base class
```

---

## Task 1: Enable the VSIX project to include its assembly and reference SQLParity.Core

**Files:**
- Modify: `src/SQLParity.Vsix/SQLParity.Vsix.csproj`

The empty VSIX template was generated with `IncludeAssemblyInVSIXContainer=false` and `CopyBuildOutputToOutputDirectory=false`. We need to flip these so the extension's DLL and Core's DLL are actually deployed when F5 launches SSMS.

This task requires **Visual Studio GUI interaction** for adding the project reference to `SQLParity.Core`.

- [ ] **Step 1: Open the solution in Visual Studio 2022**

Open `c:\Code\SQLCompare\SQLParity.sln` in Visual Studio 2022.

- [ ] **Step 2: Add a project reference from VSIX to Core**

In Solution Explorer, right-click **SQLParity.Vsix** → **Add → Reference...** → **Projects** → check **SQLParity.Core** → click OK.

This adds a `<ProjectReference>` to the VSIX csproj pointing at `SQLParity.Core.csproj`.

- [ ] **Step 3: Edit the VSIX csproj to enable assembly inclusion**

In the VSIX csproj (`src/SQLParity.Vsix/SQLParity.Vsix.csproj`), change these properties from `false` to `true`:

```xml
<GeneratePkgDefFile>true</GeneratePkgDefFile>
<IncludeAssemblyInVSIXContainer>true</IncludeAssemblyInVSIXContainer>
<IncludeDebugSymbolsInVSIXContainer>true</IncludeDebugSymbolsInVSIXContainer>
<IncludeDebugSymbolsInLocalVSIXDeployment>true</IncludeDebugSymbolsInLocalVSIXDeployment>
<CopyBuildOutputToOutputDirectory>true</CopyBuildOutputToOutputDirectory>
<CopyOutputSymbolsToOutputDirectory>true</CopyOutputSymbolsToOutputDirectory>
```

Save the file.

- [ ] **Step 4: Build from Visual Studio**

Build the solution from VS (Ctrl+Shift+B). Verify 0 errors. The VSIX project should now produce a `.vsix` file that includes the `SQLParity.Vsix.dll` and `SQLParity.Core.dll`.

- [ ] **Step 5: Save and close Visual Studio, commit**

```bash
git add src/SQLParity.Vsix/SQLParity.Vsix.csproj
git commit -m "chore(vsix): enable assembly inclusion and add Core project reference"
```

---

## Task 2: MVVM helpers (ViewModelBase, RelayCommand, EnvironmentTagColors)

**Files:**
- Create: `src/SQLParity.Vsix/Helpers/ViewModelBase.cs`
- Create: `src/SQLParity.Vsix/Helpers/RelayCommand.cs`
- Create: `src/SQLParity.Vsix/Helpers/EnvironmentTagColors.cs`

These are small utility classes needed by all view-models. No VS SDK dependency — pure WPF.

- [ ] **Step 1: Create ViewModelBase**

Create `src/SQLParity.Vsix/Helpers/ViewModelBase.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SQLParity.Vsix.Helpers
{
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
```

- [ ] **Step 2: Create RelayCommand**

Create `src/SQLParity.Vsix/Helpers/RelayCommand.cs`:

```csharp
using System;
using System.Windows.Input;

namespace SQLParity.Vsix.Helpers
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object parameter) => _execute(parameter);
    }
}
```

- [ ] **Step 3: Create EnvironmentTagColors**

Create `src/SQLParity.Vsix/Helpers/EnvironmentTagColors.cs`:

```csharp
using System.Windows.Media;
using SQLParity.Core.Model;

namespace SQLParity.Vsix.Helpers
{
    /// <summary>
    /// Maps EnvironmentTag values to WPF colors per the design spec:
    /// PROD=red, STAGING=orange, DEV=green, SANDBOX=blue, UNTAGGED=gray.
    /// </summary>
    public static class EnvironmentTagColors
    {
        public static Color GetColor(EnvironmentTag tag)
        {
            switch (tag)
            {
                case EnvironmentTag.Prod: return Color.FromRgb(220, 53, 69);     // Red
                case EnvironmentTag.Staging: return Color.FromRgb(255, 152, 0);   // Orange
                case EnvironmentTag.Dev: return Color.FromRgb(40, 167, 69);       // Green
                case EnvironmentTag.Sandbox: return Color.FromRgb(0, 123, 255);   // Blue
                default: return Color.FromRgb(108, 117, 125);                     // Gray
            }
        }

        public static SolidColorBrush GetBrush(EnvironmentTag tag)
        {
            var brush = new SolidColorBrush(GetColor(tag));
            brush.Freeze();
            return brush;
        }
    }
}
```

- [ ] **Step 4: Build from Visual Studio or MSBuild**

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SQLParity.sln -t:Build -p:Configuration=Debug -v:minimal
```

Expected: `Build succeeded.`

If the build fails because the VSIX project doesn't find the `SQLParity.Core.Model` namespace (the `EnvironmentTag` type), ensure the project reference from Task 1 was saved correctly.

- [ ] **Step 5: Commit**

```bash
git add src/SQLParity.Vsix/Helpers/
git commit -m "feat(vsix): add MVVM helpers (ViewModelBase, RelayCommand, EnvironmentTagColors)"
```

---

## Task 3: SQLParityPackage — AsyncPackage registration with Tools menu command

**Files:**
- Create: `src/SQLParity.Vsix/SQLParityPackage.cs`
- Modify: `src/SQLParity.Vsix/source.extension.vsixmanifest`

This task creates the VS package class that registers the extension and adds a "SQLParity → New Comparison" command to the Tools menu.

- [ ] **Step 1: Update the VSIX manifest**

Replace the contents of `src/SQLParity.Vsix/source.extension.vsixmanifest` with:

```xml
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Id="SQLParity.214618a2-e13a-49d0-a25a-ac0f2ae6e811" Version="1.0.0" Language="en-US" Publisher="SQLParity" />
    <DisplayName>SQLParity</DisplayName>
    <Description>Schema comparison and sync for SQL Server databases.</Description>
    <Tags>SQL Server, Schema, Compare, Sync, Database</Tags>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[17.0, 19.0)">
      <ProductArchitecture>amd64</ProductArchitecture>
    </InstallationTarget>
  </Installation>
  <Dependencies>
    <Dependency Id="Microsoft.Framework.NDP" DisplayName="Microsoft .NET Framework" d:Source="Manual" Version="[4.5,)" />
  </Dependencies>
  <Prerequisites>
    <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="[17.0,19.0)" DisplayName="Visual Studio core editor" />
  </Prerequisites>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.VsPackage" d:Source="Project" d:ProjectName="%CurrentProject%" Path="|%CurrentProject%;PkgdefProjectOutputGroup|" />
  </Assets>
</PackageManifest>
```

Key changes from the template: updated DisplayName/Description, widened version range to `[17.0, 19.0)` (covers both VS 2022 and SSMS 22's 18.0 shell), and added the `Assets` section that tells the VSIX to include the package's pkgdef.

- [ ] **Step 2: Create the package class**

Create `src/SQLParity.Vsix/SQLParityPackage.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SQLParity.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ComparisonToolWindow), Style = VsDockStyle.Tabbed, DocumentLikeTool = true)]
    public sealed class SQLParityPackage : AsyncPackage
    {
        public const string PackageGuidString = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await NewComparisonCommand.InitializeAsync(this);
        }
    }
}
```

- [ ] **Step 3: Create the tool window class**

Create `src/SQLParity.Vsix/ComparisonToolWindow.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SQLParity.Vsix
{
    [Guid("b2c3d4e5-f6a7-8901-bcde-f12345678901")]
    public sealed class ComparisonToolWindow : ToolWindowPane
    {
        public ComparisonToolWindow() : base(null)
        {
            this.Caption = "SQLParity";
            this.Content = new Views.ComparisonHostView();
        }
    }
}
```

- [ ] **Step 4: Create the menu command class**

Create `src/SQLParity.Vsix/NewComparisonCommand.cs`:

```csharp
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SQLParity.Vsix
{
    internal sealed class NewComparisonCommand
    {
        public static readonly Guid CommandSet = new Guid("c3d4e5f6-a7b8-9012-cdef-123456789012");
        public const int CommandId = 0x0100;

        private readonly AsyncPackage _package;

        private NewComparisonCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                new NewComparisonCommand(package, commandService);
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = _package.FindToolWindow(typeof(ComparisonToolWindow), 0, true);
            if (window?.Frame == null)
                throw new NotSupportedException("Cannot create tool window");

            var windowFrame = (IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }
    }
}
```

- [ ] **Step 5: Create the .vsct command table file**

Create `src/SQLParity.Vsix/SQLParityPackage.vsct`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<CommandTable xmlns="http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable" xmlns:xs="http://www.w3.org/2001/XMLSchema">
  <Extern href="stdidcmd.h" />
  <Extern href="vsshlids.h" />

  <Commands package="guidSQLParityPackage">
    <Groups>
      <Group guid="guidSQLParityPackageCmdSet" id="SQLParityMenuGroup" priority="0x0600">
        <Parent guid="guidSHLMainMenu" id="IDM_VS_MENU_TOOLS" />
      </Group>
    </Groups>

    <Buttons>
      <Button guid="guidSQLParityPackageCmdSet" id="NewComparisonCommandId" priority="0x0100" type="Button">
        <Parent guid="guidSQLParityPackageCmdSet" id="SQLParityMenuGroup" />
        <Strings>
          <ButtonText>SQLParity: New Comparison</ButtonText>
        </Strings>
      </Button>
    </Buttons>
  </Commands>

  <Symbols>
    <GuidSymbol name="guidSQLParityPackage" value="{a1b2c3d4-e5f6-7890-abcd-ef1234567890}" />
    <GuidSymbol name="guidSQLParityPackageCmdSet" value="{c3d4e5f6-a7b8-9012-cdef-123456789012}">
      <IDSymbol name="SQLParityMenuGroup" value="0x1020" />
      <IDSymbol name="NewComparisonCommandId" value="0x0100" />
    </GuidSymbol>
  </Symbols>
</CommandTable>
```

- [ ] **Step 6: Add the .vsct file to the VSIX project**

In the VSIX csproj, add inside the `<ItemGroup>` that has `<Compile>`:

```xml
<VSCTCompile Include="SQLParityPackage.vsct">
  <ResourceName>Menus.ctmenu</ResourceName>
</VSCTCompile>
```

Also add the `.cs` files to the `<Compile>` item group:

```xml
<Compile Include="SQLParityPackage.cs" />
<Compile Include="ComparisonToolWindow.cs" />
<Compile Include="NewComparisonCommand.cs" />
<Compile Include="Helpers\ViewModelBase.cs" />
<Compile Include="Helpers\RelayCommand.cs" />
<Compile Include="Helpers\EnvironmentTagColors.cs" />
```

**Note:** In old-style csproj, files are NOT auto-discovered. Every `.cs` and `.xaml` file must be explicitly listed in the csproj. This is different from SDK-style projects where all files are auto-included. Every subsequent task that creates new files must also add them to the csproj.

- [ ] **Step 7: Build and F5 test**

Build from VS (Ctrl+Shift+B). If the build succeeds, F5 into SSMS 22. In SSMS, check:
- **Tools menu** should have "SQLParity: New Comparison" entry
- Clicking it should open a tool window titled "SQLParity" (it will be empty for now)

If F5 succeeds and the menu item appears: this is the validation checkpoint. Commit.

If the menu item does NOT appear: the package may not be loading. Check the SSMS Activity Log (`%APPDATA%\Microsoft\VisualStudio\18.0_*\ActivityLog.xml`) for errors mentioning SQLParity. Common issues:
- Package GUID mismatch between `.cs` and `.vsct`
- Missing `ProvideMenuResource` attribute
- The `Assets` section in the manifest is wrong

- [ ] **Step 8: Commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): register SQLParity package with Tools menu command and tool window"
```

---

## Task 4: Placeholder ComparisonHostView (empty host that we'll fill in)

**Files:**
- Create: `src/SQLParity.Vsix/Views/ComparisonHostView.xaml`
- Create: `src/SQLParity.Vsix/Views/ComparisonHostView.xaml.cs`
- Create: `src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs`

This is the top-level WPF UserControl that lives inside the tool window. It will eventually switch between ConnectionSetup, Confirmation, and Results states. For now, it just shows a placeholder message.

- [ ] **Step 1: Create the view-model**

Create `src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs`:

```csharp
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public enum WorkflowState
    {
        ConnectionSetup,
        Confirmation,
        Comparing,
        Results
    }

    public class ComparisonHostViewModel : ViewModelBase
    {
        private WorkflowState _currentState = WorkflowState.ConnectionSetup;

        public WorkflowState CurrentState
        {
            get => _currentState;
            set => SetProperty(ref _currentState, value);
        }

        private string _statusMessage = "Configure both connections to begin.";

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }
    }
}
```

- [ ] **Step 2: Create the XAML view**

Create `src/SQLParity.Vsix/Views/ComparisonHostView.xaml`:

```xml
<UserControl x:Class="SQLParity.Vsix.Views.ComparisonHostView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:SQLParity.Vsix.ViewModels"
             Background="{DynamicResource {x:Static SystemColors.WindowBrushKey}}">
    <UserControl.DataContext>
        <vm:ComparisonHostViewModel />
    </UserControl.DataContext>

    <Grid>
        <TextBlock Text="{Binding StatusMessage}"
                   FontSize="16"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   Foreground="{DynamicResource {x:Static SystemColors.WindowTextBrushKey}}" />
    </Grid>
</UserControl>
```

- [ ] **Step 3: Create the code-behind**

Create `src/SQLParity.Vsix/Views/ComparisonHostView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace SQLParity.Vsix.Views
{
    public partial class ComparisonHostView : UserControl
    {
        public ComparisonHostView()
        {
            InitializeComponent();
        }
    }
}
```

- [ ] **Step 4: Add files to the VSIX csproj**

Add to the `<Compile>` item group:

```xml
<Compile Include="ViewModels\ComparisonHostViewModel.cs" />
<Compile Include="Views\ComparisonHostView.xaml.cs">
  <DependentUpon>ComparisonHostView.xaml</DependentUpon>
</Compile>
```

Add a new `<ItemGroup>` for the XAML page:

```xml
<ItemGroup>
  <Page Include="Views\ComparisonHostView.xaml">
    <Generator>MSBuild:Compile</Generator>
    <SubType>Designer</SubType>
  </Page>
</ItemGroup>
```

- [ ] **Step 5: Build and F5 test**

Build and F5. In SSMS, click Tools → "SQLParity: New Comparison". The tool window should open and display "Configure both connections to begin."

- [ ] **Step 6: Commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add ComparisonHostView placeholder with workflow state machine"
```

---

## Task 5: ConnectionSideViewModel

**Files:**
- Create: `src/SQLParity.Vsix/ViewModels/ConnectionSideViewModel.cs`

This VM represents one side of the connection setup (server, auth, database, label, tag).

- [ ] **Step 1: Create the view-model**

Create `src/SQLParity.Vsix/ViewModels/ConnectionSideViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using SQLParity.Core.Model;
using SQLParity.Core.Project;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ConnectionSideViewModel : ViewModelBase
    {
        private string _serverName = string.Empty;
        private string _databaseName = string.Empty;
        private string _label = string.Empty;
        private EnvironmentTag _tag = EnvironmentTag.Untagged;
        private bool _useWindowsAuth = true;
        private string _sqlLogin = string.Empty;
        private string _sqlPassword = string.Empty;
        private bool _isConnected;

        public string ServerName
        {
            get => _serverName;
            set => SetProperty(ref _serverName, value);
        }

        public string DatabaseName
        {
            get => _databaseName;
            set => SetProperty(ref _databaseName, value);
        }

        public string Label
        {
            get => _label;
            set
            {
                if (SetProperty(ref _label, value))
                {
                    // Auto-suggest tag from label text
                    Tag = EnvironmentTagStore.SuggestTagFromLabel(value);
                }
            }
        }

        public EnvironmentTag Tag
        {
            get => _tag;
            set => SetProperty(ref _tag, value);
        }

        public bool UseWindowsAuth
        {
            get => _useWindowsAuth;
            set => SetProperty(ref _useWindowsAuth, value);
        }

        public string SqlLogin
        {
            get => _sqlLogin;
            set => SetProperty(ref _sqlLogin, value);
        }

        public string SqlPassword
        {
            get => _sqlPassword;
            set => SetProperty(ref _sqlPassword, value);
        }

        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }

        public ObservableCollection<string> AvailableDatabases { get; } = new ObservableCollection<string>();

        /// <summary>
        /// Returns true if all required fields are filled in.
        /// </summary>
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(ServerName) &&
            !string.IsNullOrWhiteSpace(DatabaseName) &&
            !string.IsNullOrWhiteSpace(Label);

        /// <summary>
        /// Builds a connection string from the current settings. Never returns
        /// a connection string with persisted credentials — this is for in-memory
        /// use only.
        /// </summary>
        public string BuildConnectionString()
        {
            var builder = new System.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = ServerName,
                InitialCatalog = DatabaseName,
                IntegratedSecurity = UseWindowsAuth,
                TrustServerCertificate = true,
            };

            if (!UseWindowsAuth)
            {
                builder.UserID = SqlLogin;
                builder.Password = SqlPassword;
            }

            return builder.ConnectionString;
        }

        /// <summary>
        /// Converts the current state to a ConnectionSide for project file persistence.
        /// </summary>
        public ConnectionSide ToConnectionSide()
        {
            return new ConnectionSide
            {
                ServerName = ServerName,
                DatabaseName = DatabaseName,
                Label = Label,
                Tag = Tag,
            };
        }
    }
}
```

**Note:** This uses `System.Data.SqlClient.SqlConnectionStringBuilder` (available in net472 without a NuGet package) rather than `Microsoft.Data.SqlClient.SqlConnectionStringBuilder` because the VSIX project targets net472 and referencing Microsoft.Data.SqlClient from the old-style csproj is more complex. The connection string format is identical.

- [ ] **Step 2: Add to csproj**

Add to `<Compile>` item group:

```xml
<Compile Include="ViewModels\ConnectionSideViewModel.cs" />
```

- [ ] **Step 3: Build**

Build from VS or MSBuild. Verify it compiles.

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Vsix/ViewModels/ConnectionSideViewModel.cs src/SQLParity.Vsix/SQLParity.Vsix.csproj
git commit -m "feat(vsix): add ConnectionSideViewModel with auth, label, tag auto-suggest"
```

---

## Task 6: ConnectionSetupView — two-panel connection UI

**Files:**
- Create: `src/SQLParity.Vsix/Views/ConnectionSetupView.xaml`
- Create: `src/SQLParity.Vsix/Views/ConnectionSetupView.xaml.cs`
- Create: `src/SQLParity.Vsix/ViewModels/ConnectionSetupViewModel.cs`

The connection setup screen has two large panels side-by-side (SideA and SideB), each with: server name, auth method, database, label, and tag. A "Continue" button at the bottom is enabled only when both sides are complete.

- [ ] **Step 1: Create ConnectionSetupViewModel**

Create `src/SQLParity.Vsix/ViewModels/ConnectionSetupViewModel.cs`:

```csharp
using System;
using System.Windows.Input;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ConnectionSetupViewModel : ViewModelBase
    {
        public ConnectionSideViewModel SideA { get; } = new ConnectionSideViewModel();
        public ConnectionSideViewModel SideB { get; } = new ConnectionSideViewModel();

        public ICommand ContinueCommand { get; }

        public event EventHandler ContinueRequested;

        public ConnectionSetupViewModel()
        {
            ContinueCommand = new RelayCommand(
                _ => ContinueRequested?.Invoke(this, EventArgs.Empty),
                _ => SideA.IsComplete && SideB.IsComplete);

            SideA.PropertyChanged += (_, __) => OnPropertyChanged(nameof(CanContinue));
            SideB.PropertyChanged += (_, __) => OnPropertyChanged(nameof(CanContinue));
        }

        public bool CanContinue => SideA.IsComplete && SideB.IsComplete;
    }
}
```

- [ ] **Step 2: Create the XAML view**

Create `src/SQLParity.Vsix/Views/ConnectionSetupView.xaml`:

```xml
<UserControl x:Class="SQLParity.Vsix.Views.ConnectionSetupView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:SQLParity.Vsix.ViewModels"
             xmlns:helpers="clr-namespace:SQLParity.Vsix.Helpers"
             Background="{DynamicResource {x:Static SystemColors.WindowBrushKey}}">

    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- Header -->
        <TextBlock Grid.Row="0" Text="Configure Both Connections"
                   FontSize="20" FontWeight="Bold" Margin="0,0,0,16"
                   Foreground="{DynamicResource {x:Static SystemColors.WindowTextBrushKey}}" />

        <!-- Two-panel connection setup -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="16" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <!-- Side A -->
            <GroupBox Grid.Column="0" Header="Side A" Padding="12">
                <StackPanel>
                    <TextBlock Text="Server:" Margin="0,0,0,4" />
                    <TextBox Text="{Binding SideA.ServerName, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,8" />

                    <CheckBox Content="Windows Authentication" IsChecked="{Binding SideA.UseWindowsAuth}" Margin="0,0,0,8" />

                    <StackPanel Visibility="{Binding SideA.UseWindowsAuth, Converter={StaticResource InverseBoolToVisibility}}"
                                Margin="0,0,0,8">
                        <TextBlock Text="Login:" Margin="0,0,0,4" />
                        <TextBox Text="{Binding SideA.SqlLogin, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,4" />
                        <TextBlock Text="Password:" Margin="0,0,0,4" />
                        <PasswordBox Margin="0,0,0,4" />
                    </StackPanel>

                    <TextBlock Text="Database:" Margin="0,0,0,4" />
                    <TextBox Text="{Binding SideA.DatabaseName, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,8" />

                    <TextBlock Text="Label (required):" Margin="0,0,0,4" />
                    <TextBox Text="{Binding SideA.Label, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,8"
                             FontWeight="Bold" FontSize="14" />

                    <TextBlock Text="Environment Tag:" Margin="0,0,0,4" />
                    <ComboBox ItemsSource="{Binding Source={helpers:EnumValues {x:Type core:EnvironmentTag}}}"
                              SelectedItem="{Binding SideA.Tag}" Margin="0,0,0,8" />
                </StackPanel>
            </GroupBox>

            <!-- Side B -->
            <GroupBox Grid.Column="2" Header="Side B" Padding="12">
                <StackPanel>
                    <TextBlock Text="Server:" Margin="0,0,0,4" />
                    <TextBox Text="{Binding SideB.ServerName, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,8" />

                    <CheckBox Content="Windows Authentication" IsChecked="{Binding SideB.UseWindowsAuth}" Margin="0,0,0,8" />

                    <TextBlock Text="Database:" Margin="0,0,0,4" />
                    <TextBox Text="{Binding SideB.DatabaseName, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,8" />

                    <TextBlock Text="Label (required):" Margin="0,0,0,4" />
                    <TextBox Text="{Binding SideB.Label, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,8"
                             FontWeight="Bold" FontSize="14" />

                    <TextBlock Text="Environment Tag:" Margin="0,0,0,4" />
                    <ComboBox SelectedItem="{Binding SideB.Tag}" Margin="0,0,0,8" />
                </StackPanel>
            </GroupBox>
        </Grid>

        <!-- Continue button -->
        <Button Grid.Row="2" Content="Continue →" Command="{Binding ContinueCommand}"
                HorizontalAlignment="Right" Padding="24,8" Margin="0,16,0,0"
                FontSize="14" />
    </Grid>
</UserControl>
```

**Note:** The XAML above is a starting point. The `EnumValues` markup extension and `InverseBoolToVisibility` converter may not exist yet. The implementer should simplify the XAML to whatever compiles — the important thing is that both panels render with text boxes for server/database/label and a Continue button. Polish comes later. If the enum binding or visibility converter causes issues, hardcode the tag options and remove the SQL-auth panel for now.

- [ ] **Step 3: Create the code-behind**

Create `src/SQLParity.Vsix/Views/ConnectionSetupView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace SQLParity.Vsix.Views
{
    public partial class ConnectionSetupView : UserControl
    {
        public ConnectionSetupView()
        {
            InitializeComponent();
        }
    }
}
```

- [ ] **Step 4: Add files to the csproj**

Add to `<Compile>`:
```xml
<Compile Include="ViewModels\ConnectionSetupViewModel.cs" />
<Compile Include="Views\ConnectionSetupView.xaml.cs">
  <DependentUpon>ConnectionSetupView.xaml</DependentUpon>
</Compile>
```

Add to the `<Page>` item group:
```xml
<Page Include="Views\ConnectionSetupView.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</Page>
```

- [ ] **Step 5: Wire ConnectionSetupView into ComparisonHostView**

Update `src/SQLParity.Vsix/Views/ComparisonHostView.xaml` to show the ConnectionSetupView when the state is `ConnectionSetup`. Replace the existing `<Grid>` content with:

```xml
<Grid>
    <views:ConnectionSetupView x:Name="SetupView"
        Visibility="{Binding IsSetupVisible, Converter={StaticResource BoolToVisibility}}" />
    <TextBlock Text="{Binding StatusMessage}"
               FontSize="16"
               HorizontalAlignment="Center"
               VerticalAlignment="Center"
               Visibility="{Binding IsSetupVisible, Converter={StaticResource InverseBoolToVisibility}}" />
</Grid>
```

**Or, more simply** (if converters aren't wired yet), just replace the TextBlock with the ConnectionSetupView for now:

```xml
<Grid>
    <views:ConnectionSetupView />
</Grid>
```

The full state-machine switching (setup → confirmation → results) will be wired in Task 7. For now, the tool window always shows the connection setup.

- [ ] **Step 6: Build and F5 test**

Build and F5. Click Tools → "SQLParity: New Comparison". The tool window should open and display the two-panel connection setup UI with text boxes for server, database, label, and a Continue button.

**Acceptable at this stage:** The UI may look rough — no polished styling, no database dropdown populated, no registered-servers picker. The point is that the two-panel layout renders and the Continue button enables when both labels are filled in.

- [ ] **Step 7: Commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add ConnectionSetupView with two-panel layout"
```

---

## Task 7: ConfirmationView and workflow state switching

**Files:**
- Create: `src/SQLParity.Vsix/Views/ConfirmationView.xaml`
- Create: `src/SQLParity.Vsix/Views/ConfirmationView.xaml.cs`
- Create: `src/SQLParity.Vsix/ViewModels/ConfirmationViewModel.cs`
- Modify: `src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs`
- Modify: `src/SQLParity.Vsix/Views/ComparisonHostView.xaml`

The confirmation screen shows both sides' identities (labels, colors, servers, databases, tags) in large, unmissable format with "Begin Comparison" and "Back" buttons.

- [ ] **Step 1: Create ConfirmationViewModel**

Create `src/SQLParity.Vsix/ViewModels/ConfirmationViewModel.cs`:

```csharp
using System;
using System.Windows.Input;
using System.Windows.Media;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ConfirmationViewModel : ViewModelBase
    {
        // Side A
        public string LabelA { get; set; } = string.Empty;
        public string ServerA { get; set; } = string.Empty;
        public string DatabaseA { get; set; } = string.Empty;
        public EnvironmentTag TagA { get; set; }
        public SolidColorBrush ColorA => EnvironmentTagColors.GetBrush(TagA);

        // Side B
        public string LabelB { get; set; } = string.Empty;
        public string ServerB { get; set; } = string.Empty;
        public string DatabaseB { get; set; } = string.Empty;
        public EnvironmentTag TagB { get; set; }
        public SolidColorBrush ColorB => EnvironmentTagColors.GetBrush(TagB);

        public ICommand BeginComparisonCommand { get; set; }
        public ICommand BackCommand { get; set; }

        public void PopulateFrom(ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
        {
            LabelA = sideA.Label;
            ServerA = sideA.ServerName;
            DatabaseA = sideA.DatabaseName;
            TagA = sideA.Tag;

            LabelB = sideB.Label;
            ServerB = sideB.ServerName;
            DatabaseB = sideB.DatabaseName;
            TagB = sideB.Tag;

            OnPropertyChanged(string.Empty); // Refresh all bindings
        }
    }
}
```

- [ ] **Step 2: Create the confirmation XAML**

Create `src/SQLParity.Vsix/Views/ConfirmationView.xaml`:

```xml
<UserControl x:Class="SQLParity.Vsix.Views.ConfirmationView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource {x:Static SystemColors.WindowBrushKey}}">

    <Grid Margin="32">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- Header -->
        <TextBlock Grid.Row="0" Text="Confirm Both Sides Before Comparing"
                   FontSize="22" FontWeight="Bold" Margin="0,0,0,24"
                   HorizontalAlignment="Center"
                   Foreground="{DynamicResource {x:Static SystemColors.WindowTextBrushKey}}" />

        <!-- Two-panel identity display -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="40" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <!-- Side A -->
            <Border Grid.Column="0" BorderThickness="3" CornerRadius="8" Padding="20"
                    BorderBrush="{Binding ColorA}">
                <StackPanel>
                    <TextBlock Text="{Binding LabelA}" FontSize="28" FontWeight="Bold"
                               Foreground="{Binding ColorA}" Margin="0,0,0,8" />
                    <TextBlock Text="{Binding TagA}" FontSize="14" Margin="0,0,0,16"
                               Foreground="{Binding ColorA}" />
                    <TextBlock Text="Server:" FontSize="12" Foreground="Gray" />
                    <TextBlock Text="{Binding ServerA}" FontSize="16" Margin="0,0,0,8" />
                    <TextBlock Text="Database:" FontSize="12" Foreground="Gray" />
                    <TextBlock Text="{Binding DatabaseA}" FontSize="16" />
                </StackPanel>
            </Border>

            <!-- Arrow between -->
            <TextBlock Grid.Column="1" Text="⟷" FontSize="32"
                       HorizontalAlignment="Center" VerticalAlignment="Center" />

            <!-- Side B -->
            <Border Grid.Column="2" BorderThickness="3" CornerRadius="8" Padding="20"
                    BorderBrush="{Binding ColorB}">
                <StackPanel>
                    <TextBlock Text="{Binding LabelB}" FontSize="28" FontWeight="Bold"
                               Foreground="{Binding ColorB}" Margin="0,0,0,8" />
                    <TextBlock Text="{Binding TagB}" FontSize="14" Margin="0,0,0,16"
                               Foreground="{Binding ColorB}" />
                    <TextBlock Text="Server:" FontSize="12" Foreground="Gray" />
                    <TextBlock Text="{Binding ServerB}" FontSize="16" Margin="0,0,0,8" />
                    <TextBlock Text="Database:" FontSize="12" Foreground="Gray" />
                    <TextBlock Text="{Binding DatabaseB}" FontSize="16" />
                </StackPanel>
            </Border>
        </Grid>

        <!-- Buttons -->
        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,24,0,0">
            <Button Content="← Back" Command="{Binding BackCommand}"
                    Padding="24,8" Margin="0,0,16,0" FontSize="14" />
            <Button Content="Begin Comparison" Command="{Binding BeginComparisonCommand}"
                    Padding="24,8" FontSize="14" FontWeight="Bold"
                    Background="#28a745" Foreground="White" />
        </StackPanel>
    </Grid>
</UserControl>
```

- [ ] **Step 3: Create the code-behind**

Create `src/SQLParity.Vsix/Views/ConfirmationView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace SQLParity.Vsix.Views
{
    public partial class ConfirmationView : UserControl
    {
        public ConfirmationView()
        {
            InitializeComponent();
        }
    }
}
```

- [ ] **Step 4: Update ComparisonHostViewModel with state transitions**

Replace `src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs`:

```csharp
using System;
using System.Windows.Input;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public enum WorkflowState
    {
        ConnectionSetup,
        Confirmation,
        Comparing,
        Results
    }

    public class ComparisonHostViewModel : ViewModelBase
    {
        private WorkflowState _currentState = WorkflowState.ConnectionSetup;

        public WorkflowState CurrentState
        {
            get => _currentState;
            set
            {
                if (SetProperty(ref _currentState, value))
                {
                    OnPropertyChanged(nameof(ShowSetup));
                    OnPropertyChanged(nameof(ShowConfirmation));
                    OnPropertyChanged(nameof(ShowComparing));
                    OnPropertyChanged(nameof(ShowResults));
                }
            }
        }

        public bool ShowSetup => CurrentState == WorkflowState.ConnectionSetup;
        public bool ShowConfirmation => CurrentState == WorkflowState.Confirmation;
        public bool ShowComparing => CurrentState == WorkflowState.Comparing;
        public bool ShowResults => CurrentState == WorkflowState.Results;

        public ConnectionSetupViewModel SetupViewModel { get; }
        public ConfirmationViewModel ConfirmationViewModel { get; }

        public ComparisonHostViewModel()
        {
            SetupViewModel = new ConnectionSetupViewModel();
            ConfirmationViewModel = new ConfirmationViewModel();

            SetupViewModel.ContinueRequested += OnContinueToConfirmation;

            ConfirmationViewModel.BackCommand = new RelayCommand(_ => CurrentState = WorkflowState.ConnectionSetup);
            ConfirmationViewModel.BeginComparisonCommand = new RelayCommand(_ => BeginComparison());
        }

        private void OnContinueToConfirmation(object sender, EventArgs e)
        {
            ConfirmationViewModel.PopulateFrom(SetupViewModel.SideA, SetupViewModel.SideB);
            CurrentState = WorkflowState.Confirmation;
        }

        private void BeginComparison()
        {
            // Plan 6 will implement the comparison execution and results view.
            // For now, just switch to Comparing state as a placeholder.
            CurrentState = WorkflowState.Comparing;
        }
    }
}
```

- [ ] **Step 5: Update ComparisonHostView to switch between views**

Replace `src/SQLParity.Vsix/Views/ComparisonHostView.xaml`:

```xml
<UserControl x:Class="SQLParity.Vsix.Views.ComparisonHostView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="clr-namespace:SQLParity.Vsix.Views"
             xmlns:vm="clr-namespace:SQLParity.Vsix.ViewModels"
             Background="{DynamicResource {x:Static SystemColors.WindowBrushKey}}">
    <UserControl.DataContext>
        <vm:ComparisonHostViewModel />
    </UserControl.DataContext>

    <Grid>
        <!-- Connection Setup -->
        <views:ConnectionSetupView DataContext="{Binding SetupViewModel}"
            Visibility="{Binding DataContext.ShowSetup, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />

        <!-- Confirmation -->
        <views:ConfirmationView DataContext="{Binding ConfirmationViewModel}"
            Visibility="{Binding DataContext.ShowConfirmation, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />

        <!-- Comparing (placeholder) -->
        <TextBlock Text="Comparing schemas..."
                   FontSize="18" HorizontalAlignment="Center" VerticalAlignment="Center"
                   Visibility="{Binding ShowComparing, Converter={StaticResource BoolToVisibility}}" />
    </Grid>
</UserControl>
```

**Note:** This requires a `BooleanToVisibilityConverter` resource. Add it to the UserControl's Resources:

```xml
<UserControl.Resources>
    <BooleanToVisibilityConverter x:Key="BoolToVisibility" />
</UserControl.Resources>
```

- [ ] **Step 6: Add all new files to the csproj**

Add to `<Compile>`:
```xml
<Compile Include="ViewModels\ConfirmationViewModel.cs" />
<Compile Include="Views\ConfirmationView.xaml.cs">
  <DependentUpon>ConfirmationView.xaml</DependentUpon>
</Compile>
```

Add to `<Page>` item group:
```xml
<Page Include="Views\ConfirmationView.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</Page>
```

- [ ] **Step 7: Build and F5 test**

Build and F5. The full workflow should now work:
1. Tools → "SQLParity: New Comparison" opens the tool window
2. The connection setup UI appears with two panels
3. Fill in both server names, database names, and labels
4. Click "Continue →" — transitions to the confirmation screen
5. The confirmation screen shows both labels, servers, databases with color-coded borders
6. Click "← Back" — returns to connection setup
7. Click "Begin Comparison" — shows "Comparing schemas..." placeholder

**This is the Plan 5 acceptance checkpoint.** The UI may be rough (no registered-servers dropdown, no database auto-population, no actual comparison running). Those are later plans. The workflow state machine works.

- [ ] **Step 8: Commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add ConfirmationView and wire up setup → confirmation → comparing workflow"
```

---

## Task 8: Final verification and tag

**Files:** none (verification only)

- [ ] **Step 1: Full solution build with MSBuild**

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SQLParity.sln -t:Build -p:Configuration=Debug -v:minimal
```

Expected: `Build succeeded.`

- [ ] **Step 2: Core tests still pass**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: 120 unit + 30 integration = 150 total, all passing. (The VSIX code has no automated tests.)

- [ ] **Step 3: F5 end-to-end verification**

In Visual Studio, F5 into SSMS 22. Verify the complete workflow:
1. Tools menu has "SQLParity: New Comparison"
2. Clicking it opens the tool window
3. Connection setup shows two panels
4. Filling in both sides and clicking Continue shows the confirmation screen
5. Back button returns to setup
6. Begin Comparison shows the comparing placeholder

- [ ] **Step 4: Verify clean git status**

```bash
git status
```

- [ ] **Step 5: Tag**

```bash
git tag plan-5-complete
```

---

## Plan 5 Acceptance Criteria

- ✅ SQLParity VSIX package registers in SSMS 22 (verified via F5)
- ✅ "SQLParity: New Comparison" appears in the Tools menu
- ✅ Clicking it opens a tool window
- ✅ Connection setup UI shows two panels (server, auth, database, label, tag)
- ✅ Continue button transitions to confirmation screen
- ✅ Confirmation screen shows both sides' identities with color-coded borders
- ✅ Back button returns to connection setup
- ✅ Begin Comparison button transitions to "Comparing" placeholder
- ✅ Core tests still pass (150 total)
- ✅ Full solution builds with MSBuild
- ✅ Git tagged `plan-5-complete`

---

## What Plan 5 does NOT include (deferred to later plans)

- SSMS registered-servers dropdown population (Plan 6 or later — requires SSMS SDK assembly references)
- Database dropdown auto-population after connecting (Plan 6 or later)
- Object Explorer right-click context menu integration (Plan 6 or later)
- `.sqlparity` project file open/save from the UI (Plan 6 or later)
- Actual comparison execution (Plan 6)
- The three-region results view (Plan 6)

## What Plan 6 inherits from Plan 5

- A registered VSIX package with a tool window and menu command
- WPF infrastructure: ViewModelBase, RelayCommand, EnvironmentTagColors
- A working workflow state machine: ConnectionSetup → Confirmation → Comparing
- ConnectionSideViewModel with server, auth, database, label, tag fields
- The full Core library wired as a project reference
