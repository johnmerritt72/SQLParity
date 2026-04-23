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
}
