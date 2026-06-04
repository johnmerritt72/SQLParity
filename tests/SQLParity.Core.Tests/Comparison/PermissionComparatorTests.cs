using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class PermissionComparatorTests
{
    private static PermissionModel Obj(string grantee, string perm, PermissionState state,
        string schema = "dbo", string name = "MyProc") => new()
    {
        Class = PermissionClass.Object,
        GranteeName = grantee,
        PermissionName = perm,
        State = state,
        TargetSchema = schema,
        TargetName = name,
    };

    private static IList<PermissionChange> ForProc(
        IReadOnlyList<PermissionModel> a, IReadOnlyList<PermissionModel> b,
        string schema = "dbo", string name = "MyProc")
    {
        var diff = PermissionComparator.Compare(a, b);
        var key = new PermissionTargetKey(PermissionClass.Object, schema, name);
        return diff.TryGetValue(key, out var list) ? list : new List<PermissionChange>();
    }

    [Fact]
    public void AddedGrant_SideAOnly_HasNullSideB()
    {
        var changes = ForProc(
            new[] { Obj("AppRole", "EXECUTE", PermissionState.Grant) },
            new PermissionModel[0]);
        var pc = Assert.Single(changes);
        Assert.Equal("AppRole", pc.GranteeName);
        Assert.Equal("EXECUTE", pc.PermissionName);
        Assert.Equal(PermissionState.Grant, pc.StateSideA);
        Assert.Null(pc.StateSideB);
    }

    [Fact]
    public void RemovedGrant_SideBOnly_HasNullSideA()
    {
        var changes = ForProc(
            new PermissionModel[0],
            new[] { Obj("AppRole", "EXECUTE", PermissionState.Grant) });
        var pc = Assert.Single(changes);
        Assert.Null(pc.StateSideA);
        Assert.Equal(PermissionState.Grant, pc.StateSideB);
    }

    [Fact]
    public void StateFlip_GrantToDeny_PopulatesBothStates()
    {
        var changes = ForProc(
            new[] { Obj("AppRole", "EXECUTE", PermissionState.Deny) },
            new[] { Obj("AppRole", "EXECUTE", PermissionState.Grant) });
        var pc = Assert.Single(changes);
        Assert.Equal(PermissionState.Deny, pc.StateSideA);
        Assert.Equal(PermissionState.Grant, pc.StateSideB);
    }

    [Fact]
    public void IdenticalSets_ProduceNoChange()
    {
        var changes = ForProc(
            new[] { Obj("AppRole", "EXECUTE", PermissionState.Grant) },
            new[] { Obj("AppRole", "EXECUTE", PermissionState.Grant) });
        Assert.Empty(changes);
    }

    [Fact]
    public void GranteeMatch_IsCaseInsensitive()
    {
        var changes = ForProc(
            new[] { Obj("approle", "EXECUTE", PermissionState.Grant) },
            new[] { Obj("APPROLE", "EXECUTE", PermissionState.Grant) });
        Assert.Empty(changes);
    }

    [Fact]
    public void WithGrantUpgrade_IsAChange()
    {
        var changes = ForProc(
            new[] { Obj("AppRole", "EXECUTE", PermissionState.GrantWithGrant) },
            new[] { Obj("AppRole", "EXECUTE", PermissionState.Grant) });
        var pc = Assert.Single(changes);
        Assert.Equal(PermissionState.GrantWithGrant, pc.StateSideA);
        Assert.Equal(PermissionState.Grant, pc.StateSideB);
    }

    [Fact]
    public void SchemaLevelGrant_KeyedUnderSchemaClass()
    {
        var a = new[]
        {
            new PermissionModel
            {
                Class = PermissionClass.Schema, GranteeName = "AppRole",
                PermissionName = "EXECUTE", State = PermissionState.Grant,
                TargetSchema = "sales", TargetName = "sales",
            }
        };
        var diff = PermissionComparator.Compare(a, new PermissionModel[0]);
        var key = new PermissionTargetKey(PermissionClass.Schema, "sales", "sales");
        Assert.True(diff.ContainsKey(key));
        Assert.Single(diff[key]);
    }
}
