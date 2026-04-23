using System;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class ColumnRiskClassifierTests
{
    private static ColumnModel MakeCol(
        string name = "Col",
        string type = "Int",
        int maxLen = 0,
        bool nullable = false,
        DefaultConstraintModel? dc = null,
        string? collation = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "T", name),
        Name = name,
        DataType = type,
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
        DefaultConstraint = dc,
        OrdinalPosition = 0,
    };

    private static ColumnChange MakeColumnChange(
        ChangeStatus status,
        ColumnModel? sideA,
        ColumnModel? sideB) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "T", sideA?.Name ?? sideB?.Name ?? "Col"),
        ColumnName = sideA?.Name ?? sideB?.Name ?? "Col",
        Status = status,
        SideA = sideA,
        SideB = sideB,
    };

    private static DefaultConstraintModel MakeDc(string def = "((0))") => new()
    {
        Name = "DF_T_Col",
        Definition = def,
    };

    [Fact]
    public void NewNullableColumn_IsSafe()
    {
        var col = MakeCol(nullable: true);
        var change = MakeColumnChange(ChangeStatus.New, col, null);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Safe, tier);
    }

    [Fact]
    public void NewNotNullColumnWithDefault_IsCaution()
    {
        var col = MakeCol(nullable: false, dc: MakeDc());
        var change = MakeColumnChange(ChangeStatus.New, col, null);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void NewNotNullColumnWithoutDefault_IsRisky()
    {
        var col = MakeCol(nullable: false);
        var change = MakeColumnChange(ChangeStatus.New, col, null);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DroppedColumn_IsDestructive()
    {
        var col = MakeCol();
        var change = MakeColumnChange(ChangeStatus.Dropped, col, null);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void WidenedColumn_IsCaution()
    {
        var colA = MakeCol(type: "nvarchar", maxLen: 50);
        var colB = MakeCol(type: "nvarchar", maxLen: 100);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void NarrowedColumn_IsRisky()
    {
        var colA = MakeCol(type: "nvarchar", maxLen: 100);
        var colB = MakeCol(type: "nvarchar", maxLen: 50);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DataTypeChanged_IsRisky()
    {
        var colA = MakeCol(type: "Int");
        var colB = MakeCol(type: "BigInt");
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void CollationChanged_IsRisky()
    {
        var colA = MakeCol(type: "nvarchar", collation: "SQL_Latin1_General_CP1_CI_AS");
        var colB = MakeCol(type: "nvarchar", collation: "Latin1_General_BIN");
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void NullableToNotNull_IsCaution()
    {
        var colA = MakeCol(nullable: true);
        var colB = MakeCol(nullable: false);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void NotNullToNullable_IsSafe()
    {
        var colA = MakeCol(nullable: false);
        var colB = MakeCol(nullable: true);
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Safe, tier);
    }

    [Fact]
    public void DefaultChanged_IsRisky()
    {
        var colA = MakeCol(dc: MakeDc("((0))"));
        var colB = MakeCol(dc: MakeDc("((1))"));
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DefaultAdded_IsSafe()
    {
        var colA = MakeCol();
        var colB = MakeCol(dc: MakeDc());
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Safe, tier);
    }

    [Fact]
    public void DefaultRemoved_IsRisky()
    {
        var colA = MakeCol(dc: MakeDc());
        var colB = MakeCol();
        var change = MakeColumnChange(ChangeStatus.Modified, colA, colB);
        var (tier, _) = ColumnRiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void ClassifyAlwaysReturnsReasons()
    {
        // Test with a few representative cases
        var cases = new (ChangeStatus status, ColumnModel? a, ColumnModel? b)[]
        {
            (ChangeStatus.New, MakeCol(nullable: true), null),
            (ChangeStatus.Dropped, MakeCol(), null),
            (ChangeStatus.Modified, MakeCol(type: "Int"), MakeCol(type: "BigInt")),
        };

        foreach (var (status, a, b) in cases)
        {
            var change = MakeColumnChange(status, a, b);
            var (_, reasons) = ColumnRiskClassifier.Classify(change);
            Assert.NotEmpty(reasons);
        }
    }
}
