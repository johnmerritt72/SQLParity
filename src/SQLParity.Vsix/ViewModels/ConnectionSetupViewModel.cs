using System;
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
                        DuplicateLabelWarning = "Open a SSMS Solution before switching Side B to Folder mode.";
                        OnPropertyChanged(nameof(HasValidationError));
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

        public string DuplicateLabelWarning
        {
            get => _validationWarning;
            private set => SetProperty(ref _validationWarning, value);
        }

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
                DuplicateLabelWarning = "Side A and Side B have the same label. Please use distinct labels.";
            else if (HasSameDatabase)
                DuplicateLabelWarning = "Side A and Side B point to the same database. Please select different databases.";
            else
                DuplicateLabelWarning = string.Empty;

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
            DuplicateLabelWarning = string.Empty;
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
                    DuplicateLabelWarning = msg.Trim();
                    OnPropertyChanged(nameof(HasValidationError));
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
