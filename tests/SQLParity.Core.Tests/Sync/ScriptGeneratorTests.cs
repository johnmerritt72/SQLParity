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

    [Fact]
    public void PairedModified_RewritesCreateProcedureName()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "Foo"),  // DB's correct name
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            DdlSideA = "CREATE PROCEDURE dbo.Fooo AS SELECT 1",  // file's typo'd DDL (apply DDL)
            DdlSideB = "CREATE PROCEDURE dbo.Foo AS SELECT 1",   // existing DB DDL (display only)
            PairedFromName = "Fooo",
            ColumnChanges = System.Array.Empty<ColumnChange>(),
        };

        var script = ScriptGenerator.Generate(new[] { change }, DefaultOptions());
        var result = script.SqlText;

        Assert.Contains("dbo.Foo AS SELECT 1", result);
        Assert.DoesNotContain("Fooo", result);
    }

    [Fact]
    public void PairedModified_RewritesCreateOrAlterForm()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "Foo"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            DdlSideA = "CREATE OR ALTER PROCEDURE [dbo].[Fooo] AS SELECT 1",  // file's typo'd DDL (apply DDL)
            DdlSideB = "CREATE OR ALTER PROCEDURE dbo.Foo AS SELECT 1",        // existing DB DDL (display only)
            PairedFromName = "Fooo",
            ColumnChanges = System.Array.Empty<ColumnChange>(),
        };

        var script = ScriptGenerator.Generate(new[] { change }, DefaultOptions());
        var result = script.SqlText;

        Assert.Contains("CREATE OR ALTER PROCEDURE", result);
        Assert.Contains("Foo", result);
        Assert.DoesNotContain("Fooo", result);
    }

    [Fact]
    public void PairedModified_OnlyRewritesCreateNameToken_NotBodyReferences()
    {
        // Recursive reference in body — should be left alone for the user to spot.
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "Foo"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            DdlSideA = "CREATE PROCEDURE dbo.Fooo AS BEGIN EXEC dbo.Fooo END",  // file's typo'd DDL (apply DDL)
            DdlSideB = "CREATE PROCEDURE dbo.Foo AS BEGIN EXEC dbo.Foo END",    // existing DB DDL (display only)
            PairedFromName = "Fooo",
            ColumnChanges = System.Array.Empty<ColumnChange>(),
        };

        var script = ScriptGenerator.Generate(new[] { change }, DefaultOptions());
        var result = script.SqlText;

        // Header rewritten...
        Assert.Contains("PROCEDURE dbo.Foo", result);
        // ...but the body reference is left alone (user must fix that separately).
        Assert.Contains("EXEC dbo.Fooo", result);
    }

    [Fact]
    public void NonPaired_DoesNotRewrite()
    {
        var change = new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "Foo"),
            ObjectType = ObjectType.StoredProcedure,
            Status = ChangeStatus.Modified,
            DdlSideA = "CREATE PROCEDURE dbo.Foo AS SELECT 1",
            DdlSideB = "CREATE PROCEDURE dbo.Foo AS SELECT 2",
            PairedFromName = null,  // not paired
            ColumnChanges = System.Array.Empty<ColumnChange>(),
        };

        var script = ScriptGenerator.Generate(new[] { change }, DefaultOptions());
        var result = script.SqlText;

        // DdlSideA is the apply DDL — passes through unchanged (no rewrite since not paired).
        Assert.Contains("SELECT 1", result);
    }
}
