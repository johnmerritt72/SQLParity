# SQLParity — Plan 6-8: Results View, Detail Views, Destructive Gauntlet & Sync Wiring

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the "Begin Comparison" button to the Core library's SchemaReader + SchemaComparator pipeline, display comparison results in a three-region layout (identity bar, change tree, detail panel), implement the sync actions (Generate Script, Apply Live) with the destructive-change gauntlet, and enforce the PROD live-apply block.

**Architecture:** The ComparisonHostViewModel orchestrates the pipeline: reads both schemas on a background thread with progress reporting, runs the comparator, and populates a ResultsViewModel. The results view shows a TreeView of changes grouped by object type, a detail panel showing DDL diffs, and action buttons. The gauntlet is a sequence of WPF dialogs. All logic lives in the Core library; the VSIX shell is thin UI wiring.

**Tech Stack:** C#, WPF/XAML, SQLParity.Core (SchemaReader, SchemaComparator, ScriptGenerator, LiveApplier, HistoryWriter), VS SDK.

**Spec reference:** [design spec §3 (Identity bar, Direction)](../specs/2026-04-09-sqlparity-design.md), [§4 Steps 4-9](../specs/2026-04-09-sqlparity-design.md), [§5 (Gauntlet)](../specs/2026-04-09-sqlparity-design.md), [§7 (Detail views)](../specs/2026-04-09-sqlparity-design.md).

**What this plan inherits from Plan 5:**
- Working VSIX with Tools-menu entry, tool window, connection setup, and confirmation screen
- Workflow state machine: ConnectionSetup → Confirmation → Comparing → Results
- ComparisonHostViewModel, ConnectionSideViewModel, ConnectionSetupViewModel, ConfirmationViewModel
- All Core library features (SchemaReader, Comparator, RiskClassifier, ScriptGenerator, LiveApplier, HistoryWriter, ProjectFileSerializer, EnvironmentTagStore)
- 150 passing Core tests

**⚠️ VSIX plan rules (same as Plan 5):**
- No unit tests for WPF code. Verified manually via VSIX reinstall into SSMS 22.
- Build with MSBuild.exe. Deploy via VSIXInstaller PowerShell commands.
- Old-style csproj: every new file must be added to `<Compile>` or `<Page>` ItemGroups.

**Deferred to Plan 9 (polish):**
- External diff tool (WinMerge) integration
- Full SSMS-style two-pane table tree view (this plan uses DDL diff for all object types)
- Rename hints
- Filter bar (risk tier, status, text search)
- "Mark as ignored" functionality
- "Unsupported objects" panel
- Synchronized scrolling in detail views
- Registered-servers integration / database dropdown
- Object Explorer right-click context menu
- Pre-apply re-read of destination schema

---

## File Structure

```
src/SQLParity.Vsix/
  ViewModels/
    ComparisonHostViewModel.cs            MODIFY — add background comparison, results state
    ResultsViewModel.cs                   CREATE — wraps ComparisonResult for the results view
    ChangeTreeItemViewModel.cs            CREATE — tree item for the change tree (groups + leaves)
    DetailPanelViewModel.cs               CREATE — shows DDL diff for selected change
    SyncDirectionViewModel.cs             CREATE — manages direction state + arrow display
    GauntletViewModel.cs                  CREATE — drives the destructive gauntlet dialogs
  Views/
    ResultsView.xaml + .xaml.cs           CREATE — three-region results layout
    IdentityBarView.xaml + .xaml.cs       CREATE — persistent top bar with labels/colors/direction
    ChangeTreeView.xaml + .xaml.cs        CREATE — left panel: grouped change tree
    DetailPanelView.xaml + .xaml.cs       CREATE — right panel: DDL diff display
    ActionBarView.xaml + .xaml.cs         CREATE — bottom bar: Generate Script / Apply Live
    GauntletReviewDialog.xaml + .xaml.cs  CREATE — destructive changes review + label confirmation
    GauntletFinalDialog.xaml + .xaml.cs   CREATE — 3-second last-chance dialog
    ComparisonHostView.xaml               MODIFY — add ResultsView state
  Helpers/
    BoolToVisibilityInverter.cs           CREATE — inverts BooleanToVisibilityConverter
```

---

## Task 1: SyncDirectionViewModel + direction logic

**Files:**
- Create: `src/SQLParity.Vsix/ViewModels/SyncDirectionViewModel.cs`

The sync direction controls which side is the "source" (read-from) and which is the "destination" (written-to). Direction is unset initially. The user must pick a direction before any sync action is enabled.

- [ ] **Step 1: Create the view-model**

Create `src/SQLParity.Vsix/ViewModels/SyncDirectionViewModel.cs`:

```csharp
using System;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public enum SyncDirection
    {
        Unset,
        AtoB,
        BtoA
    }

    public class SyncDirectionViewModel : ViewModelBase
    {
        private SyncDirection _direction = SyncDirection.Unset;
        private string _labelA = string.Empty;
        private string _labelB = string.Empty;
        private EnvironmentTag _tagA;
        private EnvironmentTag _tagB;

        public SyncDirection Direction
        {
            get => _direction;
            set
            {
                if (SetProperty(ref _direction, value))
                {
                    OnPropertyChanged(nameof(IsSet));
                    OnPropertyChanged(nameof(ArrowText));
                    OnPropertyChanged(nameof(DestinationLabel));
                    OnPropertyChanged(nameof(DestinationTag));
                    OnPropertyChanged(nameof(IsDestinationProd));
                    DirectionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string LabelA
        {
            get => _labelA;
            set => SetProperty(ref _labelA, value);
        }

        public string LabelB
        {
            get => _labelB;
            set => SetProperty(ref _labelB, value);
        }

        public EnvironmentTag TagA
        {
            get => _tagA;
            set => SetProperty(ref _tagA, value);
        }

        public EnvironmentTag TagB
        {
            get => _tagB;
            set => SetProperty(ref _tagB, value);
        }

        public bool IsSet => Direction != SyncDirection.Unset;

        public string ArrowText
        {
            get
            {
                switch (Direction)
                {
                    case SyncDirection.AtoB: return $"{LabelA}  ──▶  {LabelB}";
                    case SyncDirection.BtoA: return $"{LabelB}  ──▶  {LabelA}";
                    default: return "Select sync direction";
                }
            }
        }

        public string DestinationLabel => Direction == SyncDirection.AtoB ? LabelB
            : Direction == SyncDirection.BtoA ? LabelA : string.Empty;

        public EnvironmentTag DestinationTag => Direction == SyncDirection.AtoB ? TagB
            : Direction == SyncDirection.BtoA ? TagA : EnvironmentTag.Untagged;

        public bool IsDestinationProd => DestinationTag == EnvironmentTag.Prod;

        public event EventHandler DirectionChanged;

        public RelayCommand SetAtoBCommand => new RelayCommand(_ => Direction = SyncDirection.AtoB);
        public RelayCommand SetBtoACommand => new RelayCommand(_ => Direction = SyncDirection.BtoA);
        public RelayCommand FlipCommand => new RelayCommand(_ =>
        {
            Direction = Direction == SyncDirection.AtoB ? SyncDirection.BtoA : SyncDirection.AtoB;
        }, _ => IsSet);

        public void PopulateFrom(ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
        {
            LabelA = sideA.Label;
            LabelB = sideB.Label;
            TagA = sideA.Tag;
            TagB = sideB.Tag;
        }
    }
}
```

- [ ] **Step 2: Add to csproj, build, commit**

Add `<Compile Include="ViewModels\SyncDirectionViewModel.cs" />` to the csproj.

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SQLParity.sln -t:Build -p:Configuration=Debug -v:minimal
```

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add SyncDirectionViewModel with direction state and PROD detection"
```

---

## Task 2: ChangeTreeItemViewModel + ResultsViewModel

**Files:**
- Create: `src/SQLParity.Vsix/ViewModels/ChangeTreeItemViewModel.cs`
- Create: `src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs`

- [ ] **Step 1: Create ChangeTreeItemViewModel**

Create `src/SQLParity.Vsix/ViewModels/ChangeTreeItemViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    /// <summary>
    /// A node in the change tree. Can be a group header (object type or schema)
    /// or a leaf (individual change).
    /// </summary>
    public class ChangeTreeItemViewModel : ViewModelBase
    {
        private bool _isSelected;
        private bool _isChecked = true;

        public string DisplayName { get; set; } = string.Empty;
        public string StatusIcon { get; set; } = string.Empty;
        public string RiskIcon { get; set; } = string.Empty;
        public bool IsGroup { get; set; }
        public Change Change { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }

        public ObservableCollection<ChangeTreeItemViewModel> Children { get; }
            = new ObservableCollection<ChangeTreeItemViewModel>();

        public static string GetStatusIcon(ChangeStatus status)
        {
            switch (status)
            {
                case ChangeStatus.New: return "+";
                case ChangeStatus.Modified: return "~";
                case ChangeStatus.Dropped: return "−";
                default: return "?";
            }
        }

        public static string GetRiskIcon(RiskTier tier)
        {
            switch (tier)
            {
                case RiskTier.Safe: return "🟢";
                case RiskTier.Caution: return "🟡";
                case RiskTier.Risky: return "🟠";
                case RiskTier.Destructive: return "🔴";
                default: return "⚪";
            }
        }
    }
}
```

- [ ] **Step 2: Create ResultsViewModel**

Create `src/SQLParity.Vsix/ViewModels/ResultsViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ResultsViewModel : ViewModelBase
    {
        private ChangeTreeItemViewModel _selectedTreeItem;
        private Change _selectedChange;
        private ComparisonResult _comparisonResult;

        public SyncDirectionViewModel Direction { get; } = new SyncDirectionViewModel();

        public ObservableCollection<ChangeTreeItemViewModel> TreeItems { get; }
            = new ObservableCollection<ChangeTreeItemViewModel>();

        public ChangeTreeItemViewModel SelectedTreeItem
        {
            get => _selectedTreeItem;
            set
            {
                if (SetProperty(ref _selectedTreeItem, value))
                {
                    SelectedChange = value?.Change;
                }
            }
        }

        public Change SelectedChange
        {
            get => _selectedChange;
            set
            {
                if (SetProperty(ref _selectedChange, value))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectedDdlA));
                    OnPropertyChanged(nameof(SelectedDdlB));
                    OnPropertyChanged(nameof(SelectedObjectName));
                    OnPropertyChanged(nameof(SelectedRiskText));
                }
            }
        }

        public bool HasSelection => SelectedChange != null;
        public string SelectedDdlA => SelectedChange?.DdlSideA ?? string.Empty;
        public string SelectedDdlB => SelectedChange?.DdlSideB ?? string.Empty;
        public string SelectedObjectName => SelectedChange?.Id.ToString() ?? string.Empty;
        public string SelectedRiskText => SelectedChange != null
            ? $"[{SelectedChange.Risk}] {SelectedChange.Status} — {string.Join("; ", SelectedChange.Reasons.Select(r => r.Description))}"
            : string.Empty;

        // Summary
        public string SummaryText
        {
            get
            {
                if (_comparisonResult == null) return string.Empty;
                var total = _comparisonResult.TotalCount;
                var selected = TreeItems.Sum(CountChecked);
                return $"{total} differences — {_comparisonResult.SafeCount} safe, {_comparisonResult.CautionCount} caution, " +
                       $"{_comparisonResult.RiskyCount} risky, {_comparisonResult.DestructiveCount} destructive. " +
                       $"{selected} selected for sync.";
            }
        }

        public ComparisonResult ComparisonResult => _comparisonResult;

        public void Populate(ComparisonResult result, ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
        {
            _comparisonResult = result;
            Direction.PopulateFrom(sideA, sideB);

            TreeItems.Clear();

            // Group changes by ObjectType
            var groups = result.Changes
                .GroupBy(c => c.ObjectType)
                .OrderBy(g => g.Key.ToString());

            foreach (var group in groups)
            {
                var groupItem = new ChangeTreeItemViewModel
                {
                    DisplayName = $"{group.Key} ({group.Count()})",
                    IsGroup = true,
                };

                foreach (var change in group.OrderBy(c => c.Id.ToString()))
                {
                    groupItem.Children.Add(new ChangeTreeItemViewModel
                    {
                        DisplayName = change.Id.ToString(),
                        StatusIcon = ChangeTreeItemViewModel.GetStatusIcon(change.Status),
                        RiskIcon = ChangeTreeItemViewModel.GetRiskIcon(change.Risk),
                        IsGroup = false,
                        Change = change,
                    });
                }

                TreeItems.Add(groupItem);
            }

            OnPropertyChanged(nameof(SummaryText));
        }

        public IEnumerable<Change> GetSelectedChanges()
        {
            foreach (var group in TreeItems)
            {
                foreach (var item in group.Children)
                {
                    if (item.IsChecked && item.Change != null)
                        yield return item.Change;
                }
            }
        }

        private static int CountChecked(ChangeTreeItemViewModel item)
        {
            if (!item.IsGroup) return item.IsChecked ? 1 : 0;
            return item.Children.Sum(CountChecked);
        }
    }
}
```

- [ ] **Step 3: Add to csproj, build, commit**

Add both `<Compile>` entries. Build. Commit:

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add ResultsViewModel and ChangeTreeItemViewModel for comparison results"
```

---

## Task 3: Wire background comparison into ComparisonHostViewModel

**Files:**
- Modify: `src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs`

When the user clicks "Begin Comparison", the host VM runs the SchemaReader + SchemaComparator on a background thread and transitions to the Results state.

- [ ] **Step 1: Update ComparisonHostViewModel**

Replace `src/SQLParity.Vsix/ViewModels/ComparisonHostViewModel.cs` with:

```csharp
using System;
using System.Threading.Tasks;
using System.Windows;
using SQLParity.Core;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
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
        private string _statusMessage = "Configure both connections to begin.";
        private string _progressText = string.Empty;

        public ComparisonHostViewModel()
        {
            SetupViewModel = new ConnectionSetupViewModel();
            ConfirmationViewModel = new ConfirmationViewModel();
            ResultsViewModel = new ResultsViewModel();

            SetupViewModel.ContinueRequested += (s, e) =>
            {
                ConfirmationViewModel.PopulateFrom(SetupViewModel.SideA, SetupViewModel.SideB);
                CurrentState = WorkflowState.Confirmation;
            };

            ConfirmationViewModel.BackRequested += (s, e) =>
            {
                CurrentState = WorkflowState.ConnectionSetup;
            };

            ConfirmationViewModel.BeginComparisonRequested += (s, e) =>
            {
                CurrentState = WorkflowState.Comparing;
                RunComparisonAsync();
            };

            GenerateScriptCommand = new RelayCommand(_ => GenerateScript(),
                _ => CurrentState == WorkflowState.Results && ResultsViewModel.Direction.IsSet);

            ApplyLiveCommand = new RelayCommand(_ => ApplyLive(),
                _ => CurrentState == WorkflowState.Results
                     && ResultsViewModel.Direction.IsSet
                     && !ResultsViewModel.Direction.IsDestinationProd);

            NewComparisonCommand = new RelayCommand(_ =>
            {
                CurrentState = WorkflowState.ConnectionSetup;
            });
        }

        public ConnectionSetupViewModel SetupViewModel { get; }
        public ConfirmationViewModel ConfirmationViewModel { get; }
        public ResultsViewModel ResultsViewModel { get; }

        public RelayCommand GenerateScriptCommand { get; }
        public RelayCommand ApplyLiveCommand { get; }
        public RelayCommand NewComparisonCommand { get; }

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

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value);
        }

        private async void RunComparisonAsync()
        {
            try
            {
                ProgressText = "Reading schema from Side A...";

                var connA = SetupViewModel.SideA.BuildConnectionString();
                var dbA = SetupViewModel.SideA.DatabaseName;
                var connB = SetupViewModel.SideB.BuildConnectionString();
                var dbB = SetupViewModel.SideB.DatabaseName;

                DatabaseSchema schemaA = null;
                DatabaseSchema schemaB = null;
                ComparisonResult result = null;

                await Task.Run(() =>
                {
                    schemaA = new SchemaReader(connA, dbA).ReadSchema();
                });

                ProgressText = "Reading schema from Side B...";

                await Task.Run(() =>
                {
                    schemaB = new SchemaReader(connB, dbB).ReadSchema();
                });

                ProgressText = "Comparing schemas...";

                await Task.Run(() =>
                {
                    result = SchemaComparator.Compare(schemaA, schemaB);
                });

                ResultsViewModel.Populate(result, SetupViewModel.SideA, SetupViewModel.SideB);
                CurrentState = WorkflowState.Results;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Comparison failed: {ex.Message}";
                CurrentState = WorkflowState.Confirmation;
                MessageBox.Show($"Comparison failed:\n\n{ex.Message}", "SQLParity",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateScript()
        {
            try
            {
                var selectedChanges = ResultsViewModel.GetSelectedChanges();
                var ordered = SQLParity.Core.Sync.DependencyOrderer.Order(selectedChanges);

                var dir = ResultsViewModel.Direction;
                var options = new SQLParity.Core.Sync.ScriptGenerationOptions
                {
                    SourceLabel = dir.Direction == SyncDirection.AtoB ? dir.LabelA : dir.LabelB,
                    SourceServer = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideA.ServerName : SetupViewModel.SideB.ServerName,
                    SourceDatabase = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideA.DatabaseName : SetupViewModel.SideB.DatabaseName,
                    DestinationLabel = dir.DestinationLabel,
                    DestinationServer = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideB.ServerName : SetupViewModel.SideA.ServerName,
                    DestinationDatabase = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideB.DatabaseName : SetupViewModel.SideA.DatabaseName,
                };

                var script = SQLParity.Core.Sync.ScriptGenerator.Generate(ordered, options);

                // Show save dialog
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Sync Script",
                    Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
                    DefaultExt = ".sql",
                    FileName = $"SQLParity_Sync_{DateTime.Now:yyyy-MM-dd_HHmmss}.sql",
                };

                if (saveDialog.ShowDialog() == true)
                {
                    System.IO.File.WriteAllText(saveDialog.FileName, script.SqlText);
                    MessageBox.Show($"Script saved to:\n{saveDialog.FileName}\n\n" +
                        $"{script.TotalChanges} changes, {script.DestructiveChanges} destructive.",
                        "SQLParity — Script Generated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Script generation failed:\n\n{ex.Message}", "SQLParity",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyLive()
        {
            // Check for destructive changes first
            var selectedChanges = System.Linq.Enumerable.ToList(ResultsViewModel.GetSelectedChanges());
            var hasDestructive = System.Linq.Enumerable.Any(selectedChanges, c => c.Risk == RiskTier.Destructive);

            if (hasDestructive)
            {
                // Show the gauntlet
                var gauntletVm = new GauntletViewModel();
                gauntletVm.Populate(selectedChanges, ResultsViewModel.Direction.DestinationLabel,
                    ResultsViewModel.Direction.DestinationTag);

                var reviewDialog = new Views.GauntletReviewDialog();
                reviewDialog.DataContext = gauntletVm;
                reviewDialog.Owner = Application.Current.MainWindow;
                if (reviewDialog.ShowDialog() != true)
                    return;

                var finalDialog = new Views.GauntletFinalDialog();
                finalDialog.DataContext = gauntletVm;
                finalDialog.Owner = Application.Current.MainWindow;
                if (finalDialog.ShowDialog() != true)
                    return;
            }

            try
            {
                var ordered = SQLParity.Core.Sync.DependencyOrderer.Order(selectedChanges);
                var dir = ResultsViewModel.Direction;
                var destConn = dir.Direction == SyncDirection.AtoB
                    ? SetupViewModel.SideB.BuildConnectionString()
                    : SetupViewModel.SideA.BuildConnectionString();

                var options = new SQLParity.Core.Sync.ScriptGenerationOptions
                {
                    SourceLabel = dir.Direction == SyncDirection.AtoB ? dir.LabelA : dir.LabelB,
                    SourceServer = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideA.ServerName : SetupViewModel.SideB.ServerName,
                    SourceDatabase = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideA.DatabaseName : SetupViewModel.SideB.DatabaseName,
                    DestinationLabel = dir.DestinationLabel,
                    DestinationServer = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideB.ServerName : SetupViewModel.SideA.ServerName,
                    DestinationDatabase = dir.Direction == SyncDirection.AtoB
                        ? SetupViewModel.SideB.DatabaseName : SetupViewModel.SideA.DatabaseName,
                };

                var applier = new SQLParity.Core.Sync.LiveApplier(destConn);
                var result = applier.Apply(ordered, options);

                var msg = result.FullySucceeded
                    ? $"All {result.SucceededCount} changes applied successfully."
                    : $"{result.SucceededCount} succeeded, {result.FailedCount} failed.\n\n" +
                      $"First failure: {System.Linq.Enumerable.FirstOrDefault(result.Steps, s => !s.Succeeded)?.ErrorMessage}";

                MessageBox.Show(msg, "SQLParity — Apply Result",
                    MessageBoxButton.OK,
                    result.FullySucceeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Live apply failed:\n\n{ex.Message}", "SQLParity",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
```

- [ ] **Step 2: Build, commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): wire background comparison and sync actions into host VM"
```

---

## Task 4: GauntletViewModel

**Files:**
- Create: `src/SQLParity.Vsix/ViewModels/GauntletViewModel.cs`

- [ ] **Step 1: Create the view-model**

Create `src/SQLParity.Vsix/ViewModels/GauntletViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class GauntletViewModel : ViewModelBase
    {
        private string _typedLabel = string.Empty;
        private bool _timerExpired;
        private int _countdownSeconds = 3;

        public string DestinationLabel { get; private set; } = string.Empty;
        public EnvironmentTag DestinationTag { get; private set; }
        public SolidColorBrush DestinationColor => EnvironmentTagColors.GetBrush(DestinationTag);

        public List<Change> DestructiveChanges { get; private set; } = new List<Change>();
        public int DestructiveCount => DestructiveChanges.Count;
        public int TotalSelectedCount { get; private set; }

        public string TypedLabel
        {
            get => _typedLabel;
            set
            {
                if (SetProperty(ref _typedLabel, value))
                    OnPropertyChanged(nameof(LabelMatches));
            }
        }

        public bool LabelMatches => string.Equals(TypedLabel, DestinationLabel, StringComparison.Ordinal);

        public bool TimerExpired
        {
            get => _timerExpired;
            set
            {
                if (SetProperty(ref _timerExpired, value))
                    OnPropertyChanged(nameof(CanProceedFinal));
            }
        }

        public int CountdownSeconds
        {
            get => _countdownSeconds;
            set => SetProperty(ref _countdownSeconds, value);
        }

        public bool CanProceedFinal => TimerExpired;

        public void Populate(IEnumerable<Change> selectedChanges, string destinationLabel, EnvironmentTag destinationTag)
        {
            var list = selectedChanges.ToList();
            DestinationLabel = destinationLabel;
            DestinationTag = destinationTag;
            DestructiveChanges = list.Where(c => c.Risk == RiskTier.Destructive).ToList();
            TotalSelectedCount = list.Count;

            OnPropertyChanged(nameof(DestinationLabel));
            OnPropertyChanged(nameof(DestinationTag));
            OnPropertyChanged(nameof(DestinationColor));
            OnPropertyChanged(nameof(DestructiveChanges));
            OnPropertyChanged(nameof(DestructiveCount));
            OnPropertyChanged(nameof(TotalSelectedCount));
        }

        public void StartCountdown()
        {
            CountdownSeconds = 3;
            TimerExpired = false;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                CountdownSeconds--;
                if (CountdownSeconds <= 0)
                {
                    timer.Stop();
                    TimerExpired = true;
                }
            };
            timer.Start();
        }
    }
}
```

- [ ] **Step 2: Add to csproj, build, commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add GauntletViewModel with label confirmation and countdown"
```

---

## Task 5: ResultsView, IdentityBarView, ChangeTreeView, DetailPanelView, ActionBarView

**Files:**
- Create: `src/SQLParity.Vsix/Views/IdentityBarView.xaml` + `.xaml.cs`
- Create: `src/SQLParity.Vsix/Views/ChangeTreeView.xaml` + `.xaml.cs`
- Create: `src/SQLParity.Vsix/Views/DetailPanelView.xaml` + `.xaml.cs`
- Create: `src/SQLParity.Vsix/Views/ActionBarView.xaml` + `.xaml.cs`
- Create: `src/SQLParity.Vsix/Views/ResultsView.xaml` + `.xaml.cs`

This is the largest task. All five views compose into the three-region results layout.

- [ ] **Step 1: Create all view XAML and code-behind files**

The implementer should create these files with functional but simple XAML. Key requirements:

**IdentityBarView.xaml** — horizontal bar showing:
- SideA label + color swatch (left)
- Direction arrow text with flip button (center)
- SideB label + color swatch (right)
- Two buttons: "A → B" and "B → A" for setting direction (visible when direction is Unset)
- Flip button (visible when direction is Set)

**ChangeTreeView.xaml** — left panel:
- A `TreeView` bound to `ResultsViewModel.TreeItems`
- Each group node expands to show children
- Each leaf shows: `StatusIcon` `RiskIcon` `DisplayName` and a `CheckBox`
- Summary text at the bottom bound to `ResultsViewModel.SummaryText`

**DetailPanelView.xaml** — right panel:
- Object name header
- Risk classification text
- Two `TextBox` controls (read-only, monospace font, vertical scroll) showing Side A DDL and Side B DDL
- Labels showing the side labels (from Direction VM), not "Source"/"Target"

**ActionBarView.xaml** — bottom bar:
- "Generate Script" button (primary, bound to `GenerateScriptCommand`)
- "Apply Live" button (secondary, bound to `ApplyLiveCommand`)
- Text showing "PROD — live apply disabled" when destination is PROD

**ResultsView.xaml** — composes all four:
```
┌─────────────────────────────────┐
│        IdentityBarView          │
├──────────────┬──────────────────┤
│              │                  │
│ ChangeTree   │  DetailPanel     │
│   View       │    View          │
│              │                  │
├──────────────┴──────────────────┤
│         ActionBarView           │
└─────────────────────────────────┘
```

The implementer has full latitude on XAML layout details — the priority is that it compiles and the data bindings work. Visual polish is Plan 9.

- [ ] **Step 2: Add all files to csproj**

Add 5 `<Compile>` entries (for .xaml.cs) and 5 `<Page>` entries (for .xaml).

- [ ] **Step 3: Build**

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SQLParity.sln -t:Build -p:Configuration=Debug -v:minimal
```

- [ ] **Step 4: Commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add ResultsView with identity bar, change tree, detail panel, and action bar"
```

---

## Task 6: GauntletReviewDialog and GauntletFinalDialog

**Files:**
- Create: `src/SQLParity.Vsix/Views/GauntletReviewDialog.xaml` + `.xaml.cs`
- Create: `src/SQLParity.Vsix/Views/GauntletFinalDialog.xaml` + `.xaml.cs`

- [ ] **Step 1: Create GauntletReviewDialog**

A WPF `Window` (not UserControl — this is a modal dialog) showing:
- Header: "Destructive Changes — Review Required"
- Destination label + color, large
- List of all destructive changes with their names and risk reasons
- A TextBox: "Type [destination label] to confirm:"
- Confirm button (disabled until label matches) → sets `DialogResult = true`
- Cancel button → closes

**GauntletReviewDialog.xaml.cs:**
```csharp
using System.Windows;

namespace SQLParity.Vsix.Views
{
    public partial class GauntletReviewDialog : Window
    {
        public GauntletReviewDialog()
        {
            InitializeComponent();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
```

- [ ] **Step 2: Create GauntletFinalDialog**

A WPF `Window` showing:
- "Last Chance" header
- Destination label, color, server, database
- Change count
- A countdown: "Confirm button enables in 3... 2... 1..."
- Proceed button (disabled until countdown expires) → sets `DialogResult = true`
- Cancel button

The dialog calls `GauntletViewModel.StartCountdown()` on Loaded.

**GauntletFinalDialog.xaml.cs:**
```csharp
using System.Windows;
using SQLParity.Vsix.ViewModels;

namespace SQLParity.Vsix.Views
{
    public partial class GauntletFinalDialog : Window
    {
        public GauntletFinalDialog()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                var vm = DataContext as GauntletViewModel;
                vm?.StartCountdown();
            };
        }

        private void Proceed_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
```

- [ ] **Step 3: Add to csproj, build, commit**

Add `<Compile>` entries for both `.xaml.cs` files and `<Page>` entries for both `.xaml` files.

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): add destructive gauntlet dialogs (review + final countdown)"
```

---

## Task 7: Update ComparisonHostView to show ResultsView

**Files:**
- Modify: `src/SQLParity.Vsix/Views/ComparisonHostView.xaml`

- [ ] **Step 1: Update the host view XAML**

Add the `ResultsView` to the host view, visible when `ShowResults` is true. The host view should now show four states:
- ConnectionSetupView (when ShowSetup)
- ConfirmationView (when ShowConfirmation)
- "Comparing schemas..." progress text (when ShowComparing)
- ResultsView (when ShowResults)

All four are overlaid in the same Grid cell, with `Visibility` bound to the corresponding bool through `BooleanToVisibilityConverter`.

The ResultsView's DataContext should be bound to `ResultsViewModel`.

- [ ] **Step 2: Build and commit**

```bash
git add src/SQLParity.Vsix/
git commit -m "feat(vsix): wire ResultsView into ComparisonHostView state machine"
```

---

## Task 8: Build, reinstall, and verify end-to-end

**Files:** none (verification only)

- [ ] **Step 1: Full solution build**

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SQLParity.sln -t:Rebuild -p:Configuration=Debug -v:minimal
```

- [ ] **Step 2: Core tests still pass**

```bash
dotnet test tests/SQLParity.Core.Tests/SQLParity.Core.Tests.csproj
dotnet test tests/SQLParity.Core.IntegrationTests/SQLParity.Core.IntegrationTests.csproj
```

Expected: 150 total, all passing.

- [ ] **Step 3: Reinstall VSIX into SSMS 22**

In PowerShell:
```powershell
# Uninstall
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /uninstall:SQLParity.214618a2-e13a-49d0-a25a-ac0f2ae6e811
# Wait for GUI completion, then:
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /instanceIds:919b8d66 "C:\Code\SQLCompare\src\SQLParity.Vsix\bin\Debug\SQLParity.Vsix.vsix"
```

- [ ] **Step 4: End-to-end verification in SSMS 22**

Launch SSMS 22. Click Tools → "SQLParity: New Comparison". Verify the full workflow:

1. **Connection setup** — enter two different databases (e.g., a real DB and a copy with some differences)
2. **Confirmation screen** — shows both sides with colors
3. **Begin Comparison** — shows progress, then results
4. **Results view**:
   - Identity bar shows both labels with colors
   - Direction buttons (A→B, B→A) allow setting direction
   - Change tree shows grouped changes with risk icons
   - Clicking a change shows DDL in the detail panel
   - Summary strip shows counts
5. **Generate Script** — enabled after direction is set; opens save dialog; generates SQL
6. **Apply Live** — enabled after direction is set (disabled if destination is PROD)
7. **Destructive gauntlet** (if applying live with destructive changes):
   - Review dialog lists destructive changes, requires typing destination label
   - Final dialog has 3-second countdown before Proceed enables

- [ ] **Step 5: Commit and tag**

```bash
git status  # should be clean
git tag plan-6-8-complete
```

---

## Plan 6-8 Acceptance Criteria

- ✅ "Begin Comparison" runs SchemaReader + SchemaComparator on a background thread with progress
- ✅ Results view shows the three-region layout: identity bar, change tree, detail panel
- ✅ Identity bar shows both labels, colors, and sync direction arrow
- ✅ Sync direction must be explicitly set before Generate Script or Apply Live buttons enable
- ✅ Change tree groups changes by object type, shows risk icons and checkboxes
- ✅ Detail panel shows DDL for selected change (Side A and Side B)
- ✅ Summary strip shows total/safe/caution/risky/destructive counts and selected count
- ✅ Generate Script produces a dependency-ordered script via ScriptGenerator and opens a save dialog
- ✅ Apply Live executes selected changes via LiveApplier (blocked for PROD destinations)
- ✅ Destructive gauntlet: review dialog with label confirmation + final 3-second countdown dialog
- ✅ Core tests still pass (150 total)
- ✅ Full solution builds with MSBuild
- ✅ Git tagged `plan-6-8-complete`

---

## What Plan 9 (Polish) inherits from Plan 6-8

- A fully functional end-to-end schema comparison and sync tool
- The complete pipeline wired from UI to Core: connection → comparison → review → sync
- Plan 9 adds polish: external diff tool, SSMS-style table tree, rename hints, filter bar, "mark as ignored," unsupported objects panel, registered-servers dropdown, Object Explorer right-click, pre-apply re-read, and UI styling
