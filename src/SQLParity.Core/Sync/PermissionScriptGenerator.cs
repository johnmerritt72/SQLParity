using System.Collections.Generic;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

/// <summary>
/// Renders GRANT/DENY/REVOKE statements for a Change's permission sub-changes.
/// The parent Change's ObjectType decides OBJECT:: vs SCHEMA:: scope. Each
/// distinct grantee is preceded by an existence guard that hard-fails (RAISERROR)
/// if the principal is missing on the destination.
/// </summary>
public static class PermissionScriptGenerator
{
    /// <summary>
    /// Returns the permission T-SQL for this change (no GO batching), or an empty
    /// string when there are no permission changes.
    /// </summary>
    public static string Generate(Change change)
    {
        if (change.PermissionChanges == null || change.PermissionChanges.Count == 0)
            return string.Empty;

        string scope = change.ObjectType == ObjectType.Schema
            ? $"SCHEMA::[{change.Id.Schema}]"
            : $"OBJECT::[{change.Id.Schema}].[{change.Id.Name}]";

        var sb = new StringBuilder();

        // Group by grantee so each principal gets one existence guard.
        var byGrantee = new Dictionary<string, List<PermissionChange>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var pc in change.PermissionChanges)
        {
            if (!byGrantee.TryGetValue(pc.GranteeName, out var list))
            {
                list = new List<PermissionChange>();
                byGrantee[pc.GranteeName] = list;
            }
            list.Add(pc);
        }

        foreach (var kvp in byGrantee)
        {
            string grantee = kvp.Key;
            string granteeLiteral = grantee.Replace("'", "''");

            sb.AppendLine(
                $"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{granteeLiteral}')");
            sb.AppendLine(
                $"    RAISERROR('Principal [{granteeLiteral}] does not exist on destination — cannot apply its permissions.', 16, 1);");

            foreach (var pc in kvp.Value)
                foreach (var stmt in StatementsFor(pc, scope, grantee))
                    sb.AppendLine(stmt);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> StatementsFor(PermissionChange pc, string scope, string grantee)
    {
        string perm = pc.PermissionName;
        var source = pc.StateSideA;   // desired
        var dest = pc.StateSideB;     // current on destination

        // Remove (source absent): REVOKE, CASCADE if the destination grant carried
        // a grant option.
        if (source is null)
        {
            string cascade = dest == PermissionState.GrantWithGrant ? " CASCADE" : string.Empty;
            yield return $"REVOKE {perm} ON {scope} FROM [{grantee}]{cascade};";
            yield break;
        }

        // Source = DENY: a DENY overrides any existing grant, so DENY alone suffices.
        if (source == PermissionState.Deny)
        {
            yield return $"DENY {perm} ON {scope} TO [{grantee}];";
            yield break;
        }

        // Source = GRANT WITH GRANT OPTION: if destination currently denies, lift it
        // first, then grant with option.
        if (source == PermissionState.GrantWithGrant)
        {
            if (dest == PermissionState.Deny)
                yield return $"REVOKE {perm} ON {scope} FROM [{grantee}];";
            yield return $"GRANT {perm} ON {scope} TO [{grantee}] WITH GRANT OPTION;";
            yield break;
        }

        // Source = plain GRANT.
        // Destination downgrade case: dest has WITH GRANT OPTION → remove just the
        // option, leaving the grant intact.
        if (dest == PermissionState.GrantWithGrant)
        {
            yield return $"REVOKE GRANT OPTION FOR {perm} ON {scope} FROM [{grantee}] CASCADE;";
            yield break;
        }

        // Destination denies → lift the deny before granting.
        if (dest == PermissionState.Deny)
            yield return $"REVOKE {perm} ON {scope} FROM [{grantee}];";

        yield return $"GRANT {perm} ON {scope} TO [{grantee}];";
    }
}
