using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SQLParity.Core.Model;
using SQLParity.Core.Project;
using SQLParity.Vsix.Helpers;
using SQLParity.Vsix.Options;

namespace SQLParity.Vsix.ViewModels
{
    /// <summary>
    /// An entry in the server dropdown. Displays "Server (Label)" but
    /// resolves to just the server name for connections.
    /// </summary>
    public class ServerEntry
    {
        public string ServerName { get; set; }
        public string Label { get; set; }

        public string DisplayText =>
            string.IsNullOrWhiteSpace(Label) ? ServerName : $"{ServerName}  ({Label})";

        public override string ToString() => ServerName;
    }

    public class ConnectionSideViewModel : ViewModelBase
    {
        private static readonly ConnectionHistoryStore _historyStore = new ConnectionHistoryStore();
        private bool _isAutoFilling;

        private string _serverName = string.Empty;
        private string _databaseName = string.Empty;
        private string _label = string.Empty;
        private EnvironmentTag _tag = EnvironmentTag.Untagged;
        private bool _useWindowsAuth = true;
        private string _sqlLogin = string.Empty;
        private string _sqlPassword = string.Empty;
        private bool _isConnecting;
        private string _connectionError = string.Empty;
        private bool _forceRefresh;
        private bool _isFolderMode;
        private string _folderPath = string.Empty;

        /// <summary>
        /// True when this side reads from a folder of .sql files instead of a
        /// live database. Always false on Side A (database-only). Toggled by
        /// the Side B mode selector — see step 6 of the folder-mode rollout.
        /// </summary>
        public bool IsFolderMode
        {
            get => _isFolderMode;
            set
            {
                if (SetProperty(ref _isFolderMode, value))
                {
                    OnPropertyChanged(nameof(IsDatabaseMode));
                    OnPropertyChanged(nameof(IsComplete));
                }
            }
        }

        /// <summary>True when this side connects to a live database (the default).</summary>
        public bool IsDatabaseMode
        {
            get => !_isFolderMode;
            set
            {
                if (value != !_isFolderMode)
                    IsFolderMode = !value;
            }
        }

        /// <summary>The on-disk folder path when <see cref="IsFolderMode"/> is true.</summary>
        public string FolderPath
        {
            get => _folderPath;
            set
            {
                if (SetProperty(ref _folderPath, value ?? string.Empty))
                    OnPropertyChanged(nameof(IsComplete));
            }
        }

        public ConnectionSideViewModel()
        {
            AvailableServers = new ObservableCollection<ServerEntry>();
            AvailableDatabases = new ObservableCollection<string>();
            ConnectCommand = new RelayCommand(_ => ConnectAsync(), _ => CanConnect());

            // Apply default options
            try
            {
                var opts = OptionsHelper.GetOptions();
                if (opts != null)
                {
                    UseWindowsAuth = opts.DefaultAuthentication == Options.AuthenticationMethod.WindowsAuthentication;
                    if (opts.DefaultAuthentication == Options.AuthenticationMethod.SqlServerAuthentication
                        && !string.IsNullOrWhiteSpace(opts.DefaultSqlLogin))
                        SqlLogin = opts.DefaultSqlLogin;
                }
            }
            catch { }

            RefreshAvailableServers();
        }

        public ObservableCollection<ServerEntry> AvailableServers { get; }
        public ObservableCollection<string> AvailableDatabases { get; }
        public RelayCommand ConnectCommand { get; }

        public string ServerName
        {
            get => _serverName;
            set
            {
                if (SetProperty(ref _serverName, value))
                {
                    OnPropertyChanged(nameof(IsComplete));
                    OnPropertyChanged(nameof(HasCachedSchema));
                    OnPropertyChanged(nameof(CacheStatus));
                    CommandManager.InvalidateRequerySuggested();

                    // Auto-fill from connection history and auto-connect
                    if (!_isAutoFilling && !string.IsNullOrWhiteSpace(value))
                    {
                        _isAutoFilling = true;
                        try
                        {
                            var saved = _historyStore.FindByServer(value);
                            if (saved != null)
                            {
                                UseWindowsAuth = saved.UseWindowsAuth;
                                if (!saved.UseWindowsAuth)
                                {
                                    SqlLogin = saved.SqlLogin;
                                    if (PasswordSavingEnabled())
                                        SqlPassword = saved.GetPassword();
                                }
                                if (!string.IsNullOrWhiteSpace(saved.DatabaseName))
                                    DatabaseName = saved.DatabaseName;
                                if (!string.IsNullOrWhiteSpace(saved.Label))
                                    Label = saved.Label;

                                // Auto-connect to populate database list
                                // (don't refresh server list — it clears the ComboBox selection)
                                ConnectAsyncNoRefresh();
                            }
                        }
                        finally
                        {
                            _isAutoFilling = false;
                        }
                    }
                }
            }
        }

        public string DatabaseName
        {
            get => _databaseName;
            set
            {
                if (SetProperty(ref _databaseName, value))
                {
                    OnPropertyChanged(nameof(IsComplete));
                    OnPropertyChanged(nameof(HasCachedSchema));
                    OnPropertyChanged(nameof(CacheStatus));
                }
            }
        }

        public string Label
        {
            get => _label;
            set
            {
                if (SetProperty(ref _label, value))
                {
                    Tag = EnvironmentTagStore.SuggestTagFromLabel(value);
                    OnPropertyChanged(nameof(IsComplete));
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
            set
            {
                if (SetProperty(ref _useWindowsAuth, value))
                    ClearLoginError();
            }
        }

        public string SqlLogin
        {
            get => _sqlLogin;
            set
            {
                if (SetProperty(ref _sqlLogin, value))
                    ClearLoginError();
            }
        }

        public string SqlPassword
        {
            get => _sqlPassword;
            set
            {
                if (SetProperty(ref _sqlPassword, value))
                    ClearLoginError();
            }
        }

        public bool IsConnecting
        {
            get => _isConnecting;
            set
            {
                if (SetProperty(ref _isConnecting, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ConnectionError
        {
            get => _connectionError;
            set => SetProperty(ref _connectionError, value);
        }

        public bool ForceRefresh
        {
            get => _forceRefresh;
            set => SetProperty(ref _forceRefresh, value);
        }

        public bool HasCachedSchema => SchemaCache.GetAge(ServerName, DatabaseName) != null;

        public string CacheStatus
        {
            get
            {
                var age = SchemaCache.GetAge(ServerName, DatabaseName);
                if (age == null) return string.Empty;
                return $"Cached ({age.Value.TotalMinutes:F0} min ago)";
            }
        }

        public bool IsComplete => IsFolderMode
            ? !string.IsNullOrWhiteSpace(FolderPath) && !string.IsNullOrWhiteSpace(Label)
            : !string.IsNullOrWhiteSpace(ServerName)
                && !string.IsNullOrWhiteSpace(DatabaseName)
                && !string.IsNullOrWhiteSpace(Label);

        /// <summary>
        /// Explicitly save the current connection to history. Called on Continue
        /// so that if the user picks a different database from the dropdown
        /// (without clicking Connect again) the saved connection still reflects
        /// the current selection.
        /// </summary>
        public void SaveToHistory()
        {
            SaveCurrentConnection();
        }

        /// <summary>
        /// Persist the current (Server, Database, auth, login, optional password,
        /// Label) tuple to the connection-history store. Returns silently if either
        /// ServerName or DatabaseName is blank, or if the underlying store throws.
        ///
        /// Centralises the SaveConnection call shared by SaveToHistory (Continue
        /// gate), the DoConnectAsync post-connect path, and the DatabaseName
        /// setter's "user picked a DB after a successful connect" trigger.
        /// </summary>
        private void SaveCurrentConnection()
        {
            if (string.IsNullOrWhiteSpace(ServerName) || string.IsNullOrWhiteSpace(DatabaseName))
                return;
            try
            {
                var savePassword = !UseWindowsAuth && PasswordSavingEnabled();
                _historyStore.SaveConnection(ServerName, DatabaseName, UseWindowsAuth, SqlLogin,
                    savePassword ? SqlPassword : null, Label);
            }
            catch { }
        }

        /// <summary>
        /// Apply a specific saved connection's credentials to this side, using the
        /// caller-supplied server/database names verbatim. Suppresses the
        /// <see cref="ServerName"/>-setter auto-fill so it does not race a second
        /// <c>FindByServer</c> lookup against the partially populated state, then
        /// kicks off an async connect to populate the database dropdown.
        ///
        /// Used by the "Compare Selected Database" menu command, which already
        /// knows the exact (server, database) pair from the Object Explorer node.
        /// </summary>
        public void LoadFromSavedConnection(SavedConnection saved, string overrideServer, string overrideDatabase)
        {
            if (saved == null) return;

            _isAutoFilling = true;
            try
            {
                ServerName = overrideServer ?? string.Empty;
                DatabaseName = overrideDatabase ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(saved.Label))
                    Label = saved.Label;

                UseWindowsAuth = saved.UseWindowsAuth;
                if (!saved.UseWindowsAuth)
                {
                    SqlLogin = saved.SqlLogin ?? string.Empty;
                    if (PasswordSavingEnabled())
                        SqlPassword = saved.GetPassword();
                }
            }
            finally
            {
                _isAutoFilling = false;
            }

            // Auto-connect to populate the database list. Fire-and-forget mirrors
            // the existing ServerName-setter auto-fill behaviour.
            ConnectAsyncNoRefresh();
        }

        public string BuildConnectionString() => BuildConnectionString(null);

        /// <summary>
        /// Builds a connection string using this side's credentials but with
        /// a caller-specified database. Used by folder mode's multi-DB Side A
        /// flow, which needs to read each USE-referenced database with the
        /// same credentials.
        /// </summary>
        public string BuildConnectionString(string? overrideDatabase)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = ServerName,
                InitialCatalog = overrideDatabase ?? DatabaseName,
                TrustServerCertificate = true,
            };

            if (UseWindowsAuth)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = SqlLogin;
                builder.Password = SqlPassword;
            }

            return builder.ConnectionString;
        }

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

        private void RefreshAvailableServers()
        {
            AvailableServers.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add history servers first (most recently used), with their labels
            try
            {
                foreach (var saved in _historyStore.GetAll())
                {
                    if (!string.IsNullOrWhiteSpace(saved.ServerName) && seen.Add(saved.ServerName))
                    {
                        AvailableServers.Add(new ServerEntry
                        {
                            ServerName = saved.ServerName,
                            Label = saved.Label,
                        });
                    }
                }
            }
            catch { }

            // Add registered servers (no label available)
            try
            {
                var servers = RegisteredServerHelper.GetRegisteredServerNames();
                foreach (var s in servers)
                {
                    if (seen.Add(s))
                        AvailableServers.Add(new ServerEntry { ServerName = s });
                }
            }
            catch
            {
                // Swallow - registered-servers lookup must not crash.
            }
        }

        private static bool PasswordSavingEnabled()
        {
            try
            {
                var opts = OptionsHelper.GetOptions();
                return opts == null || opts.SavePasswords;
            }
            catch { return true; }
        }

        private bool _hasLoginError;

        private bool CanConnect()
        {
            return !string.IsNullOrWhiteSpace(ServerName) && !IsConnecting && !_hasLoginError;
        }

        private void ClearLoginError()
        {
            if (_hasLoginError)
            {
                _hasLoginError = false;
                ConnectionError = string.Empty;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private async void ConnectAsync()
        {
            await DoConnectAsync(refreshServerList: true);
        }

        private async void ConnectAsyncNoRefresh()
        {
            await DoConnectAsync(refreshServerList: false);
        }

        private async Task DoConnectAsync(bool refreshServerList)
        {
            IsConnecting = true;
            ConnectionError = string.Empty;
            AvailableDatabases.Clear();

            try
            {
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = ServerName,
                    TrustServerCertificate = true,
                    ConnectTimeout = 10,
                };

                if (UseWindowsAuth)
                {
                    builder.IntegratedSecurity = true;
                }
                else
                {
                    builder.UserID = SqlLogin;
                    builder.Password = SqlPassword;
                }

                var databases = await Task.Run(() =>
                {
                    var list = new System.Collections.Generic.List<string>();
                    using (var conn = new SqlConnection(builder.ConnectionString))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name";
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                    list.Add(reader.GetString(0));
                            }
                        }
                    }
                    return list;
                });

                foreach (var db in databases)
                    AvailableDatabases.Add(db);

                // If the current DatabaseName (typically autofilled from history)
                // no longer exists on the server, clear it so the user can pick
                // a valid one instead of proceeding with an invalid name.
                if (!string.IsNullOrWhiteSpace(DatabaseName)
                    && !databases.Contains(DatabaseName, StringComparer.OrdinalIgnoreCase))
                {
                    DatabaseName = string.Empty;
                }

                // Save to history after successful connect.
                // SaveCurrentConnection is a no-op when DatabaseName is blank, so
                // we don't double-guard here (saving blank would overwrite the
                // good row and cause the same issue next time).
                SaveCurrentConnection();
                if (refreshServerList)
                {
                    var currentServer = ServerName;
                    RefreshAvailableServers();
                    // Restore the text — Clear() causes the editable ComboBox to blank out
                    _isAutoFilling = true;
                    try { ServerName = currentServer; }
                    finally { _isAutoFilling = false; }
                }
            }
            catch (Exception ex)
            {
                ConnectionError = ex.Message;

                // Detect login failures — block re-connect until credentials are changed
                // SQL Server error 18456 = "Login failed for user"
                var msg = ex.Message ?? string.Empty;
                if (msg.Contains("Login failed") || msg.Contains("18456")
                    || msg.Contains("password") || msg.Contains("credential"))
                {
                    _hasLoginError = true;
                    ConnectionError = ex.Message + "\n\nPlease correct your credentials before trying again.";
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
            finally
            {
                IsConnecting = false;
            }
        }
    }
}
