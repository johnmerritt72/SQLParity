using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class PermissionComparatorTests
{
    [Fact]
    public void PermissionModel_Constructs()
    {
        var p = new PermissionModel
        {
            Class = PermissionClass.Object,
            GranteeName = "AppRole",
            PermissionName = "EXECUTE",
            State = PermissionState.Grant,
            TargetSchema = "dbo",
            TargetName = "MyProc",
        };
        Assert.Equal("AppRole", p.GranteeName);
    }
}
