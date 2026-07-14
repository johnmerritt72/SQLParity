using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class LiveApplierFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[ExistingTable] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(100) NOT NULL
)
GO
";
}

public class LiveApplierTests : IClassFixture<LiveApplierFixture>
{
    private readonly LiveApplierFixture _fixture;
    private readonly ScriptGenerationOptions _options;

    public LiveApplierTests(LiveApplierFixture fixture)
    {
        _fixture = fixture;
        _options = new ScriptGenerationOptions
        {
            DestinationServer = @"(localdb)\MSSQLLocalDB",
            DestinationDatabase = fixture.DatabaseName,
            DestinationLabel = "TEST",
            SourceServer = "source",
            SourceDatabase = "sourceDb",
            SourceLabel = "SRC",
        };
    }

    [Fact]
    public void Apply_NewTable_Succeeds()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "NewTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[NewTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.True(result.FullySucceeded);
        Assert.Equal(1, result.SucceededCount);
        Assert.True(TableExists("dbo", "NewTable"));
    }

    [Fact]
    public void Apply_InvalidSql_StopsOnFailure()
    {
        var goodChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "GoodTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[GoodTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };
        var badChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "BadTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "THIS IS NOT VALID SQL",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };
        var afterBadChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "AfterBadTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[AfterBadTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { goodChange, badChange, afterBadChange }, _options);

        Assert.False(result.FullySucceeded);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(2, result.Steps.Count); // AfterBadTable not attempted
        Assert.NotNull(result.Steps[1].ErrorMessage);
    }

    [Fact]
    public void Apply_DroppedTable_Succeeds()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "ExistingTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.Dropped,
            DdlSideA = null,
            DdlSideB = "CREATE TABLE ...",
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.True(result.FullySucceeded);
        Assert.False(TableExists("dbo", "ExistingTable"));
    }

    [Fact]
    public void Apply_PermissionOnlyChange_GrantsPermissionOnDestination()
    {
        // The user-reported bug: Apply Live reports success but the destination
        // permissions are unchanged. A change carrying PermissionChanges (here,
        // a permission-only change on a proc that already matches by body) must
        // emit GRANT statements against the destination.
        Exec("IF SUSER_ID('TestGrantUser1') IS NULL CREATE USER [TestGrantUser1] WITHOUT LOGIN");
        Exec("CREATE PROCEDURE [dbo].[PermOnlyProc] AS BEGIN SELECT 1 END");

        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "PermOnlyProc"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            ColumnChanges = Array.Empty<ColumnChange>(),
            DdlSideA = "CREATE PROCEDURE [dbo].[PermOnlyProc] AS BEGIN SELECT 1 END",
            DdlSideB = "CREATE PROCEDURE [dbo].[PermOnlyProc] AS BEGIN SELECT 1 END",
            IsPermissionOnlyChange = true,
        };
        change.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "TestGrantUser1",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.True(result.FullySucceeded, "Apply should succeed; errors: "
            + string.Join("; ", System.Linq.Enumerable.Select(result.Steps, s => s.ErrorMessage ?? "")));
        Assert.True(HasExecutePermission("dbo", "PermOnlyProc", "TestGrantUser1"),
            "Expected EXECUTE on [dbo].[PermOnlyProc] to be granted to [TestGrantUser1] after Apply Live");
    }

    [Fact]
    public void Apply_ModifiedProcWithGrant_AppliesBothDdlAndPermission()
    {
        // The exact scenario the user reported: a Modified proc that also has a
        // permission diff. The DDL must update AND the grant must be applied.
        Exec("IF SUSER_ID('TestGrantUser2') IS NULL CREATE USER [TestGrantUser2] WITHOUT LOGIN");
        Exec("CREATE PROCEDURE [dbo].[ModifiedProc] AS BEGIN SELECT 1 END");

        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "ModifiedProc"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            ColumnChanges = Array.Empty<ColumnChange>(),
            DdlSideA = "CREATE PROCEDURE [dbo].[ModifiedProc] AS BEGIN SELECT 2 END",
            DdlSideB = "CREATE PROCEDURE [dbo].[ModifiedProc] AS BEGIN SELECT 1 END",
        };
        change.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "TestGrantUser2",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.True(result.FullySucceeded);
        Assert.True(HasExecutePermission("dbo", "ModifiedProc", "TestGrantUser2"));
    }

    [Fact]
    public void Apply_MissingPrincipal_SkipsItButGrantsOthers()
    {
        // User scenario: a permission update lists three grantees; the first
        // does not exist on the destination. The script must skip the missing
        // principal and still apply grants to the other two — not abort the
        // whole batch.
        Exec("IF SUSER_ID('PresentUserA') IS NULL CREATE USER [PresentUserA] WITHOUT LOGIN");
        Exec("IF SUSER_ID('PresentUserB') IS NULL CREATE USER [PresentUserB] WITHOUT LOGIN");
        // Intentionally do NOT create [MissingUser].
        Exec("CREATE PROCEDURE [dbo].[ThreeGranteeProc] AS BEGIN SELECT 1 END");

        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "ThreeGranteeProc"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            ColumnChanges = Array.Empty<ColumnChange>(),
            DdlSideA = "CREATE PROCEDURE [dbo].[ThreeGranteeProc] AS BEGIN SELECT 1 END",
            DdlSideB = "CREATE PROCEDURE [dbo].[ThreeGranteeProc] AS BEGIN SELECT 1 END",
            IsPermissionOnlyChange = true,
        };
        change.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "MissingUser",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });
        change.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "PresentUserA",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });
        change.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "PresentUserB",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.True(result.FullySucceeded, "Missing principal must not abort the apply");
        Assert.True(HasExecutePermission("dbo", "ThreeGranteeProc", "PresentUserA"),
            "Existing principal A should still receive its grant");
        Assert.True(HasExecutePermission("dbo", "ThreeGranteeProc", "PresentUserB"),
            "Existing principal B should still receive its grant");

        // The PRINT 'Skipped...' message for the missing principal must be
        // captured into the step's InfoMessages so the UI can surface it.
        var permStep = result.Steps.Single(s => s.ObjectName.EndsWith("(permissions)"));
        Assert.Contains(permStep.InfoMessages,
            m => m.Contains("Skipped permissions for [MissingUser]"));
    }

    [Fact]
    public void Apply_PermissionRollsBackWhenLaterStepFails()
    {
        // If a later step fails, the whole transaction must roll back — including
        // any permission changes applied earlier in the same Apply call.
        Exec("IF SUSER_ID('TestGrantUser3') IS NULL CREATE USER [TestGrantUser3] WITHOUT LOGIN");
        Exec("CREATE PROCEDURE [dbo].[RollbackProc] AS BEGIN SELECT 1 END");

        var permChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "RollbackProc"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            ColumnChanges = Array.Empty<ColumnChange>(),
            DdlSideA = "CREATE PROCEDURE [dbo].[RollbackProc] AS BEGIN SELECT 1 END",
            DdlSideB = "CREATE PROCEDURE [dbo].[RollbackProc] AS BEGIN SELECT 1 END",
            IsPermissionOnlyChange = true,
        };
        permChange.PermissionChanges.Add(new PermissionChange
        {
            GranteeName = "TestGrantUser3",
            PermissionName = "EXECUTE",
            StateSideA = PermissionState.Grant,
            StateSideB = null,
        });
        var badChange = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "RollbackBadTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "THIS IS NOT VALID SQL",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { permChange, badChange }, _options);

        Assert.False(result.FullySucceeded);
        Assert.False(HasExecutePermission("dbo", "RollbackProc", "TestGrantUser3"),
            "Permission must be rolled back when a later step fails");
    }

    [Fact]
    public void Apply_SetsResultMetadata()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "MetaTable"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE [dbo].[MetaTable] ([Id] INT NOT NULL PRIMARY KEY)",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
        };

        var applier = new LiveApplier(_fixture.ConnectionString);
        var result = applier.Apply(new[] { change }, _options);

        Assert.Equal(_fixture.DatabaseName, result.DestinationDatabase);
        Assert.True(result.StartedAtUtc <= result.CompletedAtUtc);
        Assert.True(result.Steps[0].Duration.TotalMilliseconds >= 0);
    }

    private bool TableExists(string schema, string name)
    {
        using var conn = new SqlConnection(_fixture.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{name}'";
        return (int)cmd.ExecuteScalar()! > 0;
    }

    private void Exec(string sql)
    {
        using var conn = new SqlConnection(_fixture.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool HasExecutePermission(string schema, string proc, string grantee)
    {
        using var conn = new SqlConnection(_fixture.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM sys.database_permissions p
            JOIN sys.database_principals u ON p.grantee_principal_id = u.principal_id
            JOIN sys.objects o ON p.major_id = o.object_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE p.class_desc = 'OBJECT_OR_COLUMN'
              AND p.state IN ('G', 'W')
              AND p.permission_name = 'EXECUTE'
              AND u.name = @grantee
              AND s.name = @schema
              AND o.name = @proc";
        cmd.Parameters.AddWithValue("@grantee", grantee);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@proc", proc);
        return (int)cmd.ExecuteScalar()! > 0;
    }
}
