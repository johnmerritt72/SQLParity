using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace SQLParity.Vsix.Helpers
{
    [DataContract]
    public class SavedConnection
    {
        [DataMember]
        public string ServerName { get; set; } = string.Empty;

        [DataMember]
        public string DatabaseName { get; set; } = string.Empty;

        [DataMember]
        public bool UseWindowsAuth { get; set; } = true;

        [DataMember]
        public string SqlLogin { get; set; } = string.Empty;

        /// <summary>
        /// Encrypted password stored using Windows DPAPI (per-user, per-machine).
        /// Only populated for SQL Authentication connections.
        /// </summary>
        [DataMember]
        public string EncryptedPassword { get; set; } = string.Empty;

        [DataMember]
        public string Label { get; set; } = string.Empty;

        [DataMember]
        public DateTime LastUsedUtc { get; set; }

        /// <summary>
        /// Display string for the server dropdown: "ServerName (DatabaseName)"
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DatabaseName))
                    return $"{ServerName} ({DatabaseName})";
                return ServerName;
            }
        }

        public void SetPassword(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword))
            {
                EncryptedPassword = string.Empty;
                return;
            }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plainPassword);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                EncryptedPassword = Convert.ToBase64String(encrypted);
            }
            catch
            {
                EncryptedPassword = string.Empty;
            }
        }

        public string GetPassword()
        {
            if (string.IsNullOrEmpty(EncryptedPassword))
                return string.Empty;
            try
            {
                var encrypted = Convert.FromBase64String(EncryptedPassword);
                var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public class ConnectionHistoryStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SQLParity", "connection-history.json");

        private static readonly DataContractJsonSerializerSettings JsonSettings =
            new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
            };

        private List<SavedConnection> _connections;

        public ConnectionHistoryStore()
        {
            _connections = Load();
        }

        /// <summary>
        /// Returns all saved connections, most recently used first.
        /// </summary>
        public IReadOnlyList<SavedConnection> GetAll()
        {
            return _connections.OrderByDescending(c => c.LastUsedUtc).ToList();
        }

        /// <summary>
        /// Returns distinct server names from history, most recent first.
        /// </summary>
        public List<string> GetServerNames()
        {
            return _connections
                .OrderByDescending(c => c.LastUsedUtc)
                .Select(c => c.ServerName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Finds a saved connection matching the server name (case-insensitive).
        /// Returns the most recently used match, or null.
        /// </summary>
        public SavedConnection FindByServer(string serverName)
        {
            return _connections
                .Where(c => string.Equals(c.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.LastUsedUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Saves or updates a connection after successful connect.
        /// Updates LastUsedUtc. If a matching server+database+auth already exists, updates it.
        /// </summary>
        /// <summary>
        /// Finds a saved connection matching server + database (case-insensitive).
        /// </summary>
        public SavedConnection FindByServerAndDatabase(string serverName, string databaseName)
        {
            return _connections
                .Where(c => string.Equals(c.ServerName, serverName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.LastUsedUtc)
                .FirstOrDefault();
        }

        public void SaveConnection(string serverName, string databaseName, bool useWindowsAuth, string sqlLogin, string password = null, string label = null)
        {
            var existing = _connections.FirstOrDefault(c =>
                string.Equals(c.ServerName, serverName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.UseWindowsAuth = useWindowsAuth;
                existing.SqlLogin = useWindowsAuth ? string.Empty : (sqlLogin ?? string.Empty);
                if (!useWindowsAuth && password != null)
                    existing.SetPassword(password);
                if (!string.IsNullOrWhiteSpace(label))
                    existing.Label = label;
                existing.LastUsedUtc = DateTime.UtcNow;
            }
            else
            {
                var conn = new SavedConnection
                {
                    ServerName = serverName,
                    DatabaseName = databaseName,
                    UseWindowsAuth = useWindowsAuth,
                    SqlLogin = useWindowsAuth ? string.Empty : (sqlLogin ?? string.Empty),
                    Label = label ?? string.Empty,
                    LastUsedUtc = DateTime.UtcNow,
                };
                if (!useWindowsAuth && password != null)
                    conn.SetPassword(password);
                _connections.Add(conn);
            }

            // Keep max 50 entries
            if (_connections.Count > 50)
            {
                _connections = _connections.OrderByDescending(c => c.LastUsedUtc).Take(50).ToList();
            }

            Save();
        }

        private List<SavedConnection> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    // Use ReadAllText to strip the UTF-8 BOM that WriteAllText adds,
                    // then convert to bytes for the serializer.
                    var text = File.ReadAllText(FilePath);
                    var bytes = Encoding.UTF8.GetBytes(text);
                    using (var stream = new MemoryStream(bytes))
                    {
                        var serializer = new DataContractJsonSerializer(
                            typeof(List<SavedConnection>), JsonSettings);
                        return (List<SavedConnection>)serializer.ReadObject(stream)
                            ?? new List<SavedConnection>();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SQLParity ConnectionHistory Load failed: " + ex.Message);
            }
            return new List<SavedConnection>();
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new DataContractJsonSerializer(
                    typeof(List<SavedConnection>), JsonSettings);
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, _connections);
                    var json = Encoding.UTF8.GetString(stream.ToArray());
                    File.WriteAllText(FilePath, json, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
