using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using SQLParity.Core.Model;

namespace SQLParity.Core;

/// <summary>
/// Reads object- and schema-level permissions from sys.database_permissions.
/// Mirrors <see cref="ExternalReferenceReader"/>: one query off the connection
/// string, defensive against failure. Excludes system principals/objects and
/// column-level grants.
/// </summary>
public static class PermissionReader
{
    private const string Sql = @"
SELECT
    dp.class,
    dp.permission_name,
    dp.state_desc,
    pr.name        AS grantee_name,
    s_obj.name     AS object_schema,
    o.name         AS object_name,
    s_sch.name     AS schema_name
FROM sys.database_permissions dp
JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
LEFT JOIN sys.objects  o     ON dp.class = 1 AND o.object_id = dp.major_id
LEFT JOIN sys.schemas  s_obj ON o.schema_id = s_obj.schema_id
LEFT JOIN sys.schemas  s_sch ON dp.class = 3 AND s_sch.schema_id = dp.major_id
WHERE dp.class IN (1, 3)
  AND dp.minor_id = 0
  AND ISNULL(o.is_ms_shipped, 0) = 0
  AND pr.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
  AND pr.is_fixed_role = 0
  AND pr.sid <> 0x01;";

    public static IReadOnlyList<PermissionModel> Read(string connectionString)
    {
        var result = new List<PermissionModel>();
        try
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = new SqlCommand(Sql, conn);
            cmd.CommandTimeout = 0;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int cls = reader.GetByte(0);                  // 1 = object, 3 = schema (tinyint column)
                string permName = reader.GetString(1);
                string stateDesc = reader.GetString(2);
                string grantee = reader.GetString(3);

                PermissionClass permClass;
                string targetSchema;
                string targetName;

                if (cls == 1)
                {
                    if (reader.IsDBNull(4) || reader.IsDBNull(5)) continue; // object metadata missing
                    permClass = PermissionClass.Object;
                    targetSchema = reader.GetString(4);
                    targetName = reader.GetString(5);
                }
                else // cls == 3
                {
                    if (reader.IsDBNull(6)) continue;
                    permClass = PermissionClass.Schema;
                    targetSchema = reader.GetString(6);
                    targetName = targetSchema;
                }

                result.Add(new PermissionModel
                {
                    Class = permClass,
                    GranteeName = grantee,
                    PermissionName = permName,
                    State = MapState(stateDesc),
                    TargetSchema = targetSchema,
                    TargetName = targetName,
                });
            }
        }
        catch
        {
            // Permissions query failed (insufficient rights, old version, etc.).
            // Return what we have; comparison treats missing perms as 'none'.
        }
        return result;
    }

    private static PermissionState MapState(string stateDesc) => stateDesc switch
    {
        "GRANT_WITH_GRANT_OPTION" => PermissionState.GrantWithGrant,
        "DENY" => PermissionState.Deny,
        _ => PermissionState.Grant,   // GRANT (and any unexpected value) → Grant
    };
}
