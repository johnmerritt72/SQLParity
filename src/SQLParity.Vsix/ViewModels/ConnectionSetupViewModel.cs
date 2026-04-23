using System;
using System.ComponentModel;
using System.Data.SqlClient;
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

            // Check same server + database
            bool bothServersSet = !string.IsNullOrWhiteSpace(SideA.ServerName) && !string.IsNullOrWhiteSpace(SideB.ServerName);
            bool bothDbsSet = !string.IsNullOrWhiteSpace(SideA.DatabaseName) && !string.IsNullOrWhiteSpace(SideB.DatabaseName);
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
                var errorA = await ValidateDatabaseExists(SideA, "Side A");
                var errorB = await ValidateDatabaseExists(SideB, "Side B");

                if (errorA != null || errorB != null)
                {
                    var msg = string.Join("\n", new[] { errorA, errorB });
                    DuplicateLabelWarning = msg.Trim();
                    OnPropertyChanged(nameof(HasValidationError));
                    return;
                }

                // Persist the final connection state (the DatabaseName may have
                // been changed via the dropdown without a Connect click).
                SideA.SaveToHistory();
                SideB.SaveToHistory();

                ContinueRequested?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                IsValidating = false;
            }
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
