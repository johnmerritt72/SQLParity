using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.SqlClient;

namespace SQLParity.Core;

/// <summary>
/// Reads cross-database and linked-server references from sys.sql_expression_dependencies.
/// Uses SQL Server's own dependency tracker rather than text-parsing DDL.
/// </summary>
internal static class ExternalReferenceReader
{
    /// <summary>
    /// Returns a map of (schema, objectName) → list of distinct external reference
    /// descriptors. Objects with no external refs are absent from the map.
    /// </summary>
    public static Dictionary<(string schema, string name), List<string>> ReadAll(string connectionString)
    {
        var result = new Dictionary<(string, string), List<string>>(
            new SchemaNameComparer());

        const string sql = @"
SELECT
    OBJECT_SCHEMA_NAME(referencing_id) AS ObjSchema,
    OBJECT_NAME(referencing_id) AS ObjName,
    referenced_server_name AS Server,
    referenced_database_name AS Db
FROM sys.sql_expression_dependencies
WHERE (referenced_database_name IS NOT NULL AND referenced_database_name <> DB_NAME())
   OR referenced_server_name IS NOT NULL";

        try
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 0;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var objSchema = reader.GetString(0);
                var objName = reader.GetString(1);
                var server = reader.IsDBNull(2) ? null : reader.GetString(2);
                var db = reader.IsDBNull(3) ? null : reader.GetString(3);

                string descriptor;
                if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(db))
                    descriptor = $"[{server}].[{db}] (linked server)";
                else if (!string.IsNullOrEmpty(server))
                    descriptor = $"[{server}] (linked server)";
                else
                    descriptor = $"[{db}]";

                var key = (objSchema, objName);
                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    result[key] = list;
                }
                if (!list.Contains(descriptor, StringComparer.OrdinalIgnoreCase))
                    list.Add(descriptor);
            }
        }
        catch
        {
            // If the query fails (permissions, old SQL Server version, etc.), return what we have.
            // sys.sql_expression_dependencies requires VIEW DEFINITION on objects.
        }

        return result;
    }

    private sealed class SchemaNameComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1 ?? "")
            ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2 ?? "");
    }
}
