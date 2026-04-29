using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SQLParity.Core;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using SQLParity.Vsix.Helpers;
using SQLParity.Vsix.Options;

namespace SQLParity.Vsix.ViewModels
{
    public enum WorkflowState
    {
        ConnectionSetup,
        Confirmation,
        Comparing,
        Results
    }

    public class GauntletRequestedEventArgs : EventArgs
    {
        public GauntletRequestedEventArgs(IEnumerable<Change> selectedChanges, string destinationLabel, EnvironmentTag destinationTag)
        {
            SelectedChanges = selectedChanges;
            DestinationLabel = destinationLabel;
            DestinationTag = destinationTag;
        }

        public IEnumerable<Change> SelectedChanges { get; }
        public string DestinationLabel { get; }
        public EnvironmentTag DestinationTag { get; }
        public bool Confirmed { get; set; }
    }

    public class ComparisonHostViewModel : ViewModelBase
    {
        private WorkflowState _currentState = WorkflowState.ConnectionSetup;
        private string _statusMessage = "Configure both connections to begin.";
        private string _progressText = string.Empty;
        private System.Threading.CancellationTokenSource _comparisonCts;

        public ComparisonHostViewModel()
        {
            SetupViewModel = new ConnectionSetupViewModel();
            ConfirmationViewModel = new ConfirmationViewModel();
            ResultsViewModel = new ResultsViewModel();

            GenerateScriptCommand = new RelayCommand(_ => GenerateScript(), _ => CurrentState == WorkflowState.Results && !IsBusy);
            ApplyLiveCommand = new RelayCommand(_ => ApplyLiveAsync(), _ => CanApplyLive() && !IsBusy);
            NewComparisonCommand = new RelayCommand(_ => StartNewComparison());
            CancelComparisonCommand = new RelayCommand(_ => CancelComparison(), _ => CurrentState == WorkflowState.Comparing);
            StartOverCommand = new RelayCommand(_ => { CurrentState = WorkflowState.ConnectionSetup; }, _ => CurrentState == WorkflowState.Results && !IsBusy);

            SetupViewModel.ContinueRequested += (s, e) =>
            {
                bool skipConfirmation = false;
                try { skipConfirmation = OptionsHelper.GetOptions()?.SkipConfirmationPage ?? false; } catch { }

                if (skipConfirmation)
                {
                    RunComparisonAsync();
                }
                else
                {
                    ConfirmationViewModel.PopulateFrom(SetupViewModel.SideA, SetupViewModel.SideB);
                    CurrentState = WorkflowState.Confirmation;
                }
            };

            ConfirmationViewModel.BackRequested += (s, e) =>
            {
                CurrentState = WorkflowState.ConnectionSetup;
            };

            ConfirmationViewModel.BeginComparisonRequested += (s, e) =>
            {
                RunComparisonAsync();
            };
        }

        public ConnectionSetupViewModel SetupViewModel { get; }
        public ConfirmationViewModel ConfirmationViewModel { get; }
        public ResultsViewModel ResultsViewModel { get; }

        public ICommand GenerateScriptCommand { get; }
        public ICommand ApplyLiveCommand { get; }
        public ICommand NewComparisonCommand { get; }
        public ICommand CancelComparisonCommand { get; }
        public ICommand StartOverCommand { get; }

        private void CancelComparison()
        {
            _comparisonCts?.Cancel();
            ProgressText = "Cancelling...";
        }

        public event EventHandler<GauntletRequestedEventArgs> GauntletRequested;

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

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        private double _progressMaximum = 100;
        public double ProgressMaximum
        {
            get => _progressMaximum;
            set => SetProperty(ref _progressMaximum, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private async void RunComparisonAsync()
        {
            _comparisonCts?.Cancel();
            _comparisonCts = new System.Threading.CancellationTokenSource();
            var ct = _comparisonCts.Token;

            CurrentState = WorkflowState.Comparing;
            ProgressText = "Connecting...";

            try
            {
                var sideA = SetupViewModel.SideA;
                var sideB = SetupViewModel.SideB;
                var connStrA = sideA.BuildConnectionString();
                var connStrB = sideB.BuildConnectionString();
                var readOptions = SetupViewModel.ObjectTypeFilter.ToSchemaReadOptions();

                DatabaseSchema schemaA = null;
                DatabaseSchema schemaB = null;
                ComparisonResult result = null;

                // Get cache TTL from options (default 5 if options not available)
                int cacheTtl = 5;
                bool ignoreCommentsInSps = false;
                bool ignoreWhitespaceInSps = false;
                bool ignoreOptionalBrackets = false;
                bool limitToFolderObjects = true;
                try
                {
                    var opts = OptionsHelper.GetOptions();
                    if (opts != null)
                    {
                        cacheTtl = opts.SchemaCacheTtlMinutes;
                        ignoreCommentsInSps = opts.IgnoreCommentsInStoredProcedures;
                        ignoreWhitespaceInSps = opts.IgnoreWhitespaceInStoredProcedures;
                        ignoreOptionalBrackets = opts.IgnoreOptionalBrackets;
                        limitToFolderObjects = opts.LimitComparisonToFolderObjects;
                    }
                }
                catch { }

                // --- Read Side A (database 1 of 2) ---
                var cachedA = SchemaCache.Get(sideA.ServerName, sideA.DatabaseName, cacheTtl);
                if (cachedA != null && !sideA.ForceRefresh)
                {
                    schemaA = cachedA;
                    var ageA = SchemaCache.GetAge(sideA.ServerName, sideA.DatabaseName);
                    ProgressText = $"Using cached schema for [{sideA.Label}] (read {ageA?.TotalMinutes:F0} minutes ago)";
                }
                else
                {
                    string phaseA = $"Database 1 of 2: [{sideA.Label}] ({sideA.ServerName}/{sideA.DatabaseName})";
                    var progressA = new Progress<SchemaReadProgress>(p =>
                    {
                        ProgressText = $"{phaseA}  —  {p.CurrentOperation}";
                        ProgressValue = p.CompletedItems;
                        // Maximum = 0 triggers indeterminate animation in the UI
                        ProgressMaximum = p.TotalItems;
                    });

                    ProgressText = phaseA;
                    ProgressValue = 0;
                    ProgressMaximum = 0;

                    schemaA = await Task.Run(() =>
                    {
                        var reader = new SchemaReader(connStrA, sideA.DatabaseName);
                        return reader.ReadSchema(progressA, readOptions, ct);
                    });

                    SchemaCache.Put(sideA.ServerName, sideA.DatabaseName, schemaA);
                }

                ct.ThrowIfCancellationRequested();

                // --- Read Side B (database 2 of 2) ---
                var cachedB = SchemaCache.Get(sideB.ServerName, sideB.DatabaseName, cacheTtl);
                if (cachedB != null && !sideB.ForceRefresh)
                {
                    schemaB = cachedB;
                    var ageB = SchemaCache.GetAge(sideB.ServerName, sideB.DatabaseName);
                    ProgressText = $"Using cached schema for [{sideB.Label}] (read {ageB?.TotalMinutes:F0} minutes ago)";
                }
                else
                {
                    string phaseB = $"Database 2 of 2: [{sideB.Label}] ({sideB.ServerName}/{sideB.DatabaseName})";
                    var progressB = new Progress<SchemaReadProgress>(p =>
                    {
                        ProgressText = $"{phaseB}  —  {p.CurrentOperation}";
                        ProgressValue = p.CompletedItems;
                        ProgressMaximum = p.TotalItems;
                    });

                    ProgressText = phaseB;
                    ProgressValue = 0;
                    ProgressMaximum = 0;

                    schemaB = await Task.Run(() =>
                    {
                        var reader = new SchemaReader(connStrB, sideB.DatabaseName);
                        return reader.ReadSchema(progressB, readOptions, ct);
                    });

                    SchemaCache.Put(sideB.ServerName, sideB.DatabaseName, schemaB);
                }

                ct.ThrowIfCancellationRequested();

                ProgressText = "Comparing schemas...";

                // Folder-mode-only filter: when Side B is folder-sourced (set
                // in step 6), the user-configurable LimitComparisonToFolderObjects
                // hides A-only changes. For DB↔DB compares the flag is forced
                // off so today's behavior is preserved. An empty Side B (no
                // objects parsed) also forces the flag off so the user sees all
                // of A's objects — the explicit "scaffold from DB" workflow.
                bool sideBIsFolder = sideB.IsFolderMode;
                bool sideBIsEmpty = IsSchemaEmpty(schemaB);
                bool effectiveLimit = sideBIsFolder && !sideBIsEmpty && limitToFolderObjects;

                result = await Task.Run(() => SchemaComparator.Compare(
                    schemaA, schemaB, readOptions,
                    ignoreCommentsInSps, ignoreWhitespaceInSps, ignoreOptionalBrackets,
                    effectiveLimit));

                ProgressText = string.Empty;

                if (result.TotalCount == 0)
                {
                    MessageBox.Show(
                        "No differences found! The two databases have identical schemas.",
                        "SQLParity",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    CurrentState = WorkflowState.ConnectionSetup;
                    return;
                }

                ProgressText = $"{result.TotalCount} differences found. Loading results...";

                ResultsViewModel.Populate(result, sideA, sideB);
                ProgressText = string.Empty;
                CurrentState = WorkflowState.Results;
            }
            catch (OperationCanceledException)
            {
                ProgressText = string.Empty;
                CurrentState = WorkflowState.ConnectionSetup;
                return;
            }
            catch (Exception ex)
            {
                ProgressText = string.Empty;
                var fullError = ex.ToString();
                System.Diagnostics.Debug.WriteLine("SQLParity comparison error: " + fullError);

                // Log to a temp file so the user can see the full error even if the dialog disappears
                try
                {
                    var errorLogPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "SQLParity_error.log");
                    System.IO.File.WriteAllText(errorLogPath,
                        $"[{DateTime.Now}] Comparison failed:\r\n{fullError}\r\n");
                }
                catch { }

                MessageBox.Show(
                    $"Comparison failed:\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Full details written to: {System.IO.Path.GetTempPath()}SQLParity_error.log",
                    "SQLParity — Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                CurrentState = WorkflowState.Confirmation;
            }
        }

        private async Task<bool> CheckDestinationUnchangedAsync()
        {
            var dir = ResultsViewModel.Direction;
            ConnectionSideViewModel destination;
            if (dir.Direction == SyncDirection.AtoB)
                destination = SetupViewModel.SideB;
            else
                destination = SetupViewModel.SideA;

            // Determine original counts from the comparison result's destination side
            var originalResult = ResultsViewModel.ComparisonResult;
            if (originalResult == null)
                return true;

            var originalDest = dir.Direction == SyncDirection.AtoB
                ? originalResult.SideB
                : originalResult.SideA;

            int origTables = originalDest.Tables.Count;
            int origViews = originalDest.Views.Count;
            int origProcs = originalDest.StoredProcedures.Count;

            ProgressText = "Verifying destination...";
            ProgressValue = 0;
            ProgressMaximum = 1;

            try
            {
                // Use lightweight COUNT queries instead of a full schema re-read
                var connStr = destination.BuildConnectionString();
                var counts = await Task.Run(() =>
                {
                    using (var conn = new System.Data.SqlClient.SqlConnection(connStr))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "SELECT " +
                                "(SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0), " +
                                "(SELECT COUNT(*) FROM sys.views WHERE is_ms_shipped = 0), " +
                                "(SELECT COUNT(*) FROM sys.procedures WHERE is_ms_shipped = 0)";
                            using (var reader = cmd.ExecuteReader())
                            {
                                reader.Read();
                                return (tables: reader.GetInt32(0), views: reader.GetInt32(1), procs: reader.GetInt32(2));
                            }
                        }
                    }
                });

                ProgressText = string.Empty;

                if (counts.tables != origTables || counts.views != origViews || counts.procs != origProcs)
                {
                    var msg = string.Format(
                        "The destination database [{0}] appears to have changed since the comparison was run.\n\n" +
                        "Original: {1} tables, {2} views, {3} procs\n" +
                        "Now: {4} tables, {5} views, {6} procs\n\n" +
                        "Do you want to proceed anyway?",
                        destination.Label,
                        origTables, origViews, origProcs,
                        counts.tables, counts.views, counts.procs);

                    var result = MessageBox.Show(msg, "SQLParity - Destination Changed",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    return result == MessageBoxResult.Yes;
                }

                return true;
            }
            catch (Exception ex)
            {
                ProgressText = string.Empty;
                MessageBox.Show(
                    "Could not verify destination schema:\n\n" + ex.Message +
                    "\n\nProceeding anyway.",
                    "SQLParity - Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return true;
            }
        }

        private async void GenerateScript()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                var dir = ResultsViewModel.Direction;
                if (dir.Direction == SyncDirection.Unset)
                {
                    MessageBox.Show(
                        "Please select a sync direction before generating a script.",
                        "SQLParity",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var selectedChanges = ResultsViewModel.GetSelectedChanges().ToList();
                if (selectedChanges.Count == 0)
                {
                    MessageBox.Show(
                        "No changes are selected for sync.",
                        "SQLParity",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!await CheckDestinationUnchangedAsync())
                    return;

                ConnectionSideViewModel source, destination;
                if (dir.Direction == SyncDirection.AtoB)
                {
                    source = SetupViewModel.SideA;
                    destination = SetupViewModel.SideB;
                }
                else
                {
                    source = SetupViewModel.SideB;
                    destination = SetupViewModel.SideA;
                }

                // Ask where to save before generating — avoids the dialog
                // appearing behind other windows after a long generation
                var dlg = new SaveFileDialog
                {
                    Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
                    DefaultExt = ".sql",
                    FileName = string.Format("SQLParity-{0}({1})-{2}({3})-{4}.sql", source.Label, source.DatabaseName, destination.Label, destination.DatabaseName, DateTime.Now.ToString("yyyy-MM-dd")),
                };

                if (dlg.ShowDialog() != true)
                    return;

                var savePath = dlg.FileName;

                var options = new ScriptGenerationOptions
                {
                    SourceServer = source.ServerName,
                    SourceDatabase = source.DatabaseName,
                    SourceLabel = source.Label,
                    DestinationServer = destination.ServerName,
                    DestinationDatabase = destination.DatabaseName,
                    DestinationLabel = destination.Label,
                };

                // Pre-load DDL for any selected tables whose DDL wasn't loaded
                // yet (tables use lazy-load; if the user didn't view them, their
                // DdlSideA is empty and the script would be missing CREATE TABLE).
                var tablesNeedingDdl = selectedChanges
                    .Where(c => c.ObjectType == ObjectType.Table
                        && c.Status == ChangeStatus.New
                        && string.IsNullOrEmpty(c.DdlSideA))
                    .ToList();

                if (tablesNeedingDdl.Count > 0)
                {
                    ProgressText = $"Loading DDL for {tablesNeedingDdl.Count} tables...";
                    ProgressValue = 0;
                    ProgressMaximum = tablesNeedingDdl.Count;

                    var sourceConnStr = source.BuildConnectionString();
                    var sourceDbName = source.DatabaseName;
                    var total = tablesNeedingDdl.Count;

                    var ddlProgress = new Progress<(int completed, string current)>(p =>
                    {
                        ProgressValue = p.completed;
                        ProgressText = $"Loading DDL... {p.completed}/{total}  —  {p.current}";
                    });

                    await Task.Run(() =>
                    {
                        var reader = new SQLParity.Core.SchemaReader(sourceConnStr, sourceDbName);
                        int loaded = 0;
                        foreach (var change in tablesNeedingDdl)
                        {
                            try
                            {
                                change.DdlSideA = reader.ScriptTable(change.Id.Schema, change.Id.Name);
                            }
                            catch { }
                            loaded++;
                            ((IProgress<(int, string)>)ddlProgress).Report((loaded, change.Id.ToString()));
                        }
                    });
                }

                ProgressText = $"Ordering {selectedChanges.Count} changes by dependency...";
                ProgressValue = 0;
                ProgressMaximum = 0; // indeterminate look

                var progress = new Progress<(int completed, int total, string current)>(p =>
                {
                    ProgressValue = p.completed;
                    ProgressMaximum = p.total;
                    ProgressText = $"Generating script... {p.completed}/{p.total}  —  {p.current}";
                });

                // Ordering and generation both run on background thread
                var script = await Task.Run(() =>
                {
                    var ordered = DependencyOrderer.Order(selectedChanges);
                    return ScriptGenerator.Generate(ordered, options, progress);
                });

                ProgressText = "Saving...";
                await Task.Run(() => File.WriteAllText(savePath, script.SqlText));
                ProgressText = string.Empty;

                MessageBox.Show(
                    string.Format("Script saved to {0}\n{1} changes, {2} destructive.",
                        savePath, script.TotalChanges, script.DestructiveChanges),
                    "SQLParity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ProgressText = string.Empty;
                MessageBox.Show(
                    "Generate script failed: " + ex.Message,
                    "SQLParity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanApplyLive()
        {
            if (CurrentState != WorkflowState.Results)
                return false;

            var dir = ResultsViewModel.Direction;
            if (dir.Direction == SyncDirection.Unset)
                return false;

            if (dir.IsDestinationProd)
                return false;

            return true;
        }

        private async void ApplyLiveAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await ApplyLiveInternalAsync();
            }
            finally
            {
                // Always clear the busy state — any early return or exception
                // in the inner method must not leave the overlay visible.
                IsBusy = false;
                ProgressText = string.Empty;
                ProgressValue = 0;
                ProgressMaximum = 100;
            }
        }

        private async Task ApplyLiveInternalAsync()
        {
            var dir = ResultsViewModel.Direction;
            var selectedChanges = ResultsViewModel.GetSelectedChanges().ToList();

            if (selectedChanges.Count == 0)
            {
                MessageBox.Show(
                    "No changes are selected for sync.",
                    "SQLParity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!await CheckDestinationUnchangedAsync())
                return;

            bool hasDestructive = selectedChanges.Any(c => c.Risk == RiskTier.Destructive);
            if (hasDestructive)
            {
                var args = new GauntletRequestedEventArgs(selectedChanges, dir.DestinationLabel, dir.DestinationTag);
                GauntletRequested?.Invoke(this, args);
                if (!args.Confirmed)
                    return;
            }

            ConnectionSideViewModel source, destination;
            if (dir.Direction == SyncDirection.AtoB)
            {
                source = SetupViewModel.SideA;
                destination = SetupViewModel.SideB;
            }
            else
            {
                source = SetupViewModel.SideB;
                destination = SetupViewModel.SideA;
            }

            var connStr = destination.BuildConnectionString();

            var options = new ScriptGenerationOptions
            {
                SourceServer = source.ServerName,
                SourceDatabase = source.DatabaseName,
                SourceLabel = source.Label,
                DestinationServer = destination.ServerName,
                DestinationDatabase = destination.DatabaseName,
                DestinationLabel = destination.Label,
            };

            // Pre-load DDL for any selected tables whose DDL wasn't loaded yet
            var tablesNeedingDdl = selectedChanges
                .Where(c => c.ObjectType == ObjectType.Table
                    && c.Status == ChangeStatus.New
                    && string.IsNullOrEmpty(c.DdlSideA))
                .ToList();

            if (tablesNeedingDdl.Count > 0)
            {
                ProgressText = $"Loading DDL for {tablesNeedingDdl.Count} tables...";
                ProgressValue = 0;
                ProgressMaximum = tablesNeedingDdl.Count;

                var sourceConnStr = source.BuildConnectionString();
                var sourceDbName = source.DatabaseName;
                var totalDdl = tablesNeedingDdl.Count;

                var ddlProgress = new Progress<(int completed, string current)>(p =>
                {
                    ProgressValue = p.completed;
                    ProgressText = $"Loading DDL... {p.completed}/{totalDdl}  —  {p.current}";
                });

                await Task.Run(() =>
                {
                    var reader = new SQLParity.Core.SchemaReader(sourceConnStr, sourceDbName);
                    int loaded = 0;
                    foreach (var change in tablesNeedingDdl)
                    {
                        try
                        {
                            change.DdlSideA = reader.ScriptTable(change.Id.Schema, change.Id.Name);
                        }
                        catch { }
                        loaded++;
                        ((IProgress<(int, string)>)ddlProgress).Report((loaded, change.Id.ToString()));
                    }
                });
            }

            ProgressText = "Ordering changes by dependency...";
            ProgressValue = 0;
            ProgressMaximum = 0;

            var applyProgress = new Progress<(int completed, int total, string current)>(p =>
            {
                ProgressValue = p.completed;
                ProgressMaximum = p.total;
                ProgressText = $"Applying changes... {p.completed}/{p.total}  —  {p.current}";
            });

            try
            {
                var applier = new LiveApplier(connStr);
                // Ordering and applying both run on background thread
                var result = await Task.Run(() =>
                {
                    var ordered = DependencyOrderer.Order(selectedChanges).ToList();
                    return applier.Apply(ordered, options, applyProgress);
                });

                ProgressText = string.Empty;

                if (result.FullySucceeded)
                {
                    MessageBox.Show(
                        string.Format("All {0} changes applied successfully.", result.SucceededCount),
                        "SQLParity",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        string.Format("Apply stopped on error. {0} succeeded, {1} failed.\n\nFirst error: {2}",
                            result.SucceededCount,
                            result.FailedCount,
                            result.Steps.FirstOrDefault(s => !s.Succeeded)?.ErrorMessage ?? "Unknown"),
                        "SQLParity",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                ProgressText = string.Empty;
                MessageBox.Show(
                    "Apply failed: " + ex.Message,
                    "SQLParity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void StartNewComparison()
        {
            CurrentState = WorkflowState.ConnectionSetup;
            ProgressText = string.Empty;
            StatusMessage = "Configure both connections to begin.";
        }

        /// <summary>
        /// Returns true when a schema has zero objects of every type. Used to
        /// suppress the folder-only filter when Side B is empty (the user wants
        /// to scaffold a fresh project from the live DB, so they need to see
        /// all of A's objects as "New").
        /// </summary>
        private static bool IsSchemaEmpty(DatabaseSchema s)
        {
            if (s == null) return true;
            return s.Tables.Count == 0
                && s.Views.Count == 0
                && s.StoredProcedures.Count == 0
                && s.Functions.Count == 0
                && s.Sequences.Count == 0
                && s.Synonyms.Count == 0
                && s.UserDefinedDataTypes.Count == 0
                && s.UserDefinedTableTypes.Count == 0
                && s.Schemas.Count == 0;
        }
    }
}
