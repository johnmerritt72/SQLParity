using System;
using System.Collections.Generic;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class ScriptGeneratorTests
{
    private static Change MakeChange(ObjectType type, ChangeStatus status,
        string name = "TestObj", string? ddlA = "CREATE ...", string? ddlB = null,
        RiskTier risk = RiskTier.Safe) => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", name),
        ObjectType = type,
        Status = status,
        DdlSideA = status == ChangeStatus.Dropped ? null : ddlA,
        DdlSideB = status == ChangeStatus.New ? null : (ddlB ?? "OLD DDL"),
        Risk = risk,
        ColumnChanges = Array.Empty<ColumnChange>(),
    };

    private static ScriptGenerationOptions DefaultOptions() => new()
    {
        DestinationServer = "PROD-SERVER",
        DestinationDatabase = "MyDb",
        DestinationLabel = "PROD",
        SourceServer = "DEV-SERVER",
        SourceDatabase = "MyDb_Dev",
        SourceLabel = "DEV",
    };

    [Fact]
    public void GeneratedScript_StartsWithHeaderBanner()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.StartsWith("/*", script.SqlText.TrimStart());
    }

    [Fact]
    public void HeaderBanner_ContainsBothLabels()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.Contains("PROD", script.SqlText);
        Assert.Contains("DEV", script.SqlText);
    }

    [Fact]
    public void HeaderBanner_ContainsBothServerNames()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.Contains("PROD-SERVER", script.SqlText);
        Assert.Contains("DEV-SERVER", script.SqlText);
    }

    [Fact]
    public void HeaderBanner_ContainsUseStatement()
    {
        var changes = new List<Change> { MakeChange(ObjectType.Table, ChangeStatus.New) };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.Contains("USE [MyDb]", script.SqlText);
    }

    [Fact]
    public void HeaderBanner_ContainsRiskTierCounts()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1", risk: RiskTier.Safe),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "T2", risk: RiskTier.Destructive),
        };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.Contains("Safe", script.SqlText);
        Assert.Contains("Destructive", script.SqlText);
    }

    [Fact]
    public void Script_ContainsNewObjectDdl()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "Orders", "CREATE TABLE [dbo].[Orders] (Id INT)"),
        };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.Contains("CREATE TABLE [dbo].[Orders]", script.SqlText);
    }

    [Fact]
    public void Script_DroppedObject_ContainsDropStatement()
    {
        var change = MakeChange(ObjectType.View, ChangeStatus.Dropped, "OldView");
        change.Risk = RiskTier.Destructive;
        var script = ScriptGenerator.Generate(new[] { change }, DefaultOptions());
        Assert.Contains("DROP VIEW", script.SqlText);
        Assert.Contains("[dbo].[OldView]", script.SqlText);
    }

    [Fact]
    public void Script_EachChangeHasGoSeparator()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1", "CREATE TABLE T1"),
            MakeChange(ObjectType.Table, ChangeStatus.New, "T2", "CREATE TABLE T2"),
        };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.Contains("GO", script.SqlText);
    }

    [Fact]
    public void Script_SetsMetadata()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, risk: RiskTier.Safe),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "Old", risk: RiskTier.Destructive),
        };
        var script = ScriptGenerator.Generate(changes, DefaultOptions());
        Assert.Equal("MyDb", script.DestinationDatabase);
        Assert.Equal("PROD-SERVER", script.DestinationServer);
        Assert.Equal(2, script.TotalChanges);
        Assert.Equal(1, script.DestructiveChanges);
    }

    [Fact]
    public void EmptyChanges_ProducesHeaderOnly()
    {
        var script = ScriptGenerator.Generate(new List<Change>(), DefaultOptions());
        Assert.Contains("/*", script.SqlText);
        Assert.Equal(0, script.TotalChanges);
    }
}
