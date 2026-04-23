using System;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class RiskClassifierTests
{
    private static Change MakeChange(ObjectType type, ChangeStatus status) => new()
    {
        Id = SchemaQualifiedName.TopLevel("dbo", "TestObj"),
        ObjectType = type,
        Status = status,
        DdlSideA = status == ChangeStatus.Dropped ? null : "DDL A",
        DdlSideB = status == ChangeStatus.New ? null : "DDL B",
        ColumnChanges = Array.Empty<ColumnChange>(),
    };

    [Theory]
    [InlineData(ObjectType.Table)]
    [InlineData(ObjectType.View)]
    [InlineData(ObjectType.StoredProcedure)]
    [InlineData(ObjectType.UserDefinedFunction)]
    [InlineData(ObjectType.Schema)]
    [InlineData(ObjectType.Sequence)]
    [InlineData(ObjectType.Synonym)]
    [InlineData(ObjectType.UserDefinedDataType)]
    [InlineData(ObjectType.UserDefinedTableType)]
    public void NewObject_IsSafe(ObjectType type)
    {
        var change = MakeChange(type, ChangeStatus.New);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Safe, tier);
    }

    [Fact]
    public void DroppedTable_IsDestructive()
    {
        var change = MakeChange(ObjectType.Table, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Theory]
    [InlineData(ObjectType.View)]
    [InlineData(ObjectType.StoredProcedure)]
    [InlineData(ObjectType.UserDefinedFunction)]
    [InlineData(ObjectType.Sequence)]
    [InlineData(ObjectType.Synonym)]
    [InlineData(ObjectType.UserDefinedDataType)]
    [InlineData(ObjectType.UserDefinedTableType)]
    public void DroppedCodeObject_IsDestructive(ObjectType type)
    {
        var change = MakeChange(type, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedSchema_IsDestructive()
    {
        var change = MakeChange(ObjectType.Schema, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedIndex_IsRisky()
    {
        var change = MakeChange(ObjectType.Index, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Risky, tier);
    }

    [Fact]
    public void DroppedForeignKey_IsDestructive()
    {
        var change = MakeChange(ObjectType.ForeignKey, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedCheckConstraint_IsDestructive()
    {
        var change = MakeChange(ObjectType.CheckConstraint, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Fact]
    public void DroppedTrigger_IsDestructive()
    {
        var change = MakeChange(ObjectType.Trigger, ChangeStatus.Dropped);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Destructive, tier);
    }

    [Theory]
    [InlineData(ObjectType.View)]
    [InlineData(ObjectType.StoredProcedure)]
    [InlineData(ObjectType.UserDefinedFunction)]
    public void ModifiedRoutine_IsCaution(ObjectType type)
    {
        var change = MakeChange(type, ChangeStatus.Modified);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void ModifiedIndex_IsCaution()
    {
        var change = MakeChange(ObjectType.Index, ChangeStatus.Modified);
        var (tier, _) = RiskClassifier.Classify(change);
        Assert.Equal(RiskTier.Caution, tier);
    }

    [Fact]
    public void ClassifyReturnsReasons()
    {
        var change = MakeChange(ObjectType.Table, ChangeStatus.Dropped);
        var (_, reasons) = RiskClassifier.Classify(change);
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
    }
}
