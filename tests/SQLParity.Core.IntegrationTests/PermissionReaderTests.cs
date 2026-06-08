using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class PermissionFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE SCHEMA [sales]
GO
CREATE PROCEDURE [dbo].[DoThing] AS BEGIN SELECT 1 END
GO
CREATE TABLE [dbo].[Widget] ([Id] INT NOT NULL PRIMARY KEY)
GO
CREATE ROLE [AppRole]
GO
CREATE ROLE [ReportRole]
GO
GRANT EXECUTE ON [dbo].[DoThing] TO [AppRole]
GO
GRANT SELECT ON [dbo].[Widget] TO [AppRole] WITH GRANT OPTION
GO
DENY SELECT ON [dbo].[Widget] TO [ReportRole]
GO
GRANT EXECUTE ON SCHEMA::[sales] TO [AppRole]
GO
";
}

public class PermissionReaderTests : IClassFixture<PermissionFixture>
{
    private readonly System.Collections.Generic.IReadOnlyList<PermissionModel> _perms;

    public PermissionReaderTests(PermissionFixture fixture)
    {
        _perms = PermissionReader.Read(fixture.ConnectionString);
    }

    [Fact]
    public void ReadsObjectExecuteGrant()
    {
        var p = _perms.SingleOrDefault(x =>
            x.Class == PermissionClass.Object && x.TargetName == "DoThing"
            && x.GranteeName == "AppRole" && x.PermissionName == "EXECUTE");
        Assert.NotNull(p);
        Assert.Equal(PermissionState.Grant, p!.State);
        Assert.Equal("dbo", p.TargetSchema);
    }

    [Fact]
    public void ReadsWithGrantOption()
    {
        var p = _perms.Single(x =>
            x.TargetName == "Widget" && x.GranteeName == "AppRole" && x.PermissionName == "SELECT");
        Assert.Equal(PermissionState.GrantWithGrant, p.State);
    }

    [Fact]
    public void ReadsDeny()
    {
        var p = _perms.Single(x =>
            x.TargetName == "Widget" && x.GranteeName == "ReportRole" && x.PermissionName == "SELECT");
        Assert.Equal(PermissionState.Deny, p.State);
    }

    [Fact]
    public void ReadsSchemaLevelGrant()
    {
        var p = _perms.SingleOrDefault(x =>
            x.Class == PermissionClass.Schema && x.TargetName == "sales"
            && x.GranteeName == "AppRole" && x.PermissionName == "EXECUTE");
        Assert.NotNull(p);
        Assert.Equal("sales", p!.TargetSchema);
    }

    [Fact]
    public void ExcludesSystemPrincipals()
    {
        Assert.DoesNotContain(_perms, x =>
            x.GranteeName == "dbo" || x.GranteeName == "sys"
            || x.GranteeName == "guest" || x.GranteeName == "INFORMATION_SCHEMA");
    }
}
