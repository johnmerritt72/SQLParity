using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.SqlClient;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ConnectionSetupViewModel : ViewModelBase
    {
        private bool _hasDuplicateLabels;
        private bool _hasSameDatabase;
        private string _validationWarning = string.Empty;

        public ConnectionSetupViewModel()
        {
            SideA = new ConnectionSideViewModel();
            SideB = new ConnectionSideViewModel();
            ContinueCommand = new RelayCommand(_ => OnContinueRequested(), _ => BothSidesComplete() && !HasValidationError);

            SideA.PropertyChanged += OnSidePropertyChanged;
            SideB.PropertyChanged += OnSidePropertyChanged;

            // Live-track SSMS's solution open/close so the Folder Mode radio
            // enables the moment a solution opens — no need to reopen the
            // SQLParity window. The service raises this event on the UI
            // thread (where IVsSolutionEvents callbacks fire), so the
            // PropertyChanged invocation is thread-safe for WPF bindings.
            try
            {
                SsmsSolutionService.EnsureSubscribed();
                SsmsSolutionService.SolutionStateChanged += OnSolutionStateChanged;
            }
            catch { /* SDK service unavailable in tests / design-time */ }

            // When the user toggles Side B to folder mode, auto-populate the
            // folder path from SSMS's open solution. If no solution is open,
            // bounce the toggle back to database mode (the UI will show the
            // disabled state via IsSolutionOpen, but a programmatic flip via
            // bound radio still needs to be repaired here).
            SideB.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ConnectionSideViewModel.IsFolderMode) && SideB.IsFolderMode)
                {
                    string dir = null;
                    try { dir = SsmsSolutionService.GetSolutionDirectory(); } catch { /* swallow */ }

                    if (string.IsNullOrEmpty(dir))
                    {
                        SideB.IsFolderMode = false;
                        ValidationWarning = "Open a SSMS Solution before switching Side B to Folder mode.";
                        return;
                    }

                    SideB.FolderPath = dir;
                    if (string.IsNullOrWhiteSpace(SideB.Label))
                        SideB.Label = "Solution Folder";
                }
            };
        }

        private void OnSolutionStateChanged(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(IsSolutionOpen));
            // If the user had Folder mode selected and the solution closes,
            // bounce the toggle back to Database mode so the IsComplete
            // gate doesn't trap them with a now-stale folder path.
            if (!IsSolutionOpen && SideB.IsFolderMode)
            {
                SideB.IsFolderMode = false;
                SideB.FolderPath = string.Empty;
            }
        }

        /// <summary>True when the host SSMS instance has a solution loaded.</summary>
        public bool IsSolutionOpen
        {
            get
            {
                try { return SsmsSolutionService.IsSolutionOpen(); }
                catch { return false; }
            }
        }

        public ConnectionSideViewModel SideA { get; }
        public ConnectionSideViewModel SideB { get; }
        public ObjectTypeFilterViewModel ObjectTypeFilter { get; } = new ObjectTypeFilterViewModel();
        public ICommand ContinueCommand { get; }

        public const string AllSchemasItem = "(All schemas)";

        private string _selectedSchema = AllSchemasItem;
        private int _schemaRefreshSeq;

        public ObservableCollection<string> AvailableSchemas { get; } =
            new ObservableCollection<string> { AllSchemasItem };

        /// <summary>
        /// The dropdown selection. Never null — coerced back to
        /// <see cref="AllSchemasItem"/> so a cleared ComboBox can't
        /// produce a null filter string downstream.
        /// </summary>
        public string SelectedSchema
        {
            get => _selectedSchema;
            set => SetProperty(ref _selectedSchema, string.IsNullOrWhiteSpace(value) ? AllSchemasItem : value);
        }

        /// <summary>
        /// The effective schema filter for the comparison: null when
        /// "(All schemas)" is selected, otherwise the schema name.
        /// </summary>
        public string SchemaFilter =>
            _selectedSchema == AllSchemasItem ? null : _selectedSchema;

        public bool HasDuplicateLabels
        {
            get => _hasDuplicateLabels;
            private set => SetProperty(ref _hasDuplicateLabels, value);
        }

        public bool HasSameDatabase
        {
            get => _hasSameDatabase;
            private set => SetProperty(ref _hasSameDatabase, value);
        }

        public bool HasValidationError => HasDuplicateLabels || HasSameDatabase;

        /// <summary>
        /// Free-form red warning text shown above the Continue button. Carries
        /// any of: duplicate-label warning, same-database warning, connect
        /// failure during the Continue-time validation, missing-folder error,
        /// or "open a solution before switching to folder mode". Empty string
        /// when no warning to show.
        /// </summary>
        public string ValidationWarning
        {
            get => _validationWarning;
            private set
            {
                if (SetProperty(ref _validationWarning, value))
                    OnPropertyChanged(nameof(HasValidationWarning));
            }
        }

        /// <summary>
        /// True when <see cref="ValidationWarning"/> has visible content.
        /// Drives the visibility of the warning TextBlock in the setup view.
        /// Replaces the prior bind-to-HasDuplicateLabels which silently hid
        /// connect-failure and folder-missing errors.
        /// </summary>
        public bool HasValidationWarning => !string.IsNullOrWhiteSpace(_validationWarning);

        public event EventHandler ContinueRequested;

        private bool BothSidesComplete() => SideA.IsComplete && SideB.IsComplete;

        private void OnSidePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConnectionSideViewModel.Label)
                || e.PropertyName == nameof(ConnectionSideViewModel.ServerName)
                || e.PropertyName == nameof(ConnectionSideViewModel.DatabaseName))
            {
                EvaluateValidation();
            }

            // Auto-copy Side A's database name to Side B if Side B is still empty
            if (sender == SideA && e.PropertyName == nameof(ConnectionSideViewModel.DatabaseName))
            {
                if (!string.IsNullOrWhiteSpace(SideA.DatabaseName)
                    && string.IsNullOrWhiteSpace(SideB.DatabaseName))
                {
                    SideB.DatabaseName = SideA.DatabaseName;
                }
            }

            // Repopulate the schema-filter dropdown when Side A's database
            // changes, or when a Side A connect finishes (IsConnecting flips
            // false) — the connect may make the same DatabaseName newly readable.
            if (sender == SideA
                && (e.PropertyName == nameof(ConnectionSideViewModel.DatabaseName)
                    || (e.PropertyName == nameof(ConnectionSideViewModel.IsConnecting) && !SideA.IsConnecting)))
            {
                RefreshAvailableSchemasAsync();
            }
        }

        /// <summary>
        /// Repopulates the schema dropdown from Side A's selected database.
        /// Fire-and-forget: connection failures leave just "(All schemas)".
        /// A sequence counter discards stale results when the user changes
        /// server/database faster than queries return.
        /// </summary>
        private async void RefreshAvailableSchemasAsync()
        {
            int seq = ++_schemaRefreshSeq;
            List<string> schemas = null;

            if (!SideA.IsFolderMode
                && !string.IsNullOrWhiteSpace(SideA.ServerName)
                && !string.IsNullOrWhiteSpace(SideA.DatabaseName))
            {
                try
                {
                    var connStr = SideA.BuildConnectionString();
                    schemas = await Task.Run(() =>
                    {
                        var list = new List<string>();
                        using (var conn = new SqlConnection(connStr))
                        {
                            conn.Open();
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText =
                                    "SELECT name FROM sys.schemas " +
                                    "WHERE schema_id < 16384 " +   // excludes fixed db-role schemas (16384+)
                                    "  AND name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest') " +
                                    "ORDER BY name";
                                using (var reader = cmd.ExecuteReader())
                                {
                                    while (reader.Read())
                                        list.Add(reader.GetString(0));
                                }
                            }
                        }
                        return list;
                    });
                }
                catch
                {
                    schemas = null; // can't connect yet — leave just "(All schemas)"
                }
            }

            if (seq != _schemaRefreshSeq)
                return; // a newer refresh superseded this one

            var prior = SelectedSchema;
            AvailableSchemas.Clear();
            AvailableSchemas.Add(AllSchemasItem);
            if (schemas != null)
                foreach (var s in schemas)
                    AvailableSchemas.Add(s);

            SelectedSchema = AvailableSchemas.Contains(prior) ? prior : AllSchemasItem;
        }

        private void EvaluateValidation()
        {
            // Check duplicate labels
            bool bothLabelsSet = !string.IsNullOrWhiteSpace(SideA.Label) && !string.IsNullOrWhiteSpace(SideB.Label);
            HasDuplicateLabels = bothLabelsSet
                && string.Equals(SideA.Label.Trim(), SideB.Label.Trim(), StringComparison.OrdinalIgnoreCase);

            // Check same server + database. Folder mode never collides with a
            // live DB so the check is skipped when either side is folder-sourced.
            bool bothAreDb = !SideA.IsFolderMode && !SideB.IsFolderMode;
            bool bothServersSet = bothAreDb
                && !string.IsNullOrWhiteSpace(SideA.ServerName)
                && !string.IsNullOrWhiteSpace(SideB.ServerName);
            bool bothDbsSet = bothAreDb
                && !string.IsNullOrWhiteSpace(SideA.DatabaseName)
                && !string.IsNullOrWhiteSpace(SideB.DatabaseName);
            HasSameDatabase = bothServersSet && bothDbsSet
                && string.Equals(SideA.ServerName.Trim(), SideB.ServerName.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(SideA.DatabaseName.Trim(), SideB.DatabaseName.Trim(), StringComparison.OrdinalIgnoreCase);

            // Build warning message
            if (HasDuplicateLabels)
                ValidationWarning = "Side A and Side B have the same label. Please use distinct labels.";
            else if (HasSameDatabase)
                ValidationWarning = "Side A and Side B point to the same database. Please select different databases.";
            else
                ValidationWarning = string.Empty;

            OnPropertyChanged(nameof(HasValidationError));
        }

        private bool _isValidating;

        public bool IsValidating
        {
            get => _isValidating;
            private set => SetProperty(ref _isValidating, value);
        }

        private async void OnContinueRequested()
        {
            IsValidating = true;
            ValidationWarning = string.Empty;
            try
            {
                // Validate each side per its mode: live DB → can it connect?
                // Folder side → does the folder still exist?
                string errorA = SideA.IsFolderMode
                    ? ValidateFolderExists(SideA, "Side A")
                    : await ValidateDatabaseExists(SideA, "Side A");
                string errorB = SideB.IsFolderMode
                    ? ValidateFolderExists(SideB, "Side B")
                    : await ValidateDatabaseExists(SideB, "Side B");

                if (errorA != null || errorB != null)
                {
                    var msg = string.Join("\n", new[] { errorA, errorB });
                    ValidationWarning = msg.Trim();
                    return;
                }

                // Persist the final connection state for DB sides only.
                if (!SideA.IsFolderMode) SideA.SaveToHistory();
                if (!SideB.IsFolderMode) SideB.SaveToHistory();

                ContinueRequested?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                IsValidating = false;
            }
        }

        private static string ValidateFolderExists(ConnectionSideViewModel side, string label)
        {
            if (string.IsNullOrWhiteSpace(side.FolderPath))
                return $"{label}: No folder selected.";
            if (!Directory.Exists(side.FolderPath))
                return $"{label}: Folder '{side.FolderPath}' does not exist.";
            return null;
        }

        private static async Task<string> ValidateDatabaseExists(ConnectionSideViewModel side, string label)
        {
            try
            {
                // Connect without InitialCatalog so we can check DB existence
                // even if the database doesn't exist
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = side.ServerName,
                    TrustServerCertificate = true,
                    ConnectTimeout = 10,
                };
                if (side.UseWindowsAuth)
                    builder.IntegratedSecurity = true;
                else
                {
                    builder.UserID = side.SqlLogin;
                    builder.Password = side.SqlPassword;
                }

                return await Task.Run(() =>
                {
                    using (var conn = new SqlConnection(builder.ConnectionString))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT DB_ID(@db)";
                            cmd.Parameters.AddWithValue("@db", side.DatabaseName);
                            var result = cmd.ExecuteScalar();
                            if (result == null || result == DBNull.Value)
                                return $"{label}: Database '{side.DatabaseName}' does not exist on {side.ServerName}.";
                        }
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return $"{label}: Could not connect to {side.ServerName} — {ex.Message}";
            }
        }
    }
}
