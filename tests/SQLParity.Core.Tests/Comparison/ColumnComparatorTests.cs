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
        int precision = 0,
        int scale = 0,
        bool nullable = false,
        int ordinal = 0,
        string? collation = null,
        DefaultConstraintModel? defaultConstraint = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", table, name),
        Name = name,
        DataType = dataType,
        MaxLength = maxLen,
        Precision = precision,
        Scale = scale,
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

    // Fixed-width types (bigint/int/smallint/tinyint/datetime/float/bit/etc.) carry
    // a non-zero byte size in sys.columns.max_length but the folder-side parser has
    // no parameter to extract, so MaxLength is 0. The size is fully determined by
    // the type itself, so a difference here is not a real schema change.
    [Theory]
    [InlineData("bigint", 8)]
    [InlineData("int", 4)]
    [InlineData("smallint", 2)]
    [InlineData("tinyint", 1)]
    [InlineData("datetime", 8)]
    [InlineData("float", 8)]
    [InlineData("bit", 1)]
    public void MaxLengthOnFixedWidthType_NotDetectedAsModified(string dataType, int dbMaxLength)
    {
        var colA = MakeColumn("Orders", "Col", dataType: dataType, maxLen: dbMaxLength);
        var colB = MakeColumn("Orders", "Col", dataType: dataType, maxLen: 0);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Empty(result);
    }

    [Fact]
    public void MaxLengthOnVarcharStillCompared()
    {
        var colA = MakeColumn("Orders", "Code", dataType: "varchar", maxLen: 50);
        var colB = MakeColumn("Orders", "Code", dataType: "varchar", maxLen: 100);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, result[0].Status);
    }

    // sys.columns.precision reports an implementation-defined number of digits
    // for every numeric type, but the T-SQL grammar only lets you write a
    // precision parameter on decimal/numeric. For everything else the folder
    // parser leaves Precision at 0, so an unguarded comparison flags every
    // int/bigint/tinyint/smallint/datetime/bit/float column as modified.
    [Theory]
    [InlineData("int", 10)]
    [InlineData("bigint", 19)]
    [InlineData("smallint", 5)]
    [InlineData("tinyint", 3)]
    [InlineData("bit", 1)]
    [InlineData("datetime", 23)]
    [InlineData("float", 53)]
    public void PrecisionOnNonDecimalType_NotDetectedAsModified(string dataType, int dbPrecision)
    {
        var colA = MakeColumn("Orders", "Col", dataType: dataType, precision: dbPrecision);
        var colB = MakeColumn("Orders", "Col", dataType: dataType, precision: 0);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Empty(result);
    }

    [Fact]
    public void PrecisionOnDecimalStillCompared()
    {
        var colA = MakeColumn("Orders", "Amount", dataType: "decimal", precision: 18, scale: 2);
        var colB = MakeColumn("Orders", "Amount", dataType: "decimal", precision: 10, scale: 2);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, result[0].Status);
    }

    // datetime has Scale=3 in sys.columns but no scale parameter in T-SQL
    // syntax. Same parity gap as Precision.
    [Theory]
    [InlineData("datetime", 3)]
    [InlineData("money", 4)]
    [InlineData("smallmoney", 4)]
    public void ScaleOnNonExplicitScaleType_NotDetectedAsModified(string dataType, int dbScale)
    {
        var colA = MakeColumn("Orders", "Col", dataType: dataType, scale: dbScale);
        var colB = MakeColumn("Orders", "Col", dataType: dataType, scale: 0);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("decimal")]
    [InlineData("numeric")]
    [InlineData("datetime2")]
    [InlineData("time")]
    [InlineData("datetimeoffset")]
    public void ScaleOnExplicitScaleType_StillCompared(string dataType)
    {
        var colA = MakeColumn("Orders", "Col", dataType: dataType, scale: 3);
        var colB = MakeColumn("Orders", "Col", dataType: dataType, scale: 7);

        var result = ColumnComparator.Compare("dbo", "Orders",
            new[] { colA }, new[] { colB });

        Assert.Single(result);
        Assert.Equal(ChangeStatus.Modified, result[0].Status);
    }
}
