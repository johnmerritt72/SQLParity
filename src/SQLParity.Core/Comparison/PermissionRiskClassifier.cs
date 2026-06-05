using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Classifies the risk of a single permission change, framed by what applying
/// the source's intent to the destination would do. Safety-first: adding access
/// is Caution, removing or denying access is Risky/Destructive.
/// </summary>
public static class PermissionRiskClassifier
{
    public static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) Classify(PermissionChange change)
    {
        var source = change.StateSideA;   // desired end-state (source side)
        var dest = change.StateSideB;     // current destination state
        var who = change.GranteeName;
        var what = change.PermissionName;

        // Absent on source, present on destination → REVOKE (remove access).
        if (source is null)
        {
            return (RiskTier.Destructive, One(RiskTier.Destructive,
                $"Revoking {what} from [{who}] may break applications relying on this access."));
        }

        // Source wants DENY.
        if (source == PermissionState.Deny)
        {
            return (RiskTier.Risky, One(RiskTier.Risky,
                $"Denying {what} to [{who}] actively blocks access."));
        }

        // Source wants GRANT WITH GRANT OPTION (add or upgrade).
        if (source == PermissionState.GrantWithGrant)
        {
            return (RiskTier.Risky, One(RiskTier.Risky,
                $"Granting {what} to [{who}] WITH GRANT OPTION lets it re-grant to others."));
        }

        // Source wants plain GRANT.
        // Destination absent or DENY → adding/restoring access.
        if (dest is null || dest == PermissionState.Deny)
        {
            return (RiskTier.Caution, One(RiskTier.Caution,
                $"Granting {what} to [{who}] widens access."));
        }

        // Destination has WITH GRANT OPTION, source wants plain grant → downgrade.
        if (dest == PermissionState.GrantWithGrant)
        {
            return (RiskTier.Risky, One(RiskTier.Risky,
                $"Removing GRANT OPTION on {what} for [{who}] (downgrade)."));
        }

        // dest == Grant, source == Grant: no effective change (shouldn't be reached).
        return (RiskTier.Safe, One(RiskTier.Safe, $"Permission {what} for [{who}] unchanged."));
    }

    private static IReadOnlyList<RiskReason> One(RiskTier tier, string description) =>
        new[] { new RiskReason { Tier = tier, Description = description } };
}
