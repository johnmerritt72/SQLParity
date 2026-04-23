using System;
using System.Collections.Generic;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class ColumnComparatorTests
{
    private static ColumnModel MakeColumn(
        string table,
        string name,
        string dataType = "Int",
        int maxLen = 0,
        bool nullable = false,
        int ordinal = 0,
        string? collation = null,
        DefaultConstraintModel? defaultConstraint = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", table, name),
        Name = name,
        DataType = dataType,
        MaxLength = maxLen,
        Precision = 0,
        Scale = 0,
        IsNullable = nullable,
        IsIdentity = false,
        IdentitySeed = 0,
        IdentityIncrement = 0,
        IsComputed = false,
        ComputedText = null,
        IsPersisted = false,
        Collation = collation,
        DefaultConstraint = defaultConstraint,
        OrdinalPosition = ordinal,
    };

    [Fact]
    public void IdenticalColumns_NoChanges()
    {
        var colA = MakeColumn("Orders", "Id");
        var colB = MakeColumn("Orders", "Id");

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Empty(result);
    }

    [Fact]
    public void NewColumn_DetectedAsNew()
    {
        var colA = MakeColumn("Orders", "NewCol");

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, Array.Empty<ColumnModel>());

        Assert.Single(result);
        Assert.Equal(ChangeStatus.New, result[0].Status);
        Assert.Equal("NewCol", result[0].ColumnName);
        Assert.Same(colA, result[0].SideA);
        Assert.Null(result[0].SideB);
    }

    [Fact]
    public void DroppedColumn_DetectedAsDropped()
    {
        var colB = MakeColumn("Orders", "OldCol");

        var result = ColumnComparator.Compare("dbo", "Orders",
            Array.Empty<ColumnModel>(), new[] { colB });

        Assert.Single(result);
        Assert.Equal(ChangeStatus.Dropped, result[0].Status);
        Assert.Equal("OldCol", result[0].ColumnName);
        Assert.Null(result[0].SideA);
        Assert.Same(colB, result[0].SideB);
    }

    [Fact]
    public void ModifiedColumn_DifferentDataType_DetectedAsModified()
    {
        var colA = MakeColumn("Orders", "Amount", dataType: "Int");
        var colB = MakeColumn("Orders", "Amount", dataType: "BigInt");

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, result[0].Status);
        Assert.Equal("Amount", result[0].ColumnName);
        Assert.Same(colA, result[0].SideA);
        Assert.Same(colB, result[0].SideB);
    }

    [Fact]
    public void ModifiedColumn_DifferentNullability_DetectedAsModified()
    {
        var colA = MakeColumn("Orders", "Note", nullable: false);
        var colB = MakeColumn("Orders", "Note", nullable: true);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, result[0].Status);
    }

    [Fact]
    public void ModifiedColumn_DifferentMaxLength_DetectedAsModified()
    {
        var colA = MakeColumn("Orders", "Code", dataType: "nvarchar", maxLen: 50);
        var colB = MakeColumn("Orders", "Code", dataType: "nvarchar", maxLen: 100);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, result[0].Status);
    }

    [Fact]
    public void UnchangedColumn_NotDetected()
    {
        var colA = MakeColumn("Orders", "Status", dataType: "nvarchar", maxLen: 20, nullable: true);
        var colB = MakeColumn("Orders", "Status", dataType: "nvarchar", maxLen: 20, nullable: true);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Empty(result);
    }
}
