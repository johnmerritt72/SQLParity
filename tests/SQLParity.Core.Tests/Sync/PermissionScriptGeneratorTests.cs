using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class PermissionScriptGeneratorTests
{
    private static Change ProcChange(params PermissionChange[] perms)
    {
        var c = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "MyProc"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            ColumnChanges = System.Array.Empty<ColumnChange>(),
        };
        foreach (var p in perms) c.PermissionChanges.Add(p);
        return c;
    }

    private static PermissionChange PC(string grantee, string perm,
        PermissionState? a, PermissionState? b) => new()
    {
        GranteeName = grantee,
        PermissionName = perm,
        StateSideA = a,
        StateSideB = b,
    };

    [Fact]
    public void AddGrant_EmitsGrantStatement()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange(
            PC("AppRole", "EXECUTE", PermissionState.Grant, null)));
        Assert.Contains("GRANT EXECUTE ON OBJECT::[dbo].[MyProc] TO [AppRole]", sql);
    }

    [Fact]
    public void AddWithGrant_EmitsWithGrantOption()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange(
            PC("AppRole", "SELECT", PermissionState.GrantWithGrant, null)));
        Assert.Contains("WITH GRANT OPTION", sql);
    }

    [Fact]
    public void SourceDeny_EmitsDeny()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange(
            PC("AppRole", "EXECUTE", PermissionState.Deny, PermissionState.Grant)));
        Assert.Contains("DENY EXECUTE ON OBJECT::[dbo].[MyProc] TO [AppRole]", sql);
    }

    [Fact]
    public void RemoveGrant_EmitsRevoke()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange(
            PC("AppRole", "EXECUTE", null, PermissionState.Grant)));
        Assert.Contains("REVOKE EXECUTE ON OBJECT::[dbo].[MyProc] FROM [AppRole]", sql);
    }

    [Fact]
    public void RemoveWithGrant_EmitsCascade()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange(
            PC("AppRole", "SELECT", null, PermissionState.GrantWithGrant)));
        Assert.Contains("REVOKE SELECT ON OBJECT::[dbo].[MyProc] FROM [AppRole] CASCADE;", sql);
    }

    [Fact]
    public void EmitsPrincipalExistenceGuard()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange(
            PC("AppRole", "EXECUTE", PermissionState.Grant, null)));
        Assert.Contains("sys.database_principals", sql);
        Assert.Contains("THROW", sql);
        Assert.Contains("AppRole", sql);
    }

    [Fact]
    public void SchemaClass_EmitsSchemaScope()
    {
        var c = new Change
        {
            Id = SchemaQualifiedName.TopLevel("sales", "sales"),
            ObjectType = ObjectType.Schema,
            Status = ChangeStatus.Modified,
            ColumnChanges = System.Array.Empty<ColumnChange>(),
        };
        c.PermissionChanges.Add(PC("AppRole", "EXECUTE", PermissionState.Grant, null));
        var sql = PermissionScriptGenerator.Generate(c);
        Assert.Contains("ON SCHEMA::[sales] TO [AppRole]", sql);
    }

    [Fact]
    public void NoPermissionChanges_ReturnsEmpty()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange());
        Assert.Equal(string.Empty, sql);
    }

    [Fact]
    public void MultipleGrantees_EmittedInAlphabeticalOrder()
    {
        var sql = PermissionScriptGenerator.Generate(ProcChange(
            PC("Zeta", "EXECUTE", PermissionState.Grant, null),
            PC("Alpha", "EXECUTE", PermissionState.Grant, null)));
        int alpha = sql.IndexOf("[Alpha]", System.StringComparison.Ordinal);
        int zeta = sql.IndexOf("[Zeta]", System.StringComparison.Ordinal);
        Assert.True(alpha >= 0 && zeta >= 0);
        Assert.True(alpha < zeta, "Alpha grantee block should precede Zeta");
    }
}
