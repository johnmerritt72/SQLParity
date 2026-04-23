using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class TableTestFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE SCHEMA [sales]
GO

CREATE TABLE [dbo].[Categories] (
    [CategoryId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(100) NOT NULL,
    [Description] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_Categories] PRIMARY KEY CLUSTERED ([CategoryId])
)
GO

CREATE TABLE [sales].[Orders] (
    [OrderId] INT NOT NULL IDENTITY(1,1),
    [OrderDate] DATETIME2 NOT NULL CONSTRAINT [DF_Orders_OrderDate] DEFAULT (GETUTCDATE()),
    [CategoryId] INT NOT NULL,
    [Total] DECIMAL(18,2) NOT NULL,
    [Notes] NVARCHAR(500) NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId]),
    CONSTRAINT [FK_Orders_Categories] FOREIGN KEY ([CategoryId]) REFERENCES [dbo].[Categories]([CategoryId]),
    CONSTRAINT [CK_Orders_Total] CHECK ([Total] >= 0)
)
GO

CREATE NONCLUSTERED INDEX [IX_Orders_OrderDate] ON [sales].[Orders] ([OrderDate] DESC) INCLUDE ([Total])
GO

CREATE TRIGGER [sales].[TR_Orders_Audit] ON [sales].[Orders]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    RETURN;
END
GO
";
}

public class SchemaReaderTableTests : IClassFixture<TableTestFixture>
{
    private readonly DatabaseSchema _schema;

    public SchemaReaderTableTests(TableTestFixture fixture)
    {
        var reader = new SchemaReader(fixture.ConnectionString, fixture.DatabaseName);
        _schema = reader.ReadSchema();
    }

    [Fact]
    public void ReadsTables()
    {
        Assert.Equal(2, _schema.Tables.Count);
        Assert.Contains(_schema.Tables, t => t.Name == "Categories" && t.Schema == "dbo");
        Assert.Contains(_schema.Tables, t => t.Name == "Orders" && t.Schema == "sales");
    }

    [Fact]
    public void ReadsColumns()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        Assert.Equal(5, orders.Columns.Count);
        Assert.Contains(orders.Columns, c => c.Name == "OrderId" && c.IsIdentity);
        Assert.Contains(orders.Columns, c => c.Name == "Total" && !c.IsNullable);
        Assert.Contains(orders.Columns, c => c.Name == "Notes" && c.IsNullable);
    }

    [Fact]
    public void ReadsColumnOrdinalPositions()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var ordinals = orders.Columns.Select(c => c.OrdinalPosition).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ordinals);
    }

    [Fact]
    public void ReadsPrimaryKey()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var pk = orders.Indexes.SingleOrDefault(i => i.IsPrimaryKey);
        Assert.NotNull(pk);
        Assert.Equal("PK_Orders", pk.Name);
        Assert.True(pk.IsClustered);
    }

    [Fact]
    public void ReadsNonClusteredIndex()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var idx = orders.Indexes.SingleOrDefault(i => i.Name == "IX_Orders_OrderDate");
        Assert.NotNull(idx);
        Assert.False(idx.IsClustered);

        var keyCol = idx.Columns.Single(c => !c.IsIncluded);
        Assert.Equal("OrderDate", keyCol.Name);
        Assert.True(keyCol.IsDescending);

        var inclCol = idx.Columns.Single(c => c.IsIncluded);
        Assert.Equal("Total", inclCol.Name);
    }

    [Fact]
    public void ReadsForeignKey()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var fk = orders.ForeignKeys.SingleOrDefault(f => f.Name == "FK_Orders_Categories");
        Assert.NotNull(fk);
        Assert.Equal("dbo", fk.ReferencedTableSchema);
        Assert.Equal("Categories", fk.ReferencedTableName);
        Assert.Single(fk.Columns);
        Assert.Equal("CategoryId", fk.Columns[0].LocalColumn);
        Assert.Equal("CategoryId", fk.Columns[0].ReferencedColumn);
    }

    [Fact]
    public void ReadsCheckConstraint()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var chk = orders.CheckConstraints.SingleOrDefault(c => c.Name == "CK_Orders_Total");
        Assert.NotNull(chk);
        Assert.Contains("Total", chk.Definition);
        Assert.Contains(">=", chk.Definition);
    }

    [Fact]
    public void ReadsDefaultConstraint()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var orderDate = orders.Columns.Single(c => c.Name == "OrderDate");
        Assert.NotNull(orderDate.DefaultConstraint);
        Assert.Equal("DF_Orders_OrderDate", orderDate.DefaultConstraint.Name);
        Assert.Contains("getutcdate", orderDate.DefaultConstraint.Definition, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsTrigger()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        var trig = orders.Triggers.SingleOrDefault(t => t.Name == "TR_Orders_Audit");
        Assert.NotNull(trig);
        Assert.True(trig.IsEnabled);
        Assert.True(trig.FiresOnInsert);
        Assert.True(trig.FiresOnUpdate);
        Assert.False(trig.FiresOnDelete);
    }

    [Fact]
    public void TableDdlIsNotEmpty()
    {
        var orders = _schema.Tables.Single(t => t.Name == "Orders");
        Assert.False(string.IsNullOrWhiteSpace(orders.Ddl));
        Assert.Contains("CREATE TABLE", orders.Ddl);
    }
}
