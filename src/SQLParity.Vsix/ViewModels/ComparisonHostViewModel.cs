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
using SQLParity.Core.Parsing;
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

        /// <summary>
        /// When Side B is folder-sourced, one <see cref="FolderSchemaContext"/>
        /// per database referenced by the folder's .sql files (keyed by DB
        /// name, case-insensitive). The A→B sync writer reads this to know
        /// which file backs each object — and which Side A connection to
        /// use when re-loading any new-table DDL.
        /// </summary>
        private Dictionary<string, FolderSchemaContext> _sideBFolderContextsByDb;

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

                // --- Read Side B (database 2 of 2, OR a folder of .sql files) ---
                if (sideB.IsFolderMode)
                {
                    // Folder mode reads files first, groups parsed objects by
                    // their declared USE [Db], and then re-reads Side A for
                    // each referenced database. The DB read above (using
                    // sideA.DatabaseName) is replaced — we'll discard schemaA
                    // and read per-DB as needed. This avoids a wasted read of
                    // a DB that no file actually targets.
                    schemaA = null;
                    result = await BuildMultiDbFolderResultAsync(
                        sideA, sideB, readOptions,
                        ignoreCommentsInSps, ignoreWhitespaceInSps, ignoreOptionalBrackets,
                        limitToFolderObjects, ct);
                }
                else
                {
                    _sideBFolderContextsByDb = null;
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
                    result = await Task.Run(() => SchemaComparator.Compare(
                        schemaA, schemaB, readOptions,
                        ignoreCommentsInSps, ignoreWhitespaceInSps, ignoreOptionalBrackets,
                        limitToFolderObjects: false, sideBIsFolder: false));
                }

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

            // Folder destination has no live DB to query — the verify step is a
            // no-op. The folder writer does its own freshness check by reading
            // current file contents per object before overwriting.
            if (destination.IsFolderMode)
                return true;

            // Multi-database B→A: the change set spans multiple DBs but
            // destination.BuildConnectionString() points at only one. A
            // single COUNT query against that one DB doesn't match what's
            // really being applied, so skip the verification entirely. The
            // per-DB applies surface their own errors if drift hit them.
            var resultRef = ResultsViewModel.ComparisonResult;
            bool multiDb = resultRef?.Changes.Any(c => c.SourceDatabase != null) == true;
            if (multiDb)
                return true;

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

            // Folder-destination branch (A→B with Side B as a folder of .sql
            // files). Writes per-object .sql files via FolderSyncWriter and
            // bypasses the LiveApplier's DB-specific machinery (gauntlet,
            // dependency ordering, transactions). See spec §3.4.
            if (destination.IsFolderMode)
            {
                await ApplyFolderWriteAsync(source, destination, selectedChanges);
                return;
            }

            // Multi-database B→A branch (folder source, DB destination). Each
            // change carries SourceDatabase from the multi-DB compare loop;
            // changes route to the matching DB on Side A's server with Side
            // A's credentials + InitialCatalog override. Single-DB folder
            // syncs and DB↔DB syncs fall through to the legacy flow below
            // since their changes have SourceDatabase == null.
            bool multiDb = selectedChanges.Any(c => c.SourceDatabase != null);
            if (multiDb)
            {
                await ApplyLiveMultiDbAsync(source, destination, selectedChanges);
                return;
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
        /// A→B sync when Side B is a folder of .sql files. Drives
        /// <see cref="FolderSyncWriter"/> with the folder context stashed
        /// during the comparison read, then surfaces the manifest summary
        /// and (best-effort) registers new files with the open SSMS solution.
        /// </summary>
        private async Task ApplyFolderWriteAsync(
            ConnectionSideViewModel source,
            ConnectionSideViewModel destination,
            List<Change> selectedChanges)
        {
            if (_sideBFolderContextsByDb == null || _sideBFolderContextsByDb.Count == 0)
            {
                MessageBox.Show(
                    "Folder context is missing — re-run the comparison and try again.",
                    "SQLParity", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Group selected changes by their declared source database. Single-DB
            // folders produce one group; multi-DB folders produce one per USE.
            // Changes without a SourceDatabase tag (shouldn't happen post-multi-DB
            // wiring but kept defensive) get routed to Side A's selected DB.
            var byDb = selectedChanges
                .GroupBy(c => c.SourceDatabase ?? source.DatabaseName,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalUpdated = 0, totalCreated = 0, totalCommentedOut = 0, totalSkipped = 0, totalErrors = 0;
            string firstError = null;

            int dbIndex = 0;
            foreach (var group in byDb)
            {
                dbIndex++;
                string dbName = group.Key;
                var changesForDb = group.ToList();

                if (!_sideBFolderContextsByDb.TryGetValue(dbName, out var folderContext))
                {
                    // The change references a DB the comparison didn't read for —
                    // shouldn't happen in practice. Skip with a counted "Skipped" entry.
                    totalSkipped += changesForDb.Count;
                    continue;
                }

                ProgressText = $"Writing [{dbName}] ({dbIndex} of {byDb.Count}) — {changesForDb.Count} change(s)…";
                ProgressValue = 0;
                ProgressMaximum = 0;

                var manifest = await Task.Run(() =>
                {
                    var writer = new FolderSyncWriter();
                    return writer.WriteChanges(
                        changesForDb,
                        folderContext,
                        sourceServerName: source.ServerName,
                        sourceDatabaseName: dbName,
                        nowUtc: DateTime.UtcNow);
                });

                totalUpdated += manifest.FilesUpdated.Count;
                totalCreated += manifest.FilesCreated.Count;
                totalCommentedOut += manifest.FilesCommentedOut.Count;
                totalSkipped += manifest.Skipped.Count;
                totalErrors += manifest.Errors.Count;
                if (firstError == null && manifest.Errors.Count > 0)
                    firstError = $"[{dbName}] {manifest.Errors[0]}";
            }

            ProgressText = string.Empty;

            string summary = string.Format(
                "Folder sync complete across {0} database(s).\n\n  Updated: {1}\n  Created: {2}\n  Commented out: {3}\n  Skipped: {4}\n  Errors: {5}\n\nRe-run the comparison to refresh the change list.",
                byDb.Count, totalUpdated, totalCreated, totalCommentedOut, totalSkipped, totalErrors);

            if (firstError != null)
                summary += "\n\nFirst error: " + firstError;

            ShowResultDialog(summary,
                totalErrors > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // Solution Explorer integration:
            //   - SSMS "Open Folder" mode (the modern, directory-based view)
            //     watches the filesystem and auto-shows new files. No work
            //     needed — confirmed working empirically.
            //   - Traditional .ssmssln + .ssmsproj solutions list files
            //     explicitly in the project file. Programmatic registration
            //     via DTE.Solution.Projects.Item(N).ProjectItems.AddFromFile
            //     is required to make new files visible without a manual
            //     "Add Existing Item". Deferred to v1.3 when (if) anyone
            //     reports needing it.
        }

        /// <summary>
        /// Shows a result MessageBox owned by the host application's main window
        /// so it can't render behind the comparison tool window. Falls back to
        /// the ownerless overload if no main window is available (e.g. during
        /// early shutdown).
        /// </summary>
        private static void ShowResultDialog(string text, MessageBoxImage image)
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible)
                MessageBox.Show(owner, text, "SQLParity", MessageBoxButton.OK, image);
            else
                MessageBox.Show(text, "SQLParity", MessageBoxButton.OK, image);
        }

        /// <summary>
        /// Multi-database folder-mode read+compare. Walks Side B's folder once,
        /// groups parsed objects by their declared <c>USE [Db]</c>, then for
        /// each referenced database reads Side A using the same credentials
        /// with <c>InitialCatalog</c> overridden, runs the comparator, and
        /// merges the result. Each <see cref="Change"/> is tagged with
        /// <c>SourceDatabase</c> so the apply path knows where to route writes.
        /// </summary>
        private async Task<ComparisonResult> BuildMultiDbFolderResultAsync(
            ConnectionSideViewModel sideA,
            ConnectionSideViewModel sideB,
            SchemaReadOptions readOptions,
            bool ignoreCommentsInSps,
            bool ignoreWhitespaceInSps,
            bool ignoreOptionalBrackets,
            bool limitToFolderObjects,
            System.Threading.CancellationToken ct)
        {
            ProgressText = $"Reading solution folder [{sideB.Label}] at {sideB.FolderPath}";
            ProgressValue = 0;
            ProgressMaximum = 0;

            var folderByDb = await Task.Run(() =>
                new FolderSchemaReader().ReadFolderByDatabase(
                    sideB.FolderPath, "Folder", sideA.DatabaseName, recursive: false));

            // Stash per-DB folder contexts for the apply writer (step 5).
            _sideBFolderContextsByDb = folderByDb.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Context,
                StringComparer.OrdinalIgnoreCase);

            // Empty folder special case: nothing to route, fall back to Side
            // A's selected DB and a synthetic empty Side B so the user sees
            // "all of A's objects as New" — the scaffold-from-DB workflow.
            if (folderByDb.Count == 0)
            {
                ProgressText = "Folder is empty — reading Side A and showing all of it as New.";
                var connStrFallback = sideA.BuildConnectionString();
                var schemaA = await Task.Run(() =>
                    new SchemaReader(connStrFallback, sideA.DatabaseName)
                        .ReadSchema(null, readOptions, ct));
                var emptyB = MakeEmptySchema("Folder", sideB.Label);
                return SchemaComparator.Compare(
                    schemaA, emptyB, readOptions,
                    ignoreCommentsInSps, ignoreWhitespaceInSps, ignoreOptionalBrackets,
                    limitToFolderObjects: false, sideBIsFolder: true);
            }

            var dbNames = folderByDb.Keys.ToList();
            var mergedChanges = new List<Change>();
            var perDbASchemas = new List<DatabaseSchema>();
            var perDbBSchemas = new List<DatabaseSchema>();
            var readWarnings = new List<string>();

            int dbIndex = 0;
            int dbTotal = dbNames.Count;
            foreach (var dbName in dbNames)
            {
                ct.ThrowIfCancellationRequested();
                dbIndex++;
                ProgressText = $"Reading database {dbIndex} of {dbTotal}: [{dbName}]…";
                ProgressValue = 0;
                ProgressMaximum = 0;

                var connStr = sideA.BuildConnectionString(dbName);
                DatabaseSchema schemaA;
                try
                {
                    schemaA = await Task.Run(() =>
                        new SchemaReader(connStr, dbName).ReadSchema(null, readOptions, ct));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    readWarnings.Add(
                        $"Skipped database '{dbName}' — could not read schema: {ex.Message}");
                    continue;
                }

                var folderResult = folderByDb[dbName];
                var schemaB = folderResult.Schema;
                bool effectiveLimit = limitToFolderObjects && !IsSchemaEmpty(schemaB);

                ProgressText = $"Comparing [{dbName}] ({dbIndex} of {dbTotal})…";
                var sideBFileNames = folderResult.Context.ObjectToFile.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.FileName);
                var perDbResult = await Task.Run(() => SchemaComparator.Compare(
                    schemaA, schemaB, readOptions,
                    ignoreCommentsInSps, ignoreWhitespaceInSps, ignoreOptionalBrackets,
                    effectiveLimit, sideBIsFolder: true,
                    sideBFileNames: sideBFileNames));

                foreach (var c in perDbResult.Changes)
                {
                    c.SourceDatabase = dbName;
                    if (folderResult.Context.ObjectToFile.TryGetValue(c.Id, out var backing))
                        c.SourceFilePath = backing.FilePath;
                }

                mergedChanges.AddRange(perDbResult.Changes);
                perDbASchemas.Add(schemaA);
                perDbBSchemas.Add(schemaB);
            }

            // Surface any read warnings so the user notices DBs that were
            // skipped (permission issues, missing DBs, etc.).
            if (readWarnings.Count > 0 || folderByDb.Values.Any(r => r.Context.ParseWarnings.Count > 0))
            {
                var sb = new System.Text.StringBuilder();
                foreach (var w in readWarnings) sb.AppendLine(w);
                foreach (var r in folderByDb.Values)
                    foreach (var w in r.Context.ParseWarnings) sb.AppendLine(w);
                if (sb.Length > 0)
                    StatusMessage = "Folder-mode warnings:\n" + sb.ToString().TrimEnd();
            }

            return new ComparisonResult
            {
                SideA = MergeSchemas(perDbASchemas, sideA.ServerName, $"{dbTotal} database(s)"),
                SideB = MergeSchemas(perDbBSchemas, "Folder", sideB.Label),
                Changes = mergedChanges,
            };
        }

        /// <summary>
        /// Live-applies a multi-database change set to Side A. Used when the
        /// comparison was sourced from a folder of .sql files declaring
        /// multiple <c>USE [Db]</c> targets. Changes are grouped by
        /// <c>SourceDatabase</c>, ordered within each group, and applied via
        /// a per-DB <see cref="LiveApplier"/> using the destination side's
        /// credentials with <c>InitialCatalog</c> overridden.
        /// </summary>
        private async Task ApplyLiveMultiDbAsync(
            ConnectionSideViewModel source,
            ConnectionSideViewModel destination,
            List<Change> selectedChanges)
        {
            var byDb = selectedChanges
                .GroupBy(c => c.SourceDatabase ?? destination.DatabaseName,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalSucceeded = 0;
            int totalFailed = 0;
            int totalAttempted = 0;
            string firstError = null;

            int dbIndex = 0;
            foreach (var group in byDb)
            {
                dbIndex++;
                string dbName = group.Key;
                var changesForDb = group.ToList();
                totalAttempted += changesForDb.Count;

                var connStr = destination.BuildConnectionString(dbName);
                var options = new ScriptGenerationOptions
                {
                    SourceServer = source.ServerName,
                    SourceDatabase = source.IsFolderMode ? "(folder)" : source.DatabaseName,
                    SourceLabel = source.Label,
                    DestinationServer = destination.ServerName,
                    DestinationDatabase = dbName,
                    DestinationLabel = $"{destination.Label}/{dbName}",
                };

                ProgressText = $"Ordering [{dbName}] ({dbIndex} of {byDb.Count})…";
                ProgressValue = 0;
                ProgressMaximum = 0;

                int totalForGroup = changesForDb.Count;
                var applyProgress = new Progress<(int completed, int total, string current)>(p =>
                {
                    ProgressValue = p.completed;
                    ProgressMaximum = p.total;
                    ProgressText = $"Applying [{dbName}] ({dbIndex}/{byDb.Count})… {p.completed}/{p.total} — {p.current}";
                });

                try
                {
                    var applier = new LiveApplier(connStr);
                    var groupResult = await Task.Run(() =>
                    {
                        var ordered = DependencyOrderer.Order(changesForDb).ToList();
                        return applier.Apply(ordered, options, applyProgress);
                    });

                    totalSucceeded += groupResult.SucceededCount;
                    totalFailed += groupResult.FailedCount;
                    if (firstError == null && !groupResult.FullySucceeded)
                    {
                        var step = groupResult.Steps.FirstOrDefault(s => !s.Succeeded);
                        firstError = $"[{dbName}] {step?.ErrorMessage ?? "Unknown"}";
                    }
                }
                catch (Exception ex)
                {
                    totalFailed += changesForDb.Count;
                    if (firstError == null) firstError = $"[{dbName}] {ex.Message}";
                }
            }

            ProgressText = string.Empty;

            string summary = totalFailed == 0
                ? $"All {totalSucceeded} changes applied successfully across {byDb.Count} database(s)."
                : $"Apply finished with errors. {totalSucceeded} succeeded, {totalFailed} failed across {byDb.Count} database(s).";
            if (firstError != null)
                summary += "\n\nFirst error: " + firstError;

            ShowResultDialog(summary,
                totalFailed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private static DatabaseSchema MakeEmptySchema(string serverName, string databaseName) => new()
        {
            ServerName = serverName,
            DatabaseName = databaseName,
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

        private static DatabaseSchema MergeSchemas(
            IReadOnlyList<DatabaseSchema> schemas, string serverName, string databaseName)
        {
            if (schemas.Count == 0) return MakeEmptySchema(serverName, databaseName);
            return new DatabaseSchema
            {
                ServerName = serverName,
                DatabaseName = databaseName,
                ReadAtUtc = DateTime.UtcNow,
                Schemas = schemas.SelectMany(s => s.Schemas).ToList(),
                Tables = schemas.SelectMany(s => s.Tables).ToList(),
                Views = schemas.SelectMany(s => s.Views).ToList(),
                StoredProcedures = schemas.SelectMany(s => s.StoredProcedures).ToList(),
                Functions = schemas.SelectMany(s => s.Functions).ToList(),
                Sequences = schemas.SelectMany(s => s.Sequences).ToList(),
                Synonyms = schemas.SelectMany(s => s.Synonyms).ToList(),
                UserDefinedDataTypes = schemas.SelectMany(s => s.UserDefinedDataTypes).ToList(),
                UserDefinedTableTypes = schemas.SelectMany(s => s.UserDefinedTableTypes).ToList(),
            };
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
