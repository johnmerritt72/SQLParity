using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class ComparatorSideAFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[Products] (
    [ProductId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(200) NOT NULL,
    [Price] DECIMAL(18,2) NOT NULL,
    [Description] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([ProductId])
)
GO

CREATE VIEW [dbo].[ExpensiveProducts]
AS
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [Price] > 100
GO
";
}

public sealed class ComparatorSideBFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[Products] (
    [ProductId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(100) NOT NULL,
    [Price] DECIMAL(18,2) NOT NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([ProductId])
)
GO

CREATE TABLE [dbo].[OldTable] (
    [Id] INT NOT NULL PRIMARY KEY
)
GO
";
}

public class ComparatorIntegrationTests
    : IClassFixture<ComparatorSideAFixture>, IClassFixture<ComparatorSideBFixture>
{
    private readonly ComparisonResult _result;

    public ComparatorIntegrationTests(ComparatorSideAFixture sideA, ComparatorSideBFixture sideB)
    {
        var schemaA = new SchemaReader(sideA.ConnectionString, sideA.DatabaseName).ReadSchema();
        var schemaB = new SchemaReader(sideB.ConnectionString, sideB.DatabaseName).ReadSchema();
        _result = SchemaComparator.Compare(schemaA, schemaB);
    }

    [Fact]
    public void DetectsNewView()
    {
        var viewChange = _result.Changes.SingleOrDefault(
            c => c.ObjectType == ObjectType.View && c.Id.Name == "ExpensiveProducts");

        Assert.NotNull(viewChange);
        Assert.Equal(ChangeStatus.New, viewChange.Status);
        Assert.Equal(RiskTier.Safe, viewChange.Risk);
    }

    [Fact]
    public void DetectsDroppedTable()
    {
        var droppedChange = _result.Changes.SingleOrDefault(
            c => c.ObjectType == ObjectType.Table && c.Id.Name == "OldTable");

        Assert.NotNull(droppedChange);
        Assert.Equal(ChangeStatus.Dropped, droppedChange.Status);
        Assert.Equal(RiskTier.Destructive, droppedChange.Risk);
        Assert.NotNull(droppedChange.PreFlightSql);
    }

    [Fact]
    public void DetectsModifiedTable_WithColumnChanges()
    {
        var productsChange = _result.Changes.SingleOrDefault(
            c => c.ObjectType == ObjectType.Table && c.Id.Name == "Products");

        Assert.NotNull(productsChange);
        Assert.Equal(ChangeStatus.Modified, productsChange.Status);
        Assert.NotEmpty(productsChange.ColumnChanges);

        // Name column is widened (200 in A vs 100 in B) — should appear as Modified
        var nameChange = productsChange.ColumnChanges.SingleOrDefault(cc => cc.ColumnName == "Name");
        Assert.NotNull(nameChange);
        Assert.Equal(ChangeStatus.Modified, nameChange.Status);

        // Description column exists only in A — should appear as New
        var descChange = productsChange.ColumnChanges.SingleOrDefault(cc => cc.ColumnName == "Description");
        Assert.NotNull(descChange);
        Assert.Equal(ChangeStatus.New, descChange.Status);
    }

    [Fact]
    public void ComparisonResult_HasTierCounts()
    {
        Assert.True(_result.TotalCount > 0);
    }
}
