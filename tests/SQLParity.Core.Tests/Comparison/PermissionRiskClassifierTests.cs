using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class PermissionRiskClassifierTests
{
    private static PermissionChange PC(PermissionState? a, PermissionState? b) => new()
    {
        GranteeName = "AppRole",
        PermissionName = "EXECUTE",
        StateSideA = a,
        StateSideB = b,
    };

    [Fact]
    public void AddGrant_IsCaution()
    {
        var (tier, reasons) = PermissionRiskClassifier.Classify(PC(PermissionState.Grant, null));
        Assert.Equal(RiskTier.Caution, tier);
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void RemoveGrant_IsDestructive()
    {
        var (tier, _) = PermissionRiskClassifier.Classify(PC(null, PermissionState.Grant));
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void SourceDeny_IsRisky()
    {
        var (tier, _) = PermissionRiskClassifier.Classify(PC(PermissionState.Deny, PermissionState.Grant));
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void AddWithGrant_IsRisky()
    {
        var (tier, _) = PermissionRiskClassifier.Classify(PC(PermissionState.GrantWithGrant, null));
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void RemoveDeny_BySourceGrant_IsCaution()
    {
        // Source grants, dest denies → applying widens access (revoke deny + grant).
        var (tier, _) = PermissionRiskClassifier.Classify(PC(PermissionState.Grant, PermissionState.Deny));
        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void DowngradeWithGrantToGrant_IsRisky()
    {
        // Source = Grant, dest = GrantWithGrant → remove the grant option.
        var (tier, _) = PermissionRiskClassifier.Classify(PC(PermissionState.Grant, PermissionState.GrantWithGrant));
        Assert.Equal(RiskTier.Risky, tier);
    }
}
