using System.Collections.Generic;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class ScriptGeneratorPermissionTests
{
    private static ScriptGenerationOptions Opts() => new()
    {
        DestinationServer = "destSrv",
        DestinationDatabase = "destDb",
        DestinationLabel = "DEST",
        SourceServer = "srcSrv",
        SourceDatabase = "srcDb",
        SourceLabel = "SRC",
    };

    [Fact]
    public void PermissionOnlyChange_EmitsGrant_NotObjectDdl()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "MyProc"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            ColumnChanges = System.Array.Empty<ColumnChange>(),
            DdlSideA = "CREATE PROCEDURE [dbo].[MyProc] AS BEGIN SELECT 1 END",
            DdlSideB = "CREATE PROCEDURE [dbo].[MyProc] AS BEGIN SELECT 1 END",
            IsPermissionOnlyChange = true,
        };
        change.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "AppRole",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });

        var script = ScriptGenerator.Generate(new[] { change }, Opts());

        Assert.Contains("GRANT EXECUTE ON OBJECT::[dbo].[MyProc] TO [AppRole]", script.SqlText);
        // The object body must NOT be re-created for a permission-only change.
        Assert.DoesNotContain("CREATE OR ALTER PROCEDURE", script.SqlText);
    }

    [Fact]
    public void ModifiedProcWithGrant_EmitsBothDdlAndPermission()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "MyProc"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            ColumnChanges = System.Array.Empty<ColumnChange>(),
            DdlSideA = "CREATE PROCEDURE [dbo].[MyProc] AS BEGIN SELECT 2 END",
            DdlSideB = "CREATE PROCEDURE [dbo].[MyProc] AS BEGIN SELECT 1 END",
        };
        change.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "AppRole",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });

        var script = ScriptGenerator.Generate(new[] { change }, Opts());

        Assert.Contains("CREATE OR ALTER PROCEDURE", script.SqlText);
        Assert.Contains("GRANT EXECUTE ON OBJECT::[dbo].[MyProc] TO [AppRole]", script.SqlText);
    }
}
