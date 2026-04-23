using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class DependencyOrdererTests
{
    private static Change MakeChange(ObjectType type, ChangeStatus status, string name = "Obj") => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", name),
        ObjectType = type,
        Status = status,
        DdlSideA = status == ChangeStatus.Dropped ? null : "DDL A",
        DdlSideB = status == ChangeStatus.New ? null : "DDL B",
        ColumnChanges = Array.Empty<ColumnChange>(),
    };

    [Fact]
    public void DropsBeforeCreates_WhenMixed()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "NewTable"),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "OldTable"),
        };
        var ordered = DependencyOrderer.Order(changes).ToList();
        var dropIdx = ordered.FindIndex(c => c.Status == ChangeStatus.Dropped);
        var createIdx = ordered.FindIndex(c => c.Status == ChangeStatus.New);
        Assert.True(dropIdx < createIdx, "Drops should come before creates");
    }

    [Fact]
    public void SchemasCreatedBeforeTables()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1"),
            MakeChange(ObjectType.Schema, ChangeStatus.New, "S1"),
        };
        var ordered = DependencyOrderer.Order(changes).ToList();
        var schemaIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Schema);
        var tableIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Table);
        Assert.True(schemaIdx < tableIdx, "Schemas should be created before tables");
    }

    [Fact]
    public void TablesDroppedBeforeSchemas()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Schema, ChangeStatus.Dropped, "S1"),
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "T1"),
        };
        var ordered = DependencyOrderer.Order(changes).ToList();
        var tableIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Table);
        var schemaIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Schema);
        Assert.True(tableIdx < schemaIdx, "Tables should be dropped before schemas");
    }

    [Fact]
    public void ForeignKeysDroppedBeforeTables()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.Dropped, "T1"),
            MakeChange(ObjectType.ForeignKey, ChangeStatus.Dropped, "FK1"),
        };
        var ordered = DependencyOrderer.Order(changes).ToList();
        var fkIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.ForeignKey);
        var tableIdx = ordered.FindIndex(c => c.ObjectType == ObjectType.Table);
        Assert.True(fkIdx < tableIdx, "Foreign keys should be dropped before tables");
    }

    [Fact]
    public void EmptyList_ReturnsEmpty()
    {
        var ordered = DependencyOrderer.Order(new List<Change>());
        Assert.Empty(ordered);
    }

    [Fact]
    public void PreservesAllChanges()
    {
        var changes = new List<Change>
        {
            MakeChange(ObjectType.Table, ChangeStatus.New, "T1"),
            MakeChange(ObjectType.View, ChangeStatus.New, "V1"),
        };
        var ordered = DependencyOrderer.Order(changes);
        Assert.Equal(2, ordered.Count());
    }
}
