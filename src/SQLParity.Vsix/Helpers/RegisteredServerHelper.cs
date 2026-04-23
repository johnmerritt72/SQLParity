using System;
using System.Collections.Generic;

namespace SQLParity.Vsix.Helpers
{
    /// <summary>
    /// Reads SQL Server registered server names from SSMS's local registered-servers store.
    /// Falls back to an empty list if the API is unavailable or throws.
    /// </summary>
    public static class RegisteredServerHelper
    {
        public static List<string> GetRegisteredServerNames()
        {
            var names = new List<string>();

            // Approach 1: Try the static LocalFileStore (works when SSMS has loaded the store)
            try
            {
                var store = Microsoft.SqlServer.Management.RegisteredServers.RegisteredServersStore.LocalFileStore;
                if (store != null)
                {
                    var dbEngineGroup = store.DatabaseEngineServerGroup;
                    if (dbEngineGroup != null)
                        CollectServerNames(dbEngineGroup, names);
                }
            }
            catch { }

            if (names.Count > 0) return names;

            // Approach 2: Try the default constructor (creates a new in-memory store)
            try
            {
                var store = new Microsoft.SqlServer.Management.RegisteredServers.RegisteredServersStore();
                var dbEngineGroup = store.DatabaseEngineServerGroup;
                if (dbEngineGroup != null)
                    CollectServerNames(dbEngineGroup, names);
            }
            catch { }

            return names;
        }

        private static void CollectServerNames(
            Microsoft.SqlServer.Management.RegisteredServers.ServerGroup group,
            List<string> names)
        {
            try
            {
                foreach (Microsoft.SqlServer.Management.RegisteredServers.RegisteredServer server in group.RegisteredServers)
                {
                    if (!string.IsNullOrWhiteSpace(server.ServerName))
                        names.Add(server.ServerName);
                }

                foreach (Microsoft.SqlServer.Management.RegisteredServers.ServerGroup subGroup in group.ServerGroups)
                {
                    CollectServerNames(subGroup, names);
                }
            }
            catch
            {
                // Swallow errors from individual groups.
            }
        }
    }
}
