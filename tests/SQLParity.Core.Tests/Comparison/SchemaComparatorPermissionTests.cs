using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class SchemaComparatorPermissionTests
{
    private static StoredProcedureModel Proc(string name, string ddl) => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", name),
        Schema = "dbo",
        Name = name,
        Ddl = ddl,
    };

    private static SynonymModel Syn(string name, string ddl) => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", name),
        Schema = "dbo",
        Name = name,
        BaseObject = string.Empty,
        Ddl = ddl,
    };

    private static PermissionModel ProcGrant(string proc, string grantee, PermissionState state) => new()
    {
        Class = PermissionClass.Object,
        GranteeName = grantee,
        PermissionName = "EXECUTE",
        State = state,
        TargetSchema = "dbo",
        TargetName = proc,
    };

    private static PermissionModel SynGrant(string syn, string grantee, PermissionState state) => new()
    {
        Class = PermissionClass.Object,
        GranteeName = grantee,
        PermissionName = "SELECT",
        State = state,
        TargetSchema = "dbo",
        TargetName = syn,
    };

    private static DatabaseSchema Schema(
        IReadOnlyList<StoredProcedureModel> procs,
        IReadOnlyList<PermissionModel> perms,
        IReadOnlyList<SynonymModel>? synonyms = null) => new()
    {
        ServerName = "S",
        DatabaseName = "D",
        ReadAtUtc = DateTime.UtcNow,
        Schemas = Array.Empty<SchemaModel>(),
        Tables = Array.Empty<TableModel>(),
        Views = Array.Empty<ViewModel>(),
        StoredProcedures = procs,
        Functions = Array.Empty<UserDefinedFunctionModel>(),
        Sequences = Array.Empty<SequenceModel>(),
        Synonyms = synonyms ?? Array.Empty<SynonymModel>(),
        UserDefinedDataTypes = Array.Empty<UserDefinedDataTypeModel>(),
        UserDefinedTableTypes = Array.Empty<UserDefinedTableTypeModel>(),
        Permissions = perms,
    };

    private const string ProcDdl = "CREATE PROCEDURE [dbo].[P] AS BEGIN SELECT 1 END";

    [Fact]
    public void IdenticalProc_DifferentGrant_EmitsPermissionOnlyModifiedChange()
    {
        var a = Schema(new[] { Proc("P", ProcDdl) },
            new[] { ProcGrant("P", "AppRole", PermissionState.Grant) });
        var b = Schema(new[] { Proc("P", ProcDdl) }, Array.Empty<PermissionModel>());

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.True(change.IsPermissionOnlyChange);
        var pc = Assert.Single(change.PermissionChanges);
        Assert.Equal("AppRole", pc.GranteeName);
        Assert.Equal(PermissionState.Grant, pc.StateSideA);
        Assert.Null(pc.StateSideB);
    }

    [Fact]
    public void RevokedGrant_OnIdenticalProc_IsDestructive()
    {
        var a = Schema(new[] { Proc("P", ProcDdl) }, Array.Empty<PermissionModel>());
        var b = Schema(new[] { Proc("P", ProcDdl) },
            new[] { ProcGrant("P", "AppRole", PermissionState.Grant) });

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All);

        var change = Assert.Single(result.Changes);
        Assert.Equal(RiskTier.Destructive, change.Risk);
    }

    [Fact]
    public void NewProc_WithGrant_StaysSafe_ButCarriesPermissionChange()
    {
        var a = Schema(new[] { Proc("P", ProcDdl) },
            new[] { ProcGrant("P", "AppRole", PermissionState.Grant) });
        var b = Schema(Array.Empty<StoredProcedureModel>(), Array.Empty<PermissionModel>());

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.New, change.Status);
        Assert.Equal(RiskTier.Safe, change.Risk);            // New-object grants don't elevate
        Assert.Single(change.PermissionChanges);             // but are carried for emission
        Assert.False(change.IsPermissionOnlyChange);
    }

    [Fact]
    public void FolderMode_SkipsPermissions_NoFalseDrift()
    {
        var a = Schema(new[] { Proc("P", ProcDdl) },
            new[] { ProcGrant("P", "AppRole", PermissionState.Grant) });
        var b = Schema(new[] { Proc("P", ProcDdl) }, Array.Empty<PermissionModel>());

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All,
            sideBIsFolder: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void IncludePermissionsOff_SkipsPermissions()
    {
        var a = Schema(new[] { Proc("P", ProcDdl) },
            new[] { ProcGrant("P", "AppRole", PermissionState.Grant) });
        var b = Schema(new[] { Proc("P", ProcDdl) }, Array.Empty<PermissionModel>());

        var opts = SchemaReadOptions.All;
        opts.IncludePermissions = false;

        var result = SchemaComparator.Compare(a, b, opts);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void ModifiedSynonym_WithGrantDiff_ProducesSingleChange_NotDuplicate()
    {
        // Synonym differs in DDL (Modified) AND has a grant difference. The grant
        // diff must attach to the existing Modified change, not spawn a second one.
        var synA = Syn("S", "CREATE SYNONYM [dbo].[S] FOR [dbo].[TargetA]");
        var synB = Syn("S", "CREATE SYNONYM [dbo].[S] FOR [dbo].[TargetB]");

        var a = Schema(Array.Empty<StoredProcedureModel>(),
            new[] { SynGrant("S", "AppRole", PermissionState.Grant) },
            synonyms: new[] { synA });
        var b = Schema(Array.Empty<StoredProcedureModel>(),
            Array.Empty<PermissionModel>(),
            synonyms: new[] { synB });

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All);

        var change = Assert.Single(result.Changes);   // exactly one — no duplicate
        Assert.Equal(ObjectType.Synonym, change.ObjectType);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Single(change.PermissionChanges);
    }
}
